namespace Wanxiang.UI

open System
open System.IO
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks

/// 决策 251：外部打开的 `wanxiang://` 链接的收件箱。
/// 启动参数、第二个实例转发、macOS 系统激活都投到这里；MainView 订阅后在 UI 线程打开。
/// MainView 还没起来时先排队，订阅时一次交付。
module ExternalLinks =
    let private gate = obj ()
    let private queue = System.Collections.Generic.Queue<string>()
    let mutable private sink: (string -> unit) option = None

    let post (url: string) =
        if not (String.IsNullOrWhiteSpace url) then
            let target = lock gate (fun () -> match sink with Some s -> Some s | None -> queue.Enqueue url; None)
            target |> Option.iter (fun s -> s url)

    /// 订阅（MainView 构造时）。返回此前排队的链接已交付。
    let subscribe (handler: string -> unit) =
        let pending =
            lock gate (fun () ->
                sink <- Some handler
                let items = queue.ToArray()
                queue.Clear()
                items)
        for url in pending do handler url

/// 决策 251：系统注册 `wanxiang://` 协议。只写当前用户范围，幂等（内容不变就不碰）。
module UrlSchemeRegistration =

    let desktopFileName = "wanxiang-url-handler.desktop"

    /// Linux 桌面文件：`%u` 把链接交给万象；NoDisplay 不在应用菜单里多出一个图标。
    let desktopEntry (command: string) =
        String.concat "\n"
            [ "[Desktop Entry]"
              "Type=Application"
              "Name=万象"
              "Comment=打开万象消息链接"
              sprintf "Exec=%s %%u" command
              "Terminal=false"
              "NoDisplay=true"
              "MimeType=x-scheme-handler/wanxiang;"
              "" ]

    /// 引号包住含空格的路径（桌面文件与注册表命令都用）。
    let quote (path: string) = if path.Contains ' ' then "\"" + path + "\"" else path

    /// 当前进程的启动命令：apphost 直接用它；`dotnet wanxiang.dll` 时带上程序集。
    let launchCommand () : string option =
        match Environment.ProcessPath with
        | null -> None
        | exe ->
            let name = Path.GetFileNameWithoutExtension exe
            if name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) then
                match Reflection.Assembly.GetEntryAssembly() with
                | null -> None
                | asm when not (String.IsNullOrEmpty asm.Location) -> Some(quote exe + " " + quote asm.Location)
                | _ -> None
            else Some(quote exe)

    let private runQuiet (file: string) (args: string list) =
        try
            let psi = Diagnostics.ProcessStartInfo(file)
            for a in args do psi.ArgumentList.Add a
            psi.UseShellExecute <- false
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            use p = Diagnostics.Process.Start psi
            p.WaitForExit 3000 |> ignore
        with _ -> ()

    let private registerLinux (command: string) =
        let dataHome =
            match Environment.GetEnvironmentVariable "XDG_DATA_HOME" with
            | s when not (String.IsNullOrWhiteSpace s) -> s
            | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share")
        let dir = Path.Combine(dataHome, "applications")
        let path = Path.Combine(dir, desktopFileName)
        let content = desktopEntry command
        let existing = if File.Exists path then (try File.ReadAllText path with _ -> "") else ""
        if existing <> content then
            Directory.CreateDirectory dir |> ignore
            File.WriteAllText(path, content)
            runQuiet "xdg-mime" [ "default"; desktopFileName; "x-scheme-handler/wanxiang" ]
            runQuiet "update-desktop-database" [ dir ]

    let private registerWindows (command: string) =
        if OperatingSystem.IsWindows() then
            use key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\wanxiang")
            key.SetValue("", "URL:万象消息链接")
            key.SetValue("URL Protocol", "")
            use cmd = key.CreateSubKey(@"shell\open\command")
            let wanted = command + " \"%1\""
            if (cmd.GetValue("") :?> string) <> wanted then cmd.SetValue("", wanted)

    /// 尽力而为：失败只记日志，不影响启动。macOS 由 .app 的 Info.plist（CFBundleURLTypes）声明，
    /// 运行时无需注册；系统通过 IActivatableLifetime 的 OpenUri 激活把链接交进来。
    /// `WANXIANG_NO_URL_SCHEME=1` 关闭（开发 / 测试环境不改写用户的协议关联）。
    let registerCurrentUser (log: string -> unit) =
        if Environment.GetEnvironmentVariable "WANXIANG_NO_URL_SCHEME" <> "1" then
            match launchCommand () with
            | None -> ()
            | Some command ->
                try
                    if OperatingSystem.IsLinux() then registerLinux command
                    elif OperatingSystem.IsWindows() then registerWindows command
                with e -> log (sprintf "url scheme registration skipped: %s" e.Message)

/// 决策 251：单实例转发。主实例在配置目录下监听一个本机 Unix 套接字（Windows 10+ 同样支持），
/// 第二次启动（系统打开链接时就是这样）先连它：连上就把链接（或空行 = 只是唤起）交过去后退出，
/// 不再去抢数据锁；连不上才当主实例。
module SingleInstance =

    let socketPath (configDir: string) = Path.Combine(configDir, "ui.sock")

    /// 尝试交给已在运行的实例。成功返回 true（调用方应退出）。
    let tryForward (path: string) (url: string option) : bool =
        if not (File.Exists path) then false
        else
            try
                use s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                s.Connect(UnixDomainSocketEndPoint path)
                let line = (url |> Option.defaultValue "").Replace("\n", "").Replace("\r", "") + "\n"
                s.Send(Encoding.UTF8.GetBytes line) |> ignore
                let buf = Array.zeroCreate<byte> 3
                s.ReceiveTimeout <- 2000
                let n = try s.Receive buf with _ -> 0
                n > 0 && Encoding.ASCII.GetString(buf, 0, n).StartsWith "ok"
            with _ -> false

    /// 主实例监听。每条连接读一行：非空就当链接交给 onLink，空行只唤起窗口（onActivate）。
    let listen (path: string) (onLink: string -> unit) (onActivate: unit -> unit) : IDisposable =
        try
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            if File.Exists path then (try File.Delete path with _ -> ())
            let server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            server.Bind(UnixDomainSocketEndPoint path)
            try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
            server.Listen 4
            let cts = new CancellationTokenSource()
            let loop () =
                task {
                    while not cts.IsCancellationRequested do
                        try
                            let! client = server.AcceptAsync(cts.Token)
                            use client = client
                            use stream = new NetworkStream(client)
                            use reader = new StreamReader(stream, Encoding.UTF8)
                            let readTask = reader.ReadLineAsync()
                            let! finished = Task.WhenAny(readTask, Task.Delay 2000)
                            if obj.ReferenceEquals(finished, readTask) then
                                let line = readTask.Result
                                let ack = Encoding.ASCII.GetBytes "ok\n"
                                stream.Write(ack, 0, ack.Length)
                                match line with
                                | null -> ()
                                | l when String.IsNullOrWhiteSpace l -> onActivate ()
                                | l -> onLink (l.Trim())
                        with _ -> ()
                }
            Task.Run(fun () -> loop () :> Task) |> ignore
            { new IDisposable with
                member _.Dispose() =
                    cts.Cancel()
                    (try server.Dispose() with _ -> ())
                    (try File.Delete path with _ -> ()) }
        with _ ->
            { new IDisposable with member _.Dispose() = () }
