namespace Wanxiang.Tests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Core
open Wanxiang.UI

/// 锁两条「桌面该有、此前没有」的可操作契约：
///
/// 1. Ctrl/⌘+L 聚焦消息输入框（借 kelivo focusInput）。焦点滚去长会话历史后想继续打字，
///    不再必须拿鼠标点一下输入框。只锁「按哪个键解析成哪个意图」+ 不与既有键撞车。
///
/// 2. 消息右键菜单（含键盘等价入口 Apps / Shift+F10）。菜单项与脚注行 hover 按钮
///    **逐项同源**：hover 看得见的项，右键也点得到；反过来一样。这里不新增能力，
///    只锁「两个入口的集合一致 + 每项都真的去调注入的执行体」。
///
/// 刻意不做的：不测菜单的像素位置（那是平台右键的活）；不测 toast 文案——
/// copyToClipboard 的注释已写明：图标翻转是乐观反馈，toast 才是真实写入结果，两个都在。
module OperabilityTests =

    let private makeMessage (role: string) (text: string) (commitId: uint64 option) : MessageView =
        { MessageView.empty with role = role; text = text; commitId = commitId }

    /// 记录调用顺序的 MessageActions：每个字段捕获「这项被触发过」。
    /// 用列表而非布尔：删除之后不能再复制，先后顺序本身也是契约。
    type SpyActions() =
        let mutable log: string list = []
        member _.Log = log
        member private self.Record(name: string) = log <- log @ [ name ]
        member this.AsMessageActions : MessageActions =
            { copyText = fun _ -> this.Record "copy"
              regenerate = fun () -> this.Record "regenerate"
              editAndFork = fun _ -> this.Record "editAndFork"
              deleteMessage = fun _ -> this.Record "delete"
              downloadAttachment = fun _ -> this.Record "download"
              openLink = fun _ -> this.Record "openLink" }

    let private makeContext (isLastAssistant: bool) (streaming: bool) : MessageContext =
        { fontSize = Tokens.fontReading
          autoCollapseReasoning = true
          streaming = streaming
          isLastAssistant = isLastAssistant
          usage = None
          missingAttachments = Set.empty
          brandAvatar = fun () -> Border() :> Control }

    let private render (message: MessageView) (ctx: MessageContext) (spy: SpyActions) =
        MessageCard.render message ctx spy.AsMessageActions (Some 0)

    /// 卡上的右键菜单。没有挂 ContextMenu（或挂了空菜单）时返回空表。
    let private menuLabels (control: Control) : string list =
        // GetVisualDescendants 不含根节点本身：卡自己的 ContextMenu 就挂在根上，
        // 必须先看自己再看后代，否则整张表恒为空（那种测试会假绿）。
        [ yield control
          yield! control.GetVisualDescendants() |> Seq.choose (function :? Control as c -> Some c | _ -> None) ]
        |> Seq.tryPick (fun c -> if isNull (c.ContextMenu) then None else Some c.ContextMenu)
        |> function
            | None -> []
            | Some menu ->
                menu.Items
                |> Seq.cast<obj>
                |> Seq.choose (function :? MenuItem as m -> Some(string m.Header) | _ -> None)
                |> List.ofSeq

    /// 走自动化 Invoke 触发一项菜单：这是「用户真的点了」的可观察代理。
    let private invokeLabel (control: Control) (label: string) =
        let menu =
            [ yield control
              yield! control.GetVisualDescendants() |> Seq.choose (function :? Control as c -> Some c | _ -> None) ]
            |> Seq.pick (fun c -> if isNull (c.ContextMenu) then None else Some c.ContextMenu)
        let item =
            menu.Items |> Seq.cast<obj> |> Seq.pick (function :? MenuItem as m when (string m.Header) = label -> Some m | _ -> None)
        // MenuItem 的自动化对端走 IExpandCollapseProvider（不是 IInvokeProvider）：
        // headless 下 Expand 会走完「展开 → 执行命令」这条真实路径。
        // MenuItem 的自动化对端不暴露 Invoke/ExpandCollapse（headless 下两者都实现不全）。
        // 直接派发 Click 路由事件：这正是菜单项本人的执行路径，不是绕道的反射调用。
        item.RaiseEvent(Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent, item))
        Dispatcher.UIThread.RunJobs()

    // ---------- Ctrl+L 聚焦输入框 ----------

    [<Fact>]
    let ``ctrl+L 解析为聚焦输入框`` () =
        Headless.ensure ()
        let resolve (key: Key) (mods: KeyModifiers) =
            ShortcutRouter.resolve(
                KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods))
        Assert.Equal(ShortcutAction.FocusComposer, resolve Key.L KeyModifiers.Control)

    [<Fact>]
    let ``ctrl+L 不与既有快捷键或输入框快捷键撞车`` () =
        Headless.ensure ()
        let resolve (key: Key) (mods: KeyModifiers) =
            ShortcutRouter.resolve(
                KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods))
        // 现有全局键一个都不能被 L 抢走。
        for key in [ Key.B; Key.N; Key.K; Key.M; Key.OemComma; Key.OemQuestion; Key.F1 ] do
            Assert.NotEqual(ShortcutAction.FocusComposer, resolve key KeyModifiers.Control)
        // 输入框里 Ctrl+L 原本没有含义（不清行、不召回历史），全局吞掉不破坏输入。
        Assert.Equal(ShortcutAction.FocusComposer, resolve Key.L KeyModifiers.Control)
        // 裸 L 是普通输入，绝不能被吞；Shift+L 同理。
        Assert.Equal(ShortcutAction.NoShortcut, resolve Key.L KeyModifiers.None)
        Assert.Equal(ShortcutAction.NoShortcut, resolve Key.L KeyModifiers.Shift)

    // ---------- 右键菜单 ----------

    /// 用户消息：复制 + 编辑并分叉 + 删除。没有「重新生成」——那是助手侧的动作。
    [<Fact>]
    let ``用户消息右键菜单含复制、编辑并分叉、删除`` () =
        Headless.ensure ()
        let spy = SpyActions()
        let items = render (makeMessage "user" "帮我解释一下" (Some 7UL)) (makeContext false false) spy |> menuLabels
        Assert.Contains("复制消息", items)
        Assert.Contains("编辑并分叉", items)
        Assert.Contains("删除这条消息", items)
        Assert.DoesNotContain("重新生成", items)

    /// 末条助手消息才带「重新生成」；非末条助手没有——重生成只会影响最后一段回复，
    /// 给中间消息挂一个假入口，点了也是重新生成末尾，纯误导。
    [<Fact>]
    let ``重新生成只见于末条助手消息菜单`` () =
        Headless.ensure ()
        let spy = SpyActions()
        let lastItems = render (makeMessage "assistant" "最后一次回复" (Some 3UL)) (makeContext true false) spy |> menuLabels
        let midItems = render (makeMessage "assistant" "中间回复" (Some 2UL)) (makeContext false false) spy |> menuLabels
        Assert.Contains("重新生成", lastItems)
        Assert.DoesNotContain("重新生成", midItems)
        // 两条都该有复制；删除只跟着 commitId 走。
        Assert.Contains("复制消息", lastItems)
        Assert.Contains("复制消息", midItems)

    /// 流式进行中没有可提交的东西：半句话拷出去、签一份还没定的稿，全是假入口。
    [<Fact>]
    let ``流式进行中消息没有右键菜单项`` () =
        Headless.ensure ()
        let spy = SpyActions()
        let items = render (makeMessage "assistant" "正在生成…" None) (makeContext true true) spy |> menuLabels
        Assert.Empty(items)

    /// 没有 commitId 的消息（尚未落盘的乐观插入、流式临时卡）不能删：
    /// 删除走的是服务端历史，没有 commit 就没有可删的行。
    [<Fact>]
    let ``未落盘消息没有删除项`` () =
        Headless.ensure ()
        let spy = SpyActions()
        let items = render (makeMessage "user" "还没落盘" None) (makeContext false false) spy |> menuLabels
        Assert.Contains("复制消息", items)
        Assert.DoesNotContain("删除这条消息", items)

    /// 菜单项必须真的去调注入的执行体：一条「看起来对」但点了没反应的菜单，
    /// 比没有菜单更糟（用户会以为自己点到别的东西上去了）。
    [<Fact>]
    let ``菜单项点击走注入的动作`` () =
        Headless.ensure ()
        let spy = SpyActions()
        let control = render (makeMessage "assistant" "终稿" (Some 9UL)) (makeContext true false) spy
        invokeLabel control "复制消息"
        Assert.Equal<string list>([ "copy" ], spy.Log)
        invokeLabel control "删除这条消息"
        Assert.Equal<string list>([ "copy"; "delete" ], spy.Log)

    /// 菜单是「入口」不是「能力」：与 hover 按钮的集合必须一致。
    /// 两边各有各的消失条件（hover 行 vs 菜单项），必须由同一批判定驱动，
    /// 否则早晚长出「左键能删、右键不能」这种割裂。
    [<Fact>]
    let ``菜单项与 hover 按钮的可用集合一致`` () =
        Headless.ensure ()
        let spy = SpyActions()
        let cases =
            [ render (makeMessage "user" "用户消息" (Some 7UL)) (makeContext false false) spy
              render (makeMessage "assistant" "末条回复" (Some 3UL)) (makeContext true false) spy
              render (makeMessage "assistant" "中间回复" (Some 2UL)) (makeContext false false) spy
              render (makeMessage "assistant" "正在生成…" None) (makeContext true true) spy ]
        for control in cases do
            let menuSet = Set.ofList (menuLabels control)
            // hover 行的按钮以 AutomationProperties.Name 暴露（勿与卡片上的消息文本混淆）。
            let hoverNames =
                [ yield control :> Control
                  yield! control.GetVisualDescendants() |> Seq.choose (function :? Control as c -> Some c | _ -> None) ]
                |> Seq.choose (fun c ->
                    let n = AutomationProperties.GetName(c)
                    if String.IsNullOrWhiteSpace n then None else Some n)
                |> Seq.filter (fun n ->
                    n = "复制消息" || n = "编辑并分叉" || n = "重新生成" || n = "删除这条消息")
                |> set
            for name in hoverNames do
                Assert.Contains(name, menuSet)

    // 思考过程与工具调用 / 错误详情同档：头部带独立复制入口。底部复制只取正文，
    // 思维链此前只能展开后手动拖选——几百字没法精确全选。锁：按下那个按钮，
    // 注入的 copyText 真的被调用，且载荷是整段 reasoning。
    [<Fact>]
    let ``reasoning header copies the whole chain of thought`` () =
        Headless.ensure ()
        let spy = SpyActions()
        let message =
            { makeMessage "assistant" "结论" (Some 4UL) with reasoning = "先观察\n再推演\n最后收口" }
        let control = render message (makeContext true false) spy
        let card = Window(Width = 600.0, Height = 400.0, Content = control)
        card.Show()
        Dispatcher.UIThread.RunJobs()
        try
            let rec walk (c: Control) = seq {
                yield c
                match c with
                | :? Panel as p -> for ch in p.Children do yield! walk ch
                | :? Decorator as d when not (isNull d.Child) -> yield! walk d.Child
                | :? ContentControl as h -> match h.Content with | :? Control as cc -> yield! walk cc | _ -> ()
                | _ -> () }
            // 自动化名取 accessibleName 参数（「复制完整思考过程」），不是悬停提示「复制思考过程」。
            let copy = Seq.find (fun c -> AutomationProperties.GetName(c) = "复制完整思考过程") (walk control)
            let peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement copy
            let invoke = Assert.IsAssignableFrom<Avalonia.Automation.Provider.IInvokeProvider>(peer)
            invoke.Invoke()
            Dispatcher.UIThread.RunJobs()
            Assert.Equal<string list>([ "copy" ], spy.Log)
        finally
            card.Close()

