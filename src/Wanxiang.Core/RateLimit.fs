namespace Wanxiang.Core

open System
open System.Globalization
open System.Text.RegularExpressions

/// 模型服务在响应头里给出的限流 / 额度信号（决策 241）。
///
/// 只保留对用户有用的几项：还剩多少、多久恢复、上游明确要求等多久。
/// 全部可选——不同服务的头各不相同，缺哪项就不说哪项，绝不编造。
type ProviderRateLimit = {
    /// 剩余请求数（x-ratelimit-remaining-requests / anthropic-ratelimit-requests-remaining / x-ratelimit-remaining）
    remainingRequests: int option
    /// 剩余 token 数（x-ratelimit-remaining-tokens / anthropic-ratelimit-tokens-remaining）
    remainingTokens: int option
    /// 额度多少秒后恢复（各 reset 头里取最早一个已耗尽维度，否则取最晚）
    resetSeconds: int option
    /// 上游明确要求等待的秒数（Retry-After）
    retryAfterSeconds: int option
}

module ProviderRateLimit =

    let empty =
        { remainingRequests = None
          remainingTokens = None
          resetSeconds = None
          retryAfterSeconds = None }

    let isEmpty (r: ProviderRateLimit) = r = empty

    /// 头名统一小写比较。
    let private intOf (s: string) =
        match Int64.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, v when v >= 0L -> Some(int (min v (int64 Int32.MaxValue)))
        | _ ->
            match Double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, v when v >= 0.0 -> Some(int (min v (float Int32.MaxValue)))
            | _ -> None

    let private durationRx = Regex(@"^(?:(\d+(?:\.\d+)?)h)?(?:(\d+(?:\.\d+)?)m(?!s))?(?:(\d+(?:\.\d+)?)s)?(?:(\d+(?:\.\d+)?)ms)?$", RegexOptions.Compiled)

    /// 秒数（向上取整，至少 1 秒；0 表示已恢复）。
    let private ceilSeconds (seconds: float) =
        if seconds <= 0.0 then 0 else int (Math.Ceiling(min seconds (86400.0 * 7.0)))

    /// 解析一个「多久后恢复」的值。支持：
    /// - 纯秒数（`30`、`1.5`）
    /// - Go 风格时长（OpenAI：`6m0s`、`1s`、`20ms`、`1h2m3s`）
    /// - 绝对时间（Anthropic：RFC 3339；HTTP-date）
    /// - Unix 纪元秒（某些兼容服务：`x-ratelimit-reset: 1760000000`）
    let parseDelay (now: DateTimeOffset) (raw: string) : int option =
        if String.IsNullOrWhiteSpace raw then None
        else
            let v = raw.Trim()
            match Double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, n when n >= 0.0 ->
                // 大于约 3 年的「秒数」只可能是纪元时间戳（毫秒级时间戳同理）。
                if n > 1.0e12 then Some(ceilSeconds ((DateTimeOffset.FromUnixTimeMilliseconds(int64 n) - now).TotalSeconds))
                elif n > 1.0e8 then Some(ceilSeconds ((DateTimeOffset.FromUnixTimeSeconds(int64 n) - now).TotalSeconds))
                else Some(ceilSeconds n)
            | _ ->
                let m = durationRx.Match v
                if m.Success && m.Length > 0 then
                    let part (i: int) =
                        if m.Groups[i].Success then Double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) else 0.0
                    Some(ceilSeconds (part 1 * 3600.0 + part 2 * 60.0 + part 3 + part 4 / 1000.0))
                else
                    match DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
                    | true, at -> Some(ceilSeconds ((at - now).TotalSeconds))
                    | _ -> None

    /// 从响应头读出限流信号。`lookup` 按头名（不区分大小写）取第一个值。
    /// 一项都读不到时返回 None——不下发空事件。
    let ofHeaders (now: DateTimeOffset) (lookup: string -> string option) : ProviderRateLimit option =
        let first (names: string list) = names |> List.tryPick lookup
        let remainingRequests =
            first [ "x-ratelimit-remaining-requests"; "anthropic-ratelimit-requests-remaining"; "x-ratelimit-remaining"; "ratelimit-remaining" ]
            |> Option.bind intOf
        let remainingTokens =
            first [ "x-ratelimit-remaining-tokens"; "anthropic-ratelimit-tokens-remaining"; "anthropic-ratelimit-input-tokens-remaining" ]
            |> Option.bind intOf
        let resetOf names = first names |> Option.bind (parseDelay now)
        let resetRequests = resetOf [ "x-ratelimit-reset-requests"; "anthropic-ratelimit-requests-reset"; "x-ratelimit-reset"; "ratelimit-reset" ]
        let resetTokens = resetOf [ "x-ratelimit-reset-tokens"; "anthropic-ratelimit-tokens-reset"; "anthropic-ratelimit-input-tokens-reset" ]
        // 哪个维度已经见底就等哪个；都没见底（或不知道）就等较晚的那个，保证重试时真的恢复了。
        let resetSeconds =
            let exhausted remaining reset =
                match remaining, reset with
                | Some 0, Some s -> Some s
                | _ -> None
            match exhausted remainingRequests resetRequests, exhausted remainingTokens resetTokens with
            | Some a, Some b -> Some(max a b)
            | Some a, None | None, Some a -> Some a
            | None, None ->
                match resetRequests, resetTokens with
                | Some a, Some b -> Some(max a b)
                | Some a, None | None, Some a -> Some a
                | None, None -> None
        let retryAfterSeconds = lookup "retry-after" |> Option.bind (parseDelay now)
        let result =
            { remainingRequests = remainingRequests
              remainingTokens = remainingTokens
              resetSeconds = resetSeconds
              retryAfterSeconds = retryAfterSeconds }
        if isEmpty result then None else Some result

    /// 建议的等待秒数：上游明确的 Retry-After 优先，其次是额度恢复时间。
    let waitSeconds (r: ProviderRateLimit) : int option =
        match r.retryAfterSeconds with
        | Some s when s > 0 -> Some s
        | _ ->
            match r.resetSeconds with
            | Some s when s > 0 -> Some s
            | _ -> None

    /// 额度是否已经见底（任一维度剩余为 0）。
    let isExhausted (r: ProviderRateLimit) =
        r.remainingRequests = Some 0 || r.remainingTokens = Some 0

    /// 倒计时文案（决策 242）：「约 1 分 05 秒后可重试」/「现在可以重试了」。
    let countdownText (remainingSeconds: int) : string =
        if remainingSeconds <= 0 then "现在可以重试了。"
        elif remainingSeconds < 60 then sprintf "%d 秒后可重试。" remainingSeconds
        elif remainingSeconds < 3600 then sprintf "%d 分 %02d 秒后可重试。" (remainingSeconds / 60) (remainingSeconds % 60)
        else sprintf "约 %d 小时 %d 分后可重试。" (remainingSeconds / 3600) ((remainingSeconds % 3600) / 60)
