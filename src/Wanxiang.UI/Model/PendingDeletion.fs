namespace Wanxiang.UI

open System

/// 删除即撤销（§214）：删除不再先弹「无法撤销」确认框，而是先从列表里隐去，
/// 给出带「撤销」的提示条；宽限期过后才真正向主机提交删除命令。
/// 宽限期内关掉应用 = 什么都没删——宁可少删，不可误删。
/// 本模块是纯状态：计时与命令发送由 AppShell 持有，便于确定性测试。
type PendingDeletionBatch = {
    token: Guid
    ids: Guid list
    /// 提示条里给用户看的说法：「已删除「标题」」/「已删除 3 个对话」
    label: string
}

type PendingDeletions = { batches: PendingDeletionBatch list }

module PendingDeletion =

    /// 宽限期：比默认提示条停留略长，读完再决定。
    let grace = TimeSpan.FromSeconds 8.0

    let empty = { batches = [] }

    let label (titles: string list) : string =
        match titles with
        | [ title ] -> sprintf "已删除「%s」" title
        | many -> sprintf "已删除 %d 个对话" (List.length many)

    /// 登记一批待删；已在别的批次里的 id 不重复登记。返回新状态与该批（空批返回 None）。
    let schedule (titles: (Guid * string) list) (state: PendingDeletions) : PendingDeletions * PendingDeletionBatch option =
        let already = state.batches |> List.collect _.ids |> Set.ofList
        let fresh = titles |> List.filter (fun (id, _) -> not (already.Contains id)) |> List.distinctBy fst
        if List.isEmpty fresh then state, None
        else
            let batch = { token = Guid.NewGuid(); ids = List.map fst fresh; label = label (List.map snd fresh) }
            { batches = state.batches @ [ batch ] }, Some batch

    /// 撤销：把整批移出待删，返回被恢复的 id（批次已提交或不存在时为空）。
    let undo (token: Guid) (state: PendingDeletions) : PendingDeletions * Guid list =
        match state.batches |> List.tryFind (fun b -> b.token = token) with
        | Some b -> { batches = state.batches |> List.filter (fun x -> x.token <> token) }, b.ids
        | None -> state, []

    /// 宽限期到：取出该批交给调用方提交。已撤销的批次返回空，不会误删。
    let commit (token: Guid) (state: PendingDeletions) : PendingDeletions * Guid list = undo token state

    let isPending (id: Guid) (state: PendingDeletions) : bool =
        state.batches |> List.exists (fun b -> List.contains id b.ids)

    /// 列表视图：隐去宽限期内的行。
    let visible (summaries: ConversationSummary list) (state: PendingDeletions) : ConversationSummary list =
        if List.isEmpty state.batches then summaries
        else summaries |> List.filter (fun s -> not (isPending s.id state))
