namespace Wanxiang.Tests

open System
open System.Reflection

/// 决策 421 续：Avalonia 的 `Dispatcher.UIThread` 由**第一个**碰它的线程创建并归其所有。
/// 纯逻辑测试（不在 `Headless.run` 里）若构造了任何 AvaloniaObject（如 `ResourceDictionary()`），
/// UI 调度器就绑到了 xunit 工作线程上；下一个 UI 测试的 PerTest 装配
/// （`EnsureIsolatedApplication` → `DefaultRenderLoop.Add`）随即抛
/// 「The calling thread cannot access this object because a different thread owns it」，
/// 看起来像那个 UI 测试自己坏了，与真正的肇事测试相隔甚远、且随顺序时有时无。
///
/// 本守卫在每个测试结束后检查：UI 调度器若归**当前测试线程**所有，就让**肇事测试本身**确定性失败并指名。
/// headless 会话线程永远不是 xunit 工作线程，所以正常的 UI 测试不会误报。
/// 读的是 Avalonia 的私有静态字段 `s_uiThread`；字段改名时守卫明确报错，而不是静默失效。
type UiDispatcherGuardAttribute() =
    inherit Xunit.v3.BeforeAfterTestAttribute()
    static let flags = BindingFlags.NonPublic
    static let uiField = typeof<Avalonia.Threading.Dispatcher>.GetField("s_uiThread", BindingFlags.Static ||| flags)
    static let threadField = typeof<Avalonia.Threading.Dispatcher>.GetField("_thread", BindingFlags.Instance ||| flags)

    static member internal UiDispatcherThreadId() : int option =
        if isNull uiField || isNull threadField then
            failwith "UiDispatcherGuard：Avalonia Dispatcher 的私有字段 s_uiThread/_thread 不存在了，需更新守卫"
        match uiField.GetValue null with
        | null -> None
        | d ->
            match threadField.GetValue d with
            | :? Threading.Thread as t -> Some t.ManagedThreadId
            | _ -> None

    override _.After(_: MethodInfo, test: Xunit.v3.IXunitTest) =
        match UiDispatcherGuardAttribute.UiDispatcherThreadId() with
        | Some id when id = Environment.CurrentManagedThreadId ->
            failwithf
                "测试「%s」在 xunit 工作线程 %d 上创建了 Avalonia UI 调度器（构造了 AvaloniaObject 却没包 Headless.run），会毒化后续 UI 测试（决策 421）"
                test.TestDisplayName id
        | _ -> ()

module UiDispatcherGuardRegistration =
    [<assembly: UiDispatcherGuard>]
    do ()
