namespace Wanxiang.Config

open System
open System.Security.Cryptography
open Wanxiang.Core

/// 令牌与配对。
/// - 每客户端永久令牌：客户端保存原文，服务端 TOML 只保存 SHA-256 哈希（决策 47/54）；
/// - 配对码：服务端按需生成、6 位十进制、单次使用、5 分钟过期、同时仅一个有效（决策 46）；
/// - 配对失败限流：每远端地址窗口内最多 N 次失败，超过冻结 M 分钟（Q188，TOML 可调）。
module Auth =

    let tokenPrefix = "wanxiang_client_"

    /// 生成高熵永久令牌（>= 256 bit 随机 + base64url）。
    let generateToken () : string =
        let bytes = RandomNumberGenerator.GetBytes 32
        let b64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        tokenPrefix + b64

    let hashToken (token: string) : string = CommandId.sha256Hex token

    /// 常量时间比较
    let constantTimeEquals (a: string) (b: string) : bool =
        CryptographicOperations.FixedTimeEquals(Text.Encoding.UTF8.GetBytes a, Text.Encoding.UTF8.GetBytes b)

    /// 生成 6 位十进制配对码（密码学安全随机源）。
    let generatePairingCode () : string =
        let n = RandomNumberGenerator.GetInt32(0, 1_000_000)
        n.ToString("D6")

    /// 本机桌面客户端在 TOML 里的固定用途名（决策 190 的“固定用途元数据”，决策 65-W2 章）。
    /// 该名称保留：经配对码创建的设备不得使用它（见 `sanitizeClientName`）。
    [<Literal>]
    let desktopClientName = "wanxiang-desktop"

    /// 是否保留名（忽略大小写与首尾空白）。
    let isReservedClientName (name: string) : bool =
        not (isNull name) && String.Equals(name.Trim(), desktopClientName, StringComparison.OrdinalIgnoreCase)

    /// 配对时客户端自报的名称：保留名一律改成 `fallback`，其余原样（空白名也回落）。
    let sanitizeClientName (requested: string option) (fallback: string) : string =
        match requested with
        | Some n when not (String.IsNullOrWhiteSpace n) && not (isReservedClientName n) -> n.Trim()
        | _ -> fallback

    let private isLoopbackAddress (address: string) : bool =
        if String.IsNullOrWhiteSpace address then false
        else
            match System.Net.IPAddress.TryParse address with
            | true, ip -> System.Net.IPAddress.IsLoopback ip || (ip.IsIPv4MappedToIPv6 && System.Net.IPAddress.IsLoopback(ip.MapToIPv4()))
            | _ -> false

    /// 仅“桌面本机客户端”有权发配对邀请：令牌记录是保留名、未吊销，且连接来自回环地址。
    /// 保留名只有本机自动配对写入；回环限制让反向代理之外的远端无法借用该身份。
    let isDesktopClient (record: ClientAuthRecord option) (remoteAddress: string) : bool =
        match record with
        | Some r -> not r.revoked && isReservedClientName r.name && isLoopbackAddress remoteAddress
        | None -> false

    /// WebSocket 握手的 Origin 检查（决策 281，防跨站 WebSocket 劫持 / DNS 重绑定）：
    /// - 无 Origin（桌面客户端、脚本等原生连接）放行；令牌/配对码仍是唯一凭据；
    /// - 有 Origin 时必须是 http(s)，且其 host:port 与本次请求的 Host 头完全一致（同源 PWA）；
    /// - "null" 或解析失败一律拒绝。反向代理须原样转发 Host（nginx: proxy_set_header Host $host）。
    let isAllowedWsOrigin (origin: string) (hostHeader: string) : bool =
        if String.IsNullOrEmpty origin then true
        elif String.IsNullOrWhiteSpace hostHeader then false
        else
            match Uri.TryCreate(origin, UriKind.Absolute) with
            | true, u when (u.Scheme = "http" || u.Scheme = "https") && u.AbsolutePath = "/" && String.IsNullOrEmpty u.UserInfo ->
                let defaultPort = if u.Scheme = "https" then 443 else 80
                let originAuthority = if u.Port = defaultPort then u.Host else sprintf "%s:%d" u.Host u.Port
                let host = hostHeader.Trim().ToLowerInvariant()
                let host = if host.EndsWith(sprintf ":%d" defaultPort) then host.Substring(0, host.Length - (string defaultPort).Length - 1) else host
                String.Equals(originAuthority.ToLowerInvariant(), host, StringComparison.Ordinal)
            | _ -> false

    /// 浏览器握手的 Host 白名单（决策 282，防 DNS 重绑定：重绑定后 Origin 与 Host 同为攻击者域名，Origin 检查拦不住）。
    /// 放行：IP 字面量、localhost；其余主机名须列在 WANXIANG_ALLOWED_HOSTS（逗号分隔，反向代理域名在此登记）。
    let isAllowedWsHost (hostHeader: string) (extraAllowed: string list) : bool =
        if String.IsNullOrWhiteSpace hostHeader then false
        else
            let h = hostHeader.Trim().ToLowerInvariant()
            let name =
                if h.StartsWith "[" then (match h.IndexOf ']' with | i when i > 0 -> h.Substring(1, i - 1) | _ -> h)
                else (match h.LastIndexOf ':' with | i when i > 0 && h.IndexOf ':' = i -> h.Substring(0, i) | _ -> h)
            name = "localhost"
            || (match System.Net.IPAddress.TryParse name with | true, _ -> true | _ -> false)
            || extraAllowed |> List.exists (fun a -> String.Equals(a.Trim(), name, StringComparison.OrdinalIgnoreCase))

    /// 设备的稳定标识：令牌哈希的前 16 位十六进制。哈希本身不可逆，截断后不能用于认证。
    let deviceId (tokenHash: string) : string =
        if isNull tokenHash then "" elif tokenHash.Length <= 16 then tokenHash else tokenHash.Substring(0, 16)

    type PairingSession = {
        code: string
        expiresAtUtc: DateTimeOffset
        /// 配对成功后置 true（单次使用）
        used: bool
    }

    type PairingState() =
        let lockObj = obj()
        let mutable session: PairingSession option = None
        /// 当前码是否由桌面客户端邀请产生（仅内存，决策 65-W2 章）。
        let mutable invited = false

        /// 发起新配对：旧码立即失效（决策 46）。
        member _.Start(nowUtc: DateTimeOffset, lifetime: TimeSpan) : string =
            lock lockObj (fun () ->
                let code = generatePairingCode ()
                session <- Some { code = code; expiresAtUtc = nowUtc + lifetime; used = false }
                invited <- false
                code)

        /// 桌面客户端发出邀请：与 Start 同样替换旧码，同时只有一个有效码；码仍只在内存。
        member _.StartInvite(nowUtc: DateTimeOffset, lifetime: TimeSpan) : string * DateTimeOffset =
            lock lockObj (fun () ->
                let code = generatePairingCode ()
                let expires = nowUtc + lifetime
                session <- Some { code = code; expiresAtUtc = expires; used = false }
                invited <- true
                code, expires)

        /// 是否存在由邀请产生的会话（含已用或已过期但尚未清理的）。
        /// 用于让提交邀请码的连接得到明确的“已用/过期”回执，而不是沉默。
        member _.HasInviteSession : bool =
            lock lockObj (fun () -> invited && session.IsSome)

        /// 仍有效（未用、未过期）的邀请码到期时间；没有则 None。
        member _.ActiveInvite(nowUtc: DateTimeOffset) : DateTimeOffset option =
            lock lockObj (fun () ->
                match session with
                | Some s when invited && not s.used && nowUtc <= s.expiresAtUtc -> Some s.expiresAtUtc
                | _ -> None)

        /// 校验配对码；成功即消耗（单次使用）。
        member _.TryConsume(nowUtc: DateTimeOffset, code: string) : Result<unit, string> =
            lock lockObj (fun () ->
                match session with
                | None -> Error "no pairing session active"
                | Some s when s.used -> Error "pairing code already used"
                | Some s when nowUtc > s.expiresAtUtc ->
                    session <- None
                    Error "pairing code expired"
                | Some s ->
                    if constantTimeEquals s.code code then
                        session <- Some { s with used = true }
                        Ok()
                    else
                        Error "invalid pairing code")

        /// 当前会话信息（供 stderr 输出配对码与到期时间）。
        member _.ActiveSession : PairingSession option =
            lock lockObj (fun () -> session)

    /// 失败限流状态（每远端地址）。
    type FailureTracker(failureWindow: TimeSpan, maxFailures: int, freezeDuration: TimeSpan) =
        let lockObj = obj()
        let failures = System.Collections.Generic.Dictionary<string, DateTimeOffset list>()
        let frozenUntil = System.Collections.Generic.Dictionary<string, DateTimeOffset>()
        /// 惰性清理阈值：条目数超过该值才扫描清理过期条目（R5：攻击者可伪造
        /// 大量不同地址反复失败，字典不得随失败次数无界增长；每次 RecordFailure
        /// 全量扫描是 O(n)，只在超过阈值时做）。
        let pruneThreshold = 1024
        /// 移除已过窗口的失败记录与已过期的冻结条目。仅在条目数超过阈值时调用，
        /// 保证长期运行内存有界且单次成本 O(n) 摊销可忽略。
        let prune (nowUtc: DateTimeOffset) : unit =
            let cutoff = nowUtc - failureWindow
            for kv in failures |> Seq.toArray do
                match kv.Value |> List.filter (fun t -> t > cutoff) with
                | [] -> failures.Remove kv.Key |> ignore
                | kept -> failures[kv.Key] <- kept
            for kv in frozenUntil |> Seq.toArray do
                if nowUtc >= kv.Value then frozenUntil.Remove kv.Key |> ignore

        member _.IsFrozen(nowUtc: DateTimeOffset, address: string) : bool =
            lock lockObj (fun () ->
                match frozenUntil.TryGetValue address with
                | true, until -> nowUtc < until
                | _ -> false)

        member _.RecordFailure(nowUtc: DateTimeOffset, address: string) : bool =
            // 返回 true = 触发冻结
            lock lockObj (fun () ->
                if failures.Count + frozenUntil.Count >= pruneThreshold then prune nowUtc
                let cutoff = nowUtc - failureWindow
                let recent =
                    match failures.TryGetValue address with
                    | true, list -> list |> List.filter (fun t -> t > cutoff)
                    | _ -> []
                let updated = recent @ [ nowUtc ]
                failures[address] <- updated
                if updated.Length >= maxFailures then
                    frozenUntil[address] <- nowUtc + freezeDuration
                    failures[address] <- []
                    true
                else
                    false)

        member _.Clear(address: string) : unit =
            lock lockObj (fun () ->
                failures.Remove address |> ignore
                frozenUntil.Remove address |> ignore)
