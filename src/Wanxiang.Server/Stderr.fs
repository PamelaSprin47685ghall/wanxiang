namespace Wanxiang.Server

open System
open System.Text.Json
open System.Text.Json.Nodes

/// stderr 结构化 JSON Lines 输出（决策 198/41）。
/// stderr 是截尾/poison 现场的最后证据，必须忠实记录；密钥按结构禁止持久化。
module Stderr =

    let private options = JsonSerializerOptions(JsonSerializerDefaults.General)

    let write (event: string) (fields: (string * obj) list) =
        let o = JsonObject()
        o["level"] <- "error"
        o["event"] <- event
        o["utc"] <- DateTimeOffset.UtcNow.UtcDateTime.ToString("o")
        for (k, v) in fields do
            match v with
            | :? string as s -> o[k] <- s
            | :? int as i -> o[k] <- i
            | :? int64 as l -> o[k] <- l
            | :? uint64 as u -> o[k] <- u
            | :? float as f -> o[k] <- f
            | :? bool as b -> o[k] <- b
            | :? JsonNode as n -> o[k] <- n.DeepClone()
            | :? exn as e -> o[k] <- sprintf "%s: %s" (e.GetType().Name) e.Message
            | null -> o[k] <- null
            | other -> o[k] <- string other
        eprintfn "%s" (o.ToJsonString options)

    let info (message: string) =
        let o = JsonObject()
        o["level"] <- "info"
        o["event"] <- "info"
        o["utc"] <- DateTimeOffset.UtcNow.UtcDateTime.ToString("o")
        o["message"] <- message
        eprintfn "%s" (o.ToJsonString options)

    /// 运行时截尾记录（决策 40：stderr 是唯一剩余证据，rawCommit 忠实输出）。
    let truncated (commitJson: string) (err: Wanxiang.Core.WanxiangError) =
        write "ndjson-tail-truncated" [ "phase", "runtime-commit"; "exceptionMessage", Wanxiang.Core.WanxiangError.message err; "rawCommit", commitJson ]

    /// 启动截尾记录。
    let replayTruncated (file: string) (reason: string) =
        write "ndjson-replay-truncated" [ "phase", "startup-replay"; "file", file; "reason", reason ]
