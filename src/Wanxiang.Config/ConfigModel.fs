namespace Wanxiang.Config

open System
open System.Text.Json.Nodes

/// Provider 配置（TOML [providers.<id>]）。
type ProviderConfig = {
    id: string
    /// "openai" = OpenAI 兼容端点（含 ollama 等）
    kind: string
    baseUrl: string
    apiKey: string option
    model: string
    extraJson: JsonNode option
}

/// MCP Server 配置（TOML [mcp.<id>]）。
type McpServerConfig = {
    id: string
    /// 本地 stdio MCP：可执行文件
    command: string option
    args: string list
    env: Map<string, string>
    /// 远程 MCP：网络端点（与 command 二选一）
    url: string option
    /// 并发上限；None = 不额外限流
    maxConcurrency: int option
}

/// 已授权客户端（令牌哈希即身份）。
type ClientAuthRecord = {
    tokenHash: string
    name: string
    createdAtUtc: DateTimeOffset
    lastSeenUtc: DateTimeOffset option
    revoked: bool
}

type RuntimeSwitches = {
    server: bool
    client: bool
    pwa: bool
    fix: bool
}

/// 唯一配置事实来源的强类型视图（TOML 是权威；本类型是其投影）。
type AppConfig = {
    configVersion: int
    instanceId: Guid
    runtime: RuntimeSwitches
    listen: string
    maxAttachmentBytes: int64
    chunkSizeBytes: int
    pairingFailureWindowMinutes: int
    pairingMaxFailures: int
    pairingFreezeMinutes: int
    providers: Map<string, ProviderConfig>
    mcpServers: Map<string, McpServerConfig>
    authClients: ClientAuthRecord list
}

module AppConfig =

    let defaults (instanceId: Guid) : AppConfig =
        { configVersion = 1
          instanceId = instanceId
          runtime = { server = true; client = true; pwa = true; fix = false }
          listen = "127.0.0.1:8765"
          maxAttachmentBytes = 64L * 1024L * 1024L
          chunkSizeBytes = 256 * 1024
          pairingFailureWindowMinutes = 1
          pairingMaxFailures = 5
          pairingFreezeMinutes = 5
          providers = Map.empty
          mcpServers = Map.empty
          authClients = [] }
