namespace Wanxiang.UI

open Wanxiang2.Theme

/// 全应用唯一的主题实例：旧 Tokens 画笔与新组件读的是同一批活画笔 / 活值，
/// 主题补间、减弱动效开关都只在这里发生一次。必须最先编译。
module SharedTheme =

    let manager = new ThemeManager()

    let styles = ThemeStyles(manager)

    /// 全应用唯一的输入方式记录（旧 InputModality）。
    let input = Wanxiang2.InputSense.InputSense()
