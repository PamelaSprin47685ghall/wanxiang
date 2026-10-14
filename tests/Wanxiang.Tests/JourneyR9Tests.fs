/// r9 新用户全旅程走查修复（决策 260–264）。
module Wanxiang.Tests.JourneyR9Tests

open System
open System.IO
open Avalonia
open Avalonia.Automation
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Threading
open Xunit
open Wanxiang.Core
open Wanxiang.UI

let private repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then failwith "repo root not found"
        elif File.Exists(Path.Combine(dir.FullName, "wanxiang.slnx")) then dir.FullName
        else up dir.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

let private read (rel: string) = File.ReadAllText(Path.Combine(repoRoot, rel))

let rec private descendants (control: Control) : seq<Control> =
    seq {
        yield control
        match control with
        | :? Panel as panel ->
            for child in panel.Children do
                yield! descendants child
        | :? ContentControl as c when (c.Content :? Control) -> yield! descendants (c.Content :?> Control)
        | :? Border as b when not (isNull b.Child) -> yield! descendants b.Child
        | :? Decorator as d when not (isNull d.Child) -> yield! descendants d.Child
        | _ -> ()
    }

let private byName (root: Control) (name: string) =
    descendants root |> Seq.tryFind (fun c -> AutomationProperties.GetName c = name)

let private msg role text (at: DateTimeOffset option) : MessageView =
    { MessageView.empty with role = role; text = text; commitId = Some 1UL; committedAt = at }

// ---- 260 ----

[<Trait("Category", "UI")>]
[<Fact>]
let ``260 startPairing 展开配对码区、发起配对、切换入口改为改用令牌`` () =
    Headless.run (fun () ->
        let overlay = OverlayHost(Grid())
        let requests = ResizeArray<ConnectRequest>()
        let handle = Dialogs.connect overlay "ws://127.0.0.1:8765/ws" requests.Add ignore
        Dispatcher.UIThread.RunJobs()
        handle.startPairing ()
        Dispatcher.UIThread.RunJobs()
        Assert.True(requests.Count = 1 && requests[0].usePairing)
        let codeBox =
            descendants overlay.Root |> Seq.pick (function :? TextBox as t when t.MaxLength = 6 -> Some t | _ -> None)
        Assert.True(codeBox.IsVisible)
        let toggle = byName overlay.Root "已有访问令牌？改用令牌"
        Assert.True(toggle.IsSome)
        let tokenBox =
            descendants overlay.Root |> Seq.pick (function :? TextBox as t when t.PasswordChar = '●' -> Some t | _ -> None)
        let rec hidden (c: Control) =
            (not c.IsVisible) || (match c.Parent with :? Control as p -> hidden p | _ -> false)
        Assert.True(hidden tokenBox, "配对模式下令牌框退场")
        // 幂等：再调一次不重复发起配对
        handle.startPairing ()
        Assert.Equal(1, requests.Count)
        // 对话框说明不再出现协议术语
        Assert.False(
            descendants overlay.Root
            |> Seq.exists (function :? TextBlock as t -> not (isNull t.Text) && t.Text.Contains "WebSocket" | _ -> false)))

[<Fact>]
let ``260 AppShell 首次打开连接对话框时自动进入配对`` () =
    let shell = read "src/Wanxiang.UI/AppShell.fs"
    Assert.Contains("PairingHint.pairingFirst lastToken hasSavedCredentials connectionError", shell)
    Assert.Contains("handle.startPairing ()", shell)

// ---- 261 ----

[<Trait("Category", "UI")>]
[<Fact>]
let ``261 输入框菜单文案为中文`` () =
    // ResourceDictionary 是 AvaloniaObject：必须在 headless UI 线程上构造，
    // 否则 Dispatcher.UIThread 绑到 xunit 工作线程，毒化后续 UI 测试（决策 421）。
    Headless.run (fun () ->
    let res = ResourceDictionary()
    TextFlyoutLocale.apply res
    Assert.Equal<obj>(box "剪切", res["StringTextFlyoutCutText"])
    Assert.Equal<obj>(box "复制", res["StringTextFlyoutCopyText"])
    Assert.Equal<obj>(box "粘贴", res["StringTextFlyoutPasteText"]))
    Assert.Contains("TextFlyoutLocale.apply this.Resources", read "src/Wanxiang.UI/App.fs")
    Assert.Contains("OfType<MenuFlyoutPresenter>", read "src/Wanxiang.UI/App.fs")

// ---- 262 ----

[<Trait("Category", "UI")>]
[<Fact>]
let ``262 限流倒计时未结束时重试按钮不可按并显示剩余秒数`` () =
    Headless.run (fun () ->
        let err = { GenerationError.create ProviderRateLimited "「OpenAI」限流了这次请求。" with retryAfterSeconds = Some 30 }
        let card = MessageCard.errorCard err ignore None
        // 决策 302：等待中读屏名带剩余秒数
        let retry = byName card "重试生成，30 秒后可用" |> Option.orElse (byName card "重试生成，29 秒后可用") |> Option.get :?> Border
        Assert.False(retry.IsEnabled)
        match retry.Child with
        | :? TextBlock as t -> Assert.StartsWith("重试（", t.Text)
        | _ -> Assert.Fail "retry label missing")

[<Trait("Category", "UI")>]
[<Fact>]
let ``262 没有倒计时的可重试错误照常可按`` () =
    Headless.run (fun () ->
        let err = GenerationError.create ProviderTimeout "请求超时。"
        let card = MessageCard.errorCard err ignore None
        let retry = byName card "重试生成" |> Option.get :?> Border
        Assert.True(retry.IsEnabled)
        match retry.Child with
        | :? TextBlock as t -> Assert.Equal("重试", t.Text)
        | _ -> Assert.Fail "retry label missing")

// ---- 263 ----

[<Fact>]
let ``263 未回复卡可重试`` () =
    Assert.True(UnansweredReply.error.retryable)

[<Fact>]
let ``263 AppShell 只在空闲且没有失败卡时补出未回复卡`` () =
    let shell = read "src/Wanxiang.UI/AppShell.fs"
    Assert.Contains("ReplyTail.isUnanswered DateTimeOffset.UtcNow messages", shell)
    Assert.Contains("ConversationSurface.decide", shell)

// ---- 264 ----

[<Fact>]
let ``264 桌面浏览器安装引导不说主屏幕`` () =
    let js = read "src/Wanxiang.Pwa/wwwroot/main.js"
    Assert.Contains("matchMedia(\"(pointer: coarse)\")", js)
    Assert.Contains("安装为桌面应用，在独立窗口中打开。", js)
    Assert.Contains("label.textContent = installLabel();", js)

// ---- 侧栏搜索：迟到的防抖 Tick 不再清空同词命中（CI 偶发失败） ----

[<Fact>]
let ``侧栏搜索防抖到期时词未变不重发查询`` () =
    let src = read "src/Wanxiang.UI/Views/Sidebar.fs"
    Assert.Contains("if SearchRequery.shouldQuery searchBox.Text searchHitsQuery then", src)

[<Fact>]
let ``粘贴位图读完时已换主机或重连就不上传`` () =
    // 剪贴板位图分支与选文件、拖入用同一归属判断，避免读完后上传落到新主机。
    let shell = read "src/Wanxiang.UI/AppShell.fs"
    let start = shell.IndexOf "| UploadFacts.PasteSource.Bitmap ->"
    let stop = shell.IndexOf("| UploadFacts.PasteSource.NotHandled", start)
    Assert.True(start >= 0 && stop > start)
    let branch = shell.Substring(start, stop - start)
    Assert.Contains("let ownerInstance = instanceId", branch)
    Assert.Contains("let ownerConnection = client.ConnectionGeneration", branch)
    Assert.Contains("if UploadFacts.stillOwned authenticated ownerInstance ownerConnection instanceId client.ConnectionGeneration then", branch)
