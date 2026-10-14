namespace Wanxiang.UI

open System
open System.IO

/// 本机界面偏好的落点（ui.json）。解析、渲染与主题判断在 Wanxiang2.UiPrefs；
/// 这里只给出取值边界、读写文件并收紧权限。
module UiPrefsFile =

    /// 取值边界的唯一事实来源在设计层。
    let limits: UiPrefLimits =
        { sidebarWidth = Tokens.sidebarWidth
          sidebarMinWidth = Tokens.sidebarMinWidth
          sidebarMaxWidth = Tokens.sidebarMaxWidth
          windowMinWidth = LayoutPolicy.desktopMinWidth }

    let defaults = UiPrefs.defaults limits

    let filePath () =
        let home =
            match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
            | s when not (String.IsNullOrWhiteSpace s) -> s
            | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
        Path.Combine(home, "ui.json")

    let private readText () =
        try LocalText.read (filePath ()) with _ -> None

    let load () : UiPrefs = UiPrefs.parse limits (readText ())

    let save (prefs: UiPrefs) =
        try
            let path = filePath ()
            if LocalText.write path (UiPrefs.render (readText ()) prefs) && not (OperatingSystem.IsBrowser()) then
                // Q118：本机偏好文件按最小用户权限落盘（与 TOML/日志/附件一致）
                try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
        with _ ->
            ()

    /// 偏好 → 实际主题。浏览器与桌面都由宿主给出系统是否深色。
    let resolveTheme (prefs: UiPrefs) (systemIsDark: bool) : ThemeMode =
        if UiPrefs.isDark prefs systemIsDark then Dark else Light
