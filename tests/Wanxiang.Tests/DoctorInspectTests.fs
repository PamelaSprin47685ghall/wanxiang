module Wanxiang.Tests.DoctorInspectTests

// 宿主运行时的命令行 doctor / 支持包（不持锁 Inspect 路径），以及「结果未知」。
// 全部用真实存储持锁、真实文件字节；结果未知用真实写失败（日志符号链接到 /dev/full）。

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Wanxiang.Core
open Wanxiang.Store
open Wanxiang.Server
open Wanxiang.Tests.Helpers

let private create (store: EventStore) =
    store.SubmitEvents [ ConversationCreated { conversationId = Guid.NewGuid(); title = "t"; config = SessionConfig.empty } ]

let private logCheck (d: Doctor.Diagnosis) =
    d.checks |> List.find (fun c -> c.name = "log-replay")

let private noConfig dir = Path.Combine(dir, "missing.toml")

let private bundleDiag (dir: string) : JsonNode =
    let out = Path.Combine(dir, "bundle-out")
    match SupportBundle.create dir (noConfig dir) out with
    | Error e -> failwithf "bundle failed: %s" e
    | Ok b -> JsonNode.Parse(File.ReadAllText(Path.Combine(b, "diagnosis.json")))

let private commit (i: uint64) =
    Events.Commit.create i (DateTimeOffset(2026, 10, 1, 0, 0, int i, TimeSpan.Zero))
        [ ConversationCreated { conversationId = Guid.NewGuid(); title = sprintf "c%d" i; config = SessionConfig.empty } ]

[<Fact>]
let ``host running: CLI doctor checks without the lock and reports healthy`` () =
    let dir = tempDir ()
    try
        use host = new EventStore(dir)
        for _ in 1..3 do
            match create host with
            | Committed _ -> ()
            | r -> failwithf "submit %A" r
        let c = logCheck (Doctor.run (noConfig dir, dir))
        Assert.True(c.ok, c.detail)
        Assert.Contains("healthy through commit 3", c.detail)
        Assert.Contains("host running", c.detail)
        let diag = bundleDiag dir
        Assert.Equal("healthy", diag["logStatus"].GetValue<string>())
        Assert.True(diag["hostRunning"].GetValue<bool>())
        // 宿主仍可写
        match create host with
        | Committed c -> Assert.Equal(4UL, c.id)
        | r -> failwithf "submit after inspect %A" r
    finally
        cleanup dir

[<Fact>]
let ``host running: half-written last record is reported as possibly in flight, not damage`` () =
    let dir = tempDir ()
    try
        use host = new EventStore(dir)
        create host |> ignore
        // 宿主追加到一半的样子：一条记录的前半截
        let frame = Assembly.frame (commit 2UL)
        use fs = new FileStream(DataPaths.logFile dir, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
        fs.Write(frame, 0, frame.Length / 2)
        fs.Flush()
        let c = logCheck (Doctor.run (noConfig dir, dir))
        Assert.True(c.ok, c.detail)
        Assert.Contains("may still be being written", c.detail)
        let diag = bundleDiag dir
        Assert.Equal("healthy", diag["logStatus"].GetValue<string>())
        Assert.True(diag["tailMayBeInFlight"].GetValue<bool>())
    finally
        cleanup dir

[<Fact>]
let ``host running: damage before the tail is real damage`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let bad = JsonSeq.Frame.encode "{not json"
        File.WriteAllBytes(DataPaths.logFile dir, Array.concat [ Assembly.frame (commit 1UL); bad; Assembly.frame (commit 2UL) ])
        use host = new EventStore(dir)
        Assert.True(host.IsReadOnly)
        let c = logCheck (Doctor.run (noConfig dir, dir))
        Assert.False(c.ok, c.detail)
        Assert.Contains("Stop the host", c.detail)
        Assert.Contains("through commit 1", c.detail)
        let diag = bundleDiag dir
        Assert.Equal("repair-required", diag["logStatus"].GetValue<string>())
        Assert.Equal(int64 (Assembly.frame (commit 1UL)).Length, diag["damageOffset"].GetValue<int64>())
    finally
        cleanup dir

[<Fact>]
let ``outcome unknown from a real write failure names the commit`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        // 真实故障：日志是指向 /dev/full 的符号链接，写入 ENOSPC 且截不回
        File.CreateSymbolicLink(DataPaths.logFile dir, "/dev/full") |> ignore
        use host = new EventStore(dir)
        match create host with
        | CommitFailed(Poisoned m) -> Assert.Contains("outcome of commit 1 unknown", m)
        | r -> failwithf "expected unknown outcome, got %A" r
        Assert.True(host.IsReadOnly)
        match Inspect.ofStore host with
        | OutcomeUnknown(_, id, _) -> Assert.Equal(1UL, id)
        | r -> failwithf "expected OutcomeUnknown, got %A" r
        // 主机内的 doctor（检查主机）给出带 commit id 的明确提示
        let c = logCheck (Doctor.runLive (noConfig dir, dir, fun () -> Inspect.ofStore host))
        Assert.False(c.ok)
        Assert.Contains("commit 1 is unknown", c.detail)
        Assert.Contains("Restart the host", c.detail)
        // 之后的提交被拒，不重试
        match create host with
        | CommitFailed(Poisoned m) -> Assert.Contains("read-only", m)
        | r -> failwithf "expected read-only, got %A" r
    finally
        cleanup dir
