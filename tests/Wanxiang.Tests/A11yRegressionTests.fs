module Wanxiang.Tests.A11yRegressionTests

// UX 无障碍回归契约（docs/ux/a11y-audit.md 的可执行版本）。
// 全部为非 UI 测试：只读调色板数值与仓库源文件，不碰 Avalonia 平台。

open System
open System.IO
open System.Text.RegularExpressions
open Avalonia.Media
open Xunit
open Wanxiang.UI

let private repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then failwith "找不到仓库根（wanxiang.slnx）"
        elif File.Exists(Path.Combine(dir.FullName, "wanxiang.slnx")) then dir.FullName
        else up dir.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

let private read (rel: string) = File.ReadAllText(Path.Combine(repoRoot, rel))

let private luminance (c: Color) =
    let lin (v: byte) =
        let x = float v / 255.0
        if x <= 0.03928 then x / 12.92 else Math.Pow((x + 0.055) / 1.055, 2.4)
    0.2126 * lin c.R + 0.7152 * lin c.G + 0.0722 * lin c.B

let private ratio (a: Color) (b: Color) =
    let la, lb = luminance a, luminance b
    (max la lb + 0.05) / (min la lb + 0.05)

let private palettes : (string * Palette) list = [ "light", Palette.light; "dark", Palette.dark ]

let private check (label: string) (minimum: float) (pairs: (string * Color * string * Color) list) =
    let bad =
        [ for (fgName, fg, bgName, bg) in pairs do
            let r = ratio fg bg
            if r < minimum then sprintf "%s: %s on %s = %.2f < %.1f" label fgName bgName r minimum ]
    Assert.True(bad.IsEmpty, String.Join("\n", bad))

// ---------- 对比度 ----------

[<Fact>]
let ``代码块内全部着色与弱化文字在代码面和代码头上过 AA`` () =
    for (label, p) in palettes do
        let inks =
            [ "codeText", p.codeText; "codeMuted", p.codeMuted; "codeComment", p.codeComment
              "codeKeyword", p.codeKeyword; "codeString", p.codeString; "codeNumber", p.codeNumber
              "codeType", p.codeType; "codeFunction", p.codeFunction; "codeMeta", p.codeMeta
              "codeVariable", p.codeVariable; "codeOperator", p.codeOperator
              "codeAddition", p.codeAddition; "codeDeletion", p.codeDeletion ]
        let grounds = [ "codeBg", p.codeBg; "codeHeaderBg", p.codeHeaderBg ]
        check label 4.5 [ for (i, f) in inks do for (g, b) in grounds -> (i, f, g, b) ]

[<Fact>]
let ``正文与次要文字在选中行行内代码表格底上过 AA`` () =
    for (label, p) in palettes do
        let grounds =
            [ "selected", p.selected; "inlineCode", p.inlineCode; "tableStripe", p.tableStripe
              "tableHeader", p.tableHeader; "accentSoft", p.accentSoft; "accentFaint", p.accentFaint ]
        check label 4.5
            [ for (g, b) in grounds do
                yield ("text", p.text, g, b)
                yield ("textMuted", p.textMuted, g, b) ]

[<Fact>]
let ``状态色文字在各自的浅底上过 AA`` () =
    for (label, p) in palettes do
        check label 4.5
            [ "danger", p.danger, "dangerSoft", p.dangerSoft
              "info", p.info, "infoSoft", p.infoSoft
              "accent", p.accent, "accentSoft", p.accentSoft ]

[<Fact>]
let ``强调色悬停态与用户气泡上的文字过 AA`` () =
    for (label, p) in palettes do
        check label 4.5
            [ "textOnAccent", p.textOnAccent, "accentHover", p.accentHover
              "textOnAccent", p.textOnAccent, "accent", p.accent
              "userBubbleText", p.userBubbleText, "userBubble", p.userBubble ]

[<Fact>]
let ``键盘焦点环颜色在所有基础表面上过非文本 3 比 1`` () =
    // WCAG 1.4.11：焦点指示器与相邻颜色至少 3:1。焦点环取 accent（见 Interaction.fs）。
    for (label, p) in palettes do
        check label 3.0
            [ for (g, b) in [ "canvas", p.canvas; "rail", p.rail; "surface", p.surface
                              "surfaceRaised", p.surfaceRaised; "surfaceContainer", p.surfaceContainer ] ->
                ("accent(focus ring)", p.accent, g, b) ]

[<Fact>]
let ``遮罩不透明度足以压住背后内容`` () =
    Assert.True(Palette.light.scrim.A >= 0x55uy, "浅色遮罩 alpha 过低")
    Assert.True(Palette.dark.scrim.A >= 0x80uy, "深色遮罩 alpha 过低")

// ---------- 共享原语：图标按钮必须有可访问名称 ----------

[<Fact>]
let ``所有图标按钮调用都带非空名称`` () =
    let files = Directory.GetFiles(Path.Combine(repoRoot, "src", "Wanxiang.UI"), "*.fs", SearchOption.AllDirectories)
    let emptyTip = Regex("""iconButton(Accent)?\s+[\w.]+\s+(""|null)""")
    let bad =
        [ for f in files do
            let lines = File.ReadAllLines f
            for i in 0 .. lines.Length - 1 do
                if emptyTip.IsMatch lines.[i] then
                    sprintf "%s:%d 图标按钮缺少 tip/可访问名称" (Path.GetRelativePath(repoRoot, f)) (i + 1) ]
    Assert.True(bad.IsEmpty, String.Join("\n", bad))

[<Fact>]
let ``共享图标按钮原语同时设置提示与自动化名称`` () =
    let src = read "src/Wanxiang.UI/Controls/Primitives.fs"
    let setNames = Regex.Matches(src, @"AutomationProperties\.SetName\(host, tip\)").Count
    let setTips = Regex.Matches(src, @"ToolTip\.SetTip\(host, tip\)").Count
    Assert.True(setNames >= 2 && setTips >= 2, "iconButton / iconButtonAccent 必须同时挂 ToolTip 与 AutomationProperties.Name")

// ---------- 文案一致性：术语表 ----------

let private uiStrings () =
    // UI、Client、App 三个项目里含中文的字符串字面量（W6：术语扫描范围）。
    let lit = Regex("\"([^\"\\\\\\n]|\\\\.)*[\\u4e00-\\u9fff]([^\"\\\\\\n]|\\\\.)*\"")
    [ for proj in [ "Wanxiang.UI"; "Wanxiang.App" ] do
        let files = Directory.GetFiles(Path.Combine(repoRoot, "src", proj), "*.fs", SearchOption.AllDirectories)
        for f in files do
            let lines = File.ReadAllLines f
            for i in 0 .. lines.Length - 1 do
                let t = lines.[i].TrimStart()
                if not (t.StartsWith "//") && not (t.StartsWith "///") then
                    for m in lit.Matches lines.[i] do
                        yield Path.GetRelativePath(repoRoot, f), i + 1, m.Value ]

[<Fact>]
let ``界面文案不使用已淘汰的同义词`` () =
    // 术语表见 docs/ux/a11y-audit.md §6。新增同义词变体前先改术语表与本测试。
    let banned =
        [ "设定", "统一用「设置」"
          "秘钥", "统一用「密钥」"
          "Api Key", "统一写作「API Key」"
          "api key", "统一写作「API Key」"
          "Apikey", "统一写作「API Key」" ]
    let bad =
        [ for (f, line, s) in uiStrings () do
            for (word, hint) in banned do
                if s.Contains(word, StringComparison.Ordinal) then
                    sprintf "%s:%d 含「%s」（%s）：%s" f line word hint s ]
    Assert.True(bad.IsEmpty, String.Join("\n", bad))

[<Fact>]
let ``README 与 PWA 页面文案同样不使用已淘汰术语`` () =
    // README、站点页、PWA 壳的用户可见文字与界面同一套术语。
    let banned = [ "会话"; "服务器"; "服务端"; "服务商"; "提供商"; "供应商"; "提供方" ]
    let files =
        [ "README.md"; "site/index.html"
          "src/Wanxiang.Pwa/wwwroot/index.html"; "src/Wanxiang.Pwa/wwwroot/main.js"
          "src/Wanxiang.Pwa/wwwroot/manifest.webmanifest" ]
    let bad =
        [ for f in files do
            let lines = File.ReadAllLines(Path.Combine(repoRoot, f))
            for i in 0 .. lines.Length - 1 do
                for w in banned do
                    if lines.[i].Contains(w, StringComparison.Ordinal) then
                        sprintf "%s:%d 含「%s」" f (i + 1) w ]
    Assert.True(bad.IsEmpty, String.Join("\n", bad))

[<Fact>]
let ``界面文案用中文省略号而不是三个英文句点`` () =
    let bad =
        [ for (f, line, s) in uiStrings () do
            if s.Contains "..." then sprintf "%s:%d %s" f line s ]
    Assert.True(bad.IsEmpty, String.Join("\n", bad))

[<Fact>]
let ``取消按钮的提示文案都写明 Esc`` () =
    // 对话框约定：取消/关闭类控件的 tooltip 必须告诉用户 Esc 可用。
    let bad =
        [ for (f, line, s) in uiStrings () do
            if Regex.IsMatch(s, "^\"取消并关闭") && not (s.Contains "Esc") then
                sprintf "%s:%d %s" f line s ]
    Assert.True(bad.IsEmpty, String.Join("\n", bad))

// ---------- PWA 壳：静态无障碍规则 ----------

[<Fact>]
let ``PWA 入口页声明语言标题并允许缩放`` () =
    let html = read "src/Wanxiang.Pwa/wwwroot/index.html"
    Assert.Matches("<html[^>]*\\blang=\"zh", html)
    Assert.Matches("<title>[^<]+</title>", html)
    Assert.DoesNotContain("user-scalable=no", html)
    Assert.DoesNotContain("user-scalable=0", html)
    Assert.DoesNotMatch("maximum-scale=1(\\.0)?[,\"\\s]", html)

[<Fact>]
let ``PWA 启动页对辅助技术可读且装饰元素隐藏`` () =
    let html = read "src/Wanxiang.Pwa/wwwroot/index.html"
    Assert.Contains("role=\"status\"", html)
    Assert.Contains("aria-live=", html)
    Assert.Contains("aria-hidden=\"true\"", html)
    Assert.Matches("<img[^>]*\\balt=\"[^\"]+\"", html)

[<Fact>]
let ``PWA 样式保留焦点环并尊重减少动态偏好`` () =
    let css = read "src/Wanxiang.Pwa/wwwroot/app.css"
    Assert.Contains("prefers-reduced-motion", css)
    Assert.Contains(":focus-visible", css)

[<Fact>]
let ``PWA 清单声明名称与图标`` () =
    let m = read "src/Wanxiang.Pwa/wwwroot/manifest.webmanifest"
    Assert.Matches("\"name\"\\s*:\\s*\"[^\"]+\"", m)
    Assert.Matches("\"short_name\"\\s*:\\s*\"[^\"]+\"", m)
    Assert.Contains("\"icons\"", m)

// ── r13 无障碍复审（决策 302–306）：r7–r12 新增的芯片 / 提示条 / 倒计时 / 切换器 ──

[<Fact>]
let ``r7-r12 新增控件的文字在浅色与深色主题下都过 AA`` () =
    for (label, p) in palettes do
        check label 4.5
            [ // 顶栏模型芯片（决策 240）
              "accent", p.accent, "accentFaint", p.accentFaint
              // 限流倒计时（决策 242）：错误卡在 surface / surfaceRaised 上
              "warning", p.warning, "surface", p.surface
              "warning", p.warning, "surfaceRaised", p.surfaceRaised
              "success", p.success, "surface", p.surface
              "success", p.success, "surfaceRaised", p.surfaceRaised
              // 提示条（撤销删除）与模型切换器浮层
              "text", p.text, "surfaceRaised", p.surfaceRaised
              "textMuted", p.textMuted, "surfaceRaised", p.surfaceRaised
              "accent", p.accent, "surfaceRaised", p.surfaceRaised
              // 选中行（切换器当前项、搜索命中）
              "text", p.text, "selected", p.selected
              "textMuted", p.textMuted, "selected", p.selected ]

[<Fact>]
let ``模型选择不再保留隐藏顶栏入口`` () =
    let src = read "src/Wanxiang.UI/AppShell.fs"
    Assert.DoesNotContain("HeaderModelAnchor", src)
    Assert.DoesNotContain("SetHeaderModel", src)
    Assert.Contains("composer.ModelAnchor()", src)

// r18 一致性（决策 350–354）：同一动作同一个名字，快捷键走 hint 列而不是塞进标签；
// 附件上传完成不再 toast；顶栏菜单不重复「回到最新」浮钮。
[<Fact>]
let ``r18 菜单与入口一致性`` () =
    let sidebar = read "vendor/wanxiang2/compose/SidebarDesk/SidebarDesk.fs"
    let chat = read "vendor/wanxiang2/compose/ChatScreen/ChatScreen.fs"
    let shell = read "src/Wanxiang.UI/AppShell.fs"
    for bad in [ "\"重命名 (F2)\""; "置顶 (P)\""; "\"删除 (Delete)\""; "已归档对话 (%d)" ] do
        Assert.DoesNotContain(bad, sidebar)
    Assert.DoesNotContain("从当前分叉", chat)
    Assert.Contains("\"从此分叉\"", chat)
    Assert.Contains("\"从此分叉\"", sidebar)
    Assert.DoesNotContain("PopupEntry.create \"回到最新\"", chat)
    Assert.DoesNotContain("上传完成\" upload.fileName) Success", shell)
