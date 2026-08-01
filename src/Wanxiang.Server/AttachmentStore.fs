namespace Wanxiang.Server

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Wanxiang.Core

/// 上传会话（运行时状态）。
type AttachmentUpload = {
    attachmentId: Guid
    tempPath: string
    expectedSha256: string
    expectedBytes: int64
    mediaType: string
    fileName: string
    mutable receivedBytes: int64
    mutable stream: FileStream
    mutable sha: IncrementalHash
}

/// 已提交附件引用。
type AttachmentCommittedRef = {
    sha256: string
    size: int64
    mediaType: string
    fileName: string
}

/// 附件内容寻址存储（决策 71/72）：
/// - SHA-256 作为永久文件标识，路径按哈希分片（attachments/ab/cd/<hash>）；
/// - 上传完成前只存在于临时文件，complete 后校验长度与哈希；
/// - 断线删除未完成上传；首版不做 GC（未来压缩时处理）。
type AttachmentStore(dataDir: string, maxBytes: int64) =

    let attachmentsDir = Wanxiang.Store.DataPaths.attachmentsDir dataDir
    let tempDir = Path.Combine(attachmentsDir, ".tmp")

    do
        Directory.CreateDirectory attachmentsDir |> ignore
        Directory.CreateDirectory tempDir |> ignore

    let uploads = System.Collections.Concurrent.ConcurrentDictionary<Guid, AttachmentUpload>()

    let isSha256 (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = 64
        && value |> Seq.forall (fun c -> Char.IsAsciiHexDigit c)

    let attachmentPath (sha256: string) =
        if not (isSha256 sha256) then None
        else
            let normalized = sha256.ToLowerInvariant()
            let dir = Path.Combine(attachmentsDir, normalized.Substring(0, 2), normalized.Substring(2, 2))
            Some(Path.Combine(dir, normalized))


    member _.Begin(attachmentId: Guid, totalBytes: int64, sha256: string, mediaType: string, fileName: string) : Result<unit, WanxiangError> =
        if totalBytes < 0L then
            Error(ValidationError "attachment size must not be negative")
        elif not (isSha256 sha256) then
            Error(ValidationError "attachment sha256 must be 64 hexadecimal characters")
        elif totalBytes > maxBytes then
            Error(AttachmentTooLarge maxBytes)
        else
            match uploads.ContainsKey attachmentId with
            | true -> Error(ValidationError "attachment id is already active")
            | false ->
                let tempPath = Path.Combine(tempDir, attachmentId.ToString("N"))
                try
                    let stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)
                    let sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                    let upload =
                        { attachmentId = attachmentId
                          tempPath = tempPath
                          expectedSha256 = sha256
                          expectedBytes = totalBytes
                          mediaType = mediaType
                          fileName = fileName
                          receivedBytes = 0L
                          stream = stream
                          sha = sha }
                    match uploads.TryAdd(attachmentId, upload) with
                    | true -> Ok()
                    | false ->
                        stream.Dispose()
                        File.Delete tempPath
                        Error(ValidationError "attachment id is already active")
                with e ->
                    Error(Poisoned(sprintf "attachment begin failed: %s" e.Message))

    member _.AppendChunk(attachmentId: Guid, dataBase64: string) : Result<unit, WanxiangError> =
        match uploads.TryGetValue attachmentId with
        | false, _ -> Error(AttachmentIncomplete attachmentId)
        | true, s ->
            try
                let bytes = Convert.FromBase64String dataBase64
                if s.receivedBytes + int64 bytes.Length > s.expectedBytes then
                    Error(ValidationError "attachment chunk exceeds declared size")
                else
                    s.stream.Write(bytes, 0, bytes.Length)
                    s.sha.AppendData(bytes, 0, bytes.Length)
                    s.receivedBytes <- s.receivedBytes + int64 bytes.Length
                    Ok()
            with e ->
                Error(ValidationError(sprintf "attachment chunk decode failed: %s" e.Message))

    member _.Complete(attachmentId: Guid, declaredSha256: string) : Result<AttachmentCommittedRef, WanxiangError> =
        match uploads.TryGetValue attachmentId with
        | false, _ -> Error(AttachmentIncomplete attachmentId)
        | true, s ->
            try
                s.stream.Flush()
                s.stream.Dispose()
                let actualHash = Convert.ToHexString(s.sha.GetHashAndReset()).ToLowerInvariant()
                if s.receivedBytes <> s.expectedBytes then
                    File.Delete s.tempPath
                    uploads.TryRemove attachmentId |> ignore
                    Error(ValidationError(sprintf "attachment size mismatch: expected %d got %d" s.expectedBytes s.receivedBytes))
                elif not (String.Equals(actualHash, s.expectedSha256, StringComparison.OrdinalIgnoreCase))
                     || not (String.Equals(declaredSha256, s.expectedSha256, StringComparison.OrdinalIgnoreCase)) then
                    File.Delete s.tempPath
                    uploads.TryRemove attachmentId |> ignore
                    Error(AttachmentHashMismatch(s.expectedSha256, actualHash))
                else
                    // 内容寻址落位；相同内容只保存一份
                    let dir = Path.Combine(attachmentsDir, actualHash.Substring(0, 2), actualHash.Substring(2, 2))
                    Directory.CreateDirectory dir |> ignore
                    let finalPath = Path.Combine(dir, actualHash)
                    if not (File.Exists finalPath) then
                        File.Move(s.tempPath, finalPath)
                    else
                        File.Delete s.tempPath
                    uploads.TryRemove attachmentId |> ignore
                    Ok
                        { sha256 = actualHash
                          size = s.receivedBytes
                          mediaType = s.mediaType
                          fileName = s.fileName }
            with e ->
                Error(Poisoned(sprintf "attachment complete failed: %s" e.Message))

    member _.Abort(attachmentId: Guid, reason: string) : unit =
        match uploads.TryGetValue attachmentId with
        | false, _ -> ()
        | true, s ->
            try
                s.stream.Dispose()
                File.Delete s.tempPath
            with _ -> ()
            uploads.TryRemove attachmentId |> ignore

    /// 下载：返回流读取器（不存在返回 None）。
    member _.OpenRead(sha256: string) : (Stream * int64) option =
        match attachmentPath sha256 with
        | None -> None
        | Some path when File.Exists path ->
            let fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            Some(fs, fs.Length)
        | Some _ -> None

    member _.Exists(sha256: string) : bool =
        attachmentPath sha256 |> Option.exists File.Exists

    /// 引用元数据。
    member _.MakeRef(sha256: string, size: int64, mediaType: string, fileName: string) : JsonNode =
        let o = JsonObject()
        o["sha256"] <- sha256
        o["size"] <- size
        o["mediaType"] <- mediaType
        o["fileName"] <- fileName
        o

    member _.Dispose() =
        for kv in uploads do
            try kv.Value.stream.Dispose() with _ -> ()
