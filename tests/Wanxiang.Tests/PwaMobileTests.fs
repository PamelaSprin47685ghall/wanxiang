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
