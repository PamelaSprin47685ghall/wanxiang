namespace Wanxiang.UI

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Wanxiang.Core
open Wanxiang.Interop

/// 客户端凭据与本地偏好存储（决策 52/53、Q191）。
/// 桌面：~/.config/wanxiang/client.toml（决策 64：server+client 同进程自动配对写入）；
/// PWA：IndexedDB，以 instanceId 为主键保存 URL、原 token 与展示名（Q191：URL 不是身份）。
module CredentialStore =

    /// 客户端标识（配对时上报；PWA 与桌面区分，决策 55 语义：客户端名用于展示与审计）。
    let clientName =
        if OperatingSystem.IsBrowser() then "PWA" else "wanxiang-desktop"

    let private configHome () =
        match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")

    /// 桌面：解析 client.toml 的 url/token（自动连接本机 S）。
    /// 浏览器中不触碰文件系统（wasm 无 File API），返回 None。
    let tryLoadClientToml () : (string * string) option =
        if OperatingSystem.IsBrowser() then None
        else
            try
                let path = Path.Combine(configHome (), "client.toml")
                if File.Exists path then
                    CredentialStore.parseClientToml (File.ReadAllLines path)
                else None
            with _ -> None

    /// 浏览器：读取 IndexedDB 中全部连接记录，返回 (url, token, instanceId) 列表（按 updatedAt 降序）。
    let tryListBrowserConnectionsAsync () : Task<(string * string * string) list> =
        task {
            if not (OperatingSystem.IsBrowser()) then
                return []
            else
                try
                    let! json = BrowserCredentials.List()
                    return CredentialStore.parseConnections json
                with _ ->
                    return []
        }

    /// 浏览器：取最近一条连接（Q191：按 instanceId 主键，URL 不是身份）。
    let tryLoadBrowserConnectionAsync () : Task<(string * string * string) option> =
        task {
            let! list = tryListBrowserConnectionsAsync ()
            return List.tryHead list
        }

    /// 浏览器：保存/更新一条连接凭据（配对成功或认证成功后，决策 52/53）。
    let saveBrowserConnectionAsync (instanceId: string) (url: string) (token: string) (name: string) : Task =
        task {
            if OperatingSystem.IsBrowser() then
                try
                    do! BrowserCredentials.Put(instanceId, url, token, name)
                with _ -> ()
        }

    /// 浏览器：删除一条连接凭据。
    let deleteBrowserConnectionAsync (instanceId: string) : Task =
        task {
            if OperatingSystem.IsBrowser() then
                try
                    do! BrowserCredentials.Delete(instanceId)
                with _ -> ()
        }

    /// 浏览器默认连接地址：与页面同源（https → wss，http → ws）；非浏览器回退桌面默认值。
    let defaultServerUrl () : string =
        if not (OperatingSystem.IsBrowser()) then CredentialStore.desktopServerUrl
        else
            try CredentialStore.serverUrlOfPage Constants.WsPath (BrowserPage.Url())
            with _ -> CredentialStore.desktopServerUrl
