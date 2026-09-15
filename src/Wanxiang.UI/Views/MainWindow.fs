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

        this.Closing.Add(fun _ ->
            prefs <-
                { prefs with
                    windowWidth = this.Width
                    windowHeight = this.Height
                    windowX = this.Position.X
                    windowY = this.Position.Y
                    hasWindowPosition = true }
            UiPrefs.save prefs)
