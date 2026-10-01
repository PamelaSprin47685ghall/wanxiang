namespace Wanxiang.UI

open System.Collections.Concurrent
open System.Runtime.CompilerServices
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open IconPacks.Avalonia.Lucide

/// 图标集：**Lucide 官方集**（ISC）的语义命名映射层。
///
/// 字形几何全部来自官方控件的官方 API：
/// `LucideImageExtension(Kind=…, Brush=…).ProvideValue(null)` 返回
/// `Avalonia.Media.DrawingImage`（实测返回类型即此），我们把它放进原生 `Image`。
///
/// **MUST NOT 自绘路径、MUST NOT 调 `Geometry.Parse`、MUST NOT 自己读 SVG。**
/// 那是把上游已经解决的问题（路径解析、像素网格对齐、视觉重量平衡）在本地重做一遍，
/// 只会得到一个「像 Lucide 但不是 Lucide」的子集。
///
/// ## 为什么不用 `PackIconLucide`（实测结论）
///
/// `PackIconLucide` 是 TemplatedControl，字形靠包内 `Lucide.axaml` 的 ControlTheme
/// 提供。在 Avalonia 12.1.2 上实测：该主题经 `AvaloniaXamlLoader.Load` 载入后是
/// **空 Styles**（0 子项），模板因此缺失，控件渲染为**零墨迹**——产品面表现为
/// 「按钮是个空色块」（`IconCenteringTests` 的墨迹断言抓这个）。
/// 而 `LucideImageExtension` 走的是纯 API 路径，不依赖模板，实测正常出墨。
///
/// ## 画笔是活的
///
/// 实测：返回的 `GeometryDrawing.Pen.Brush` 与我们传入的是**同一个实例**，
/// 因此 `Tokens.apply` 原地改色时图标自动跟随主题，无需重建视图。
///
/// 规范权威：`.agents/skills/wanxiang-ui/SKILL.md` §1。
module Icons =

    /// 图形缓存：`DrawingImage` 每次调用都会新建（实测不复用），
    /// 而它在视图重建时被反复请求，因此按 (画笔实例, 图标) 缓存。
    /// 用 `ConditionalWeakTable` 以**画笔引用**为键：画笔随 Tokens 生命周期，
    /// 令牌被丢弃时缓存自动回收。
    let private cache = ConditionalWeakTable<IBrush, ConcurrentDictionary<PackIconLucideKind, DrawingImage>>()

    let private drawing (kind: PackIconLucideKind) (brush: IBrush) : IImage =
        let perBrush = cache.GetValue(brush, fun _ -> ConcurrentDictionary<PackIconLucideKind, DrawingImage>())
        perBrush.GetOrAdd(
            kind,
            fun k ->
                let extension = LucideImageExtension(Kind = k, Brush = brush)
                extension.ProvideValue(null) :?> DrawingImage)

    /// 构造一个官方图标控件。
    ///
    /// 我们只设三样：画笔（色）、`Width`/`Height`（字形档）、对齐（居中）。
    /// 字形内部如何绘制、在画布内如何定位，全归上游。
    let private ofSize (size: float) (kind: PackIconLucideKind) (brush: IBrush) : Control =
        let image =
            Image(
                Source = drawing kind brush,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform)
        image.HorizontalAlignment <- HorizontalAlignment.Center
        image.VerticalAlignment <- VerticalAlignment.Center
        image :> Control

    /// 标准字形档（`Spacing.Glyph.md` = 16）。全应用绝大多数图标用这一档。
    let private glyph (kind: PackIconLucideKind) (brush: IBrush) : Control =
        ofSize Tokens.iconGlyph kind brush

    /// 小字形档（`Spacing.Glyph.sm` = 14）：chip / tag 内、行内提示。
    let small (kind: PackIconLucideKind) (brush: IBrush) : Control =
        ofSize Spacing.Glyph.sm kind brush

    /// 大字形档（`Spacing.Glyph.lg` = 20）：空态锚点、功能入口。
    let large (kind: PackIconLucideKind) (brush: IBrush) : Control =
        ofSize Spacing.Glyph.lg kind brush

    // ---------- 语义映射 ----------
    // 名的是形状（官方惯例），注释写的是万象里的用途。

    // 名的是形状（官方惯例），注释写的是万象里的用途。

    /// 搜索
    let search : IBrush -> Control = glyph PackIconLucideKind.Search

    /// 新增
    let plus : IBrush -> Control = glyph PackIconLucideKind.Plus

    /// 减少
    let minus : IBrush -> Control = glyph PackIconLucideKind.Minus

    /// 关闭
    let close : IBrush -> Control = glyph PackIconLucideKind.X

    /// 确认
    let check : IBrush -> Control = glyph PackIconLucideKind.Check

    /// 完成（圆圈勾）
    let checkCircle : IBrush -> Control = glyph PackIconLucideKind.CircleCheck

    /// 跳到开头
    let chevronsUp : IBrush -> Control = glyph PackIconLucideKind.ChevronsUp

    /// 跳到结尾
    let chevronsDown : IBrush -> Control = glyph PackIconLucideKind.ChevronsDown

    /// 展开
    let chevronDown : IBrush -> Control = glyph PackIconLucideKind.ChevronDown

    /// 收起
    let chevronUp : IBrush -> Control = glyph PackIconLucideKind.ChevronUp

    /// 下一级
    let chevronRight : IBrush -> Control = glyph PackIconLucideKind.ChevronRight

    /// 上一级
    let chevronLeft : IBrush -> Control = glyph PackIconLucideKind.ChevronLeft

    /// 下移
    let arrowDown : IBrush -> Control = glyph PackIconLucideKind.ArrowDown

    /// 上移
    let arrowUp : IBrush -> Control = glyph PackIconLucideKind.ArrowUp

    /// 返回
    let arrowLeft : IBrush -> Control = glyph PackIconLucideKind.ArrowLeft

    /// 附件
    let paperclip : IBrush -> Control = glyph PackIconLucideKind.Paperclip

    /// 发送（纸飞机；官方无线上发送实心图标）
    let send : IBrush -> Control = glyph PackIconLucideKind.ArrowUpFromLine

    /// 停止生成
    let stop : IBrush -> Control = glyph PackIconLucideKind.Square

    /// 复制
    let copy : IBrush -> Control = glyph PackIconLucideKind.Copy

    /// 派生会话
    let fork : IBrush -> Control = glyph PackIconLucideKind.GitFork

    /// 刷新
    let refresh : IBrush -> Control = glyph PackIconLucideKind.RefreshCw

    /// 删除
    let trash : IBrush -> Control = glyph PackIconLucideKind.Trash2

    /// 编辑
    let pencil : IBrush -> Control = glyph PackIconLucideKind.Pencil

    /// 置顶
    let pin : IBrush -> Control = glyph PackIconLucideKind.Pin

    /// 归档
    let archive : IBrush -> Control = glyph PackIconLucideKind.Archive

    /// 设置（滑块齿轮，小尺寸下比整块齿轮更可辨）
    let gear : IBrush -> Control = glyph PackIconLucideKind.Settings2

    /// 配置项
    let sliders : IBrush -> Control = glyph PackIconLucideKind.SlidersHorizontal

    /// 浅色主题
    let sun : IBrush -> Control = glyph PackIconLucideKind.Sun

    /// 深色主题
    let moon : IBrush -> Control = glyph PackIconLucideKind.Moon

    /// 工具
    let wrench : IBrush -> Control = glyph PackIconLucideKind.Wrench

    /// 服务端
    let server : IBrush -> Control = glyph PackIconLucideKind.Server

    /// 认证令牌
    let key : IBrush -> Control = glyph PackIconLucideKind.KeyRound

    /// 软换行
    let textWrap : IBrush -> Control = glyph PackIconLucideKind.TextWrap

    /// 下载
    let download : IBrush -> Control = glyph PackIconLucideKind.Download

    /// 错误
    let alert : IBrush -> Control = glyph PackIconLucideKind.CircleAlert

    /// 说明
    let info : IBrush -> Control = glyph PackIconLucideKind.Info

    /// 侧栏
    let panelLeft : IBrush -> Control = glyph PackIconLucideKind.PanelLeft

    /// 消息
    let message : IBrush -> Control = glyph PackIconLucideKind.MessageSquare

    /// 更多操作
    let more : IBrush -> Control = glyph PackIconLucideKind.Ellipsis

    /// 外部链接
    let externalLink : IBrush -> Control = glyph PackIconLucideKind.ExternalLink

    /// 文件
    let file : IBrush -> Control = glyph PackIconLucideKind.FileText

    /// 图片
    let image : IBrush -> Control = glyph PackIconLucideKind.Image

    /// 时间
    let clock : IBrush -> Control = glyph PackIconLucideKind.Clock

    /// 生成中
    let sparkle : IBrush -> Control = glyph PackIconLucideKind.Sparkles

    /// 快捷键
    let keyboard : IBrush -> Control = glyph PackIconLucideKind.Keyboard

    /// 退出
    let logOut : IBrush -> Control = glyph PackIconLucideKind.LogOut

    /// 数据目录
    let database : IBrush -> Control = glyph PackIconLucideKind.Database
