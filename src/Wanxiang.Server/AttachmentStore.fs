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
    /// 发起上传的连接 id（断线清理用）
    connectionId: int
    tempPath: string
    expectedSha256: string
    expectedBytes: int64
    mediaType: string
    fileName: string
    mutable receivedBytes: int64
    /// 已接收 chunk 数（index 严格递增校验，决策 71：顺序发送 attachment.chunk）
    mutable chunkCount: int
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
/// 元数据（声明 mediaType/fileName + 嗅探结果）随 blob 落盘 <hash>.meta，供下载回传（Q176/P2-7）。
type AttachmentStore(dataDir: string, maxBytes: int64, ?chunkSizeBytes: int) =

    let maxChunkBytes = chunkSizeBytes |> Option.defaultValue (256 * 1024)
    let attachmentsDir = Wanxiang.Store.DataPaths.attachmentsDir dataDir
    let tempDir = Path.Combine(attachmentsDir, ".tmp")

    do
        Directory.CreateDirectory attachmentsDir |> ignore
        Directory.CreateDirectory tempDir |> ignore

    /// 文件名清理（Q175：长度截断 + 不可打印字符清除；绝不用于存储路径）。
    let sanitizeFileName (name: string) : string =
        let cleaned =
            name
            |> Seq.filter (fun c -> c >= ' ')
            |> Seq.truncate 255
            |> Seq.map string
            |> String.concat ""
        cleaned.Trim()

    /// 轻量 MIME 嗅探（Q176：前 512 字节魔数；只在前 512 字节内判断）。
    let sniffMediaType (head: byte[]) : string option =
        let hasMagic (bytes: byte[]) = head.Length >= bytes.Length && Array.forall2 (=) bytes head[0 .. bytes.Length - 1]
        if hasMagic [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |] then Some "image/png"
        elif hasMagic [| 0xFFuy; 0xD8uy; 0xFFuy |] then Some "image/jpeg"
        elif hasMagic (Text.Encoding.ASCII.GetBytes "GIF8") then Some "image/gif"
        elif hasMagic (Text.Encoding.ASCII.GetBytes "%PDF") then Some "application/pdf"
        elif hasMagic [| 0x50uy; 0x4Buy; 0x03uy; 0x04uy |] then Some "application/zip"
        else
            // 文本启发式：全部可见/空白/UTF-8 多字节 → text/plain
            let mutable textLike = true
            let mutable i = 0
            while textLike && i < head.Length do
                let b = head[i]
                if b < 0x09uy || (b > 0x0Duy && b < 0x20uy) then textLike <- false
                i <- i + 1
            if textLike then Some "text/plain" else None

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

    let metaPath (sha256: string) = attachmentPath sha256 |> Option.map (fun p -> p + ".meta")

    let writeMeta (sha256: string) (declared: string) (sniffed: string option) (resolved: string) (fileName: string) (size: int64) =
        match metaPath sha256 with
        | None -> ()
        | Some path ->
            try
                let o = JsonObject()
                o["mediaType"] <- resolved
                o["declaredMediaType"] <- declared
                match sniffed with Some s -> o["sniffedMediaType"] <- s | None -> ()
                o["fileName"] <- fileName
                o["size"] <- size
                File.WriteAllText(path, o.ToJsonString())
            with _ -> ()

    let uploads = System.Collections.Concurrent.ConcurrentDictionary<Guid, AttachmentUpload>()

    /// 连接断线清理：取消该连接发起的全部未完成上传（决策 71：断线删除未完成上传，不支持续传）。
    /// connectionId 由 WsConnection 在 Begin 时登记。
    member this.AbortByConnection(connectionId: int) : unit =
        for kv in uploads do
            if kv.Value.connectionId = connectionId then
                this.Abort(kv.Key, "connection closed")

    member _.Begin(connectionId: int, attachmentId: Guid, totalBytes: int64, sha256: string, mediaType: string, fileName: string) : Result<unit, WanxiangError> =
        let cleanName = sanitizeFileName fileName
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
                    // Q118：附件文件最小用户权限
                    try File.SetUnixFileMode(tempPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
                    let sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                    let upload =
                        { attachmentId = attachmentId
                          connectionId = connectionId
                          tempPath = tempPath
                          expectedSha256 = sha256
                          expectedBytes = totalBytes
                          mediaType = mediaType
                          fileName = cleanName
                          receivedBytes = 0L
                          chunkCount = 0
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

    member _.AppendChunk(attachmentId: Guid, index: int, dataBase64: string) : Result<unit, WanxiangError> =
        match uploads.TryGetValue attachmentId with
        | false, _ -> Error(AttachmentIncomplete attachmentId)
        | true, s ->
            try
                // 决策 71：chunk 必须按 index 严格递增顺序到达；乱序/重复/跳号直接拒绝，
                // 否则重复 chunk 会写放大临时文件，乱序会等到 complete 哈希校验才被发现
                if index <> s.chunkCount then
                    Error(ValidationError(sprintf "attachment chunk index %d out of order; expected %d" index s.chunkCount))
                else
                    let bytes = Convert.FromBase64String dataBase64
                    if bytes.Length > maxChunkBytes then
                        Error(ValidationError(sprintf "attachment chunk exceeds %d bytes limit" maxChunkBytes))
                    elif s.receivedBytes + int64 bytes.Length > s.expectedBytes then
                        Error(ValidationError "attachment chunk exceeds declared size")
                    else
                        s.stream.Write(bytes, 0, bytes.Length)
                        s.sha.AppendData(bytes, 0, bytes.Length)
                        s.receivedBytes <- s.receivedBytes + int64 bytes.Length
                        s.chunkCount <- s.chunkCount + 1
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
                    // Q176：轻量嗅探（前 512 字节）；声明值为空/octet-stream 时采用嗅探结果
                    let sniffed =
                        try
                            use fs = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                            let headLen = min 512 (int fs.Length)
                            let head = Array.zeroCreate<byte> headLen
                            if fs.Read(head, 0, headLen) > 0 then sniffMediaType head else None
                        with _ -> None
                    let declared = s.mediaType
                    let resolved =
                        if String.IsNullOrWhiteSpace declared || declared = "application/octet-stream" then
                            sniffed |> Option.defaultValue (if String.IsNullOrWhiteSpace declared then "application/octet-stream" else declared)
                        else declared
                    writeMeta actualHash declared sniffed resolved s.fileName s.receivedBytes
                    uploads.TryRemove attachmentId |> ignore
                    Ok
                        { sha256 = actualHash
                          size = s.receivedBytes
                          mediaType = resolved
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

    /// 按 sha256 读取已落盘元数据（下载回传声明值，Q176/P2-7）。
    member _.Metadata(sha256: string) : (string * string * int64) option =
        match metaPath sha256 with
        | None -> None
        | Some path when File.Exists path ->
            try
                let o = JsonNode.Parse(File.ReadAllText path).AsObject()
                let getStr k =
                    let mutable n: JsonNode = null
                    if o.TryGetPropertyValue(k, &n) && n <> null && n.GetValueKind() = System.Text.Json.JsonValueKind.String then
                        n.GetValue<string>()
                    else ""
                let getInt k =
                    let mutable n: JsonNode = null
                    if o.TryGetPropertyValue(k, &n) && n <> null && n.GetValueKind() = System.Text.Json.JsonValueKind.Number then
                        match n with
                        | :? JsonValue as v ->
                            match v.TryGetValue<int64>() with true, i -> i | _ -> 0L
                        | _ -> 0L
                    else 0L
                Some(getStr "mediaType", getStr "fileName", getInt "size")
            with _ -> None
        | Some _ -> None

    member _.Dispose() =
        for kv in uploads do
            try kv.Value.stream.Dispose() with _ -> ()
