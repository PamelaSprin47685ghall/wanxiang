namespace Wanxiang.Tests

open System
open System.Reflection
open Xunit
open Wanxiang.UI

module ToolCallStatusTests =

    /// 通过反射调用 MessageCard 模块内的 private/internal resolveToolCallStatus 方法。
    /// 无论 F# 编译器将其编译为普通多参数静态方法还是柯里化方法，均能可靠调用。
    let private resolveStatus (isStreaming: bool) (call: ToolCallView) : ToolCallStatus =
        let msgCardType = typeof<ToolCallView>.Assembly.GetType("Wanxiang.UI.MessageCard")
        if isNull msgCardType then
            failwith "未能找到类型 Wanxiang.UI.MessageCard"
        let flags = BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public
        let methodInfo = msgCardType.GetMethod("resolveToolCallStatus", flags)
        if isNull methodInfo then
            let methods = msgCardType.GetMethods(flags) |> Array.map (fun m -> m.Name) |> String.concat ", "
            failwithf "在 Wanxiang.UI.MessageCard 上未找到方法 resolveToolCallStatus。可用静态方法：%s" methods
        let parameters = methodInfo.GetParameters()
        if parameters.Length = 2 then
            methodInfo.Invoke(null, [| box isStreaming; box call |]) :?> ToolCallStatus
        elif parameters.Length = 1 then
            let func = methodInfo.Invoke(null, [| box isStreaming |])
            let invokeMethod = func.GetType().GetMethod("Invoke", [| typeof<ToolCallView> |])
            invokeMethod.Invoke(func, [| box call |]) :?> ToolCallStatus
        else
            failwithf "resolveToolCallStatus 的参数数量不符合预期：%d" parameters.Length

    let private makeCall (result: string option) : ToolCallView =
        { callId = "call_test_001"
          name = "sample_tool"
          argumentsJson = """{"query":"test"}"""
          result = result }

    // =========================================================================
    // 状态 1：result = Some 无错误文本 → Success
    // =========================================================================

    [<Theory>]
    [<InlineData("操作已成功完成", false)>]
    [<InlineData("操作已成功完成", true)>]
    [<InlineData("""{"status":"ok","count":42}""", false)>]
    [<InlineData("""{"result":"hello world"}""", false)>]
    [<InlineData("""{"status":"success","data":[1,2,3]}""", false)>]
    [<InlineData("""{"success":true,"result":"completed"}""", false)>]
    [<InlineData("""{"error":null,"result":"ok"}""", false)>]
    [<InlineData("Plain text output without any error", false)>]
    [<InlineData("This documentation explains how 404 not found errors should be handled", false)>]
    [<InlineData("No syntax error found in lines 1-100", false)>]
    [<InlineData("To prevent command not found errors, make sure the path is in PATH", false)>]
    [<InlineData("The access denied dialog was redesigned for improved accessibility", false)>]
    let ``resolveToolCallStatus returns Success when result has no error pattern`` (resultText: string, isStreaming: bool) =
        let call = makeCall (Some resultText)
        let status = resolveStatus isStreaming call
        Assert.Equal(ToolCallStatus.Success, status)
        Assert.Equal("已完成", ToolCallStatus.badgeText status)
        Assert.True(ToolCallStatus.isTerminal status)

    // =========================================================================
    // 状态 2：result = Some 含错误模式 → Failed
    // =========================================================================

    [<Theory>]
    [<InlineData("error: connection refused", "connection refused")>]
    [<InlineData("Error: 未找到指定资源", "未找到指定资源")>]
    [<InlineData("error:   ", "执行失败")>]
    [<InlineData("""{"error": "参数校验失败"}""", "参数校验失败")>]
    [<InlineData("""{"error": {"message": "权限不足"}}""", "权限不足")>]
    [<InlineData("""{"isError": true}""", "执行失败")>]
    [<InlineData("500 internal server error: gateway crash", "500 Internal Server Error")>]
    [<InlineData("internal server error encountered", "Internal Server Error")>]
    [<InlineData("command failed with exit code 1", "command failed with exit code 1")>]
    [<InlineData("403 Forbidden", "403 Forbidden")>]
    [<InlineData("404 Not Found", "404 Not Found")>]
    [<InlineData("401 Unauthorized", "401 Unauthorized")>]
    [<InlineData("HTTP/1.1 403 Forbidden", "403 Forbidden")>]
    [<InlineData("HTTP 404 Not Found", "404 Not Found")>]
    [<InlineData("401: Unauthorized", "401 Unauthorized")>]
    [<InlineData("Permission denied", "Permission denied")>]
    [<InlineData("access denied", "access denied")>]
    [<InlineData("bash: /bin/secret: Permission denied", "bash: /bin/secret: Permission denied")>]
    [<InlineData("Access denied for user 'admin'@'localhost'", "Access denied for user 'admin'@'localhost'")>]
    [<InlineData("command not found", "command not found")>]
    [<InlineData("bash: mytool: command not found", "bash: mytool: command not found")>]
    [<InlineData("zsh: command not found: mytool", "zsh: command not found: mytool")>]
    [<InlineData("fatal: not a git repository (or any of the parent directories): .git", "not a git repository (or any of the parent directories): .git")>]
    [<InlineData("fatal:   ", "执行失败")>]
    [<InlineData("""{"status":"error","message":"rate limit exceeded"}""", "rate limit exceeded")>]
    [<InlineData("""{"status":"error"}""", "执行失败")>]
    [<InlineData("""{"status":"failed","reason":"request timeout"}""", "request timeout")>]
    [<InlineData("""{"status":"failure"}""", "执行失败")>]
    [<InlineData("""{"success":false,"message":"item not found"}""", "item not found")>]
    [<InlineData("""{"success":false}""", "执行失败")>]
    [<InlineData("""{"is_error":true,"msg":"invalid token"}""", "invalid token")>]
    let ``resolveToolCallStatus returns Failed when result matches error patterns`` (errorResultText: string, expectedSummary: string) =
        let call = makeCall (Some errorResultText)
        let status = resolveStatus false call
        match status with
        | ToolCallStatus.Failed (Some summary) ->
            Assert.Equal(expectedSummary, summary)
        | other ->
            Assert.Fail(sprintf "预期 ToolCallStatus.Failed(Some %A)，但得到 %A" expectedSummary other)
        Assert.Equal("执行失败", ToolCallStatus.badgeText status)
        Assert.True(ToolCallStatus.isTerminal status)

    [<Fact>]
    let ``resolveToolCallStatus extracts last line from python traceback as failure summary`` () =
        let traceback =
            "Traceback (most recent call last):\n" +
            "  File \"script.py\", line 15, in <module>\n" +
            "    main()\n" +
            "ZeroDivisionError: division by zero\n"
        let call = makeCall (Some traceback)
        let status = resolveStatus false call
        match status with
        | ToolCallStatus.Failed (Some summary) ->
            Assert.Equal("ZeroDivisionError: division by zero", summary)
        | other ->
            Assert.Fail(sprintf "预期提取出 Python traceback 异常摘要，但得到 %A" other)
        Assert.Equal("执行失败", ToolCallStatus.badgeText status)
        Assert.True(ToolCallStatus.isTerminal status)

    // =========================================================================
    // 状态 3：result = None + streaming=true → Running
    // =========================================================================

    [<Fact>]
    let ``resolveToolCallStatus returns Running when result is None and streaming is true`` () =
        let call = makeCall None
        let status = resolveStatus true call
        Assert.Equal(ToolCallStatus.Running, status)
        Assert.Equal("执行中", ToolCallStatus.badgeText status)
        Assert.False(ToolCallStatus.isTerminal status)

    // =========================================================================
    // 状态 4：result = None + streaming=false → Unknown
    // =========================================================================

    [<Fact>]
    let ``resolveToolCallStatus returns Unknown when result is None and streaming is false`` () =
        let call = makeCall None
        let status = resolveStatus false call
        Assert.Equal(ToolCallStatus.Unknown, status)
        Assert.Equal("结果未知", ToolCallStatus.badgeText status)
        Assert.True(ToolCallStatus.isTerminal status)

    // =========================================================================
    // ToolCallStatus.badgeText 与 isTerminal 的穷尽式契约验证
    // =========================================================================

    [<Fact>]
    let ``ToolCallStatus badgeText returns exact expected text for all states`` () =
        Assert.Equal("执行中", ToolCallStatus.badgeText ToolCallStatus.Running)
        Assert.Equal("已完成", ToolCallStatus.badgeText ToolCallStatus.Success)
        Assert.Equal("执行失败", ToolCallStatus.badgeText (ToolCallStatus.Failed(Some "特定错误")))
        Assert.Equal("执行失败", ToolCallStatus.badgeText (ToolCallStatus.Failed None))
        Assert.Equal("结果未知", ToolCallStatus.badgeText ToolCallStatus.Unknown)

    [<Fact>]
    let ``ToolCallStatus isTerminal returns true only for terminal states`` () =
        Assert.False(ToolCallStatus.isTerminal ToolCallStatus.Running)
        Assert.True(ToolCallStatus.isTerminal ToolCallStatus.Success)
        Assert.True(ToolCallStatus.isTerminal (ToolCallStatus.Failed(Some "错误")))
        Assert.True(ToolCallStatus.isTerminal (ToolCallStatus.Failed None))
        Assert.True(ToolCallStatus.isTerminal ToolCallStatus.Unknown)
