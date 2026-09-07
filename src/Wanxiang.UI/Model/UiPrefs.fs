namespace Wanxiang.UI

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// 主题偏好。`System` 由宿主解析成明/暗。
type ThemePreference =
    | FollowSystem
    | AlwaysLight
    | AlwaysDark

module ThemePreference =

    let toText (p: ThemePreference) =
        match p with
        | FollowSystem -> "system"
        | AlwaysLight -> "light"
        | AlwaysDark -> "dark"

    let ofText (s: string) =
        match s with
        | "light" -> AlwaysLight
        | "dark" -> AlwaysDark
        | _ -> FollowSystem

    let label (p: ThemePreference) =
        match p with
        | FollowSystem -> "跟随系统"
        | AlwaysLight -> "浅色"
        | AlwaysDark -> "深色"

/// 本机 UI 偏好（`$WANXIANG_HOME/ui.json`；Q195：不写业务 TOML、不经 NDJSON）。
type UiPrefs = {
    theme: ThemePreference
    /// 消息正文字号
    fontScale: float
    /// 完成生成后自动收起思考过程
    autoCollapseReasoning: bool
    /// 代码块长行折行显示（false = 横向滚动）
    codeWrap: bool
    /// 侧栏宽度
    sidebarWidth: float
    /// 侧栏是否折叠
    sidebarCollapsed: bool
    /// 是否展示归档会话
    showArchived: bool
    /// 减少非必要动态效果
    reduceMotion: bool
    /// 发送键：true = Enter 发送，false = Ctrl+Enter 发送
    enterSends: bool
    /// 窗口几何（桌面端）
    windowWidth: float
    windowHeight: float
    windowX: int
    windowY: int
    hasWindowPosition: bool
}

module UiPrefs =

    let minFontScale = 12.0
    let maxFontScale = 19.0

    let defaults =
        { theme = FollowSystem
          fontScale = 14.5
          autoCollapseReasoning = true
          codeWrap = false
          sidebarWidth = Tokens.sidebarWidth
          sidebarCollapsed = false
          showArchived = false
          reduceMotion = false
          enterSends = true
          windowWidth = 1240.0
          windowHeight = 800.0
          windowX = 0
          windowY = 0
          hasWindowPosition = false }

    let filePath () =
        let home =
            match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
            | s when not (String.IsNullOrWhiteSpace s) -> s
            | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
        Path.Combine(home, "ui.json")

    let private readObject () =
        try
            let path = filePath ()
            if File.Exists path then
                match JsonNode.Parse(File.ReadAllText path) with
                | null -> JsonObject()
                | node -> node.AsObject()
            else
                JsonObject()
        with _ ->
            JsonObject()

    let private clamp lo hi value = if value < lo then lo elif value > hi then hi else value

    let load () : UiPrefs =
        try
            let o = readObject ()
            let boolOf key fallback =
                let mutable n: JsonNode = null
                if o.TryGetPropertyValue(key, &n) && not (isNull n) then
                    match n.GetValueKind() with
                    | JsonValueKind.True -> true
                    | JsonValueKind.False -> false
                    | _ -> fallback
                else
                    fallback
            let floatOf key fallback =
                let mutable n: JsonNode = null
                if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
                    let v = n.GetValue<double>()
                    if Double.IsNaN v then fallback else v
                else
                    fallback
            let intOf key fallback =
                let mutable n: JsonNode = null
                if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.Number then
                    n.GetValue<int>()
                else
                    fallback
            let stringOf key fallback =
                let mutable n: JsonNode = null
                if o.TryGetPropertyValue(key, &n) && not (isNull n) && n.GetValueKind() = JsonValueKind.String then
                    n.GetValue<string>()
                else
                    fallback
            { theme = ThemePreference.ofText(stringOf "theme" "system")
              fontScale = floatOf "fontScale" defaults.fontScale |> clamp minFontScale maxFontScale
              autoCollapseReasoning = boolOf "autoCollapseReasoning" defaults.autoCollapseReasoning
              codeWrap = boolOf "codeWrap" defaults.codeWrap
              sidebarWidth = floatOf "sidebarWidth" defaults.sidebarWidth |> clamp Tokens.sidebarMinWidth Tokens.sidebarMaxWidth
              sidebarCollapsed = boolOf "sidebarCollapsed" defaults.sidebarCollapsed
              showArchived = boolOf "showArchived" defaults.showArchived
              reduceMotion = boolOf "reduceMotion" defaults.reduceMotion
              enterSends = boolOf "enterSends" defaults.enterSends
              // 桌面最小宽与主窗口下限同源（C7）：LayoutPolicy.desktopMinWidth 是唯一事实来源。
              windowWidth = floatOf "windowWidth" defaults.windowWidth |> clamp LayoutPolicy.desktopMinWidth 6000.0
              windowHeight = floatOf "windowHeight" defaults.windowHeight |> clamp 600.0 4000.0
              windowX = intOf "windowX" 0
              windowY = intOf "windowY" 0
              hasWindowPosition = boolOf "hasWindowPosition" false }
        with _ ->
            defaults

    let save (prefs: UiPrefs) =
        try
            let path = filePath ()
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let o = readObject ()
            o["theme"] <- ThemePreference.toText prefs.theme
            o["fontScale"] <- prefs.fontScale
            o["autoCollapseReasoning"] <- prefs.autoCollapseReasoning
            o["codeWrap"] <- prefs.codeWrap
            o["sidebarWidth"] <- prefs.sidebarWidth
            o["sidebarCollapsed"] <- prefs.sidebarCollapsed
            o["showArchived"] <- prefs.showArchived
            o["reduceMotion"] <- prefs.reduceMotion
            o["enterSends"] <- prefs.enterSends
            o["windowWidth"] <- prefs.windowWidth
            o["windowHeight"] <- prefs.windowHeight
            o["windowX"] <- prefs.windowX
            o["windowY"] <- prefs.windowY
            o["hasWindowPosition"] <- prefs.hasWindowPosition
            File.WriteAllText(path, o.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
            // Q118：本机偏好文件按最小用户权限落盘（与 TOML/日志/附件一致）
            try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
        with _ ->
            ()

    /// 偏好 → 实际主题。浏览器与桌面都由宿主给出系统是否深色。
    let resolveTheme (prefs: UiPrefs) (systemIsDark: bool) : ThemeMode =
        match prefs.theme with
        | AlwaysLight -> Light
        | AlwaysDark -> Dark
        | FollowSystem -> if systemIsDark then Dark else Light
