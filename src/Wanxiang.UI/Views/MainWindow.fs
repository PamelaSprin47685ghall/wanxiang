namespace Wanxiang.UI

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Styling

/// 桌面窗口壳。
///
/// UI 主体在 `MainView`（决策 48：桌面与 PWA 共用同一套视图）；
/// 这里只负责窗口本身：尺寸、位置与它们的本机持久化（Q195）。
type MainWindow() as this =
    inherit Window()

    let view = MainView()
    let mutable prefs = UiPrefs.load ()

    do
        this.Title <- "万象"
        this.MinWidth <- LayoutPolicy.desktopMinWidth
        this.MinHeight <- 620.0

        // D7：Closing 存 Width/Height/X/Y，启动同样恢复 Width；下限与 MinWidth 同源，
        // 上限钳到屏幕最大可用宽，避免换显示器后窗口超出可视区。
        let maxWidth =
            try
                let widths = this.Screens.All |> Seq.map (fun s -> float s.WorkingArea.Width) |> Seq.toList
                match widths with [] -> prefs.windowWidth | _ -> List.max widths
            with _ ->
                prefs.windowWidth
        this.Width <- Math.Clamp(prefs.windowWidth, LayoutPolicy.desktopMinWidth, Math.Max(LayoutPolicy.desktopMinWidth, maxWidth))
        this.Height <- prefs.windowHeight
        this.WindowStartupLocation <-
            if prefs.hasWindowPosition then WindowStartupLocation.Manual else WindowStartupLocation.CenterScreen
        if prefs.hasWindowPosition then
            this.Position <- PixelPoint(prefs.windowX, prefs.windowY)
        this.Background <- Tokens.canvas
        this.Content <- view
        view.Build()

        Tokens.Changed.Publish.Add(fun _ -> this.Background <- Tokens.canvas)

        if StartupTrace.enabled then
            // 采样模式直接结束进程：关窗流程（草稿落盘、主机排空）不是被测对象
            StartupTrace.requestExit <- fun () -> Environment.Exit 0
            this.Opened.Add(fun _ -> this.RequestAnimationFrame(fun _ -> StartupTrace.mark "firstFrame" |> ignore))

        this.Closing.Add(fun _ ->
            // 先落草稿：关窗口前最后一次输入也要在本机留下（W5，决策 80）。
            view.FlushDrafts()
            // 以磁盘上的最新偏好为底再写窗口几何：设置页改过的开关（如「保存未发送的草稿」）
            // 不能被构造时读到的旧副本覆盖回去。
            prefs <-
                { UiPrefs.load () with
                    windowWidth = this.Width
                    windowHeight = this.Height
                    windowX = this.Position.X
                    windowY = this.Position.Y
                    hasWindowPosition = true }
            UiPrefs.save prefs)
