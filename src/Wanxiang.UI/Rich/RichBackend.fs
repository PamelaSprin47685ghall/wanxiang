namespace Wanxiang.UI

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text.Json

type private ScriptAssemblyMarker = class end

/// 内嵌 JS 资源（highlight.js / KaTeX）的读取与 hljs 语言目录。
///
/// 资源由 `tools/build_webassets.js` 装配：hljs 拆成「基座 + 按需语言」，
/// 因为全语言打包体约 1MB，在 WASM 里一次性解析太贵。
module RichAssets =

    /// 清单资源而非 avares：预热线程与单元测试都在 Avalonia 起来之前读它。
    let private readText (name: string) =
        let assembly = typeof<ScriptAssemblyMarker>.Assembly
        use stream = assembly.GetManifestResourceStream $"js/{name}"
        if isNull stream then failwith $"missing embedded script: {name}"
        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    let katexScript () = readText "katex.min.js"
    let hljsCoreScript () = readText "hljs.core.js"
    let hljsLanguageScript (name: string) = readText $"hljs.lang/{name}.js"

    type Catalog =
        { /// 基座里已注册的语言，直接可用
          builtin: Set<string>
          /// 需要额外加载一个脚本才能注册的语言
          onDemand: Set<string>
          /// ```ts / ```F# 这类写法 → hljs 规范名
          aliases: Map<string, string> }

    let private loadCatalog () =
        try
            use doc = JsonDocument.Parse(readText "hljs.aliases.json")
            let strings (name: string) =
                doc.RootElement.GetProperty(name).EnumerateArray()
                |> Seq.map (fun e -> e.GetString())
                |> Set.ofSeq
            let aliases =
                doc.RootElement.GetProperty("aliases").EnumerateObject()
                |> Seq.map (fun p -> p.Name, p.Value.GetString())
                |> Map.ofSeq
            { builtin = strings "builtin"; onDemand = strings "onDemand"; aliases = aliases }
        with _ ->
            { builtin = Set.empty; onDemand = Set.empty; aliases = Map.empty }

    let private catalog = lazy loadCatalog ()

    /// 语言标注 → hljs 规范名。认不出的返回 None，调用方退化为不着色。
    let resolveLanguage (language: string) : string option =
        if String.IsNullOrWhiteSpace language then None
        else
            let key = language.Trim().ToLowerInvariant()
            match catalog.Value.aliases.TryFind key with
            | Some canonical -> Some canonical
            | None -> if catalog.Value.builtin.Contains key then Some key else None

    let requiresLoad (canonical: string) = catalog.Value.onDemand.Contains canonical


/// 富文本渲染所需的脚本能力。桌面端由 Jint 提供，浏览器端由宿主 JS 引擎提供。
///
/// 两端都只暴露「文本进、HTML 出」：布局与绘制全在 Avalonia 侧共享，
/// 所以两端的公式与代码长得完全一样。
type IRichBackend =
    /// hljs 的 `<span class="hljs-…">` HTML。失败时返回 None。
    abstract HighlightHtml: code: string * canonicalLanguage: string -> string option
    /// KaTeX 的 HTML。失败时返回 None。
    abstract MathHtml: tex: string * displayMode: bool -> string option


/// 后端注册处。桌面入口装 Jint 实现，PWA 入口装浏览器实现；
/// 没装时代码块与公式退化为纯文本，功能缺失但绝不崩。
module RichBackend =

    let mutable private backend: IRichBackend option = None

    let install (value: IRichBackend) = backend <- Some value
    let current () = backend

    /// 带上限的记忆化。长会话里代码块会被反复重渲染（流式追加、主题切换、
    /// 滚动复用），不缓存就会把 JS 引擎当成每帧都要付费的热路径。
    type Cache<'K, 'V when 'K: equality>(limit: int) =
        let map = Dictionary<'K, 'V>()
        let order = Queue<'K>()
        // 读发生在 UI 线程，写可能来自后台着色线程
        let gate = obj ()

        let insert key value =
            map[key] <- value
            order.Enqueue key
            while order.Count > limit do
                map.Remove(order.Dequeue()) |> ignore

        member _.TryGet(key: 'K) : 'V option =
            lock gate (fun () ->
                match map.TryGetValue key with
                | true, hit -> Some hit
                | _ -> None)

        member _.Set(key: 'K, value: 'V) = lock gate (fun () -> insert key value)

        member _.GetOrAdd(key: 'K, compute: 'K -> 'V) : 'V =
            match lock gate (fun () ->
                      match map.TryGetValue key with
                      | true, hit -> Some hit
                      | _ -> None)
                with
            | Some hit -> hit
            | None ->
                // compute 在锁外执行：着色可能要几百毫秒，占着锁会挡住 UI 线程的读
                let value = compute key
                lock gate (fun () -> insert key value)
                value

        member _.Clear() =
            lock gate (fun () ->
                map.Clear()
                order.Clear())
