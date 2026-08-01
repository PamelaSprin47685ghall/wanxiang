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

    type PairingSession = {
        code: string
        expiresAtUtc: DateTimeOffset
        /// 配对成功后置 true（单次使用）
        used: bool
    }

    type PairingState() =
        let lockObj = obj()
        let mutable session: PairingSession option = None

        /// 发起新配对：旧码立即失效（决策 46）。
        member _.Start(nowUtc: DateTimeOffset, lifetime: TimeSpan) : string =
            lock lockObj (fun () ->
                let code = generatePairingCode ()
                session <- Some { code = code; expiresAtUtc = nowUtc + lifetime; used = false }
                code)

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

        member _.IsFrozen(nowUtc: DateTimeOffset, address: string) : bool =
            lock lockObj (fun () ->
                match frozenUntil.TryGetValue address with
                | true, until -> nowUtc < until
                | _ -> false)

        member _.RecordFailure(nowUtc: DateTimeOffset, address: string) : bool =
            // 返回 true = 触发冻结
            lock lockObj (fun () ->
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
            lock lockObj (fun () -> failures.Remove address |> ignore)
