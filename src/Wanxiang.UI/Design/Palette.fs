namespace Wanxiang.UI

open Avalonia
open Avalonia.Media

/// 主题模式。跟随系统时由宿主解析成明/暗之一后再传进来。
type ThemeMode =
    | Light
    | Dark

/// 一套完整配色。明暗两套结构相同，切换只换值，不换语义。
type Palette = {
    /// 画布：应用最底层
    canvas: Color
    /// 侧栏：与画布半阶之差，靠纸色而非冷灰描边区分
    rail: Color
    /// 抬起面：卡片、输入框、弹层
    surface: Color
    /// 再抬一层：悬浮菜单、对话框
    surfaceRaised: Color
    /// 主描边
    border: Color
    /// 更轻的描边（分割线）
    borderSoft: Color
    /// 实色细线
    line: Color

    /// 正文
    text: Color
    /// 次要文字
    textMuted: Color
    /// 更弱的文字（元信息、占位）
    textFaint: Color
    /// 反色文字（落在强调色上）
    textOnAccent: Color

    /// 唯一强调色
    accent: Color
    /// 强调色悬停态
    accentHover: Color
    /// 强调色浅底（选中行、标签）
    accentSoft: Color
    /// 强调色极浅底
    accentFaint: Color
    /// 选中行底色。刻意用暖中性而非强调浅底——墨蓝浅底在暖纸上是冷的，
    /// 会成为整个界面唯一的冷色块。强调感由左缘细条承担。
    selected: Color
    /// 用户气泡自带一套色，不复用 accent：
    /// 深色主题的强调色是浅蓝，铺成大块会在近黑画布上刺眼。
    userBubble: Color
    userBubbleText: Color
    /// 行内代码底色。必须与 canvas 有可见落差：
    /// 浅色主题下若沿用「抬升面」就是白底压奶油底，等于没有底色。
    inlineCode: Color
    /// 表格隔行底色。只需要「能分出行」，落差再大就成了网格纸。
    tableStripe: Color
    /// 表头底色。刻意用暖中性而非强调浅底——理由与 `selected` 相同：
    /// 墨蓝浅底在暖纸上是冷的，会让表头成为整篇文档里唯一的冷色块。
    tableHeader: Color

    /// 成功 / 警告 / 危险：只用于状态点与提示条，不做大面积
    success: Color
    warning: Color
    danger: Color
    dangerSoft: Color

    /// 代码块
    codeBg: Color
    codeHeaderBg: Color
    codeBorder: Color
    codeText: Color
    codeMuted: Color
    /// 代码着色。深浅两套主题的代码面都是深色，故 token 只需一套配色。
    codeKeyword: Color
    codeString: Color
    codeComment: Color
    codeNumber: Color
    codeType: Color
    codeFunction: Color
    codeMeta: Color
    codeVariable: Color
    codeOperator: Color
    codeAddition: Color
    codeDeletion: Color

    /// 遮罩
    scrim: Color
    /// 阴影基色（含 alpha）
    shadow: Color
    shadowStrong: Color

    /// 交互覆盖层
    hover: Color
    pressed: Color
}

module Palette =

    let private c (hex: string) = Color.Parse hex

    /// 暖纸浅色：parchment 底 + 墨蓝强调。
    let light : Palette =
        { canvas = c "#F5F4ED"
          rail = c "#F0EEE5"
          surface = c "#FBFAF7"
          surfaceRaised = c "#FFFFFF"
          border = c "#D9D5C7"
          borderSoft = c "#E3E0D3"
          line = c "#C7C3B3"

          text = c "#141413"
          textMuted = c "#4E4C46"
          textFaint = c "#6E6B62"
          textOnAccent = c "#FBFAF7"

          accent = c "#1B365D"
          accentHover = c "#25476F"
          accentSoft = c "#E2EAF4"
          accentFaint = c "#EFF3F8"
          selected = c "#E1DDCC"
          userBubble = c "#1B365D"
          userBubbleText = c "#FBFAF7"
          inlineCode = c "#E2DCC7"
          tableStripe = c "#EAE7D8"
          tableHeader = c "#E1DCC8"

          success = c "#2F6B4F"
          warning = c "#8A5A18"
          danger = c "#8E3B2F"
          dangerSoft = c "#F5E4DF"

          codeBg = c "#17171A"
          codeHeaderBg = c "#202024"
          codeBorder = c "#2E2E33"
          codeText = c "#F2F1EC"
          codeMuted = c "#9B9890"
          codeKeyword = c "#9CBCE4"
          codeString = c "#A9CDA1"
          codeComment = c "#7E7B73"
          codeNumber = c "#E2B487"
          codeType = c "#8CC9C2"
          codeFunction = c "#D9C384"
          codeMeta = c "#B79BD0"
          codeVariable = c "#E4A9A0"
          codeOperator = c "#C6C3BA"
          codeAddition = c "#8FBF95"
          codeDeletion = c "#D98C80"

          scrim = Color.FromArgb(0x66uy, 0x14uy, 0x14uy, 0x13uy)
          shadow = Color.FromArgb(0x1Auy, 0x14uy, 0x14uy, 0x13uy)
          shadowStrong = Color.FromArgb(0x33uy, 0x14uy, 0x14uy, 0x13uy)

          hover = Color.FromArgb(0x0Duy, 0x14uy, 0x14uy, 0x13uy)
          pressed = Color.FromArgb(0x1Auy, 0x14uy, 0x14uy, 0x13uy) }

    /// 墨夜深色：同一套纸感在低光下的样子，不是简单反色。
    let dark : Palette =
        { canvas = c "#15151A"
          rail = c "#101014"
          surface = c "#22222A"
          surfaceRaised = c "#2C2C35"
          border = c "#45454F"
          borderSoft = c "#3A3A43"
          line = c "#55555F"

          text = c "#EDEBE4"
          textMuted = c "#C2BFB6"
          textFaint = c "#A19E95"
          textOnAccent = c "#0F1A28"

          accent = c "#8FB4DE"
          accentHover = c "#A6C6E9"
          accentSoft = c "#22313F"
          accentFaint = c "#1C2530"
          selected = c "#2C2C32"
          userBubble = c "#27384A"
          userBubbleText = c "#E7EEF7"
          inlineCode = c "#2E2E38"
          tableStripe = c "#25252E"
          tableHeader = c "#2E2E3A"

          success = c "#7FBF9C"
          warning = c "#D6A75E"
          danger = c "#E09385"
          dangerSoft = c "#422421"

          codeBg = c "#101013"
          codeHeaderBg = c "#18181C"
          codeBorder = c "#2A2A30"
          codeText = c "#EDEBE4"
          codeMuted = c "#8A877E"
          codeKeyword = c "#9CBCE4"
          codeString = c "#A9CDA1"
          codeComment = c "#7E7B73"
          codeNumber = c "#E2B487"
          codeType = c "#8CC9C2"
          codeFunction = c "#D9C384"
          codeMeta = c "#B79BD0"
          codeVariable = c "#E4A9A0"
          codeOperator = c "#C6C3BA"
          codeAddition = c "#8FBF95"
          codeDeletion = c "#D98C80"

          scrim = Color.FromArgb(0x99uy, 0x00uy, 0x00uy, 0x00uy)
          shadow = Color.FromArgb(0x59uy, 0x00uy, 0x00uy, 0x00uy)
          shadowStrong = Color.FromArgb(0x8Cuy, 0x00uy, 0x00uy, 0x00uy)

          hover = Color.FromArgb(0x14uy, 0xFFuy, 0xFFuy, 0xFFuy)
          pressed = Color.FromArgb(0x24uy, 0xFFuy, 0xFFuy, 0xFFuy) }

    let ofMode (mode: ThemeMode) : Palette =
        match mode with
        | Light -> light
        | Dark -> dark
