namespace Wanxiang.UI
open System
open System.IO
open System.Text
open System.Threading.Tasks
open Wanxiang.Interop

/// 草稿落点。桌面是文件，PWA 是 IndexedDB；测试用内存实现模拟「页面被回收后重开」。
type IDraftBackend =
    abstract Load: unit -> Task<string option>
    abstract Save: string -> Task
    abstract Clear: unit -> Task

/// 桌面：原子写（临时文件 + rename），文件仅当前用户可读写（Q118 同口径）。
type FileDraftBackend(path: string) =
    member _.Path = path
    interface IDraftBackend with
        member _.Load() =
            Task.FromResult(
                try
                    if File.Exists path then Some(File.ReadAllText path) else None
                with _ -> None)
        member _.Save(text) =
            try
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                let tmp = path + ".tmp"
                File.WriteAllText(tmp, text, UTF8Encoding false)
                try File.SetUnixFileMode(tmp, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
                File.Move(tmp, path, true)
            with _ -> ()
            Task.CompletedTask
        member _.Clear() =
            try if File.Exists path then File.Delete path with _ -> ()
            Task.CompletedTask

    static member DefaultPath() =
        let home =
            match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
            | s when not (String.IsNullOrWhiteSpace s) -> s
            | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
        Path.Combine(home, "drafts.json")

/// 进程内存：测试替身，也是「宿主没有任何持久存储」时的退路（此时草稿仍按进程内存工作，与旧行为一致）。
type MemoryDraftBackend() =
    let mutable stored: string option = None
    member _.Stored = stored
    interface IDraftBackend with
        member _.Load() = Task.FromResult stored
        member _.Save(text) =
            stored <- Some text
            Task.CompletedTask
        member _.Clear() =
            stored <- None
            Task.CompletedTask

/// PWA：IndexedDB（经 BrowserDrafts；wwwroot/main.js 的 draftGet/draftPut/draftDelete）。
type BrowserDraftBackend() =
    interface IDraftBackend with
        member _.Load() =
            task {
                try
                    let! text = BrowserDrafts.Get()
                    return if String.IsNullOrWhiteSpace text then None else Some text
                with _ -> return None
            }
        member _.Save(text) =
            task { try do! BrowserDrafts.Put text with _ -> () } :> Task
        member _.Clear() =
            task { try do! BrowserDrafts.Delete() with _ -> () } :> Task

/// 草稿落点外壳：把「是否启用」的唯一判断收在这里，关闭时不读。
/// 去重闸门与会话状态在 Wanxiang2.DraftSession，这里只执行它给出的写 / 删。
type LocalDraftStore(backend: IDraftBackend, isEnabled: unit -> bool) =

    /// 平台默认落点：桌面文件 / PWA IndexedDB。开关由调用方（外壳持有的偏好）决定。
    static member Default(isEnabled: unit -> bool) : LocalDraftStore =
        let backend: IDraftBackend =
            if OperatingSystem.IsBrowser() then BrowserDraftBackend() :> IDraftBackend
            else FileDraftBackend(FileDraftBackend.DefaultPath()) :> IDraftBackend
        LocalDraftStore(backend, isEnabled)

    member _.Enabled = isEnabled ()

    /// 读已存文本；关闭保存时不读。
    member _.Load() : Task<string option> =
        if not (isEnabled ()) then Task.FromResult None else backend.Load()

    /// 按 DraftSession.Flush 给出的结果写或删。
    member _.Apply(write: DraftWrite) : Task =
        match write with
        | DraftWrite.Write text -> backend.Save text
        | DraftWrite.Delete -> backend.Clear()
        | DraftWrite.Unchanged -> Task.CompletedTask

    /// 清除本地草稿（设置里的显式动作，以及关闭保存时）：删除落盘内容。
    member _.Clear() : Task = backend.Clear()
