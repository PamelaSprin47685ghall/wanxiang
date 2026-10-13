/// PWA 移动端：高 DPR 画布尺寸、安全区、软键盘、恢复即重连（第 213 节）。
module Wanxiang.Tests.PwaMobileTests

open System
open System.Diagnostics
open System.IO
open Xunit

let private repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then failwith "repo root not found"
        elif File.Exists(Path.Combine(dir.FullName, "wanxiang.slnx")) then dir.FullName
        else up dir.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

let private read (rel: string) = File.ReadAllText(Path.Combine(repoRoot, rel))

/// 与 Avalonia.Browser 12.1.x 打包体同形的最小片段：WebGL 属性 + ResizeObserver 回调。
let private fixture =
    "a={alpha:!0,depth:!0,stencil:!0,antialias:!1,premultipliedAlpha:!0,preserveDrawingBuffer:!1,failIfMajorPerformanceCaveat:!0};"
    + "static onResize(e,t){for(let r of e){let n=t.callbacks.get(r.target);if(!n)continue;let i=window.devicePixelRatio,s,o,a=i;"
    + "r.devicePixelContentBoxSize?(s=r.devicePixelContentBoxSize[0].inlineSize,o=r.devicePixelContentBoxSize[0].blockSize,a=1):"
    + "r.contentBoxSize?r.contentBoxSize[0]?(s=r.contentBoxSize[0].inlineSize,o=r.contentBoxSize[0].blockSize):(s=r.contentBoxSize.inlineSize,o=r.contentBoxSize.blockSize):"
    + "(s=r.contentRect.width,o=r.contentRect.height);let d=Math.round(s*a),u=Math.round(o*a);n(d,u,i)}}"
    + "static subscribeTextEvents(e,t){let n=o=>{l.InputHelper.OnBeforeInput(t,o.inputType,-1,-1)};e.addEventListener(\"beforeinput\",n);return 0}"

let private runPatch (path: string) =
    let psi = ProcessStartInfo("python3")
    psi.ArgumentList.Add(Path.Combine(repoRoot, "tools", "patch_avalonia_js.py"))
    psi.ArgumentList.Add path
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()
    p.WaitForExit()
    p.ExitCode, out

[<Fact>]
let ``avalonia.js 补丁只在 devicePixelContentBoxSize 与 CSS×DPR 一致时才信它，且可重复执行`` () =
    let path = Path.Combine(Path.GetTempPath(), $"wx-avalonia-{Guid.NewGuid():N}.js")
    try
        File.WriteAllText(path, fixture)
        let code, out = runPatch path
        Assert.True((code = 0), out)
        let once = File.ReadAllText path
        Assert.Contains("/*wx-dpr-guard*/", once)
        Assert.Contains("contentBoxSize[0].inlineSize:r.contentRect.width)*i)<=2", once)
        Assert.Contains("failIfMajorPerformanceCaveat:!1", once)
        // 回退分支保持原样：CSS 尺寸 × DPR
        Assert.Contains("let d=Math.round(s*a),u=Math.round(o*a)", once)
        // 决策 390：非组合上屏桥只插一次，跳过组合中与 Ctrl/⌘+V 之后的粘贴
        Assert.Contains("/*wx-insert-bridge*/", once)
        Assert.Equal(1, once.Split("/*wx-insert-bridge*/").Length - 1)
        Assert.Contains("o.isComposing||e.__wxComp", once)
        Assert.Contains("insertReplacementText|insertFromPaste", once)
        Assert.Contains("globalThis.__wxPasteKey", once)
        Assert.Contains("l.InputHelper.OnCompositionEnd(t,d", once)
        let code2, out2 = runPatch path
        Assert.True((code2 = 0), out2)
        Assert.Equal(once, File.ReadAllText path)
    finally
        if File.Exists path then File.Delete path

[<Fact>]
let ``画布容器按定位收进安全区并跟随可视视口，不再用对 absolute canvas 无效的 padding`` () =
    let css = read "src/Wanxiang.Pwa/wwwroot/app.css"
    let i = css.IndexOf("/* 刘海与手势条")
    Assert.True(i >= 0)
    let block = css.Substring(i, css.IndexOf("#out canvas { touch-action", i) - i)
    Assert.DoesNotContain("padding:", block)
    Assert.Contains("position: fixed", block)
    Assert.Contains("var(--wx-vvh, 100dvh)", block)
    Assert.Contains("env(safe-area-inset-top, 0px)", block)
    Assert.Contains("var(--wx-safe-bottom, env(safe-area-inset-bottom, 0px))", block)

[<Fact>]
let ``main.js 跟随 visualViewport 并把恢复信号交给 F# 侧`` () =
    let js = read "src/Wanxiang.Pwa/wwwroot/main.js"
    Assert.Contains("visualViewport.addEventListener(\"resize\", syncVisualViewport)", js)
    Assert.Contains("--wx-vvh", js)
    Assert.Contains("export function wxResumeEpoch()", js)
    Assert.Contains("wxResumeEpoch,", js)
    Assert.Contains("visibilitychange", js)
    let interop = read "src/Wanxiang.Interop/BrowserBridge.cs"
    Assert.Contains("[JSImport(\"wxResumeEpoch\", \"wanxiang\")]", interop)
    let shell = read "src/Wanxiang.UI/AppShell.fs"
    Assert.Contains("this.ResumeReconnectNow()", shell)

let private runPatchArgs (args: string list) =
    let psi = ProcessStartInfo("python3")
    psi.ArgumentList.Add(Path.Combine(repoRoot, "tools", "patch_avalonia_js.py"))
    for a in args do psi.ArgumentList.Add a
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()
    p.WaitForExit()
    p.ExitCode, out

[<Fact>]
let ``决策 400：avalonia.js 补丁按内容版本化，旧版本从原始文件重打而不是叠加`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"wx-avalonia-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    try
        let pristine = Path.Combine(dir, "pristine.js")
        let target = Path.Combine(dir, "avalonia.js")
        File.WriteAllText(pristine, fixture)
        let _, ver = runPatchArgs [ "--version" ]
        let ver = ver.Trim()
        Assert.Matches("^[0-9a-f]{16}$", ver)
        // 模拟旧版本补丁残留：旧版本号 + 旧标记
        File.WriteAllText(target, "/*wx-patch-version:0000000000000000*/\n" + fixture.Replace("beforeinput\",n);", "beforeinput\",n);/*wx-insert-bridge*/OLD;"))
        let code, out = runPatchArgs [ target; pristine ]
        Assert.True((code = 0), out)
        let t = File.ReadAllText target
        Assert.StartsWith($"/*wx-patch-version:{ver}*/\n", t)
        Assert.DoesNotContain("OLD;", t)
        Assert.Equal(1, t.Split("/*wx-insert-bridge*/").Length - 1)
        // 没有原始文件时拒绝在旧补丁上叠加
        File.WriteAllText(target, "/*wx-patch-version:0000000000000000*/\n/*wx-dpr-guard*/" + fixture)
        let code2, _ = runPatchArgs [ target ]
        Assert.NotEqual(0, code2)
        // 校验脚本：当前版本通过，未补丁失败
        let verify (p: string) =
            let psi = ProcessStartInfo("python3")
            psi.ArgumentList.Add(Path.Combine(repoRoot, "tools", "verify_avalonia_patch.py"))
            psi.ArgumentList.Add p
            psi.RedirectStandardOutput <- true
            use pr = Process.Start psi
            pr.StandardOutput.ReadToEnd() |> ignore
            pr.WaitForExit()
            pr.ExitCode
        let _ = runPatchArgs [ target; pristine ]
        Assert.Equal(0, verify target)
        Assert.NotEqual(0, verify pristine)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``决策 401/404：PWA 补丁挂在 AppBundle 生成之后，App 发布带上已补丁的 PWA`` () =
    let pwa = read "src/Wanxiang.Pwa/Wanxiang.Pwa.fsproj"
    Assert.Contains("AfterTargets=\"_WasmGenerateAppBundle\"", pwa)
    Assert.Contains("PatchAvaloniaJsAfterPublish", pwa)
    Assert.Contains("GeneratePathProperty=\"true\"", pwa)
    Assert.DoesNotContain("Condition=\"Exists('$(OutputPath)AppBundle/_framework/avalonia.js')\"", pwa)
    let app = read "src/Wanxiang.App/Wanxiang.App.fsproj"
    Assert.Contains("PublishPwaBundle", app)
    Assert.Contains("verify_avalonia_patch.py", app)
