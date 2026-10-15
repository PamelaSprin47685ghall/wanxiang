namespace Wanxiang.UI

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text

/// 宿主的位置边界。库只接收显式路径，不发现或同步 Git 中的运行数据。
module LocalState =

    let private rejectGit (path: string) =
        if path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
           |> Array.exists (fun part -> part.Equals(".git", StringComparison.OrdinalIgnoreCase)) then
            invalidArg "path" "Runtime data cannot be read or written inside .git"
        path

    /// 在读取或创建前检查；已存在的符号链接也不能指回 Git 元数据。
    let requireOutsideGit (path: string) =
        if String.IsNullOrWhiteSpace path then invalidArg "path" "A state path is required"
        let absolute = path |> rejectGit |> Path.GetFullPath
        let prefix = Path.GetPathRoot absolute
        let mutable current = prefix
        for part in absolute.Substring(prefix.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries) do
            let next = Path.Combine(current, part)
            let entry: FileSystemInfo =
                if Directory.Exists next then DirectoryInfo(next) :> FileSystemInfo
                else FileInfo(next) :> FileSystemInfo
            if isNull entry.LinkTarget then current <- next
            else
                let target = entry.ResolveLinkTarget true
                if isNull target then invalidArg "path" "Unresolved state path link"
                current <- rejectGit target.FullName
        rejectGit current

    let private hasGitMarker (workspace: string) =
        let mutable directory = DirectoryInfo workspace
        let mutable found = false
        while not found && not (isNull directory) do
            let marker = Path.Combine(directory.FullName, ".git")
            found <- File.Exists marker || Directory.Exists marker
            directory <- directory.Parent
        found

    let private primaryWorktree workspace =
        let withoutGit () =
            if hasGitMarker workspace then invalidOp "Cannot resolve the working tree for runtime state"
            workspace
        try
            let start = ProcessStartInfo("git")
            start.UseShellExecute <- false
            start.CreateNoWindow <- true
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            for arg in [ "-C"; workspace; "worktree"; "list"; "--porcelain"; "-z" ] do
                start.ArgumentList.Add arg
            use command = new Process(StartInfo = start)
            if not (command.Start()) then invalidOp "Cannot start Git topology query"
            let output = command.StandardOutput.ReadToEndAsync()
            let errors = command.StandardError.ReadToEndAsync()
            if not (command.WaitForExit 10000) then
                command.Kill()
                command.WaitForExit()
                invalidOp "Git topology query did not complete"
            let text = output.GetAwaiter().GetResult()
            errors.GetAwaiter().GetResult() |> ignore
            if command.ExitCode <> 0 then withoutGit ()
            else
                let first = text.Split '\u0000' |> Array.takeWhile (fun field -> field <> "")
                if Array.contains "bare" first then invalidArg "workspace" "Runtime state requires a working tree"
                match Array.tryHead first with
                | Some field when field.StartsWith("worktree ", StringComparison.Ordinal) ->
                    field.Substring("worktree ".Length) |> requireOutsideGit
                | _ -> invalidOp "Git did not identify the primary working tree"
        with :? Win32Exception -> withoutGit ()

    let rootFor workspace =
        let primary = workspace |> requireOutsideGit |> primaryWorktree
        let target = Path.Combine(primary, ".Herdchestra")
        if not (isNull (DirectoryInfo(target).LinkTarget)) || File.Exists target then
            invalidArg "workspace" ".Herdchestra must be a real directory, not a link or file"
        target

    /// 只写自身的忽略规则，不改 .git/info/exclude、config 或 hooks。
    let initialize workspace =
        let root = rootFor workspace
        Directory.CreateDirectory root |> ignore
        let ignorePath = Path.Combine(root, ".gitignore")
        if not (isNull (FileInfo(ignorePath).LinkTarget)) || Directory.Exists ignorePath then
            invalidOp ".Herdchestra/.gitignore must be a regular file"
        try
            use stream = new FileStream(ignorePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            let bytes = Encoding.UTF8.GetBytes "*\n"
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush true
        with :? IOException when File.Exists ignorePath ->
            if not (isNull (FileInfo(ignorePath).LinkTarget)) then
                invalidOp ".Herdchestra/.gitignore must not be a link"
            if (File.ReadAllText ignorePath).Trim() <> "*" then
                invalidOp ".Herdchestra/.gitignore must exclude all runtime data"
        root

    let private defaultRoot = lazy (rootFor (Directory.GetCurrentDirectory()))

    let configHome () =
        if OperatingSystem.IsBrowser() then
            // 只保留原有浏览器存储键的路径文本；不执行 Git 或磁盘操作。
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
        else
            match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
            | home when not (String.IsNullOrWhiteSpace home) -> requireOutsideGit home
            | _ -> defaultRoot.Value
