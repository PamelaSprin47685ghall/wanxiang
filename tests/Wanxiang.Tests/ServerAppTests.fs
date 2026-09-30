namespace Wanxiang.Tests

open System
open System.IO
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Wanxiang.Config
open Wanxiang.Core
open Wanxiang.Protocol
open Wanxiang.Store
open Wanxiang.Server
open Wanxiang.Tests.Helpers

module ServerAppTests =

    let private createTestDir () =
        let dir = Path.Combine(Path.GetTempPath(), sprintf "wanxiang-srv-test-%s" (Guid.NewGuid().ToString("N")))
        Directory.CreateDirectory(dir) |> ignore
        dir

    let private writeConfigFile dir port listenHost =
        let configPath = Path.Combine(dir, "config.toml")
        let instanceId = Guid.CreateVersion7()
        let token = "test-token-secret"
        let hash = Auth.hashToken token
        let cfg =
            { AppConfig.defaults instanceId with
                listen = sprintf "%s:%d" listenHost port
                generation = { (AppConfig.defaults instanceId).generation with autoTitle = false }
                providers =
                    Map.ofList
                        [ "mock-provider",
                          { id = "mock-provider"
                            kind = "openai"
                            label = "Mock Provider"
                            baseUrl = "http://127.0.0.1:1/v1"
                            apiKey = Some "sk-mock-key-12345"
                            models = [ "mock-model" ]
                            defaultModel = "mock-model"
                            timeoutSeconds = 5
                            maxRetries = 0
                            enabled = true
                            promptCaching = false
                            headers = Map.empty
                            extraJson = None } ]
                authClients =
                    [ { tokenHash = hash
                        name = "srv-test-client"
                        createdAtUtc = DateTimeOffset.UtcNow
                        lastSeenUtc = None
                        revoked = false } ] }
        File.WriteAllText(configPath, TomlCodec.serialize cfg)
        configPath

    // =========================================================================
    // 1. DataLock 单实例排他锁测试（Wanxiang.Store.DataLock 真实行为契约）
    // =========================================================================

    [<Fact>]
    let ``DataLock 两个实例无法同时锁定同一目录，释放后可重新获取`` () =
        let dir = createTestDir ()
        try
            // 首次获取必须成功
            let lockResult1 = DataLock.Acquire dir
            match lockResult1 with
            | Error e -> failwithf "初次获取锁不应失败: %s" e
            | Ok lock1 ->
                // 第二次获取同一目录锁必须返回 Error，绝不静默运行
                let lockResult2 = DataLock.Acquire dir
                match lockResult2 with
                | Ok _ -> failwith "同一目录不应被两个锁实例同时获取"
                | Error err ->
                    Assert.Contains("locked by another process", err)

                // 释放第一个锁
                (lock1 :> IDisposable).Dispose()

                // 释放后再获取必须成功
                let lockResult3 = DataLock.Acquire dir
                match lockResult3 with
                | Error e -> failwithf "锁释放后重新获取失败: %s" e
                | Ok lock3 ->
                    (lock3 :> IDisposable).Dispose()
        finally
            try Directory.Delete(dir, true) with _ -> ()

    // =========================================================================
    // 2. ServerApp 构造与生命周期契约测试（五参数真实签名）
    // =========================================================================

    [<Fact>]
    let ``ServerApp 两个实例无法同时在同一 dataDir 启动（严格单进程保护）`` () =
        let dir = createTestDir ()
        try
            let port1 = Random.Shared.Next(35000, 45000)
            let configPath1 = writeConfigFile dir port1 "127.0.0.1"

            // 构造第一个实例（持有目录锁）
            let app1 = new ServerApp(dir, configPath1, false, None, ignore)

            // 第二个实例尝试在相同 dataDir 构造必须因为抢占锁而抛出异常（failwith）
            let ex = Assert.Throws<Exception>(fun () ->
                new ServerApp(dir, configPath1, false, None, ignore) |> ignore)
            Assert.Contains("locked by another process", ex.Message)

            // 释放 app1
            (app1 :> IDisposable).Dispose()

            // app1 释放锁后，新实例可以正常构造并持有锁
            let app2 = new ServerApp(dir, configPath1, false, None, ignore)
            (app2 :> IDisposable).Dispose()
        finally
            try Directory.Delete(dir, true) with _ -> ()

    [<Fact>]
    let ``ServerApp 正常启动与停止生命周期（Start 与 Stop 幂等释放）`` () =
        let dir = createTestDir ()
        try
            let port = Random.Shared.Next(45001, 55000)
            let configPath = writeConfigFile dir port "127.0.0.1"

            let mutable loggedMessages = []
            let logInfo msg = loggedMessages <- msg :: loggedMessages

            let app = new ServerApp(dir, configPath, false, None, logInfo)
            app.Start(false)

            // 验证已记录启动监听日志
            Assert.Contains(loggedMessages, fun m -> m.Contains("wanxiang server listening on"))

            // 停止服务器
            app.Stop()

            // 验证 Stop 幂等：再次调用 Dispose 不会抛出异常
            (app :> IDisposable).Dispose()
        finally
            try Directory.Delete(dir, true) with _ -> ()

    // =========================================================================
    // 3. ServerApp 启动遇到截尾记录时的恢复（Replay 与 onTruncated 契约）
    // =========================================================================

    [<Fact>]
    let ``ServerApp 启动时正确处理截尾事件文件并恢复运行`` () =
        let dir = createTestDir ()
        try
            let port = Random.Shared.Next(35000, 45000)
            let configPath = writeConfigFile dir port "127.0.0.1"

            // 预先写入正常的事件结构目录
            DataPaths.ensureDataDirs dir
            let eventPath = DataPaths.eventFilePath dir DateTime.UtcNow

            // 写入一个半截损坏的非正常字节（模拟崩溃时的残余字节）
            let brokenBytes = Encoding.UTF8.GetBytes(""" {"streamId":"partial-json-not-closed""")
            File.WriteAllBytes(eventPath, brokenBytes)

            // 启动 ServerApp（fix = true 执行截尾恢复）
            let app = new ServerApp(dir, configPath, true, None, ignore)
            app.Start(false)

            // 能够正常启停，表明截尾容错与启动回放正常完成
            app.Stop()
            (app :> IDisposable).Dispose()
        finally
            try Directory.Delete(dir, true) with _ -> ()

    // =========================================================================
    // 4. ServerApp 暴露非本地监听且无 TLS 时的安全告警分支测试
    // =========================================================================

    [<Fact>]
    let ``ServerApp 监听 0.0.0.0 且无 TLS 时正常启动并执行 WarnIfExposedWithoutTls 逻辑`` () =
        let dir = createTestDir ()
        try
            let port = Random.Shared.Next(45000, 55000)
            let configPath = writeConfigFile dir port "0.0.0.0"

            let app = new ServerApp(dir, configPath, false, None, ignore)
            app.Start(false)

            // 验证能够正常安全停止并释放端口与锁
            app.Stop()
            (app :> IDisposable).Dispose()
        finally
            try Directory.Delete(dir, true) with _ -> ()


    // =========================================================================
    // 5. ServerApp 反复启停幂等性与资源释放深度契约测试
    // =========================================================================

    [<Fact>]
    let ``ServerApp 连续多次 Stop 与 Dispose 保持严格幂等不抛出异常`` () =
        let dir = createTestDir ()
        try
            let port = Random.Shared.Next(45000, 55000)
            let configPath = writeConfigFile dir port "127.0.0.1"

            let app = new ServerApp(dir, configPath, false, None, ignore)
            app.Start(false)

            // 第一次主动 Stop
            app.Stop()

            // 第二次重复 Stop，必须静默幂等
            app.Stop()

            // Dispose 接口实现必须幂等且不抛出异常
            (app :> IDisposable).Dispose()
            (app :> IDisposable).Dispose()
        finally
            try Directory.Delete(dir, true) with _ -> ()

    [<Fact>]
    let ``ServerApp 启动时配置不存在安全回退到默认配置并正常启停`` () =
        let dir = createTestDir ()
        try
            let nonExistentConfig = Path.Combine(dir, "does-not-exist.toml")
            let app = new ServerApp(dir, nonExistentConfig, false, None, ignore)
            // 配置不存在时生产实现记录错误并安全回退到默认配置，不裸抛未捕获异常
            app.Start(false)
            app.Stop()
            (app :> IDisposable).Dispose()
        finally
            try Directory.Delete(dir, true) with _ -> ()

