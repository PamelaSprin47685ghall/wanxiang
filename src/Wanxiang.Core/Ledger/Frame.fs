namespace Wanxiang.Core.Ledger

open System
open System.Text

/// RFC 7464 JSON Text Sequence 分帧。
///
/// 记录 = `RS`(0x1E) + JSON 对象 + `LF`(0x0A)。
/// 顶层 **MUST** 是 JSON 对象：RFC 7464 §2.4 允许顶层裸值，但裸数字在读取时
/// 依赖自定界（self-delimiting）判定，是已知陷阱；对象无此问题。
[<RequireQualifiedAccess>]
module Frame =

    [<Literal>]
    let RS = 0x1Euy

    [<Literal>]
    let LF = 0x0Auy

    /// 把一条已序列化的 JSON 文本包成一个记录（含 RS 与 LF）。
    let encode (json: string) : byte[] =
        let body = Encoding.UTF8.GetBytes json
        let buf = Array.zeroCreate<byte> (body.Length + 2)
        buf.[0] <- RS
        Array.blit body 0 buf 1 body.Length
        buf.[body.Length + 1] <- LF
        buf

    /// 切成记录边界。依据 RFC 7464 §2.1：`RS` 唯一确定边界；相邻 `RS` 之间为空段，忽略；
    /// 末尾无 `LF` 的残留内容即截断。
    ///
    /// 返回 (载荷, 是否以 `LF` 正常结尾)。无尾随 `LF` 的段即「被截断的尾记录」。
    let split (data: ReadOnlySpan<byte>) : (byte[] * bool) list =
        let result = ResizeArray<byte[] * bool>()
        let mutable i = 0
        while i < data.Length do
            if data.[i] = RS then
                let mutable j = i + 1
                while j < data.Length && data.[j] <> RS do
                    j <- j + 1
                let len = j - i - 1
                if len > 0 then
                    let seg = data.Slice(i + 1, len).ToArray()
                    let endsWithLf = seg.Length > 0 && seg.[seg.Length - 1] = LF
                    let payload = if endsWithLf then seg.[0 .. seg.Length - 2] else seg
                    result.Add(payload, endsWithLf)
                i <- j
            else
                i <- i + 1
        List.ofSeq result
