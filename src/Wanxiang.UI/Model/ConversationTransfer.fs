namespace Wanxiang.UI

open System
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Wanxiang.Core

/// 导出时附带的分叉 / 版本信息（决策 312）。全部来自对话列表快照的只读派生。
type ExportLineage = {
    /// None = 根对话
    parentId: Guid option
    parentTitle: string option
    parentDeleted: bool
    /// 被编辑的是原对话第几条消息（1 起）
    forkOrdinal: int option
    forkBaseMessageId: uint64 option
    /// 从本对话分出去的分叉（id, 标题, 分叉点）
    forks: (Guid * string * uint64 option) list
}

/// 解析通过的导入预览（决策 313）：只在用户确认后才发命令。
type ImportPreview = {
    title: string
    /// 待记录的消息载荷（已清洗）
    messages: JsonObject list
    originalConversationId: Guid option
    exportedAt: DateTimeOffset option
    /// 原文件里的分叉说明（导入后不恢复分叉关系，只告知）
    lineageNote: string option
    /// 被跳过的无效条目数
    skipped: int
    /// 引用了附件（文件本体不在导出里）
    hasAttachments: bool
}

module ConversationTransfer =

    [<Literal>]
    let formatName = "wanxiang.conversation"

    [<Literal>]
    let formatVersion = 1

    /// 导入文件大小上限：与导出 Markdown 上限同档以下，足够一份长对话。
    let maxImportBytes = 32L * 1024L * 1024L

    /// 隐私说明（导出对话框与导入预览共用一句，决策 312/313）。
    let privacyNote =
        "导出文件不含 API 密钥、访问令牌、配对信息和主机配置；但包含对话全文与工具记录，分享前请先检查内容。"

    /// 纵深防御：载荷里若出现这些键（不论大小写）一律去掉。正常的消息载荷不会有，
    /// 有就是不该离开主机的东西。
    let private secretKeys =
        set [ "apikey"; "api_key"; "api-key"; "x-api-key"; "authorization"; "accesstoken"; "access_token"
              "refreshtoken"; "refresh_token"; "secret"; "clientsecret"; "client_secret"; "password"; "bearer" ]

    let isSecretKey (key: string) = secretKeys.Contains(key.ToLowerInvariant())

    let rec scrub (node: JsonNode) : JsonNode =
        match node with
        | null -> null
        | :? JsonObject as o ->
            let copy = JsonObject()
            for kv in o do
                if not (isSecretKey kv.Key) then
                    copy[kv.Key] <- (if isNull kv.Value then null else scrub (kv.Value.DeepClone()))
            copy :> JsonNode
        | :? JsonArray as a ->
            let copy = JsonArray()
            for item in a do copy.Add(if isNull item then null else scrub (item.DeepClone()))
            copy :> JsonNode
        | other -> other.DeepClone()

    let private iso (at: DateTimeOffset) = at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)

    /// 从对话列表派生导出谱系。
    let lineageOf
        (summaries: ConversationSummary list)
        (lineage: Map<Guid, ForkLineage>)
        (conversationId: Guid)
        : ExportLineage =
        let own = lineage.TryFind conversationId
        let titleOf id = summaries |> List.tryFind (fun s -> s.id = id) |> Option.map _.title
        let forks =
            lineage
            |> Map.toList
            |> List.filter (fun (_, l) -> l.parentId = conversationId)
            |> List.choose (fun (id, l) -> titleOf id |> Option.map (fun t -> id, t, l.forkBaseMessageId))
        { parentId = own |> Option.map _.parentId
          parentTitle = own |> Option.bind (fun l -> titleOf l.parentId |> Option.orElse l.parentTitle)
          parentDeleted = own |> Option.exists (fun l -> l.parentDeleted || (titleOf l.parentId).IsNone)
          forkOrdinal = own |> Option.bind _.forkOrdinal
          forkBaseMessageId = own |> Option.bind _.forkBaseMessageId
          forks = forks }

    let rootLineage = { parentId = None; parentTitle = None; parentDeleted = false; forkOrdinal = None; forkBaseMessageId = None; forks = [] }

    /// 分叉 / 版本的一句话说明（Markdown 头与导入预览共用）。
    let lineageLines (l: ExportLineage) : string list =
        [ match l.parentId with
          | Some _ ->
              let title = l.parentTitle |> Option.defaultValue "原对话"
              if l.parentDeleted then yield "这是一个分叉，原对话已删除。"
              else
                  match l.forkOrdinal with
                  | Some n -> yield sprintf "这是「%s」第 %d 条消息的分叉（另一个版本）。" title n
                  | None -> yield sprintf "这是「%s」的分叉。" title
          | None -> ()
          if not (List.isEmpty l.forks) then
              let names = l.forks |> List.map (fun (_, t, _) -> "「" + t + "」") |> String.concat "、"
              yield sprintf "从这个对话分出了 %d 个分叉：%s。分叉需要分别导出。" l.forks.Length names ]

    /// Markdown 导出头部（插在标题之后）：来源、时间、条数、分叉信息、隐私说明。
    let markdownPreamble (l: ExportLineage) (messageCount: int) (exportedAt: DateTimeOffset) : string =
        let b = StringBuilder()
        b.AppendLine(sprintf "> 导出自万象 · %s · 共 %d 条消息" (exportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) messageCount) |> ignore
        for line in lineageLines l do b.AppendLine(">").AppendLine("> " + line) |> ignore
        b.AppendLine(">").AppendLine("> " + privacyNote).AppendLine() |> ignore
        b.ToString()

    /// 把 preamble 插到 `# 标题` 下面。
    let withPreamble (markdown: string) (preamble: string) =
        let marker = "\n\n"
        match markdown.IndexOf marker with
        | i when i >= 0 -> markdown.Substring(0, i + marker.Length) + preamble + markdown.Substring(i + marker.Length)
        | _ -> markdown + "\n\n" + preamble

    /// 生成可再导入的 JSON（决策 312）。消息按提交顺序原样（清洗后）落盘。
    let toJson
        (conversationId: Guid)
        (title: string)
        (atCommitId: CommitId)
        (exportedAt: DateTimeOffset)
        (l: ExportLineage)
        (raw: JsonObject list)
        : string =
        let root = JsonObject()
        root["format"] <- formatName
        root["formatVersion"] <- formatVersion
        root["exportedAt"] <- iso exportedAt
        root["privacy"] <- privacyNote
        let conv = JsonObject()
        conv["id"] <- conversationId.ToString("D")
        conv["title"] <- title
        conv["atCommitId"] <- string atCommitId
        conv["messageCount"] <- raw.Length
        root["conversation"] <- conv
        let lin = JsonObject()
        lin["kind"] <- (if l.parentId.IsSome then "fork" else "root")
        l.parentId |> Option.iter (fun id -> lin["parentId"] <- id.ToString("D"))
        l.parentTitle |> Option.iter (fun t -> lin["parentTitle"] <- t)
        if l.parentId.IsSome then lin["parentDeleted"] <- l.parentDeleted
        l.forkOrdinal |> Option.iter (fun n -> lin["forkOrdinal"] <- n)
        l.forkBaseMessageId |> Option.iter (fun id -> lin["forkBaseMessageId"] <- string id)
        let forks = JsonArray()
        for id, t, at in l.forks do
            let f = JsonObject()
            f["id"] <- id.ToString("D")
            f["title"] <- t
            at |> Option.iter (fun a -> f["forkBaseMessageId"] <- string a)
            forks.Add f
        lin["forks"] <- forks
        root["lineage"] <- lin
        let messages = JsonArray()
        for item in raw do
            let m = JsonObject()
            match item["commitId"] with
            | null -> ()
            | v -> m["commitId"] <- v.ToString()
            match item["committedAt"] with
            | null -> ()
            | v -> m["committedAt"] <- v.DeepClone()
            m["payload"] <- scrub item["payload"]
            messages.Add m
        root["messages"] <- messages
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

    let safeJsonFileName (title: string) (markdownName: string) =
        if markdownName.EndsWith ".md" then markdownName.Substring(0, markdownName.Length - 3) + ".json" else markdownName + ".json"

    let private validRoles = set [ "user"; "assistant"; "tool"; "system" ]

    let private str (o: JsonObject) (key: string) =
        match o[key] with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s -> Some s
            | _ -> None
        | _ -> None

    let rec private hasAttachment (node: JsonNode) =
        match node with
        | :? JsonObject as o ->
            (str o "type" = Some "attachment") || (o |> Seq.exists (fun kv -> not (isNull kv.Value) && hasAttachment kv.Value))
        | :? JsonArray as a -> a |> Seq.exists (fun n -> not (isNull n) && hasAttachment n)
        | _ -> false

    /// 解析导入文件（决策 313）。只认本应用导出的 JSON；失败时给出可读原因，绝不部分导入。
    let parseImport (byteCount: int64) (text: string) : Result<ImportPreview, string> =
        if byteCount > maxImportBytes then
            Error(sprintf "文件超过 %s，无法导入。" (AttachmentRef.formatSize maxImportBytes))
        else
        let root =
            try
                match JsonNode.Parse text with
                | :? JsonObject as o -> Some o
                | _ -> None
            with _ -> None
        match root with
        | None -> Error "这不是有效的 JSON 文件。请选择从万象「导出为 JSON」得到的文件；Markdown 导出不能再导入。"
        | Some root ->
        match str root "format" with
        | Some f when f = formatName ->
            let version =
                match root["formatVersion"] with
                | :? JsonValue as v -> (match v.TryGetValue<int>() with | true, n -> n | _ -> 0)
                | _ -> 0
            if version < 1 then Error "文件缺少格式版本，无法确认内容完整。"
            elif version > formatVersion then Error "这个文件来自更新版本的万象，请先升级再导入。"
            else
                let conv = match root["conversation"] with :? JsonObject as c -> c | _ -> JsonObject()
                let title =
                    match str conv "title" with
                    | Some t when not (String.IsNullOrWhiteSpace t) -> t.Trim()
                    | _ -> "导入的对话"
                match root["messages"] with
                | :? JsonArray as items ->
                    let mutable skipped = 0
                    let messages =
                        [ for item in items do
                            match item with
                            | :? JsonObject as m ->
                                match m["payload"] with
                                | :? JsonObject as p when (str p "role" |> Option.exists validRoles.Contains) ->
                                    yield (scrub p).AsObject()
                                | _ -> skipped <- skipped + 1
                            | _ -> skipped <- skipped + 1 ]
                    if List.isEmpty messages then Error "文件里没有可导入的消息。"
                    elif messages.Length > ImportLimits.maxMessages then
                        Error(sprintf "这份对话有 %d 条消息，超过单次导入上限 %d 条。" messages.Length ImportLimits.maxMessages)
                    else
                        let lineageNote =
                            match root["lineage"] with
                            | :? JsonObject as l ->
                                let forks = match l["forks"] with :? JsonArray as a -> a.Count | _ -> 0
                                let parts =
                                    [ if str l "kind" = Some "fork" then
                                          yield
                                              match str l "parentTitle" with
                                              | Some t -> sprintf "原本是「%s」的分叉" t
                                              | None -> "原本是一个分叉"
                                      if forks > 0 then yield sprintf "原本有 %d 个分叉" forks ]
                                if List.isEmpty parts then None
                                else Some(String.concat "，" parts + "。导入后是一个独立的新对话，分叉关系不会恢复。")
                            | _ -> None
                        Ok
                            { title = title
                              messages = messages
                              originalConversationId = str conv "id" |> Option.bind (fun s -> match Guid.TryParse s with | true, g -> Some g | _ -> None)
                              exportedAt = str root "exportedAt" |> Option.bind (fun s -> match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with | true, d -> Some d | _ -> None)
                              lineageNote = lineageNote
                              skipped = skipped
                              hasAttachments = messages |> List.exists (fun m -> hasAttachment m) }
                | _ -> Error "文件里没有消息列表，可能已损坏。"
        | _ -> Error "这不是万象导出的对话文件。请选择「导出为 JSON」得到的文件。"

    /// 导入后的标题：加后缀，与主机上可能仍在的原对话区分开。
    let importedTitle (title: string) = title + "（导入）"
