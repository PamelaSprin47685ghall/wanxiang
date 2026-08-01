namespace Wanxiang.Core

open System
open System.Security.Cryptography
open System.Text

module CommandId =

    /// 由 invocationId + 命令类型 + 规范化载荷 确定性计算 commandId。
    /// commandId = base64url( SHA-256( "wanxiang.command.v1" + invocationId + commandType + canonicalPayload ) )
    /// 网络重试复用 invocationId => 相同 commandId；内容被修改 => commandId 变化（可检测为协议错误）。
    let compute (invocationId: Guid) (commandType: string) (canonicalPayload: string) : string =
        use sha = SHA256.Create()
        let material =
            String.concat "\u0000"
                [ Constants.CommandIdPrefix
                  invocationId.ToString("D")
                  commandType
                  canonicalPayload ]
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(material))
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let sha256Hex (value: string) : string =
        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value))
        Convert.ToHexString(bytes).ToLowerInvariant()
