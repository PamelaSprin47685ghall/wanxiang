/// 视觉走查第三轮修复（第 206–212 节）。
module Wanxiang.Tests.VisualPassR3Tests

open System
open System.IO
open Avalonia.Controls
open Avalonia.Media
open Xunit
open Wanxiang.UI

let private repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then failwith "repo root not found"
        elif File.Exists(Path.Combine(dir.FullName, "wanxiang.slnx")) then dir.FullName
        else up dir.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

let private read (rel: string) = File.ReadAllText(Path.Combine(repoRoot, rel))

let private ctx (streaming: bool) (awaiting: Set<string>) : MessageContext =
    { fontSize = 14.0
      autoCollapseReasoning = false
      streaming = streaming
      isLastAssistant = true
      usage = None
      missingAttachments = Set.empty
      awaitingApproval = awaiting
      brandAvatar = fun () -> Border() :> Control }

let private call id = { callId = id; name = "builtin_file_read"; argumentsJson = "{}"; result = None }

// MessageCard 的模块初始化会碰 Avalonia 平台服务（光标等）：必须在 Headless 里跑，
// 否则在非 UI 进程里先于任何 Avalonia 初始化执行时会因类型初始化失败而偶发报错。
[<Trait("Category", "UI")>]
[<Fact>]
let ``等审批的工具调用显示「等待批准」而不是「执行中」`` () =
  Headless.run (fun () ->
    let status = MessageCard.resolveStatus (ctx true (Set.ofList [ "c1" ])) (call "c1")
    Assert.Equal(ToolCallStatus.AwaitingApproval, status)
    Assert.Equal("等待批准", ToolCallStatus.badgeText status)
    Assert.False(ToolCallStatus.isTerminal status)
    // 其他调用仍是执行中；生成结束后不再是等待批准
    Assert.Equal(ToolCallStatus.Running, MessageCard.resolveStatus (ctx true (Set.ofList [ "c1" ])) (call "c2"))
    Assert.Equal(ToolCallStatus.Unknown, MessageCard.resolveStatus (ctx false (Set.ofList [ "c1" ])) (call "c1")))

[<Trait("Category", "UI")>]
[<Fact>]
let ``审批面板汇报待批准的 callId`` () =
    Headless.run (fun () ->
        let panel = ToolApprovalPanel(fun _ _ -> ())
        let req =
            { approvalId = Guid.NewGuid(); conversationId = Guid.NewGuid(); callId = "c9"
              toolName = "t"; summary = "s"; argumentsJson = ""; policy = "first" }
        panel.Upsert req
        Assert.Equal<Set<string>>(Set.ofList [ "c9" ], panel.AwaitingCallIds)
        panel.Remove req.approvalId
        Assert.True panel.AwaitingCallIds.IsEmpty)

[<Fact>]
let ``触屏输入后按触屏对待，键盘输入后恢复`` () =
    InputModality.reset ()
    Assert.Equal(OperatingSystem.IsBrowser(), InputModality.prefersTouch ())
    InputModality.report true
    Assert.True(InputModality.prefersTouch ())
    InputModality.report false
    Assert.False(InputModality.prefersTouch ())
    InputModality.reset ()

[<Trait("Category", "UI")>]
[<Fact>]
let ``品牌标有图时不垫白底`` () =
    Headless.run (fun () ->
        match Brand.logo 48.0 with
        | :? Border as tile when not (isNull tile.Child) && (tile.Child :? Image) ->
            Assert.Equal(Brushes.Transparent :> IBrush, tile.Background)
        | _ -> ())

[<Fact>]
let ``启动画面标志不画方块阴影`` () =
    let css = read "src/Wanxiang.Pwa/wwwroot/app.css"
    let start = css.IndexOf(".splash-logo {")
    let block = css.Substring(start, css.IndexOf("}", start) - start)
    Assert.DoesNotContain("box-shadow", block)
    Assert.DoesNotContain("border-radius", block)

[<Fact>]
let ``PWA 安装引导推迟到首次成功对话后，且在顶部不压输入框`` () =
    let js = read "src/Wanxiang.Pwa/wwwroot/main.js"
    Assert.Contains("export function wxConversationSucceeded", js)
    Assert.Contains("if (!hadConversation()) { onConversationSucceeded = reveal; return; }", js)
    Assert.DoesNotContain("bar.hidden = false;\n    });", js)
    let css = read "src/Wanxiang.Pwa/wwwroot/app.css"
    Assert.Contains(".shell-install { top:", css)
    Assert.Contains("BrowserBridge.ConversationSucceeded", read "src/Wanxiang.UI/AppShell.fs")
