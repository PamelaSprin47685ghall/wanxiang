namespace Wanxiang.Core.Ledger

open System
open System.Security.Cryptography
open Jcs.Net

/// RFC 8785 JSON 规范化（JCS）。全项目唯一的规范化入口。
///
/// 全项目唯一的规范化入口（RFC 8785 JCS）。
/// **大整数陷阱**：RFC 8785 按 IEEE-754 double 序列化数字，
/// `18446744073709551615` 会被静默改写成 `18446744073709552000`（附录 D）。
/// 因此超出 double 精确范围的整数 **MUST** 以 JSON 字符串承载。
[<RequireQualifiedAccess>]
module Jcs =

    /// 规范化 UTF-8 字节。输入必须是 JSON 对象或数组。
    let canonicalizeUtf8 (json: string) : byte[] =
        JsonCanonicalizer.CanonicalizeToUtf8 json

    /// 规范化后转字符串（调试/测试用）。
    let canonicalize (json: string) : string =
        JsonCanonicalizer.Canonicalize json

    /// SHA-256(JCS(json)) 的十六进制小写。
    let sha256Hex (json: string) : string =
        canonicalizeUtf8 json
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun s -> s.ToLowerInvariant()

    /// 容错版：非法输入返回 None（不抛）。
    let trySha256Hex (json: string) : string option =
        try Some(sha256Hex json) with _ -> None
