namespace Wanxiang.Core.Ledger

open System
open System.Text

/// 日志恢复结论。`warnings` 是给人看的诊断，**不**影响 `records` 的可用性。
type RecoveryOutcome =
    { records: LedgerRecord list
      /// 尾部被截断而丢弃的字节数（0 表示无截断）。
      truncatedBytes: int
      /// 中间损坏而跳过的段数。
      skippedSegments: int
      warnings: string list }

[<RequireQualifiedAccess>]
module Recovery =

    /// 从原始字节恢复账本。
    ///
    /// 依据 RFC 7464：§2.1 解析失败继续、§2.3 末段截断可接受、§2.4 顶层自定界。
    /// 另按 RFC 5848（Reboot Session ID）思路，用 `bootId` 区分进程世代，
    /// 检测重启后的序号回退。
    let recover (data: byte[]) : RecoveryOutcome =
        let segs = Frame.split (ReadOnlySpan<byte> data)
        let warnings = ResizeArray<string>()
        let records = ResizeArray<LedgerRecord>()
        let mutable skipped = 0
        let mutable truncated = 0
        let mutable lastCommit = 0UL
        let mutable lastBoot = Guid.Empty
        let mutable seenFirst = false
        let total = segs.Length
        segs
        |> List.iteri (fun idx (payload, endsWithLf) ->
            let json = Encoding.UTF8.GetString payload
            match Record.tryFromJson json with
            | Ok r ->
                if not seenFirst then
                    seenFirst <- true
                elif r.bootId = lastBoot then
                    if r.commitId <> lastCommit + 1UL then
                        warnings.Add(sprintf "commitId gap: %d -> %d" lastCommit r.commitId)
                elif r.commitId <= lastCommit then
                    warnings.Add(sprintf "commitId regression across bootId: %d <= %d" r.commitId lastCommit)
                lastCommit <- r.commitId
                lastBoot <- r.bootId
                records.Add r
            | Error msg ->
                if idx = total - 1 && not endsWithLf then
                    truncated <- payload.Length
                    warnings.Add(sprintf "truncated tail record dropped (%d bytes)" payload.Length)
                else
                    skipped <- skipped + 1
                    warnings.Add(sprintf "skipped malformed segment #%d: %s" idx msg))
        { records = List.ofSeq records
          truncatedBytes = truncated
          skippedSegments = skipped
          warnings = List.ofSeq warnings }
