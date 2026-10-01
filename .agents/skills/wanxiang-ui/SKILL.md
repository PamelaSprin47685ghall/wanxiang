---
name: wanxiang-ui
description: 万象 UI 规范：图标（Lucide 官方集）、间距尺寸阶梯（Fluent 2 ramp）、排版阶梯、控件状态与反馈（Avalonia 原生伪类）、颜色与对比度契约、响应式断点、无障碍契约、反模式清单。
---

# 万象 UI 规范 v1

> 本文是 UI 视觉与交互的**唯一权威**。`src/Wanxiang.UI/Design/` 是它的代码形态；
> 两者冲突时以本文为准，并立即修正代码。
>
> 采纳日期：2026-10-01。clean-break：本文生效前的既有视觉值不作保留，
> 只保留**通过本文全部契约**的那些。

## 0. 本规范只做一件事：不发明

万象**不自制**图标几何、不自制间距刻度、不自制控件状态机。凡有官方标准的，一律采用官方标准：

| 领域 | 采纳的官方标准 | 来源 |
| --- | --- | --- |
| 图标 | **Lucide**（24×24 画布、2px 描边、圆头圆角） | [lucide.dev 设计原则](https://lucide.dev/contribute/icons/design-principles)；`IconPacks.Avalonia.Lucide`（ISC） |
| 间距 / 圆角 / 触控尺寸 | **Fluent 2 global spacing ramp**（4px 基准，含 2/6/10 例外档） | [fluent2.microsoft.design/layout](https://fluent2.microsoft.design/layout) |
| 控件状态与焦点 | **Avalonia 原生伪类** `:focus-visible` / `:pointerover` / `:pressed` / `:disabled` / `:checked` / `:selected` | Avalonia 12 `Avalonia.Styling.Style` + `Selector.Class` |
| 动效降级 | **`prefers-reduced-motion` 语义**（`MotionPolicy.isReduced`） | 已被 PWA 壳层 CSS 采用 |
| 文字对比度 | **WCAG 2.1 AA**：正文 ≥ 4.5:1，大字 ≥ 3:1 | [fluent2.microsoft.design/typography](https://fluent2.microsoft.design/typography) |

控件模板继续用 `FluentTheme`（Avalonia 官方控件主题），万象只覆盖颜色资源键。
**不引入第二套控件库、不引入自绘主题。**

---

## 1. 图标规范

### 1.1 唯一图标来源

**只允许 Lucide。** `Design/Icons.fs` 是 Lucide 的**语义命名映射层**，不是图标集。

- 新图标 = 从 1641 个 Lucide 图标里挑一个 + 起语义名，**不画**。
- 若某语义在 Lucide 中确实没有对应图标，才允许自绘，且必须：
  1. 遵守本节全部几何规则；
  2. 在该图标旁注明 `自绘（Lucide 无对应）`；
  3. 通过 `IconCenteringTests` 的几何与像素双层校验。
- **禁止**为"更贴合万象气质"而改写 Lucide 官方路径。
- **禁止**绕过官方 API 自建字形来源（读 SVG 文件、自己转换路径、`Geometry.Parse`
  官方 `d` 字符串）。上游负责解析与坐标系，我们只负责摆放。

依赖：`IconPacks.Avalonia.Lucide` 2.0.0（ISC）。

**装配方式（实测唯一可用的一条）**：走纯 API，不依赖任何包内主题。

```fsharp
let drawing = LucideImageExtension(Kind = kind, Brush = brush).ProvideValue(null) :?> DrawingImage
Image(Source = drawing, Width = size, Height = size, Stretch = Stretch.Uniform)
```

实测记录（Avalonia 12.1.2 + Skia，无头与真实渲染都验过）：

| 方式 | 结果 |
|---|---|
| `PackIconLucide`（官方控件） | **零墨迹**。它是 TemplatedControl，字形靠包内 `Lucide.axaml` 的 ControlTheme；该主题经 `AvaloniaXamlLoader.Load` 载入后是**空 Styles（0 子项）**，模板因此缺失，控件渲染成空色块——产品面表现为「右上角按钮是个纯色圆」 |
| `PackIconLucide.Data` 的几何 | 坐标系错位（bounds `84.375, -915.833, 831.25, 831.458`），不能直接喂给 `PathIcon` |
| `LucideImageExtension.ProvideValue(null)` | **返回 `Avalonia.Media.DrawingImage`，正常出墨**（↑ 采用） |

**画笔是活的**：返回的 `GeometryDrawing.Pen.Brush` 与传入的是**同一实例**，
因此 `Tokens.apply` 原地改色时图标自动跟随主题，无需重建视图。
`DrawingImage` 本身每次调用新建（实测不复用），故 `Icons` 按 (画笔实例, 图标) 缓存。

**零墨迹是接线故障，必须被测试抓住**：`IconCenteringTests` 有一条断言逐图标扫描
像素、要求墨迹数 > 0。只断言「控件在活树里取得了尺寸」是不够的——实测那一条
在零墨迹时照样通过。

### 1.2 几何规则（继承 Lucide 官方规范）

- 画布 **24×24**，非正方形或更大画布一律不合规。
- 描边 **2px**、**圆头**（round cap）、**圆角连接**（round join），描边居中于路径。
- 墨迹与画布边缘至少 **1px** 安全区。
- 同一图标内不同元素之间至少 **2px** 视觉间隙。
- 拐角一律带圆角；直角相交处圆角半径 **2px**（≥8px 的形状）或 **1px**（<8px）。
- 视觉重量一致：与 `circle` / `square` 并排模糊后深浅相当。

### 1.3 尺寸档（唯一三档）

| 语义 | 字形 | 命中区 | 用在哪 |
| --- | --- | --- | --- |
| `glyphSm` | 14 | 20 | 行内图标、chip、tag 内图标 |
| `glyphMd` | 16 | 24 | 按钮图标、工具栏、状态点 |
| `glyphLg` | 20 | 32 | 空态锚点、功能入口、侧栏品牌区 |

**命中区下限**（与字形分离，落地经 `Ui.setSquareTarget` 只调命中区不改字形）：

| 场景 | 命中区 | 依据 |
| --- | --- | --- |
| 桌面常规动作 | 24 | `size240` |
| 行内 / 脚注动作 | 20 | `size200` |
| compact / 触控动作 | 44 | Fluent 2 移动触控下限 |

### 1.4 图标颜色

图标颜色**只用** `Tokens.textMuted` / `Tokens.text` / `Tokens.textFaint` /
`Tokens.danger` / `Tokens.warning` / `Tokens.accent` 六档。
**禁止**给单个图标硬编码颜色、禁止用强调色大面积铺图标。

### 1.5 图标与文字的对齐

图标与文字同排时**垂直居中对齐**（`VerticalAlignment.Center`），
不写 `Margin(0, n, 0, 0)` 之类的裸偏移做基线微调。
旧档 `Tokens.iconBaselineNudge` 取消。

---

## 2. 间距与尺寸阶梯

### 2.1 唯一阶梯（Fluent 2 global spacing ramp）

基准 4px。完整 ramp（Fluent 官方，明确包含 2/6/10 三档——它们用于图标内衬与像素网格对齐）：

| 令牌 | 值 | 语义 |
| --- | --- | --- |
| `spaceNone` | 0 | 无 |
| `spaceXXs` | 2 | 图标内衬、hairline 内衬、chip 纵向内衬 |
| `spaceXs` | 4 | 紧凑堆叠间距、单行小控件纵向呼吸 |
| `spaceSm` | 6 | 字段标签 / 校验行 / hint 的贴字段留白 |
| `spaceMd` | 8 | 组件内部主间距、chip 横向内衬、块级内边距 |
| `spaceLg` | 10 | 紧排 chip 横向内衬（需与 8 视觉等重时） |
| `spaceXl` | 12 | 字段外壳内边距、图标按钮与文字间距 |
| `space2xl` | 16 | 组件之间、按钮横向内衬、页面留白（窄屏档） |
| `space3xl` | 20 | 侧栏行内边距、宽档组件间距 |
| `space4xl` | 24 | 页面留白（中小窗档）、分组卡内边距、消息间距 |
| `space5xl` | 28 | 宽屏档留白过渡 |
| `space6xl` | 32 | 区块之间、桌面档页面留白 |
| `space7xl` | 36 | 宽屏留白过渡 |
| `space8xl` | 40 | 主分节之前 |
| `space10xl` | 48 | 宽屏档页面留白 |
| `space11xl` | 52 | 分节最大档 |
| `space12xl` | 56 | 槽位宽度上限档 |

**所有间距、槽位、内边距、留白必须落在这 13 档之一。**
旧档 `space1/space2/…/space12`、`tightRowPaddingY(1)`、`compactRowPaddingY(2)`、
`fieldRowPaddingY(3)`、`fieldInsetX(2)`、`tagPaddingX(7)` 一律作废，
按语义重新映射到上表。

### 2.1a 适用范围（决定校验能否落地）

阶梯管辖的是**组件间的空间关系**，不是**组件内部的尺寸**。

**必须落 ramp**（间距语义）：`Thickness` / `Spacing` / `RowSpacing` / `ColumnSpacing` /
`Margin` / `Padding` 的全部取值——页面留白、块间距、堆叠间距、图标与文字间距、内边距。

**不受 ramp 管辖**（各有独立权威）：

| 量 | 权威 | 理由 |
| --- | --- | --- |
| 字号 8 档 | §3.2 | 10.5 / 12.5 / 14.5 是阅读节奏，**故意**非整数步进 |
| 行高 | §3.3 | 行高 = 字号 × 比例，与 ramp 无关 |
| 组件最小高 / 最小宽 | §2.5 | 由内容度量决定，量出来就是那个值 |
| 断点 | §6 | 是视口尺寸，不是空间关系 |
| 阅读列宽 / 窗口宽 | §2.5 | 内容可读性上限（CJK 每行 40–45 字） |

**结论**：设计系统不可能把所有数字都变成 4 的倍数——**墨迹尺寸与空间尺寸是两套刻度**。
把字号 / 行高 / 组件度量硬塞进 ramp，只会得到一张无人遵守的表。

### 2.1b 组件度量（第二套刻度）

组件最小高、最小宽由**内容度量**决定，不由 ramp 决定，但同样**MUST NOT** 出现裸值——
每一条都要有语义名，且同类组件同值：

| 组件 | 最小高 | 说明 |
| --- | --- | --- |
| 常规行（侧栏会话行 / 设置开关行） | 44 | 同值同源，此前两处各写一份 |
| 文字按钮 / 菜单项 | 34 | 单行文本 + 纵向内边距 |
| 设置分区导航项 | 38 | 三段文字行 |
| 紧凑动作命中区 | 44 | = 触控下限，与行高同值但语义不同，分记 |

### 2.2 槽位宽度（对齐的真正来源）

**槽位宽度不是间距，是布局列宽。** 它也必须落阶梯。映射：

| 旧值 | 新值 | 语义 |
| --- | --- | --- |
| `menuIconSlotWidth = 18` | `slotIcon = 20` | 菜单/行内图标槽 |
| `sidebarStateSlotWidth = 19` | `slotIcon = 20` | 侧栏行状态槽（与上同档，两处此前分叉 1px） |
| `composerAttachmentIconSlot = 15` | `glyphMd = 16` | 附件行图标槽 |
| `composerAttachmentStateWidth = 54` | `slotWide = 56` | 附件行状态槽 |
| `fontSizeValueMinWidth = 56` | `slotWide = 56` | 设置页数值槽 |
| `aboutKeyMinWidth = 110` | `slotWide2 = 112` | 关于页键名列槽 |

**一条测试钉住**：所有槽位宽度 ∈ 阶梯，且同类槽位同值（`slotIcon` 全应用只有 20）。

### 2.3 圆角（Fluent 2 corner radius）

| 令牌 | 值 | 用在哪 |
| --- | --- | --- |
| `Radius.xs` | 4 | 状态点、极小控件 |
| `Radius.sm` | 6 | tag、内联徽标 |
| `Radius.md` | 8 | **控件默认**（按钮、输入壳、图标按钮、行） |
| `Radius.lg` | 12 | 卡片、分组卡、浮层 |
| `Radius.xl` | 16 | 对话框、Hero 卡片 |
| `Radius.pill` | 999 | toggle track、强调图标按钮 |

控件默认圆角统一到 `Radius.md`（旧 `radiusMd = 9` 作废）。
kami 规则「亚 1px 描边 + 圆角 = 双环」：卡片**不得**同时有亚 1px 闭合描边和圆角。

### 2.4 描边宽度

| 令牌 | 值 | 用在哪 |
| --- | --- | --- |
| `Stroke.thin` | 1 | 全应用唯一描边宽度（tag / 输入壳 / 按钮 / 分组卡 / 分隔线） |
| `Stroke.thick` | 2 | 左缘强调条、选中行标记、引用块（印刷重量，两档有意） |
| `Stroke.quote` | 3 | Markdown 引用块左缘 |
| `Stroke.thick` | 2 | 焦点环外扩宽度 |

**只有三档。** 焦点环与左缘强调条同为 2（同一档，非巧合）。
旧档 `borderWidth` / `sidebarDividerWidth` / `accentEdgeWidth` / `quoteEdgeWidth` 全部改读本表。

---

## 3. 排版规范

### 3.1 字体

- 正文：`Sarasa Gothic SC`（比例宽度，OFL-1.1，内嵌）
- 等宽：`Sarasa Term SC` + 正文回落（等宽子集只含拉丁，中文必须回落否则豆腐块）

浏览器-wasm 拿不到宿主系统字体，**两端必须用同一份内嵌字形**（已如此）。

### 3.2 字号阶梯（8 档）

| 令牌 | 值 | 用在哪 |
| --- | --- | --- |
| `fontMicro` | 10.5 | 元信息、角标、字段标签 |
| `fontCaption` | 11.5 | 辅助说明 |
| `fontSmall` | 12.5 | 界面小字（按钮、标签、列表行标题） |
| `fontBody` | 13.5 | 界面正文 |
| `fontReading` | 14.5 | 消息阅读正文 |
| `fontTitle` | 16 | 小标题 |
| `fontHeading` | 19 | 区块标题 |
| `fontDisplay` | 25 | 页面主标题 |

**视图层禁止任何 `FontSize = <数字>`**（现状已 0 裸值，维持）。

### 3.3 行高

| 语义 | 比例 | 依据 |
| --- | --- | --- |
| 正文（消息） | 1.62 | CJK 字身框满高，需比拉丁的 1.55 更松 |
| 次级长文本 | 1.56 | 辅助材料层级 |
| 等宽 / 技术详情 | 1.52 | 强调扫描效率 |
| UI 正文 | 20 固定 | 单行 UI 文本不需要比例 |
| 标题 | 26 固定 | |

Markdown 标题字号 = `fontReading + {1:10, 2:6.5, 3:4, 4:2, 5+:1}`，属于阶梯派生而非临时特效。

### 3.4 字重

**只有两档**：Normal（400）与 Medium（500）。**禁止 Bold（700）**（kami 规则 5）。
层级靠字号与颜色表达，不靠加粗。

### 3.5 字距

| 令牌 | 值 | 用在哪 |
| --- | --- | --- |
| `trackingLabel` | 0.3 | 字段标签、tag |
| `trackingEmphasis` | 0.4 | 空态主标题 |
| `trackingSection` | 0.6 | 区块标签 |
| `trackingDisplay` | 0.8 | 页面级测量标签 |

### 3.6 文案

- **Sentence case**，禁止全大写（kami/Fluent 共同规则）。
- 左对齐 LTR 文本；禁止右对齐长段落。
- 居中只用于短促的强调文案或纯空态。

---

## 4. 控件状态与反馈规范

### 4.1 状态只用 Avalonia 原生伪类

**这是本规范最重要的一条。** Avalonia 框架在真实指针/焦点事件里维护这些伪类：

`:focus-visible` · `:focus` · `:focus-within` · `:pointerover` · `:pressed` ·
`:disabled` · `:checked` · `:selected`

**禁止**手写 `GotFocus` / `LostFocus` / `PointerEntered` / `PointerExited` /
`PointerPressed` / `PointerReleased` 去实现状态视觉。
（伪类只能由控件自己维护——`Classes.Set(":pointerover", true)` 会抛
`The pseudoclass ':pointerover' may only be added by the control itself`，
这正是"必须走官方机制"的机制性保证。）

### 4.2 状态外观契约

| 状态 | 表现 | 实现 |
| --- | --- | --- |
| idle | 基准面 | `Setter` |
| hover | 底色提到 `hover` 档，**不改尺寸/边框粗细** | `Style` + `:pointerover` |
| pressed | 底色 + `opacityPressed` 脉冲（瞬时，不进过渡） | `Style` + `:pressed` |
| focus | 外扩 `strokeFocus` 实色 `accent` 环（输入壳用 `accentSoft`） | `Style` + `:focus-visible` |
| disabled | `opacityDisabled`，保留可辨对比度 | `Style` + `:disabled` |
| selected | `selected` 底 + 左缘 `strokeThick` 强调条 | `Style` + `:selected` |

焦点环用**外阴影**而非边框——加边框会让内容跳一下，而焦点本该是无位移提示。

### 4.3 过渡

- 状态底色/描边过渡统一 `BrushTransition`，时长 `MotionLedger.controlStateTransition`，
  缓动 `MotionPolicy.easeOutCubic`，经 `MotionPolicy.isReduced` 降级为零。
- **几何动画全面禁止**（`MotionLedger.geometryAnimationAllowed = false`）：
  不对 transform / 尺寸 / 边宽 / 位置做过渡。
- 唯一持续运动：busy spinner。

### 4.4 命中区与可达性

- 每个可交互控件有**唯一** Tab 停靠点（行容器代理键盘，内层开关不进 Tab 序）。
- Enter / Space 激活只绑一次（绑两次 = 键盘用户永远打不开开关）。
- 图标按钮必须有 `AutomationProperties.Name`（= tooltip 文案）。

---

## 5. 颜色与对比度契约

### 5.1 结构

明暗两套 `Palette`，**结构相同**，切换只换值不换语义。语义槽位固定：
`canvas` / `rail` / `surface` / `surfaceRaised` / `surfaceContainer` / `border` /
`borderSoft` / `hairline` / `hairlineStrong` / `line` / `text` / `textMuted` /
`textFaint` / `textOnAccent` / `accent` / `accentHover` / `accentSoft` /
`accentFaint` / `selected` / `userBubble` / 状态四色 / 代码面 / `scrim` /
`hover` / `pressed`。

**禁止**新增语义槽位；需要新色时先问"现有槽位能否表达"。

### 5.2 对比度（实测基线）

明暗两套、所有前景 × 所有表面组合均过 WCAG AA：

| | canvas | surface | surfaceRaised | rail |
| --- | --- | --- | --- | --- |
| 浅 `text` | 16.72 | 17.66 | 18.43 | 15.86 |
| 浅 `textFaint`（最弱档） | 4.83 | 5.10 | 5.33 | 4.58 |
| 深 `text` | 15.25 | 13.23 | 11.59 | 14.49 |
| 深 `textFaint` | 6.79 | 5.89 | 5.16 | 6.45 |

最低值 4.58（浅色 `textFaint` on `rail`）。**一条测试锁住全表 ≥ 4.5**。

### 5.3 主题切换

切主题逐帧把 `Color` 从旧色补间到新色（200ms / 12 帧），已挂在树上的控件自动重绘，
不重建视图树。只补间颜色，不触碰 transform / 尺寸 / 边距。

---

## 6. 响应式断点

唯一断点源 `LayoutPolicy`：

| 断点 | 值 | 行为 |
| --- | --- | --- |
| `compactBreakpoint` | 720 | 侧栏转抽屉、图标命中区抬到 44 |
| `wideLayoutBreakpoint` | 1024 | 页面留白升档 |
| `desktopMinWidth` | 700 | 窗口最小宽（必须 < compactBreakpoint） |
| `formSingleColumnBreakpoint` | 440 | 双列表单退化单列 |

页面水平留白四档：`440→16` · `720→24` · `1024→32` · 更宽 `48`。

**响应式四种手段只用三种**：reflow（重排）、show/hide（显隐）、resize（改尺寸与留白）。
不做 re-architect（换一套布局）。

---

## 7. 无障碍契约

首版无权限控制**不等于**无障碍。以下从首版就在（Q197）：

- 键盘可达：Tab 序唯一、焦点环可见、Enter/Space 激活。
- 焦点环用 `:focus-visible`——指针点击**不**画环（指针已有按压反馈，画环是噪声），
  键盘聚焦**一定**画环。
- 自动化语义：`Button` / `CheckBox` / `TogglePattern` 由 `ActionBorder` /
  `ToggleBorder` 的 automation peer 提供，不是裸容器。
- 校验反馈：字段级错误行 + `HelpText`；多错时顶部 live region 摘要并聚焦摘要。
- 减少动画：`MotionPolicy.setReduced` 由宿主 `prefers-reduced-motion` 驱动。
- 对比度：见 §5.2。

---

## 8. 反模式清单（见到即错）

| # | 反模式 | 正确做法 |
| --- | --- | --- |
| 1 | 手写 `GotFocus`/`PointerEntered` 实现状态视觉 | 官方伪类 + `Style`（§4.1） |
| 2 | 视图层写 `FontSize = 13.5` | `Tokens.fontBody` |
| 3 | 间距/槽位写裸数字（尤其 18/19/54/7） | 阶梯值（§2） |
| 4 | 图标靠 `Margin(0,2,0,0)` 对齐 | 垂直居中 + 独立命中区（§1.5） |
| 5 | 自制图标几何 | Lucide 官方集（§1.1） |
| 6 | 给单个图标硬编码颜色 | 六档图标色（§1.4） |
| 7 | 同一语义在两处各写一份尺寸 | 单一真源 + 一条测试钉住 |
| 8 | 卡片同时有亚 1px 闭合描边和圆角 | 二选一（§2.3） |
| 9 | 用 Bold 做层级 | 字号 + 颜色（§3.4） |
| 10 | 全大写文案强调 | Sentence case（§3.6） |
| 11 | 几何/位移/尺寸动画 | 只补间颜色与不透明度（§4.3） |
| 12 | 新增调色板语义槽位 | 先复用现有槽位（§5.1） |
| 13 | 一个控件两个 Tab 停靠点 | 行容器代理键盘（§4.4） |
| 14 | 焦点环加边框粗细 | 外扩阴影（§4.2） |

---

## 9. 大修顺序

按依赖排序，每步都可独立验证：

1. **阶梯层**：`Design/Spacing.fs`（新）+ `Radius` + `Stroke`，删旧 `Tokens.space*` /
   `tightRowPaddingY` / `compactRowPaddingY` / `fieldRowPaddingY` / `fieldInsetX` /
   `tagPaddingX`。加「所有间距 ∈ 阶梯」测试。
2. **槽位收敛**：`slotIcon = 20` / `slotWide = 56` / `slotWide2 = 112`，
   替换 6 处分叉槽位；加「同类槽位同值」测试。
3. **图标换源**：`IconPacks.Avalonia.Lucide` 入依赖，`Design/Icons.fs` 改写为
   Lucide 语义映射（36 个语义名一一对应官方图标），删除全部自绘路径。
   `IconCenteringTests` 改挂新实现。
4. **排版归一**：字号/字距/字重/行高按 §3 重排；`iconBaselineNudge` 删除。
5. **状态归一**：`Controls/Interaction.fs` 新模块 —— 一个 `Style` 注册入口 +
   一个 `focusRing` 样式 + 一个 `surfaceFeedback` 样式；`App.fs` 统一注册。
   删除 8 处手写焦点环与 20+ 处手写 hover。
6. **视觉回归**：`tools/qa_shot.js` 增加规范校验档（阶梯越界扫描 + 对比度表 +
   图标覆盖率）。

第 1–3 步互不依赖，可并行；第 4–6 步依赖前序。
