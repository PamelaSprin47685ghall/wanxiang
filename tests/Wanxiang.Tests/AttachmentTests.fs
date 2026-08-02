module Wanxiang.Tests.AttachmentTests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Wanxiang.Core
open Wanxiang.Server
open Wanxiang.Store
open Wanxiang.Tests.Helpers

[<Fact>]
let ``test_18639`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L * 1024L)
        let payload = "hello wanxiang attachment 内容" |> Text.Encoding.UTF8.GetBytes
        let hash = Convert.ToHexString(SHA256.HashData payload).ToLowerInvariant()
        let aid = Guid.NewGuid()
        store.Begin(1, aid, int64 payload.Length, hash, "text/plain", "test.txt") |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        let chunk = Convert.ToBase64String payload
        store.AppendChunk(aid, 0, chunk) |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        match store.Complete(aid, hash) with
        | Ok ref ->
            Assert.Equal(hash, ref.sha256)
            Assert.Equal(int64 payload.Length, ref.size)
        | Error e -> failwith (WanxiangError.message e)
        // 内容寻址：相同内容复用同一文件
        let aid2 = Guid.NewGuid()
        store.Begin(1, aid2, int64 payload.Length, hash, "text/plain", "copy.txt") |> ignore
        store.AppendChunk(aid2, 0, chunk) |> ignore
        store.Complete(aid2, hash) |> function Ok _ -> () | Error e -> failwith (WanxiangError.message e)
        // 读取验证
        match store.OpenRead hash with
        | None -> failwith "attachment missing"
        | Some (stream, size) ->
            use stream = stream
            Assert.Equal(int64 payload.Length, size)
        store.Dispose()
    finally
        cleanup dir

[<Fact>]
let ``test_59388`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L * 1024L)
        let payload = "abc" |> Text.Encoding.UTF8.GetBytes
        let realHash = Convert.ToHexString(SHA256.HashData payload).ToLowerInvariant()
        let aid = Guid.NewGuid()
        store.Begin(1, aid, int64 payload.Length, realHash, "text/plain", "x.txt") |> ignore
        store.AppendChunk(aid, 0, Convert.ToBase64String payload) |> ignore
        match store.Complete(aid, "f".PadRight(64, '0')) with
        | Ok _ -> failwith "hash mismatch should be rejected"
        | Error (AttachmentHashMismatch _) -> ()
        | Error e -> failwith (WanxiangError.message e)
        store.Dispose()
    finally
        cleanup dir

[<Fact>]
let ``invalid attachment hash is rejected without throwing`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L)
        match store.Begin(1, Guid.NewGuid(), 1L, "../bad", "text/plain", "bad.txt") with
        | Error (ValidationError _) -> ()
        | result -> failwithf "expected validation error, got %A" result
        Assert.False(store.Exists "bad")
        Assert.True(store.OpenRead "bad" |> Option.isNone)
        store.Dispose()
    finally
        cleanup dir

[<Fact>]
let ``duplicate active attachment id is rejected`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L)
        let aid = Guid.NewGuid()
        let hash = "b".PadRight(64, 'b')
        Assert.True((store.Begin(1, aid, 1L, hash, "application/octet-stream", "a") |> Result.isOk))
        match store.Begin(1, aid, 1L, hash, "application/octet-stream", "b") with
        | Error (ValidationError message) -> Assert.Contains("already active", message)
        | result -> failwithf "expected duplicate id rejection, got %A" result
        store.Abort(aid, "test")
        store.Dispose()
    finally
        cleanup dir

    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L)
        match store.Begin(1, Guid.NewGuid(), -1L, "0".PadRight(64, '0'), "text/plain", "bad.txt") with
        | Error (ValidationError _) -> ()
        | result -> failwithf "expected validation error, got %A" result
        store.Dispose()
    finally
        cleanup dir

    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 10L)
        match store.Begin(1, Guid.NewGuid(), 100L, "0".PadRight(64, '0'), "text/plain", "big.txt") with
        | Error (AttachmentTooLarge _) -> ()
        | _ -> failwith "oversize should be rejected"
        store.Dispose()
    finally
        cleanup dir

[<Fact>]
let ``断线清理：AbortByConnection 删除该连接未完成上传的临时文件`` () =
    let dir = tempDir ()
    try
        DataPaths.ensureDataDirs dir
        let store = AttachmentStore(dir, 1024L * 1024L)
        let payload = "partial upload" |> Text.Encoding.UTF8.GetBytes
        let hash = Convert.ToHexString(SHA256.HashData payload).ToLowerInvariant()
        // 连接 7 发起一个未完成上传（只写了部分 chunk）
        let aid = Guid.NewGuid()
        store.Begin(7, aid, int64 payload.Length, hash, "text/plain", "p.txt") |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        store.AppendChunk(aid, 0, Convert.ToBase64String payload[0..3]) |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        let tempPath = Path.Combine(dir, "attachments", ".tmp", aid.ToString("N"))
        Assert.True(File.Exists tempPath)
        // 另一连接的上传不受影响
        let aid2 = Guid.NewGuid()
        store.Begin(9, aid2, int64 payload.Length, hash, "text/plain", "q.txt") |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        store.AppendChunk(aid2, 0, Convert.ToBase64String payload[0..3]) |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        let tempPath2 = Path.Combine(dir, "attachments", ".tmp", aid2.ToString("N"))
        Assert.True(File.Exists tempPath2)
        // 连接 7 断线清理
        store.AbortByConnection 7
        Assert.False(File.Exists tempPath)
        Assert.True(File.Exists tempPath2)
        // 连接 9 的上传未受影响：补齐剩余字节后仍可完成
        store.AppendChunk(aid2, 1, Convert.ToBase64String payload[4..]) |> function Ok () -> () | Error e -> failwith (WanxiangError.message e)
        store.Complete(aid2, hash) |> function Ok _ -> () | Error e -> failwith (WanxiangError.message e)
        store.Dispose()
    finally
        cleanup dir
