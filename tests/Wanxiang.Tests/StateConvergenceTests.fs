module Wanxiang.Tests.StateConvergenceTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Wanxiang.Tests

/// 状态视觉收敛的判定装置。
///
/// 放在模块层而非 `type` 内：F# 的 `type` 里 `let` 恒为私有且不接受 `private`
/// 修饰符，而这几个装置要被同文件的多个 `[<Fact>]` 复用。
module StateScan =

    let repoRoot () =
        let mutable dir = DirectoryInfo(Directory.GetCurrentDirectory())
        let mutable found = isNull dir
        while not found && not (File.Exists(Path.Combine(dir.FullName, "wanxiang.slnx"))) do
            dir <- dir.Parent
            found <- isNull dir
        if found then "." else dir.FullName

    /// 视觉改写信号：事件体里直接写这些属性。
    let visualSignal = Regex(@"\.(Background|BorderBrush|BoxShadow|Opacity|Foreground)\s*<-")

    let eventLine =
        Regex(@"\.(GotFocus|LostFocus|PointerEntered|PointerExited|PointerPressed|PointerReleased)\.Add")

    /// 取事件订阅的整个函数体（按缩进收敛到事件行之后）。
    let eventBody (lines: string[]) (index: int) =
        let anchor = lines.[index]
        let indent = anchor.Length - anchor.TrimStart().Length
        let body = ResizeArray<string>()
        body.Add anchor
        let mutable j = index + 1
        let mutable stop = false
        while j < lines.Length && not stop do
            let line = lines.[j]
            let stripped = line.Trim()
            let lineIndent = line.Length - line.TrimStart().Length
            let isComment = stripped.StartsWith("//")
            let continues = stripped.Length > 0 && lineIndent <= indent && not isComment
            if continues then
                stop <- true
            else
                body.Add line
                j <- j + 1
        String.Join("\n", body)

    /// 统计某文件里"事件体内改视觉"的处数。
    let countIn (path: string) =
        let lines = File.ReadAllLines path
        let mutable count = 0
        for i in 0 .. lines.Length - 1 do
            if eventLine.IsMatch lines.[i] then
                if visualSignal.IsMatch (eventBody lines i) then
                    count <- count + 1
        count

    /// 列出某文件里"事件体内改视觉"的位置。
    let listIn (path: string) =
        let lines = File.ReadAllLines path
        let found = ResizeArray<string>()
        for i in 0 .. lines.Length - 1 do
            if eventLine.IsMatch lines.[i] then
                if visualSignal.IsMatch (eventBody lines i) then
                    let where = sprintf "%d" (i + 1)
                    let what = lines.[i].Trim()
                    found.Add(sprintf "%s  %s" where what)
        List.ofSeq found

    /// 只扫**消费侧**（Views / Controls / Rich）。`Design/` 是令牌定义处——
    /// 字号 / 行高 / 字重的裸值在那里是**定义**，不是违规。
    let consumerFiles () =
        let root = Path.Combine(repoRoot (), "src", "Wanxiang.UI")
        let found = Directory.GetFiles(root, "*.fs", SearchOption.AllDirectories)
        found
        |> Array.filter (fun f ->
            let name = Path.GetFileName f
            name <> "Interaction.fs" && not (name.EndsWith(".g.fs")))
        |> Array.filter (fun f ->
            let parts = Path.GetFullPath f
            parts.Split([| Path.DirectorySeparatorChar |]) |> Array.contains "Design" |> not)
        |> Array.sort

    let sourceFiles () =
        let root = Path.Combine(repoRoot (), "src", "Wanxiang.UI")
        let found = Directory.GetFiles(root, "*.fs", SearchOption.AllDirectories)
        found |> Array.filter (fun f -> Path.GetFileName f <> "Interaction.fs") |> Array.sort

    /// 相对 `src/` 的路径（`Wanxiang.UI/Views/...`），与白名单同一坐标。
    let relativeOf (file: string) =
        let full = Path.GetFullPath file
        let parts = full.Split([| Path.DirectorySeparatorChar |])
        let index = parts |> Array.tryFindIndex (fun p -> p = "src")
        match index with
        | Some i when i + 1 < parts.Length ->
            String.Join("/", parts[(i + 1) ..])
        | _ -> Path.GetFileName full

/// 控件状态视觉的**收敛守卫**。
///
/// 规范（`.agents/skills/wanxiang-ui/SKILL.md` §4.1）：hover / focus / pressed /
/// disabled 的视觉 MUST 由 Avalonia 原生伪类驱动（`Interaction` 注册的样式表），
/// **MUST NOT** 手写 `GotFocus` / `PointerEntered` / `PointerPressed` 去改视觉属性。
///
/// 本文件把这条规则钉成三道门：
/// 1. **白名单外零视觉副本**——只有 `valuePaths` 里的文件可以写视觉。
/// 2. **白名单条目数封顶**——白名单文件里剩下的每处都计入上限，防止豁免范围悄悄扩大。
/// 3. **每条豁免都有理由**——没有理由的白名单等于没有约束。
///
/// 允许存在的**值路径**（业务事实或非表面视觉，伪类表达不了）：
/// - 聚焦 / 校验描边：`Tokens` 的笔是「切主题时原地改色」的活对象，
///   进 `BrushTransition` 会被补间改写并与全局令牌脱钩（实测红框读到中间色
///   `#ffd2cabc` 而非终点 `#ff8e3b2f`，切主题后不再变红）。
/// - hover 底依赖业务状态（会话行选中 / 运行中、附件行已选）。
/// - 非表面视觉：改 `TextBlock` 内联 `Run.Foreground`，不是控件表面状态。
module StateConvergenceTests =

    /// 排版裸值扫描器：`FontWeight` / `LetterSpacing` / `FontSize` / `LineHeight`
    /// 只能读令牌，不得写数字。
    ///
    /// `LineHeight` 允许两种非令牌形态，因为行高有两类来源：
    /// - `ReadingRhythm.*`（令牌，比例派生）
    /// - 由 `字号 × 比例` 现场算出（`let lh = size * 1.62`）——这是排版公式本身
    ///   的表达，不是裸值；此时 `LineHeight = lh` 里的 `lh` 是局部绑定。
    let private typeScaleValue = Regex(@"FontSize\s*=\s*([0-9.]+)")
    let private weightValue = Regex(@"FontWeight\s*=\s*FontWeight\.([A-Za-z]+)")
    let private lineHeightValue = Regex(@"LineHeight\s*=\s*([0-9.]+)\s*$")

    /// 豁免清单：`源文件相对路径 -> (视觉副本处数上限, 理由)`。
    ///
    /// 处数是**上限**不是期望值：文件被重构得更干净时（副本变少）不算失败——
    /// 那正是目标；变多才失败（说明有人在白名单文件里新写了状态视觉）。
    let private valuePaths : (string * int * string) list =
        [
            "Wanxiang.UI/Views/ChatView.fs", 4,
            "标题编辑壳的聚焦 / hover 描边是值路径：描边色随 Tokens 切主题变化，\
             进补间会与全局令牌脱钩（见 Interaction.setFocusedStroke 的实测记录）"

            "Wanxiang.UI/Views/Composer.fs", 2,
            "model chip 的聚焦描边是值路径：ActionBorder 已有外环阴影，这里补的是\
             与 hover 底色正交的**描边色**，表达「键盘焦点在此」而非表面状态"

            "Wanxiang.UI/Views/MessageCard.fs", 2,
            "附件行内联文字的 hover 变色改的是 `Run.Foreground`——Markdown 内联渲染\
             的产物，不是控件表面；伪类无法按内联片段选择"

            "Wanxiang.UI/Views/Sidebar.fs", 2,
            "会话行的 hover 底依赖 `isActive` / `isSelected`（选中 / 运行中 / 生成中）\
             ——业务状态，伪类表达不了；`:pointerover` 只负责无状态那一档"
        ]

    [<Fact>]
    let ``状态视觉只走 Interaction 样式表白名单外的文件零副本`` () =
        let root = StateScan.repoRoot ()
        let allowed = valuePaths |> List.map (fun (p, _, _) -> p) |> Set.ofList
        let violations = ResizeArray<string>()

        for file in StateScan.sourceFiles () do
            let rel = StateScan.relativeOf file
            if not (allowed.Contains rel) then
                for item in StateScan.listIn file do
                    violations.Add(sprintf "%s  %s" rel item)

        Assert.True(
            violations.Count = 0,
            "状态视觉必须走 Interaction 的原生伪类（规范 §4.1）。白名单外的视觉副本：\n  "
            + String.Join("\n  ", violations))

    [<Fact>]
    let ``值路径白名单不会扩大`` () =
        let root = StateScan.repoRoot ()
        for (rel, cap, reason) in valuePaths do
            let path = Path.Combine(root, "src", rel.Replace('/', Path.DirectorySeparatorChar))
            Assert.True(
                File.Exists path,
                sprintf "白名单里的 %s 不存在（文件被重命名或删除？）——同步更新 StateConvergenceTests.valuePaths" rel)

            let count = StateScan.countIn path
            Assert.True(
                count <= cap,
                sprintf "%s 的视觉副本有 %d 处，白名单上限 %d。新增若属合法值路径，请更新上限并写明理由：%s"
                    rel count cap reason)

    [<Fact>]
    let ``每条豁免都有理由`` () =
        for (rel, _, reason) in valuePaths do
            Assert.False(
                String.IsNullOrWhiteSpace reason,
                sprintf "%s 的豁免理由为空——白名单条目必须写明为什么伪类表达不了" rel)
            Assert.True(
                reason.Length >= 20,
                sprintf "%s 的豁免理由太短（%d 字），说清判据" rel reason.Length)

    // ---------- 排版契约（规范 §3）----------

    [<Fact>]
    let ``字号一律走 Tokens 不写裸值`` () =
        let root = StateScan.repoRoot ()
        let bad = ResizeArray<string>()
        for file in StateScan.consumerFiles () do
            let rel = StateScan.relativeOf file
            let lines = File.ReadAllLines file
            for i in 0 .. lines.Length - 1 do
                let m = typeScaleValue.Match lines.[i]
                if m.Success then
                    let where = sprintf "%s:%d" rel (i + 1)
                    let what = lines.[i].Trim()
                    bad.Add(sprintf "%s  %s" where what)
        Assert.True(
            bad.Count = 0,
            "字号必须走 Tokens.font*（规范 §3.2）：\n  " + String.Join("\n  ", bad))

    [<Fact>]
    let ``字重只有 Normal 与 Medium 两档`` () =
        // 规范 §3.4 / kami 规则 5：层级靠字号与颜色表达，不靠加粗。
        // SemiBold(600) 与 Bold(700) 一律越界。
        let root = StateScan.repoRoot ()
        let bad = ResizeArray<string>()
        for file in StateScan.consumerFiles () do
            let rel = StateScan.relativeOf file
            let lines = File.ReadAllLines file
            for i in 0 .. lines.Length - 1 do
                let m = weightValue.Match lines.[i]
                if m.Success then
                    let name = m.Groups.[1].Value
                    if name <> "Normal" && name <> "Medium" then
                        let where = sprintf "%s:%d" rel (i + 1)
                        let what = lines.[i].Trim()
                        bad.Add(sprintf "%s  FontWeight.%s" where name)
        Assert.True(
            bad.Count = 0,
            "字重只有 Normal(400) / Medium(500) 两档（规范 §3.4）：\n  " + String.Join("\n  ", bad))

    [<Fact>]
    let ``行高一律走 ReadingRhythm 或由字号派生`` () =
        // 规范 §3.3：行高是 `字号 × 比例` 或 ReadingRhythm 固定档，不写裸数字。
        let root = StateScan.repoRoot ()
        let bad = ResizeArray<string>()
        for file in StateScan.consumerFiles () do
            let rel = StateScan.relativeOf file
            let lines = File.ReadAllLines file
            for i in 0 .. lines.Length - 1 do
                let m = lineHeightValue.Match(lines.[i].TrimEnd())
                if m.Success then
                    let where = sprintf "%s:%d" rel (i + 1)
                    let what = lines.[i].Trim()
                    bad.Add(sprintf "%s  %s" where what)
        Assert.True(
            bad.Count = 0,
            "行高必须走 ReadingRhythm 或由字号派生（规范 §3.3）：\n  " + String.Join("\n  ", bad))
