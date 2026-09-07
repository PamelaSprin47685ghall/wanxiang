namespace Wanxiang.UI

open System
open System.Text
open System.Text.Json.Nodes
open Wanxiang.Core
open Wanxiang.Protocol

type ConversationExportStatus =
    | ExportReading of received: int * total: int option
    | ExportReady of messageCount: int
    | ExportFailed of message: string
    | ExportCancelled

type ConversationExportDocument = {
    title: string
    atCommitId: CommitId
    messages: MessageView list
}

/// 单次导出的内存状态。所有页验证完整后才允许取出文档，失败／取消立即释放半份数据。
type ConversationExportController(conversationId: Guid, ?maxBytes: int64) =
    let byteLimit = defaultArg maxBytes ConversationExportLimits.maxTranscriptBytes
    let mutable query: ConversationExportQuery option = None
    let mutable status = ExportCancelled
    let mutable pages: MessageView list list = []
    let mutable received = 0
    let mutable total: int option = None
    let mutable bytes = 0L
    let mutable title = ""
    let mutable atCommitId = 0UL
    let mutable document: ConversationExportDocument option = None
    let mutable lastActivity = DateTimeOffset.UtcNow

    let clear () =
        query <- None
        pages <- []
        document <- None

    let fail message =
        clear ()
        status <- ExportFailed message

    member _.Status = status
    member _.Document = document
    member _.CurrentQuery = query

    member _.Start() =
        clear ()
        received <- 0
        total <- None
        bytes <- 0L
        title <- ""
        atCommitId <- 0UL
        lastActivity <- DateTimeOffset.UtcNow
        status <- ExportReading(0, None)
        let next = { exportId = Guid.CreateVersion7(); conversationId = conversationId; atCommitId = None; beforeCommitId = 0UL }
        query <- Some next
        next

    member _.Cancel() =
        clear ()
        status <- ExportCancelled

    member _.Fail(exportId: Guid, message: string) =
        if query |> Option.exists (fun current -> current.exportId = exportId) then fail message

    member _.Disconnect() =
        match status with
        | ExportReading _ -> fail "连接已断开，导出未完成。重新连接后可重新导出；没有保存半份文件。"
        | _ -> ()

    member _.Expire(now: DateTimeOffset, timeout: TimeSpan) =
        match status with
        | ExportReading _ when now - lastActivity >= timeout ->
            fail "读取历史超时。请检查连接并重试；旧版服务端需先更新才能完整导出。"
            true
        | _ -> false

    member _.Accept(page: ConversationExportPageData) : ConversationExportQuery option =
        // 取消后、另一份导出或已经处理过的页一律不再接管状态。
        match query with
        | None -> None
        | Some expected when expected.exportId <> page.exportId || expected.beforeCommitId <> page.beforeCommitId -> None
        | Some expected ->
            try
                if page.conversationId <> conversationId then failwith "服务端返回了其他会话的内容。"
                if expected.atCommitId |> Option.exists ((<>) page.atCommitId) then failwith "导出水位发生变化。"
                if total |> Option.exists ((<>) page.totalMessages) then failwith "导出消息数量发生变化。"
                if isNull page.items || page.items.Count > ConversationExportLimits.pageMessages then failwith "导出分页大小无效。"
                let ids =
                    page.items |> Seq.map (fun item ->
                        let node = item.AsObject()
                        if isNull node["payload"] || not (node["payload"] :? JsonObject) then failwith "导出消息载荷缺失。"
                        node["commitId"].GetValue<uint64>()) |> List.ofSeq
                if ids |> List.exists (fun id -> id = 0UL || id > page.atCommitId || (expected.beforeCommitId <> 0UL && id >= expected.beforeCommitId)) then
                    failwith "导出消息超出分页边界。"
                if ids |> List.pairwise |> List.exists (fun (a, b) -> a >= b) then failwith "导出消息重复或顺序错误。"
                let count = received + ids.Length
                if page.totalMessages < 0 || count > page.totalMessages then failwith "导出消息数量不符。"
                if page.hasMore && (List.isEmpty ids || count >= page.totalMessages) then failwith "导出分页没有继续前进。"
                if not page.hasMore && count <> page.totalMessages then failwith "导出内容不完整。"
                bytes <- bytes + int64 (Encoding.UTF8.GetByteCount(page.items.ToJsonString()))
                if bytes > byteLimit then failwith "会话超过 64 MiB 导出安全上限；已停止，没有截断内容。"
                let parsed =
                    page.items |> Seq.map (fun item ->
                        let message = MessageView.ofSnapshotItem item
                        if MessageView.hasVisibleBody message || not (List.isEmpty message.toolResults) then message
                        else
                            { message with text = "暂不能显示这条消息，保留原始 JSON：\n\n    " + item["payload"].ToJsonString().Replace("\n", "\n    ") })
                    |> List.ofSeq
                if total.IsNone then
                    title <- page.title
                    atCommitId <- page.atCommitId
                    total <- Some page.totalMessages
                received <- count
                pages <- parsed :: pages
                lastActivity <- DateTimeOffset.UtcNow
                if page.hasMore then
                    let next = { expected with atCommitId = Some atCommitId; beforeCommitId = ids.Head }
                    query <- Some next
                    status <- ExportReading(received, total)
                    Some next
                else
                    document <- Some { title = title; atCommitId = atCommitId; messages = pages |> List.collect id }
                    pages <- []
                    query <- None
                    status <- ExportReady received
                    None
            with ex ->
                fail ("导出已停止：" + ex.Message + " 没有保存不完整文件。")
                None
