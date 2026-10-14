namespace Wanxiang.Tests

open System
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Input.TextInput
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.UI
open Wanxiang.Tests

[<Trait("Category", "UI")>]
module DialogsTests =

    let rec private descendants (control: Control) : seq<Control> =
        seq {
            yield control
            match control with
            | :? Panel as panel ->
                for child in panel.Children do
                    yield! descendants child
            | :? ContentControl as contentCtrl ->
                if not (isNull contentCtrl.Content) && contentCtrl.Content :? Control then
                    yield! descendants (contentCtrl.Content :?> Control)
            | :? Border as border ->
                if not (isNull border.Child) then
                    yield! descendants border.Child
            | _ -> ()
        }

    let private click (control: Control) =
        control.RaiseEvent(KeyEventArgs(RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = control))

    let private findButton (root: Control) (text: string) : Border option =
        descendants root
        |> Seq.tryPick (fun c ->
            match c with
            | :? Border as b when AutomationProperties.GetName(b) = text -> Some b
            | _ -> None)
