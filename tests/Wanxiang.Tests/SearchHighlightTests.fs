module Wanxiang.Tests.SearchHighlightTests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Controls.Primitives
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.VisualTree
open Xunit
open Wanxiang.Tests
open Wanxiang.UI

/// 搜索命中高亮。
///
/// 侧栏搜索此前只按纯文本过滤，标题里看不到匹配到了哪里：扫一眼列表还得自己
/// 逐行找哪个词命中了。这里锁定三件事——
///
/// 1. 分段是纯函数，口径与 ConversationSummary.matches 严格一致（分词、忽略大小写）；
/// 2. 行渲染与回收复用两条路径都上色（只做新行渲染的话，复用行一刷就丢高亮）；
/// 3. 命中色在两种调色板、标题与预览两级字重下都达到可读对比度。
///
/// 不锁的：高亮的像素位置与具体字形（那是渲染后端的事）；也不锁「哪些字段
/// 参与匹配」——那是 matches 的契约，这里只锁「能匹配到就能看见」。
let rec private descendants (control: Control) = seq {
    yield control
    match control with
    | :? Panel as panel ->
        for child in panel.Children do yield! descendants child
    | :? Decorator as decorator when not (isNull decorator.Child) ->
        yield! descendants decorator.Child
    | :? ContentControl as host ->
        match host.Content with
        | :? Control as child -> yield! descendants child
        | _ -> ()
    | _ -> ()
}

/// 侧栏行由虚拟化 ListBox 托管，不在 root 的视觉树里：经已实现容器取。
/// 行主机由 RenderRow 打上 Tag = ConversationSummary，据此锁定 Sidebar 自建行。
let private rowByName (sidebar: Sidebar) (title: string) =
    let rec visualControls (visual: Visual) =
        seq {
            match visual with
            | :? Control as control -> yield control
            | _ -> ()
            for child in visual.GetVisualChildren() do
                yield! visualControls child
        }
    let list =
        descendants sidebar
        |> Seq.pick (function :? ListBox as lb -> Some lb | _ -> None)
    list.GetRealizedContainers()
    |> Seq.collect visualControls
    |> Seq.find (fun c ->
        AutomationProperties.GetName(c) = title
        && match c with
           | :? Border as border ->
               match border.Tag with
               | :? ConversationSummary -> true
               | _ -> false
           | _ -> false)

/// 从行主机里取出标题 TextBlock（结构判断按索引：dock[0] 状态槽 / dock[1] more /
/// dock[2] 标题，column[1] 是预览）。索引口径与 RefreshRowHost 复用判断一致，
/// 因此这里取不到块的时候，那条复用路径也早就失效了。
let private titleBlockOf (row: Control) =
    match row with
    | :? Border as host ->
        match host.Child with
        | :? StackPanel as column when column.Children.Count >= 2 ->
            match column.Children.[0] with
            | :? DockPanel as titleRow when titleRow.Children.Count >= 3 ->
                match titleRow.Children.[2] with
                | :? TextBlock as block -> Some block
                | _ -> None
            | _ -> None
        | _ -> None
    | _ -> None

let private previewBlockOf (row: Control) =
    match row with
    | :? Border as host ->
        match host.Child with
        | :? StackPanel as column when column.Children.Count >= 2 ->
            match column.Children.[1] with
            | :? TextBlock as block -> Some block
            | _ -> None
        | _ -> None
    | _ -> None


/// 命中片段的文本：判据是「这个 run 的前景画的就是 accent」。
///
/// 不能用 isNull(run.Foreground) 判断：Avalonia 的 Foreground 是可继承属性，
/// 子 run 不显式设置时会解析成 TextBlock 的 Foreground，永远非 null。
/// 用户看得见的行为是「命中词被涂成强调色」，所以就照这个来断言。
let private highlightedRuns (palette: Palette) (block: TextBlock) =
    let accent = Tokens.accent  // 主题解析后的当前值
    ignore palette
    block.Inlines
    |> Seq.choose (function :? Run as r -> Some r | _ -> None)
    |> Seq.filter (fun r -> r.Foreground = accent)
    |> Seq.map (fun r -> r.Text)
    |> List.ofSeq

let private show (content: Control) width height =
    Headless.ensure ()
    let window = Window(Width = width, Height = height, Content = content)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    window

let private summary id title preview pinned =
    { id = id
      title = title
      preview = preview
      running = false
      pinned = pinned
      archived = false
      createdAt = DateTimeOffset.Now
      updatedAt = DateTimeOffset.Now
      messageCount = 2
      isFork = false
      providerId = "openai"
      model = "gpt"
      lastCommitId = 1UL }

/// 建一个带真实 OverlayHost 的 Sidebar：搜索框、行渲染、复用路径全部走真代码。
let private buildSidebar () =
    Headless.ensure ()
    let root = Grid()
    let overlay = OverlayHost(root)
    let actions: SidebarActions =
        { newConversation = ignore
          openConversation = ignore
          renameConversation = ignore
          deleteConversation = ignore
          setPinned = fun _ _ -> ()
          setArchived = fun _ _ -> ()
          selectionAllPinned = fun _ -> false
          setPinnedMany = fun _ _ -> ()
          setArchivedMany = fun _ _ -> ()
          deleteMany = fun _ -> ()
          duplicateAsFork = ignore
          exportConversation = ignore
          openSettings = ignore
          reconnect = ignore
          toggleArchivedVisibility = ignore
          closeNavigation = ignore }
    root, Sidebar(overlay, actions, fun _ -> Border() :> Control)

let private searchBoxOf (sidebar: Sidebar) =
    descendants sidebar
    |> Seq.pick (function :? TextBox as tb -> Some tb | _ -> None)

/// 写查询词并用一次快照刷新驱动列表重排。
///
/// 为什么不直接等防抖：本套件是 SetupWithoutStarting + RunJobs 的无显示制度，
/// DispatcherTimer 不可确定泵出（PresentationRefreshTests 有同样的确定性注释），
/// 靠 Sleep 等 tick 只能拿到防抖前的旧行。Rebuild 每次都会现读 searchBox.Text，
/// 所以这里写文本 + 灌同一份快照，走的仍是渲染与高亮的真实代码路径。
let private typeQuery (sidebar: Sidebar) (query: string) (items: ConversationSummary list) =
    let box = searchBoxOf sidebar
    box.Text <- query
    sidebar.SetConversations items
    Dispatcher.UIThread.RunJobs()

// =========================================================================
// 1. 纯分段函数
// =========================================================================

[<Fact>]
let ``empty query yields one plain segment covering the whole text`` () =
    let spans = SidebarText.segments "" "万象 会话标题"
    Assert.Equal<string list>([ "万象 会话标题" ], spans |> List.map _.text)
    Assert.False(spans |> List.forall _.isMatch)

[<Fact>]
let ``whitespace-only query is treated as no query`` () =
    // 与 ConversationSummary.matches 同口径：纯空白查询放行所有行，
    // 所以也不该有任何高亮——否则列表会出现「没有任何查询却到处上色」。
    let spans = SidebarText.segments "   " "随便一段文本"
    Assert.Equal<string list>([ "随便一段文本" ], spans |> List.map _.text)
    Assert.False(spans |> List.forall _.isMatch)

[<Fact>]
let ``single term highlights only the matched substring, keeping original casing`` () =
    let spans = SidebarText.segments "模型" "万象 模型 设计草案"
    // 命中片段保留原文，不是查询词本身；大小写不敏感但内容不改写。
    Assert.Equal<string list>([ "万象 "; "模型"; " 设计草案" ], spans |> List.map _.text)
    Assert.Equal<bool list>([ false; true; false ], spans |> List.map _.isMatch)

[<Fact>]
let ``segments always reassemble the original text exactly`` () =
    // 分段一旦漏字或重复取，渲染出来的标题就会和真实标题不一致——这是最直觉的回归面。
    let text = "存储模型的方案讨论与回顾"
    for query in [ "存储"; "存储 方案"; "方案 存储"; "型"; "讨论 回顾"; "xyz"; "存储 存储" ] do
        let spans = SidebarText.segments query text
        Assert.Equal(text, spans |> List.map _.text |> String.concat "")

[<Fact>]
let ``every term that matched the row is highlighted`` () =
    // 分段口径必须与 ConversationSummary.matches 对齐：matches 用「所有词都命中」
    // 过滤，这里就把这些词全部标出来。两个口径若漂移，会出现
    // 「这行被搜出来了却看不出哪里匹配」。
    let title = "万象模型选型记录"
    Assert.True(ConversationSummary.matches "模型 选型" { id = Guid.NewGuid(); title = title; preview = ""; running = false; pinned = false; archived = false; createdAt = DateTimeOffset.Now; updatedAt = DateTimeOffset.Now; messageCount = 1; isFork = false; providerId = ""; model = ""; lastCommitId = 1UL })
    let highlighted =
        SidebarText.segments "模型 选型" title
        |> List.filter _.isMatch
        |> List.map _.text
    Assert.Equal<string list>([ "模型"; "选型" ], highlighted)

[<Fact>]
let ``adjacent matches cover the whole text without gaps`` () =
    // 「存储」「模型」挨着命中：合并后不再夹一段普通文字，整块都是高亮。
    // 断言口径是「高亮片段拼回去等于原文」——分成一个还是两个 run 是渲染细节，
    // 「有没有漏出普通文字」才是用户看得见的行为。
    let spans = SidebarText.segments "存储 模型" "存储模型"
    Assert.Equal<string list>([ "存储"; "模型" ], spans |> List.map _.text)
    Assert.True(spans |> List.forall _.isMatch)
    Assert.Equal("存储模型", spans |> List.filter _.isMatch |> List.map _.text |> String.concat "")

[<Fact>]
let ``overlapping occurrences of one term are not double counted`` () =
    // 「aa」在「aaaa」里出现两次且区域重叠：合并后是两个不重叠命中，
    // 文本被完整覆盖一次，没有哪一段被算两遍（否则渲染会重复上色、拼接会多字）。
    let spans = SidebarText.segments "aa" "aaaa"
    Assert.Equal("aaaa", spans |> List.map _.text |> String.concat "")
    Assert.Equal<int>(2, spans |> List.filter _.isMatch |> List.length)

[<Fact>]
let ``a term that does not occur leaves the text unstyled`` () =
    // matches 会否掉这一行；万一调用方仍渲染它（例如调用顺序不同），
    // 也不能出现「什么都没匹配却整行上色」。
    let spans = SidebarText.segments "不存在的词" "万象 会话"
    Assert.Equal<string list>([ "万象 会话" ], spans |> List.map _.text)
    Assert.False(spans |> List.forall _.isMatch)

[<Fact>]
let ``terms helper splits on spaces and tabs and drops duplicates`` () =
    Assert.Equal<string list>([ "甲"; "乙" ], SidebarText.terms "甲\t乙 甲")
    Assert.Equal<string list>([], SidebarText.terms "   ")

// =========================================================================
// 2. 真实行渲染（含回收复用）
// =========================================================================

[<Fact>]
let ``title and preview both highlight while searching`` () =
    let id = Guid.NewGuid()
    let root = Grid()
    let _, sidebar = buildSidebar ()
    // Build 生成搜索框与列表（Build 与否决定整棵树是否存在），再挂窗灌数据。
    sidebar.Build()
    let window = show root 320.0 420.0
    root.Children.Add sidebar
    let items = [ summary id "万象模型选型" "第二行的模型摘要" false ]
    try
        sidebar.SetConversations items
        Dispatcher.UIThread.RunJobs()
        typeQuery sidebar "模型" items
        let row = rowByName sidebar "万象模型选型"

        let title = titleBlockOf row |> Option.get
        Assert.Equal<string list>([ "模型" ], highlightedRuns Palette.light title)

        let preview = previewBlockOf row |> Option.get
        Assert.Equal<string list>([ "模型" ], highlightedRuns Palette.light preview)
    finally
        window.Close ()

[<Fact>]
let ``clearing the query removes every highlight`` () =
    let id = Guid.NewGuid()
    let root = Grid()
    let _, sidebar = buildSidebar ()
    // Build 生成搜索框与列表（Build 与否决定整棵树是否存在），再挂窗灌数据。
    sidebar.Build()
    let window = show root 320.0 420.0
    root.Children.Add sidebar
    try
        sidebar.SetConversations [ summary id "万象模型选型" "模型摘要" false ]
        Dispatcher.UIThread.RunJobs()
        typeQuery sidebar "模型" [ summary id "万象模型选型" "模型摘要" false ]
        let row = rowByName sidebar "万象模型选型"
        let title = titleBlockOf row |> Option.get
        Assert.Equal<string list>([ "模型" ], highlightedRuns Palette.light title)

        typeQuery sidebar "" [ summary id "万象模型选型" "模型摘要" false ]
        let rowAfter = rowByName sidebar "万象模型选型"
        let titleAfter = titleBlockOf rowAfter |> Option.get
        // 清空后一个命中都不剩，且整段文本一次恢复（不被截断或拼接错）。
        Assert.Equal<string list>([], highlightedRuns Palette.light titleAfter)
        Assert.Equal("万象模型选型", String.concat "" (titleAfter.Inlines |> Seq.choose (function :? Run as r -> Some r | _ -> None) |> Seq.map (fun r -> r.Text)))
    finally
        window.Close ()

[<Fact>]
let ``recycled rows keep their highlight after a snapshot refresh`` () =
    // 只在新行渲染时上色的话，刷新会走 RefreshRowHost，那条路径先把 title.Text 重新赋值
    // （整块替换 Inlines），高亮就消失了。这里专门锁复用路径也重放高亮。
    let id = Guid.NewGuid()
    let root = Grid()
    let _, sidebar = buildSidebar ()
    // Build 生成搜索框与列表（Build 与否决定整棵树是否存在），再挂窗灌数据。
    sidebar.Build()
    let window = show root 320.0 420.0
    root.Children.Add sidebar
    try
        // 查询词先进箱，再灌第一份快照：行首次渲染时就带着高亮。
        let (box: TextBox) = searchBoxOf sidebar
        box.Text <- "模型"
        sidebar.SetConversations [ summary id "原模型标题" "原摘要" false ]
        Dispatcher.UIThread.RunJobs()
        // 同一 id 的新快照（模拟置顶切换 / 生成状态翻转触发的原地刷新）：
        // 复用路径 RefreshRowHost 必须重放高亮，而不是丢掉它。
        sidebar.SetConversations [ summary id "新的模型标题" "新的模型摘要" false ]
        Dispatcher.UIThread.RunJobs()

        let row = rowByName sidebar "新的模型标题"
        let title = titleBlockOf row |> Option.get
        Assert.Equal<string list>([ "模型" ], highlightedRuns Palette.light title)
    finally
        window.Close ()

// =========================================================================
// 3. 命中色可读性
// =========================================================================

[<Fact>]
let ``search highlight keeps readable contrast in both palettes`` () =
    let relativeLuminance (color: Color) =
        let channel (value: byte) =
            let c = float value / 255.0
            if c <= 0.03928 then c / 12.92 else Math.Pow((c + 0.055) / 1.055, 2.4)
        0.2126 * channel color.R + 0.7152 * channel color.G + 0.0722 * channel color.B
    let contrast foreground background =
        let a = relativeLuminance foreground
        let b = relativeLuminance background
        (max a b + 0.05) / (min a b + 0.05)
    for palette in [ Palette.light; Palette.dark ] do
        // 命中色就是 Tokens.accent：不加第二套「高亮色」token。
        // 标题是 Medium 字重的小字（fontSmall），按 WCAG AA 正文 4.5 要求。
        Assert.True(contrast palette.accent palette.canvas >= 4.5)
        Assert.True(contrast palette.accent palette.surface >= 4.5)
        // 选中行（当前会话）底色也要能压住命中色，否则选中的那行高亮会糊。
        Assert.True(contrast palette.accent palette.selected >= 4.5)
