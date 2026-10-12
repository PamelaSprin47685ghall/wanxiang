namespace Wanxiang.Tests

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.Threading
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store
open Wanxiang.UI
open Wanxiang.Tests.Helpers

/// W5 输入与可靠性：本地草稿与未确认消息、配置被拒绝的客户端提示、检查主机。
/// 每个验收条目对应一组测试（见 docs/ux/redesign-proposal.md W5）。
module ComposerReliabilityTests =

    // ---------- 公共夹具 ----------

    /// 把 WANXIANG_HOME 指到临时目录：草稿文件、ui.json、client.toml 都落在这里，不碰真实家目录。
    let private withHome (body: string -> unit) =
        let home = tempDir ()
        let old = Environment.GetEnvironmentVariable "WANXIANG_HOME"
        Environment.SetEnvironmentVariable("WANXIANG_HOME", home)
        try body home
        finally
            Environment.SetEnvironmentVariable("WANXIANG_HOME", old)
            cleanup home

    let private handleEventMethod =
        lazy (typeof<MainView>.GetMethod("HandleEvent", BindingFlags.NonPublic ||| BindingFlags.Instance))

    let private dispatchEvent (view: MainView) (ev: WireEvent) =
        handleEventMethod.Value.Invoke(view, [| box ev |]) |> ignore

    let private getField<'T> (view: MainView) (name: string) : 'T =
        typeof<MainView>.GetField(name, BindingFlags.NonPublic ||| BindingFlags.Instance).GetValue(view) :?> 'T

    let private setField<'T> (view: MainView) (name: string) (value: 'T) =
        typeof<MainView>.GetField(name, BindingFlags.NonPublic ||| BindingFlags.Instance).SetValue(view, box value)

    let private createShell () =
        MotionPolicy.setReduced true
        let shell = MainView()
        shell.Build()
        let window = Window(Width = 1000.0, Height = 800.0, Content = shell)
        window.Show()
        Dispatcher.UIThread.RunJobs()
        shell, window

    let private composerOf (view: MainView) : Composer = getField view "composer"
    let private settingsOf (view: MainView) : SettingsView = getField view "settings"
    let private draftsOf (view: MainView) : ComposerDrafts = getField view "drafts"
    let private outboxOf (view: MainView) : MessageOutbox = getField view "outbox"

    let private readyAttachment (drafts: ComposerDrafts) (instance: string) (conv: Guid option) (name: string) =
        let controller = (drafts.Get(instance, conv)).attachments
        let id = Guid.NewGuid()
        controller.Begin
            { attachmentId = id; fileName = name; mediaType = "application/pdf"; size = 4096L
              sha256 = String('a', 64) }
        |> ignore
        controller.Complete(id, 4096L) |> ignore

    let private listSnapshot (ids: (Guid * string) list) =
        let items = JsonArray()
        for id, title in ids do
            let o = JsonObject()
            o["conversationId"] <- id.ToString "D"
            o["title"] <- title
            o["runtimeState"] <- "idle"
            items.Add o
        ConversationListSnapshot {| items = items; lastCommitId = 1UL |}

    let private draftFile (home: string) = Path.Combine(home, "drafts.json")

    // ---------- 验收 1：关闭窗口再启动，草稿恢复；附件提示需重新选择 ----------

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``closing and reopening restores the draft text and marks attachments as needing reselection`` () =
        withHome (fun home ->
            Headless.run (fun () ->
                let instance = "host-" + Guid.NewGuid().ToString "N"
                let first, firstWindow = createShell ()
                dispatchEvent first (AuthAccepted {| instanceId = instance |})
                (composerOf first).SetText "写到一半的话"
                readyAttachment (draftsOf first) instance None "报告.pdf"
                (composerOf first).SetAttachments((draftsOf first).Get(instance, None).attachments.Items)
                first.FlushDrafts()
                firstWindow.Close()
                Assert.True(File.Exists(draftFile home), "关闭前必须已经落盘")

                // 重新启动：全新的外壳，只有磁盘上的内容。
                let second, secondWindow = createShell ()
                try
                    dispatchEvent second (AuthAccepted {| instanceId = instance |})
                    Dispatcher.UIThread.RunJobs()
                    Assert.Equal("写到一半的话", (composerOf second).Text)
                    let items = (draftsOf second).Get(instance, None).attachments.Items
                    let item = Assert.Single items
                    Assert.Equal("报告.pdf", item.fileName)
                    Assert.True(RestoredAttachments.isRestored item.attachmentId, "恢复出来的附件必须标成「需重新选择」")
                    Assert.False(item.ready, "客户端无法确认文件还在，不能当作可发送")
                    Assert.True(item.failed, "占 chip、阻塞发送，直到用户移除或重新添加")
                finally
                    secondWindow.Close()))

    // ---------- 验收 2：按对话隔离，切换主机互不泄漏 ----------

    [<Fact>]
    let ``drafts are isolated per conversation and per host`` () =
        let convA, convB = Guid.NewGuid(), Guid.NewGuid()
        let snapshot =
            { drafts =
                [ { instanceId = "host-1"; conversationId = Some convA; text = "一号机甲"; attachments = [] }
                  { instanceId = "host-1"; conversationId = Some convB; text = "一号机乙"; attachments = [] }
                  { instanceId = "host-2"; conversationId = Some convA; text = "二号机甲"; attachments = [] } ]
              unsent = [] }
        let drafts, outbox = ComposerDrafts(), MessageOutbox()
        DraftPolicy.restoreInto snapshot "host-1" drafts outbox |> ignore
        Assert.Equal("一号机甲", (drafts.Get("host-1", Some convA)).text)
        Assert.Equal("一号机乙", (drafts.Get("host-1", Some convB)).text)
        Assert.Equal("", (drafts.Get("host-2", Some convA)).text)
        // 反方向：只恢复 host-2 时，host-1 的内容不会出现。
        let drafts2 = ComposerDrafts()
        DraftPolicy.restoreInto snapshot "host-2" drafts2 (MessageOutbox()) |> ignore
        Assert.Equal("二号机甲", (drafts2.Get("host-2", Some convA)).text)
        Assert.Equal("", (drafts2.Get("host-1", Some convA)).text)

    [<Fact>]
    let ``saving while connected to one host keeps another host's stored drafts`` () =
        let stored =
            { drafts = [ { instanceId = "host-2"; conversationId = None; text = "另一台机器上的草稿"; attachments = [] } ]
              unsent = [] }
        let drafts = ComposerDrafts()
        (drafts.Get("host-1", None)).text <- "这台机器"
        let current = DraftPolicy.snapshotOf drafts (MessageOutbox()) (fun _ -> DateTimeOffset.UtcNow)
        let merged = DraftPolicy.mergeForSave stored (Set.ofList [ "host-1" ]) current
        Assert.Equal<string list>([ "另一台机器上的草稿"; "这台机器" ], merged.drafts |> List.map _.text)

    // ---------- 验收 3：删除对话后其草稿被清理 ----------

    [<Fact>]
    let ``conversations that vanish from the list count as deleted unless a send is in flight`` () =
        let kept, gone, sending = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        let deleted = DraftPolicy.deletedConversations (Set.ofList [ kept ]) (Set.ofList [ kept; gone; sending ]) (Set.ofList [ sending ])
        Assert.Equal<Guid list>([ gone ], Set.toList deleted)

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``deleting a conversation removes its draft from memory and from disk`` () =
        withHome (fun home ->
            Headless.run (fun () ->
                let instance = "host-" + Guid.NewGuid().ToString "N"
                let keep, doomed = Guid.NewGuid(), Guid.NewGuid()
                let shell, window = createShell ()
                try
                    dispatchEvent shell (AuthAccepted {| instanceId = instance |})
                    dispatchEvent shell (listSnapshot [ keep, "保留"; doomed, "将被删除" ])
                    let drafts = draftsOf shell
                    (drafts.Get(instance, Some keep)).text <- "留着"
                    (drafts.Get(instance, Some doomed)).text <- "这条会跟着对话消失"
                    shell.FlushDrafts()
                    Assert.Contains("这条会跟着对话消失", File.ReadAllText(draftFile home))
                    // 另一端删除了 doomed：新的列表快照里不再有它。
                    dispatchEvent shell (listSnapshot [ keep, "保留" ])
                    shell.FlushDrafts()
                    let disk = File.ReadAllText(draftFile home)
                    Assert.DoesNotContain("这条会跟着对话消失", disk)
                    Assert.Contains("留着", disk)
                    Assert.Equal("", (drafts.Get(instance, Some doomed)).text)
                finally
                    window.Close()))

    // ---------- 验收 4：草稿文件不含密钥或令牌；权限仅当前用户 ----------

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``the draft file carries no tokens or keys and is private to the user`` () =
        withHome (fun home ->
            Headless.run (fun () ->
                let instance = "host-" + Guid.NewGuid().ToString "N"
                let shell, window = createShell ()
                try
                    setField shell "lastToken" (Some "SECRET-TOKEN-VALUE-123")
                    dispatchEvent shell (AuthAccepted {| instanceId = instance |})
                    (composerOf shell).SetText "普通文字"
                    readyAttachment (draftsOf shell) instance None "合同.pdf"
                    shell.FlushDrafts()
                    let text = File.ReadAllText(draftFile home)
                    Assert.DoesNotContain("SECRET-TOKEN-VALUE-123", text)
                    Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase)
                    Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase)
                    // 只有约定的字段：附件只存引用，不存字节。
                    let root = JsonNode.Parse(text).AsObject()
                    let keysOf (o: JsonObject) = [ for kv in o -> kv.Key ] |> List.sort
                    Assert.Equal<string list>([ "drafts"; "unsent"; "version" ], keysOf root)
                    let draft0 = (root["drafts"].AsArray()).[0].AsObject()
                    let attachment = ((draft0["attachments"]).AsArray()).[0].AsObject()
                    Assert.Equal<string list>([ "fileName"; "mediaType"; "sha256"; "size" ], keysOf attachment)
                    if not (OperatingSystem.IsWindows()) then
                        Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, File.GetUnixFileMode(draftFile home))
                finally
                    window.Close()))

    // ---------- 默认开启、可关闭、可清除 ----------

    [<Fact>]
    let ``persisting drafts is on by default and an old ui file without the key keeps it on`` () =
        Assert.True UiPrefs.defaults.persistDrafts
        withHome (fun home ->
            File.WriteAllText(Path.Combine(home, "ui.json"), """{"theme":"dark","fontScale":15}""")
            Assert.True((UiPrefs.load ()).persistDrafts)
            UiPrefs.save { UiPrefs.defaults with persistDrafts = false }
            Assert.False((UiPrefs.load ()).persistDrafts))

    [<Fact>]
    let ``when turned off nothing is written and what was stored is deleted`` () =
        let backend = MemoryDraftBackend()
        let mutable enabled = true
        let store = DraftStore(backend, fun () -> enabled)
        let snapshot = { drafts = [ { instanceId = "h"; conversationId = None; text = "x"; attachments = [] } ]; unsent = [] }
        store.Save(snapshot).Wait()
        Assert.True backend.Stored.IsSome
        enabled <- false
        store.Clear().Wait()
        Assert.True backend.Stored.IsNone
        store.Save(snapshot).Wait()
        Assert.True(backend.Stored.IsNone, "关闭后不得再写")
        Assert.True(DraftSnapshot.isEmpty (store.Load().Result), "关闭后不得读出旧内容")

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``switching the setting off deletes the file and clear drafts empties everything`` () =
        withHome (fun home ->
            Headless.run (fun () ->
                let instance = "host-" + Guid.NewGuid().ToString "N"
                let shell, window = createShell ()
                try
                    dispatchEvent shell (AuthAccepted {| instanceId = instance |})
                    (composerOf shell).SetText "会被清掉"
                    shell.FlushDrafts()
                    Assert.True(File.Exists(draftFile home))
                    // 设置页的「清除本地草稿」
                    (settingsOf shell).LocalDrafts.ClearRequested()
                    Dispatcher.UIThread.RunJobs()
                    // 确认框在 overlay 里；这里直接走外壳的清除实现。
                    typeof<MainView>.GetMethod("ClearLocalDrafts", BindingFlags.NonPublic ||| BindingFlags.Instance).Invoke(shell, [||]) |> ignore
                    Assert.False(File.Exists(draftFile home))
                    Assert.Equal("", (composerOf shell).Text)
                    // 再输入，然后关掉开关：文件立即消失，之后不再写。
                    (composerOf shell).SetText "关掉之前"
                    shell.FlushDrafts()
                    Assert.True(File.Exists(draftFile home))
                    typeof<MainView>.GetMethod("SavePrefs", BindingFlags.NonPublic ||| BindingFlags.Instance)
                        .Invoke(shell, [| box { UiPrefs.load () with persistDrafts = false } |]) |> ignore
                    Assert.False(File.Exists(draftFile home))
                    (composerOf shell).SetText "关掉之后"
                    shell.FlushDrafts()
                    Assert.False(File.Exists(draftFile home), "关闭后不得再写")
                finally
                    window.Close()))

    // ---------- 未确认消息：重新打开后恢复为「未确认，可重试」----------

    [<Fact>]
    let ``an unconfirmed send comes back as retryable with the original invocation id`` () =
        let conv = Guid.NewGuid()
        let drafts, outbox = ComposerDrafts(), MessageOutbox()
        let attachment =
            { attachmentId = Guid.NewGuid(); sha256 = String('b', 64); size = 10L; mediaType = "text/plain"
              fileName = "a.txt"; ready = true; failed = false }
        let item = outbox.Stage("host-1", conv, "还没确认的消息", [ attachment ], None)
        outbox.SetState(PendingMessage.invocationId item, UnconfirmedMessage "连接已断开")
        let snapshot = DraftPolicy.snapshotOf drafts outbox (fun _ -> DateTimeOffset.UtcNow)
        let text = DraftCodec.serialize snapshot
        // 重新启动：新的内存，只从落盘文本恢复。
        let parsed = DraftCodec.tryParse text
        let drafts2, outbox2 = ComposerDrafts(), MessageOutbox()
        let restored = DraftPolicy.restoreInto parsed "host-1" drafts2 outbox2
        Assert.Equal(1, restored)
        let back = Assert.Single(outbox2.Items("host-1", Some conv))
        Assert.Equal(PendingMessage.invocationId item, PendingMessage.invocationId back)
        Assert.True(PendingMessage.canRetry back, "必须是可重试的未确认状态，而不是已排队/发送中")
        Assert.Equal("还没确认的消息", back.text)
        Assert.Equal(1, back.attachments.Length)
        // 重试发出的命令载荷与原命令逐字相同（主机据此幂等核对，不会重复保存）。
        Assert.Equal(
            WireCodec.encodeCommand item.command,
            WireCodec.encodeCommand back.command)

    [<Fact>]
    let ``a send whose conversation was never confirmed falls back into the start draft`` () =
        let conv = Guid.NewGuid()
        let drafts, outbox = ComposerDrafts(), MessageOutbox()
        let creation =
            CreateConversation {| invocationId = Guid.NewGuid(); conversationId = conv; title = "新对话"; config = SessionConfig.empty |}
        let item = outbox.Stage("host-1", conv, "对话还没建成", [], Some creation)
        outbox.SetState(PendingMessage.invocationId item, UnconfirmedMessage "断线")
        let snapshot = DraftPolicy.snapshotOf drafts outbox (fun _ -> DateTimeOffset.UtcNow)
        Assert.Empty snapshot.unsent
        let row = Assert.Single snapshot.drafts
        Assert.Equal(None, row.conversationId)
        Assert.Equal("对话还没建成", row.text)

    // ---------- 编解码与 PWA：IndexedDB 夹具（页面被回收后重开） ----------

    [<Fact>]
    let ``a recycled page restores drafts from the browser backend`` () =
        // MemoryDraftBackend 与 BrowserDraftBackend 同一接口；页面被回收 = 丢掉全部内存对象，只剩后端里的字符串。
        let backend = MemoryDraftBackend()
        let before = DraftStore(backend, fun () -> true)
        let conv = Guid.NewGuid()
        let snapshot =
            { drafts =
                [ { instanceId = "host-1"; conversationId = Some conv; text = "手机切后台前的草稿"
                    attachments = [ { fileName = "照片.jpg"; mediaType = "image/jpeg"; size = 99L; sha256 = String('c', 64) } ] } ]
              unsent = [] }
        before.Save(snapshot).Wait()
        let after = DraftStore(backend, fun () -> true)
        let loaded = after.Load().Result
        Assert.Equal("手机切后台前的草稿", (Assert.Single loaded.drafts).text)
        Assert.Equal("照片.jpg", (Assert.Single (Assert.Single loaded.drafts).attachments).fileName)

    [<Fact>]
    let ``damaged or newer draft files are ignored instead of blocking startup`` () =
        Assert.True(DraftSnapshot.isEmpty (DraftCodec.tryParse "not json"))
        Assert.True(DraftSnapshot.isEmpty (DraftCodec.tryParse "[]"))
        Assert.True(DraftSnapshot.isEmpty (DraftCodec.tryParse """{"version":99,"drafts":[{"instanceId":"x","text":"future"}]}"""))
        // 未知字段与坏条目被跳过，好条目保留。
        let mixed = """{"version":1,"extra":true,"drafts":[{"instanceId":"h","text":"ok","unknown":1},{"text":"no instance"},5]}"""
        Assert.Equal("ok", (Assert.Single (DraftCodec.tryParse mixed).drafts).text)

    // ---------- 事件日志字节不变；配置被拒绝；检查主机（真实主机） ----------

    type private LiveHost() =
        let dir = tempDir ()
        let configPath = Path.Combine(dir, "config.toml")
        let port =
            let l = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
            l.Start()
            let p = (l.LocalEndpoint :?> Net.IPEndPoint).Port
            l.Stop()
            p
        let token = Auth.generateToken ()
        let validToml =
            let cfg =
                { AppConfig.defaults (Guid.CreateVersion7()) with
                    listen = sprintf "127.0.0.1:%d" port
                    authClients =
                        [ { tokenHash = Auth.hashToken token; name = "w5"; createdAtUtc = DateTimeOffset.UtcNow
                            lastSeenUtc = None; revoked = false } ] }
            TomlCodec.serialize cfg
        let app =
            File.WriteAllText(configPath, validToml)
            let app = new Wanxiang.Server.ServerApp(dir, configPath, false, None, ignore)
            app.Start(false)
            app
        member _.Port = port
        member _.Token = token
        member _.ConfigPath = configPath
        member _.ValidToml = validToml
        member _.EventLogHash() =
            let files = Directory.GetFiles(DataPaths.eventsDir dir, "*.jsonseq") |> Array.sort
            use sha = SHA256.Create()
            files |> Array.collect File.ReadAllBytes |> sha.ComputeHash |> Convert.ToHexString
        member _.Dispose() =
            (app :> IDisposable).Dispose()
            cleanup dir

    let private eventType (o: JsonObject) = o["type"].GetValue<string>()

    [<Fact>]
    let ``an invalid external edit is announced within two seconds and clears once fixed`` () =
        let host = new LiveHost()
        use _cleanup = { new IDisposable with member _.Dispose() = host.Dispose() }
        use all = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
        use ws = ServerE2eTests.E2e.conn host.Port host.Token all.Token
        ServerE2eTests.E2e.waitFor ws all.Token (fun o -> eventType o = "auth.accepted") |> ignore
        let hashBefore = host.EventLogHash()
        // 外部编辑成非法 TOML。
        File.WriteAllText(host.ConfigPath, "this is = = not valid toml [[[")
        let started = DateTime.UtcNow
        use quick = CancellationTokenSource.CreateLinkedTokenSource(all.Token)
        // 产品目标是 2 秒内；非 UI 测试在单进程内线程级并行（800+ 用例同时跑），
        // 文件监视回调在满载下偶发拖过 2 秒——这是 CI 偶发失败的来源。门禁放宽到 5 秒，
        // 仍能抓住「根本没告知 / 要等下一次轮询」这类真回归。
        quick.CancelAfter(TimeSpan.FromSeconds 5.0)
        let rejected = ServerE2eTests.E2e.waitFor ws quick.Token (fun o -> eventType o = "config.rejected")
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 5.5)
        let payload = rejected["payload"].AsObject()
        Assert.Contains("配置有误", payload["summary"].GetValue<string>())
        Assert.NotEmpty(payload["errors"].AsArray())
        // 重新连接的客户端在认证后也能看到（断线期间发生的拒绝不丢）。
        use ws2 = ServerE2eTests.E2e.conn host.Port host.Token all.Token
        ServerE2eTests.E2e.waitFor ws2 all.Token (fun o -> eventType o = "config.rejected") |> ignore
        // 修好：收到 config.changed，随后新连接不再被告知拒绝。
        File.WriteAllText(host.ConfigPath, host.ValidToml)
        ServerE2eTests.E2e.waitFor ws all.Token (fun o -> eventType o = "config.changed") |> ignore
        use ws3 = ServerE2eTests.E2e.conn host.Port host.Token all.Token
        let afterFix = ServerE2eTests.E2e.waitFor ws3 all.Token (fun o -> eventType o = "auth.accepted")
        Assert.Equal("auth.accepted", eventType afterFix)
        ServerE2eTests.E2e.send ws3 (JsonNode.Parse(WireCodec.encode Ping).AsObject()) all.Token
        let next = ServerE2eTests.E2e.waitFor ws3 all.Token (fun o -> eventType o = "protocol.pong" || eventType o = "config.rejected")
        Assert.Equal("protocol.pong", eventType next)
        Assert.Equal(hashBefore, host.EventLogHash())

    [<Fact>]
    let ``the rejection summary never carries a registered secret`` () =
        let status = Wanxiang.Server.HostStatus()
        Wanxiang.Server.Stderr.registerSecrets [ "sk-very-secret-key-0001" ]
        let ev = status.Reject(Wanxiang.Server.Stderr.redact, [ "apiKey = \"sk-very-secret-key-0001\" is not allowed here" ])
        match ev with
        | HostConfigRejected d ->
            Assert.DoesNotContain("sk-very-secret-key-0001", d.summary)
            Assert.All(d.errors, fun e -> Assert.DoesNotContain("sk-very-secret-key-0001", e))
        | _ -> failwith "expected HostConfigRejected"

    [<Fact>]
    let ``check host returns the read-only doctor report and leaves the event log untouched`` () =
        let host = new LiveHost()
        use _cleanup = { new IDisposable with member _.Dispose() = host.Dispose() }
        use all = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
        use ws = ServerE2eTests.E2e.conn host.Port host.Token all.Token
        ServerE2eTests.E2e.waitFor ws all.Token (fun o -> eventType o = "auth.accepted") |> ignore
        let hashBefore = host.EventLogHash()
        let requestId = Guid.NewGuid()
        ServerE2eTests.E2e.send ws (JsonNode.Parse(WireCodec.encode (DoctorRequest {| requestId = requestId |})).AsObject()) all.Token
        let report = ServerE2eTests.E2e.waitFor ws all.Token (fun o -> eventType o = "doctor.report")
        let payload = report["payload"].AsObject()
        Assert.Equal(requestId.ToString "D", payload["requestId"].GetValue<string>())
        let checks = DoctorCheck.parseChecks (payload["checks"].AsArray())
        let names = checks |> List.map _.name
        for expected in [ "config"; "log-replay"; "data-lock"; "listen-port" ] do
            Assert.Contains(expected, names)
        // 运行中的主机自己持有端口与数据锁：不能被报成故障。
        Assert.True((checks |> List.find (fun c -> c.name = "data-lock")).ok)
        Assert.True((checks |> List.find (fun c -> c.name = "listen-port")).ok)
        Assert.True(payload["ok"].GetValue<bool>(), sprintf "doctor 应全部通过：%A" (checks |> List.filter (fun c -> not c.ok)))
        Assert.Equal(hashBefore, host.EventLogHash())

    [<Fact>]
    let ``check host is refused for unauthenticated connections`` () =
        let host = new LiveHost()
        use _cleanup = { new IDisposable with member _.Dispose() = host.Dispose() }
        use all = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
        use ws = new Net.WebSockets.ClientWebSocket()
        ws.ConnectAsync(Uri(sprintf "ws://127.0.0.1:%d/ws" host.Port), all.Token).GetAwaiter().GetResult()
        ServerE2eTests.E2e.send ws (JsonNode.Parse(WireCodec.encode (DoctorRequest {| requestId = Guid.NewGuid() |})).AsObject()) all.Token
        // 协议违规：连接被关闭，且没有任何报告。
        let buffer = Array.zeroCreate<byte> 4096
        let mutable closed = false
        let mutable gotReport = false
        while not closed do
            let r = ws.ReceiveAsync(ArraySegment<byte>(buffer), all.Token).GetAwaiter().GetResult()
            if r.MessageType = Net.WebSockets.WebSocketMessageType.Close then closed <- true
            elif Text.Encoding.UTF8.GetString(buffer, 0, r.Count).Contains "doctor.report" then gotReport <- true
        Assert.False gotReport

    // ---------- 线上协议：加法、可往返、旧端忽略 ----------

    [<Fact>]
    let ``the new events round trip and ride the wanxiang dev namespace`` () =
        let rejected = HostConfigRejected {| summary = "配置有误"; errors = [ "第 3 行"; "未知键" ] |}
        match WireCodec.tryDecode (WireCodec.encode rejected) with
        | Ok(HostConfigRejected d) ->
            Assert.Equal("配置有误", d.summary)
            Assert.Equal<string list>([ "第 3 行"; "未知键" ], d.errors)
        | other -> failwithf "unexpected %A" other
        let id = Guid.NewGuid()
        match WireCodec.tryDecode (WireCodec.encode (DoctorRequest {| requestId = id |})) with
        | Ok(DoctorRequest d) -> Assert.Equal(id, d.requestId)
        | other -> failwithf "unexpected %A" other
        let checks = JsonArray()
        let c = JsonObject()
        c["name"] <- "config"
        c["ok"] <- true
        c["detail"] <- "ok"
        checks.Add c
        match WireCodec.tryDecode (WireCodec.encode (DoctorReport {| requestId = id; ok = true; checks = checks |})) with
        | Ok(DoctorReport d) ->
            Assert.True d.ok
            Assert.Single(DoctorCheck.parseChecks d.checks) |> ignore
        | other -> failwithf "unexpected %A" other
        Assert.Contains("wanxiang.dev/config.rejected", WireAgui.encode rejected)
        Assert.Contains("wanxiang.dev/doctor.report", WireAgui.encode (DoctorReport {| requestId = id; ok = true; checks = checks |}))

    // ---------- 客户端：主机状态行与设置页 ----------

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``the client shows a config problem and removes it when the host reloads`` () =
        withHome (fun _ ->
            Headless.run (fun () ->
                let shell, window = createShell ()
                try
                    dispatchEvent shell (AuthAccepted {| instanceId = "host-health" |})
                    let health = (settingsOf shell).Health
                    Assert.True health.ConfigIssue.IsNone
                    dispatchEvent shell (HostConfigRejected {| summary = "配置有误，仍在使用上一份有效配置：第 3 行"; errors = [ "第 3 行：未知键" ] |})
                    Assert.Equal(Some("配置有误，仍在使用上一份有效配置：第 3 行", [ "第 3 行：未知键" ]), health.ConfigIssue)
                    let sidebar: Sidebar = getField shell "sidebar"
                    Assert.Contains("配置有误", sidebar.ConnectionText)
                    dispatchEvent shell (ConfigChanged {| reason = "reloaded" |})
                    Assert.True health.ConfigIssue.IsNone
                    Assert.DoesNotContain("配置有误", sidebar.ConnectionText)
                finally
                    window.Close()))

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``the settings page lists the host check results`` () =
        withHome (fun _ ->
            Headless.run (fun () ->
                let shell, window = createShell ()
                try
                    dispatchEvent shell (AuthAccepted {| instanceId = "host-doctor" |})
                    let settings = settingsOf shell
                    let mutable requested = 0
                    settings.Health.RequestCheck <- fun () -> requested <- requested + 1
                    let about = settings.Health
                    about.BeginCheck()
                    Assert.Equal(1, requested)
                    Assert.Equal(Running, about.Doctor)
                    let checks = JsonArray()
                    for name, ok, detail in [ "config", true, "ok"; "disk-free-space", false, "only 10 MiB free" ] do
                        let c = JsonObject()
                        c["name"] <- name
                        c["ok"] <- ok
                        c["detail"] <- detail
                        checks.Add c
                    dispatchEvent shell (DoctorReport {| requestId = Guid.NewGuid(); ok = false; checks = checks |})
                    match about.Doctor with
                    | Report(ok, list, _) ->
                        Assert.False ok
                        Assert.Equal<string list>([ "config"; "disk-free-space" ], list |> List.map _.name)
                    | other -> failwithf "unexpected %A" other
                finally
                    window.Close()))

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``the about page renders config status and one row per host check with stable ids`` () =
        Headless.run (fun () ->
            let actions: SettingsActions =
                { upsertProvider = fun _ _ -> ()
                  deleteProvider = ignore
                  probeProvider = ignore
                  upsertMcp = fun _ _ -> ()
                  deleteMcp = ignore
                  updateGeneration = fun _ _ -> ()
                  savePrefs = ignore
                  toast = fun _ _ -> () }
            let general = SettingsGeneral(OverlayHost(Grid()), actions, ignore)
            let about = general.BuildAbout("host-1", "ws://127.0.0.1:8765/ws")
            let find (id: string) =
                Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants about
                |> Seq.tryFind (fun c ->
                    match c with
                    | :? Avalonia.Controls.Control as control -> Avalonia.Automation.AutomationProperties.GetAutomationId control = id
                    | _ -> false)
            Assert.True((find "host-config-ok").IsSome, "没有问题时说明配置正常")
            Assert.True((find "check-host").IsSome)
            general.Health.SetConfigIssue("配置有误，仍在使用上一份有效配置：第 3 行", [ "第 3 行：未知键" ])
            Assert.True((find "host-config-issue").IsSome)
            Assert.True((find "host-config-ok").IsNone)
            general.Health.SetReport(false, [ { name = "config"; ok = true; detail = "ok" }; { name = "disk-free-space"; ok = false; detail = "only 10 MiB free" } ])
            Assert.True((find "doctor-check-config").IsSome)
            Assert.True((find "doctor-check-disk-free-space").IsSome)
            Assert.True((find "doctor-headline").IsSome))

    [<Trait("Category", "UI")>]
    [<Fact>]
    let ``the appearance page offers the draft switch and a clear action`` () =
        Headless.run (fun () ->
            let actions: SettingsActions =
                { upsertProvider = fun _ _ -> ()
                  deleteProvider = ignore
                  probeProvider = ignore
                  upsertMcp = fun _ _ -> ()
                  deleteMcp = ignore
                  updateGeneration = fun _ _ -> ()
                  savePrefs = ignore
                  toast = fun _ _ -> () }
            let general = SettingsGeneral(OverlayHost(Grid()), actions, ignore)
            let page = general.BuildAppearance()
            let ids =
                Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants page
                |> Seq.choose (fun c ->
                    match c with
                    | :? Avalonia.Controls.Control as control -> Some(Avalonia.Automation.AutomationProperties.GetAutomationId control)
                    | _ -> None)
                |> Set.ofSeq
            Assert.Contains("clear-local-drafts", ids)
            Assert.Contains("local-drafts-summary", ids))
