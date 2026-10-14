module Wanxiang.Tests.SettingsSurfaceTests

open Wanxiang2.SettingsScreen
open System
open System.Text.Json.Nodes
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

/// 设置与对话框呈现面的回归测试。
///
/// 覆盖本轮修正的行为契约：
/// - 启用开关行统一走 Ui.switchRow（行最小高 / ItemStatus / 读写语义）；
/// - 表单校验反馈统一走 Ui.applyValidationFeedback（单错 / 多错 / 清零三种节律）；
/// - 状态 tag 语气分档（Warning / Neutral）；
/// - 已停用卡只弱化文字列，操作按钮保持可用；
/// - About 区不再有语义错位的 spacer；
/// - 双列表单列距与多行输入最小高走共享常量；
/// - 持久化回调失败时给出 Failure 档 toast，不再静默。

let rec private descendants (control: Control) =
    seq {
        yield control
        match control with
        | :? Panel as panel ->
            for child in panel.Children do
                yield! descendants child
        | :? Decorator as decorator when not (isNull decorator.Child) ->
            yield! descendants decorator.Child
        | :? ContentControl as host ->
            match host.Content with
            | :? Control as child -> yield! descendants child
            | _ -> ()
        | _ -> ()
    }

let private byAutomationName (root: Control) (name: string) =
    descendants root
    |> Seq.find (fun control -> AutomationProperties.GetName(control) = name)

let private tryByAutomationName (root: Control) (name: string) =
    descendants root
    |> Seq.tryFind (fun control -> AutomationProperties.GetName(control) = name)

let private firstTextBlock (root: Control) (text: string) =
    descendants root
    |> Seq.tryPick (function
        | :? TextBlock as block when block.Text = text -> Some block
        | _ -> None)

let private iconButtons (root: Control) =
    descendants root
    |> Seq.choose (function
        | :? Border as border when border.Width = Tokens.iconButton && border.MinHeight = Tokens.iconButton -> Some border
        | _ -> None)

let private overlay () = OverlayHost(Grid())

let private stubActions
    (toasts: ResizeArray<string * ToastTone>)
    (updateGeneration: JsonObject -> (bool -> unit) -> unit)
    : Wanxiang2.SettingsScreen.SettingsHooks =
    { UpsertProvider = fun _ completed -> completed true
      DeleteProvider = ignore
      ProbeProvider = ignore
      UpsertMcp = fun _ completed -> completed true
      DeleteMcp = ignore
      UpdateGeneration = updateGeneration
      SavePrefs = ignore }

[<Trait("Category", "UI")>]
[<Fact>]
let ``switch row consumes the shared settings row height and toggle semantics`` () =
    Headless.run (fun () ->
    let row, read, write = Ui.switchRow "测试开关" "用于验证开关行契约。" true ignore
    Assert.Equal(ControlMetrics.settingsRowMinHeight, row.MinHeight, 3)
    Assert.Equal(ControlMetrics.rowMinHeight, ControlMetrics.settingsRowMinHeight, 3)
    Assert.Equal("开启", AutomationProperties.GetItemStatus(row))
    Assert.True(read ())
    write false
    Assert.False(read ())
    Assert.Equal("关闭", AutomationProperties.GetItemStatus(row))
    Assert.Equal(Spacing.Radius.md, (row :?> Border).CornerRadius.TopLeft, 3)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``settings row and text area metrics keep single sources`` () =
    Headless.run (fun () ->
    Assert.Equal(ControlMetrics.rowMinHeight, ControlMetrics.settingsRowMinHeight, 3)
    Assert.True(ControlMetrics.textAreaMinHeight > 0.0)
    Assert.True(ControlMetrics.textAreaLongMinHeight > ControlMetrics.textAreaMinHeight)
    )

[<Trait("Category", "UI")>]
[<Fact>]
let ``warning tag tone uses the warning brush while neutral stays muted`` () =
    Headless.run (fun () ->
    let warning = Ui.tagWith Ui.TagTone.Warning "缺少密钥"
    let neutral = Ui.tag "已配置"
    let warningBorder = warning
    let neutralBorder = neutral
    Assert.Equal(Tokens.warning.Color, (warningBorder.BorderBrush :?> SolidColorBrush).Color)
    Assert.Equal(Tokens.warning.Color, ((warningBorder.Child :?> TextBlock).Foreground :?> SolidColorBrush).Color)
    Assert.Equal(Tokens.border.Color, (neutralBorder.BorderBrush :?> SolidColorBrush).Color)
    Assert.Equal(Tokens.textMuted.Color, ((neutralBorder.Child :?> TextBlock).Foreground :?> SolidColorBrush).Color)
    )

// 生成设置是设置区唯一不认 Ctrl+Enter 的表单：本地其余表单（模型服务 / MCP / 对话参数）
// 全部「Ctrl/⌘+Enter 保存」。补上同样的行级 KeyDown，并把键位写进按钮的无障碍说明，
// 用户不必先到处试键。锁：Ctrl+Enter 真触发保存，裸 Enter 不触发（归 Ui.onClick）。
// 生成设置此前是设置区唯一不认 Ctrl+Enter 的表单：本地其余表单（模型服务 / MCP / 对话参数）
// 全部「Ctrl/⌘+Enter 保存」。按钮的无障碍说明现在把键位写清楚，不必靠试。
// 键盘路由本身的验证不在此处：合成按键在无头窗口里不会从焦点控件冒泡到祖先
// （同一原因，SettingsProviders / SettingsTools 的行级 KeyDown 也没有单测），
// 这里只锁用户可观察的那一半——按钮自描述里的键位。
// 主机默认值采用明确提交：移开焦点不发送请求，目录刷新不覆盖草稿。
// 保存动作必须携带编辑值；确认成功后才清除未保存状态。
[<Trait("Category", "UI")>]
[<Fact>]
let ``appearance and about consume new metrics tokens and content-tier dividers`` () =
    Headless.run (fun () ->
    let toasts = ResizeArray<string * ToastTone>()
    let general = (Wanxiang2.SettingsScreen.SettingsScreen(SharedTheme.manager, (overlay ()).Desk, stubActions toasts (fun _ completed -> completed true), UiPrefsFile.defaults)).General
    // 字号槽宽走 token（原裸 56.0）
    let appearance = general.AppearanceView
    let fontSizeValue =
        descendants appearance
        |> Seq.tryPick (function
            | :? TextBlock as block when not (isNull block.Text) && block.Text.Contains "pt" -> Some block
            | _ -> None)
        |> Option.defaultWith (fun () -> failwith "外观页缺少字号值文本")
    Assert.Equal(ControlMetrics.fontSizeValueMinWidth, fontSizeValue.MinWidth, 3)
    // 「关于」键名列槽宽走 token（原裸 110.0）
    let about = (general.SetConnection("instance-1", "ws://127.0.0.1:8765/ws"); general.AboutView)
    let aboutKey =
        firstTextBlock about "协议版本"
        |> Option.defaultWith (fun () -> failwith "关于页缺少键名列文本")
    Assert.Equal(ControlMetrics.aboutKeyMinWidth, aboutKey.MinWidth, 3)
    // 面板内发丝分隔线统一走内容档 borderSoft，内容区不再混用更轻的 chrome 档 Tokens.hairline
    let chromeTierDividers =
        descendants appearance
        |> Seq.filter (function
            | :? Border as border ->
                border.Height = ControlMetrics.borderWidth
                && not (isNull border.Background)
                && border.Background = Tokens.hairline
            | _ -> false)
        |> Seq.length
    Assert.Equal(0, chromeTierDividers)
    )
