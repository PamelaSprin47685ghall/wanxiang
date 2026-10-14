namespace Wanxiang.UI

open System
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Wanxiang2.CodeTint
open Wanxiang2.MarkdownView

/// 正文渲染的宿主一侧：脚本后端（RichBackend，App / Pwa 各自安装）、线程与计时、
/// 折行记忆、触屏收起悬停提示都在这里；控件与外观归 Wanxiang2.MarkdownView / ReadingLook。
module Reading =

    let private backend () = RichBackend.current ()

    /// 全应用唯一的着色实例（旧 Highlight 模块级状态）。
    let tint =
        CodeTint(
            { HighlightHtml = fun (code, language) -> backend () |> Option.bind (fun b -> b.HighlightHtml(code, language))
              Background = fun work -> Task.Run(work) |> ignore
              After = fun ms work -> Task.Run(fun () -> Task.Delay(ms).GetAwaiter().GetResult(); work ()) |> ignore
              // 浏览器端是宿主 JS 引擎，同步算即可，也没有后台线程。
              Synchronous = OperatingSystem.IsBrowser() })

    /// 代码块折行偏好（旧 MarkdownRenderer.DefaultCodeWrap）。
    let wrap = CodeWrapMemory(false)

    let private mathHtml (tex: string, display: bool) = backend () |> Option.bind (fun b -> b.MathHtml(tex, display))

    let private look =
        lazy
            // 正文里的图标按钮在触屏上不弹悬停提示（输入方式判断属于宿主）。
            let sync (spot: ActionSpot) (pointer: PointerType) =
                let touch = pointer = PointerType.Touch
                ToolTip.SetServiceEnabled(spot, not touch)
                if touch then ToolTip.SetIsOpen(spot, false)
            InputElement.PointerEnteredEvent.AddClassHandler<ActionSpot>(
                (fun spot (e: PointerEventArgs) -> sync spot e.Pointer.Type),
                RoutingStrategies.Direct ||| RoutingStrategies.Bubble,
                true)
            |> ignore
            InputElement.PointerPressedEvent.AddClassHandler<ActionSpot>(
                (fun spot (e: PointerPressedEventArgs) -> sync spot e.Pointer.Type),
                RoutingStrategies.Tunnel ||| RoutingStrategies.Bubble,
                true)
            |> ignore
            Wanxiang2.ReadingLook.ReadingLook.markdown SharedTheme.manager SharedTheme.styles

    /// 一份正文渲染器（旧 MarkdownRenderer(fontSize, copyText, openLink, allowHighlight)）。
    let view (fontSize: float) (copyText: string -> unit) (openLink: string -> unit) (allowHighlight: bool) =
        MarkdownView(
            look.Force(),
            { CopyText = copyText; OpenLink = openLink; MathHtml = mathHtml; Tint = tint; Wrap = wrap },
            fontSize,
            allowHighlight)
