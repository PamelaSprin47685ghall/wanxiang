namespace Wanxiang.Server

open System
open System.Text.Json
open System.Text.Json.Nodes

/// stderr 结构化 JSON Lines 输出（决策 198/41）。
/// stderr 是截尾/poison 现场的最后证据，必须忠实记录；密钥按结构禁止持久化。
/// 决策 41：若异常对象或第三方错误消息意外携带密钥，写 stderr 前对已知凭据值做精确替换（不删减其他上下文）。
module Stderr =

    let private options = JsonSerializerOptions(JsonSerializerDefaults.General)
    /// 已知凭据值集合（apiKey/token 等），写 stderr 前精确替换为 ***。
    /// 由 ServerApp 在启动时注册（含 MCP stderr 转发路径，Q164）。
    let mutable private secretValues: string list = []
    /// 降序缓存（redact 热路径避免每次重排）；与 secretValues 同锁维护
    let mutable private sortedSecrets: string list = []
    /// 独立锁对象（不随 secretValues 赋值漂移）
    let private secretLock = obj()

    let private rebuildSorted () =
        sortedSecrets <- secretValues |> List.sortByDescending (fun x -> x.Length)

    /// 注册已知凭据值（幂等；供写 stderr 前脱敏）。
    let registerSecrets (secrets: string seq) =
        lock secretLock (fun () ->
            let existing = set secretValues
            secretValues <-
                secrets
                |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s) && s.Length >= 8)
                |> Seq.filter (fun s -> not (existing.Contains s))
                |> Seq.toList
                |> fun newOnes -> secretValues @ newOnes
            rebuildSorted ())

    /// 清除全部已注册凭据（测试/重载用）。
    let clearSecrets () =
        lock secretLock (fun () ->
            secretValues <- []
            rebuildSorted ())

    /// 对文本做精确脱敏（已知密钥值 → ***）；不做业务内容猜测性打码。
    /// 按密钥长度降序替换：短密钥若先替换，可能破坏长密钥的完整匹配（短是长子串时）。
    let redact (text: string) : string =
        lock secretLock (fun () ->
            let mutable s = text
            for secret in sortedSecrets do
                if s.Contains secret then s <- s.Replace(secret, "***")
            s)

    let write (event: string) (fields: (string * obj) list) =
        let o = JsonObject()
        o["level"] <- "error"
        o["event"] <- event
        o["utc"] <- DateTimeOffset.UtcNow.UtcDateTime.ToString("o")
        for (k, v) in fields do
            match v with
            | :? string as s -> o[k] <- redact s
            | :? int as i -> o[k] <- i
            | :? int64 as l -> o[k] <- l
            | :? uint64 as u -> o[k] <- u
            | :? float as f -> o[k] <- f
            | :? bool as b -> o[k] <- b
            | :? JsonNode as n -> o[k] <- n.DeepClone()
            | :? exn as e ->
                // Q198：错误对象、堆栈作为转义字段保存（忠实诊断）；已知凭据值脱敏
                o[k] <- redact (sprintf "%s: %s" (e.GetType().Name) e.Message)
                o[sprintf "%s.stack" k] <- redact (e.ToString())
            | null -> o[k] <- null
            | other -> o[k] <- redact (string other)
        eprintfn "%s" (o.ToJsonString options)

    let info (message: string) =
        let o = JsonObject()
        o["level"] <- "info"
        o["event"] <- "info"
        o["utc"] <- DateTimeOffset.UtcNow.UtcDateTime.ToString("o")
        o["message"] <- redact message
        eprintfn "%s" (o.ToJsonString options)

    /// 运行时截尾记录（决策 40：stderr 是唯一剩余证据，rawCommit 忠实输出；已知密钥值脱敏）。
    let truncated (commitJson: string) (err: Wanxiang.Core.WanxiangError) =
        write "ndjson-tail-truncated" [ "phase", "runtime-commit"; "exceptionMessage", Wanxiang.Core.WanxiangError.message err; "rawCommit", redact commitJson ]

    /// 启动截尾记录。
    let replayTruncated (file: string) (reason: string) =
        write "ndjson-replay-truncated" [ "phase", "startup-replay"; "file", file; "reason", reason ]
