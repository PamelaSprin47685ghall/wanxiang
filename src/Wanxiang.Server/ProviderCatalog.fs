namespace Wanxiang.Server

open System
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Wanxiang.Config

/// 决策 330：模型能否看图的「模型服务提供的信息」。
/// 来源：OpenAI 兼容 `/models` 里的能力字段（OpenRouter 的 architecture.input_modalities、
/// 部分网关的 capabilities / modalities）、Ollama `/api/show` 的 capabilities、Anthropic 的已知清单。
/// 只在用户点「获取模型列表」时联网补充（与决策 41 一致：不在后台偷偷请求模型服务），结果留在主机内存里。
module ModelCapabilities =

    open System.Collections.Concurrent

    /// 决策 340：探测到的能力连同时间戳落盘（`<dataDir>/model-capabilities.json`），主机重启后仍在；
    /// 每次「获取模型列表」成功时整组替换该模型服务的条目（刷新，旧模型不残留）。
    let private detected = ConcurrentDictionary<string, bool * DateTimeOffset>(StringComparer.Ordinal)
    let private key (p: ProviderConfig) (model: string) = p.id + "\n" + p.baseUrl.TrimEnd('/') + "\n" + model
    let private prefix (p: ProviderConfig) = p.id + "\n" + p.baseUrl.TrimEnd('/') + "\n"
    let mutable private storePath: string option = None
    let private gate = obj ()

    let private save () =
        match storePath with
        | None -> ()
        | Some path ->
            lock gate (fun () ->
                try
                    let root = JsonObject()
                    root["version"] <- 1
                    let items = JsonObject()
                    for KeyValue(k, (v, at)) in detected do
                        let o = JsonObject()
                        o["vision"] <- v
                        o["at"] <- at.ToString("o")
                        items[k] <- o
                    root["entries"] <- items
                    let tmp = path + ".tmp"
                    IO.File.WriteAllText(tmp, root.ToJsonString())
                    IO.File.Move(tmp, path, true)
                with ex -> eprintfn "[wanxiang] 保存模型能力缓存失败：%s" ex.Message)

    /// 指定落盘位置并读回上次的缓存；文件损坏时忽略（只是缓存，下次获取模型列表会重建）。
    let configure (path: string) =
        storePath <- Some path
        try
            if IO.File.Exists path then
                match JsonNode.Parse(IO.File.ReadAllText path) with
                | :? JsonObject as root ->
                    match root["entries"] with
                    | :? JsonObject as items ->
                        for KeyValue(k, n) in items do
                            match n with
                            | :? JsonObject as o when not (isNull o["vision"]) ->
                                let v = o["vision"].GetValueKind() = JsonValueKind.True
                                let at =
                                    match o["at"] with
                                    | null -> DateTimeOffset.MinValue
                                    | a -> match DateTimeOffset.TryParse(a.ToString()) with | true, t -> t | _ -> DateTimeOffset.MinValue
                                detected[k] <- (v, at)
                            | _ -> ()
                    | _ -> ()
                | _ -> ()
        with ex -> eprintfn "[wanxiang] 读取模型能力缓存失败，已忽略：%s" ex.Message

    let record (p: ProviderConfig) (pairs: (string * bool) list) =
        let now = DateTimeOffset.UtcNow
        for (m, v) in pairs do detected[key p m] <- (v, now)
        if not (List.isEmpty pairs) then save ()

    /// 获取模型列表成功后整组替换该模型服务的能力条目。
    let replaceFor (p: ProviderConfig) (pairs: (string * bool) list) =
        let pre = prefix p
        for k in detected.Keys |> Seq.toList do
            if k.StartsWith(pre, StringComparison.Ordinal) then detected.TryRemove k |> ignore
        let now = DateTimeOffset.UtcNow
        for (m, v) in pairs do detected[key p m] <- (v, now)
        save ()

    /// 某条能力的探测时间（测试与诊断用）。
    let detectedAt (p: ProviderConfig) (model: string) =
        match detected.TryGetValue(key p model) with
        | true, (_, at) -> Some at
        | _ -> None

    /// Anthropic 原生接口不返回能力字段；Claude 3 起全系能看图，更早的只吃文字。
    let anthropicKnown (model: string) : bool option =
        let id = if isNull model then "" else model.Trim().ToLowerInvariant()
        if id.StartsWith "claude-3" || id.StartsWith "claude-sonnet-4" || id.StartsWith "claude-opus-4"
           || id.StartsWith "claude-haiku-4" || id.StartsWith "claude-4" || id.StartsWith "claude-sonnet-5"
           || id.StartsWith "claude-opus-5" then Some true
        elif id.StartsWith "claude-2" || id.StartsWith "claude-instant" then Some false
        else None

    /// 某模型的服务端信息：内存里探测到的优先，其次 Anthropic 已知清单。
    let tryGet (p: ProviderConfig) (model: string) : bool option =
        match detected.TryGetValue(key p model) with
        | true, (v, _) -> Some v
        | _ -> if p.kind = "anthropic" then anthropicKnown model else None

    let private strings (n: JsonNode) =
        match n with
        | :? JsonArray as a ->
            [ for x in a do
                  if not (isNull x) && x.GetValueKind() = JsonValueKind.String then yield x.GetValue<string>().ToLowerInvariant() ]
        | _ -> []

    /// 从 `/models` 的单个条目读能力字段；没有任何能力字段时返回 None（交给名字推断）。
    let ofModelEntry (o: JsonObject) : bool option =
        let field (name: string) = match o[name] with null -> None | v -> Some v
        let fromCaps =
            match field "capabilities" with
            | Some(:? JsonArray as a) ->
                let caps = strings a
                Some(caps |> List.exists (fun c -> c = "vision" || c = "image" || c = "image_input" || c = "images"))
            | Some(:? JsonObject as c) ->
                match c["vision"] with
                | null -> None
                | v when v.GetValueKind() = JsonValueKind.True -> Some true
                | v when v.GetValueKind() = JsonValueKind.False -> Some false
                | _ -> None
            | _ -> None
        let fromModalities (n: JsonNode) =
            match n with
            | :? JsonArray as a -> Some(strings a |> List.contains "image")
            | _ -> None
        let fromArchitecture =
            match field "architecture" with
            | Some(:? JsonObject as a) ->
                match a["input_modalities"] with
                | null ->
                    match a["modality"] with
                    | null -> None
                    | m when m.GetValueKind() = JsonValueKind.String ->
                        let input = m.GetValue<string>().Split("->").[0].ToLowerInvariant()
                        Some(input.Contains "image")
                    | _ -> None
                | m -> fromModalities m
            | _ -> None
        fromCaps
        |> Option.orElse fromArchitecture
        |> Option.orElse (field "input_modalities" |> Option.bind fromModalities)
        |> Option.orElse (field "modalities" |> Option.bind fromModalities)

    /// 整份 `/models` 响应 → (模型, 能否看图)，只列出带能力字段的条目。
    let ofModelsBody (body: string) : (string * bool) list =
        try
            match JsonNode.Parse body with
            | :? JsonObject as root ->
                match root["data"] with
                | :? JsonArray as items ->
                    [ for item in items do
                          match item with
                          | :? JsonObject as o ->
                              match o["id"] with
                              | null -> ()
                              | id when id.GetValueKind() = JsonValueKind.String ->
                                  match ofModelEntry o with
                                  | Some v -> yield id.GetValue<string>(), v
                                  | None -> ()
                              | _ -> ()
                          | _ -> () ]
                | _ -> []
            | _ -> []
        with _ -> []

    /// Ollama `/api/show` 响应 → 能否看图（新版 Ollama 才有 capabilities）。
    let ofOllamaShow (body: string) : bool option =
        try
            match JsonNode.Parse body with
            | :? JsonObject as root ->
                match root["capabilities"] with
                | :? JsonArray as a -> Some(strings a |> List.contains "vision")
                | _ -> None
            | _ -> None
        with _ -> None

    /// 决策 341：是不是 Ollama 由 provider 种类或 `/api/version` 的回应决定，不再看端口或地址里的字样。
    let isOllamaKind (p: ProviderConfig) = p.kind = "ollama"

    /// `/api/version` 响应是否是 Ollama 的（`{"version":"0.x.y"}`）。
    let ofOllamaVersion (body: string) : bool =
        try
            match JsonNode.Parse body with
            | :? JsonObject as root ->
                match root["version"] with
                | null -> false
                | v -> v.GetValueKind() = JsonValueKind.String && root.Count <= 3
            | _ -> false
        with _ -> false

    let ollamaRoot (p: ProviderConfig) =
        let t = p.baseUrl.TrimEnd '/'
        if t.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) then t.Substring(0, t.Length - 3) else t

    /// 清空（测试用）。
    let clear () =
        detected.Clear()
        storePath <- None

/// 可选 provider / 模型 / 工具的目录。
///
/// 客户端不该靠用户手打 provider id 和模型名——那是配置文件时代的交互。
/// 这里把主机已知的一切可选项整理成快照下发，选择器直接渲染。
/// **密钥永不出现在快照里**，只用 `hasApiKey` 表示是否已配置。
module ProviderCatalog =

    let private providerToJson (p: ProviderConfig) : JsonObject =
        let o = JsonObject()
        o["id"] <- p.id
        o["label"] <- ProviderConfig.displayName p
        o["kind"] <- p.kind
        o["baseUrl"] <- p.baseUrl
        o["hasApiKey"] <- (match p.apiKey with Some k -> not (String.IsNullOrWhiteSpace k) | None -> false)
        o["models"] <- JsonArray([| for m in p.models -> JsonNode.op_Implicit m |])
        o["defaultModel"] <- p.defaultModel
        o["timeoutSeconds"] <- p.timeoutSeconds
        o["maxRetries"] <- p.maxRetries
        o["enabled"] <- p.enabled
        if not (Map.isEmpty p.headers) then
            let h = JsonObject()
            for KeyValue(k, v) in p.headers do h[k] <- v
            o["headers"] <- h
        // 决策 330/331：看图能力的两层来源，客户端按「用户指定 > 服务信息 > 名字推断」合成。
        // 都是可选键，旧客户端忽略。
        if not (Map.isEmpty p.visionOverrides) then
            let v = JsonObject()
            for KeyValue(m, b) in p.visionOverrides do v[m] <- b
            o["visionOverrides"] <- v
        let detectedVision = JsonObject()
        for m in p.models do
            match ModelCapabilities.tryGet p m with
            | Some b -> detectedVision[m] <- b
            | None -> ()
        if detectedVision.Count > 0 then o["visionDetected"] <- detectedVision
        o

    let private generationToJson (g: GenerationDefaults) : JsonObject =
        let o = JsonObject()
        match g.temperature with Some t -> o["temperature"] <- t | None -> ()
        match g.topP with Some t -> o["topP"] <- t | None -> ()
        match g.maxTokens with Some m -> o["maxTokens"] <- m | None -> ()
        match g.instructions with Some s -> o["instructions"] <- s | None -> ()
        o["maxContextMessages"] <- g.maxContextMessages
        o["autoTitle"] <- g.autoTitle
        o["maxToolRounds"] <- g.maxToolRounds
        // 全局默认模型（可选键；缺省不下发，旧客户端无感）。
        match g.defaultProvider with Some p -> o["defaultProvider"] <- p | None -> ()
        match g.defaultModel with Some m -> o["defaultModel"] <- m | None -> ()
        o

    let private mcpToJson (m: McpServerConfig) : JsonObject =
        let o = JsonObject()
        o["id"] <- m.id
        o["label"] <- McpServerConfig.displayName m
        match m.command with Some c -> o["command"] <- c | None -> ()
        if not (List.isEmpty m.args) then
            o["args"] <- JsonArray([| for a in m.args -> JsonNode.op_Implicit a |])
        if not (Map.isEmpty m.env) then
            let e = JsonObject()
            for KeyValue(k, v) in m.env do e[k] <- v
            o["env"] <- e
        match m.url with Some u -> o["url"] <- u | None -> ()
        match m.maxConcurrency with Some c -> o["maxConcurrency"] <- c | None -> ()
        o["callTimeoutSeconds"] <- m.callTimeoutSeconds
        o["enabled"] <- m.enabled
        o

    /// providers 数组（含未启用者，设置界面需要看到全部）。
    let providers (cfg: AppConfig) : JsonArray =
        JsonArray(
            [| for p in cfg.providers |> Map.toList |> List.map snd |> List.sortBy (fun p -> p.id) ->
                   providerToJson p :> JsonNode |])

    let mcpServers (cfg: AppConfig) : JsonArray =
        JsonArray(
            [| for m in cfg.mcpServers |> Map.toList |> List.map snd |> List.sortBy (fun m -> m.id) ->
                   mcpToJson m :> JsonNode |])

    /// 目录快照的 generation 段：生成默认值 + MCP 服务清单
    /// （客户端设置界面与对话设置共用同一份数据）。
    let generation (cfg: AppConfig) : JsonObject =
        let o = generationToJson cfg.generation
        o["mcpServers"] <- mcpServers cfg
        let t = JsonObject()
        t["fileReadRoots"] <- JsonArray([| for r in cfg.tools.fileReadRoots -> JsonNode.op_Implicit r |])
        t["callTimeoutSeconds"] <- cfg.tools.callTimeoutSeconds
        t["policyDefault"] <- ToolPolicy.toToml cfg.tools.policyDefault
        let pol = JsonObject()
        for KeyValue(id, p) in cfg.tools.policy do pol[id] <- ToolPolicy.toToml p
        t["policy"] <- pol
        t["approvalTimeoutSeconds"] <- cfg.tools.approvalTimeoutSeconds
        t["exposeDebug"] <- cfg.tools.exposeDebug
        o["tools"] <- t
        // 决策 432：预设随目录快照下发（按名称排序），客户端无需单独请求。
        let presets = JsonArray()
        for p in cfg.presets |> Map.toList |> List.map snd |> List.sortBy (fun p -> p.name) do
            let po = JsonObject()
            po["id"] <- p.id
            po["name"] <- p.name
            po["provider"] <- p.provider
            po["model"] <- p.model
            match p.instructions with Some i -> po["instructions"] <- i | None -> ()
            po["tools"] <- JsonArray([| for x in p.tools -> JsonNode.op_Implicit x |])
            match p.temperature with Some x -> po["temperature"] <- x | None -> ()
            match p.topP with Some x -> po["topP"] <- x | None -> ()
            match p.maxTokens with Some x -> po["maxTokens"] <- x | None -> ()
            match p.thinkingBudget with Some x -> po["thinkingBudget"] <- x | None -> ()
            presets.Add po
        o["presets"] <- presets
        o

    let private stringField (name: string) (item: JsonNode) =
        match item with
        | :? JsonObject as o ->
            match o[name] with
            | null -> None
            | value ->
                if value.GetValueKind() = JsonValueKind.String then Some(value.GetValue<string>()) else None
        | _ -> None

    let private arrayField (root: JsonObject) (key: string) =
        match root[key] with
        | :? JsonArray as items -> List.ofSeq (Seq.cast<JsonNode> items)
        | _ -> []

    /// 从探活响应里取模型名。
    ///
    /// OpenAI 与 Anthropic 都返回 `{data:[{id}]}`；Gemini 返回
    /// `{models:[{name:"models/gemini-2.5-pro"}]}`，那层 `models/` 前缀要去掉，
    /// 否则填回模型列表后每个模型名都是错的。
    let modelIdsFromProbe (body: string) : Result<string list, string> =
        try
            match JsonNode.Parse body with
            | :? JsonObject as root ->
                let fromData = arrayField root "data" |> List.choose (stringField "id")
                let fromModels =
                    arrayField root "models"
                    |> List.choose (stringField "name")
                    |> List.map (fun name ->
                        if name.StartsWith("models/", StringComparison.Ordinal) then name.Substring 7 else name)
                match fromData @ fromModels with
                | [] -> Error "响应里没有模型清单（既无 data 也无 models）"
                | ids -> Ok(ids |> List.distinct |> List.sort)
            | _ -> Error "响应不是 JSON 对象"
        with _ -> Error "响应不是合法 JSON"

    /// 探活请求的端点与鉴权头。三种传输各不相同，用错就是 401 或 404。
    let private probeRequest (p: ProviderConfig) =
        let trimmed = p.baseUrl.TrimEnd '/'
        let key = p.apiKey |> Option.filter (String.IsNullOrWhiteSpace >> not)
        match p.kind with
        | "anthropic" ->
            let url =
                if trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) then trimmed + "/models"
                else trimmed + "/v1/models"
            url,
            [ match key with
              | Some k -> "x-api-key", k
              | None -> ()
              "anthropic-version", "2023-06-01" ]
        | "gemini" ->
            let root = if trimmed.Contains "/v1" then trimmed else trimmed + "/v1beta"
            root + "/models",
            [ match key with
              | Some k -> "x-goog-api-key", k
              | None -> () ]
        | _ ->
            trimmed + "/models",
            [ match key with
              | Some k -> "Authorization", "Bearer " + k
              | None -> () ]

    /// 探活失败的类型化原因。界面按它决定文案与下一步，不靠文案关键词判断。
    type ProbeFailure =
        /// 401 / 403：密钥无效、过期或没有权限
        | InvalidKey
        /// 404：端点路径不对（baseUrl 多写或少写了 /v1 之类）
        | EndpointNotFound
        /// 429：被限流或额度用尽
        | RateLimited
        /// 5xx：对方服务内部错误
        | ServerError
        /// DNS / 连接被拒 / TLS：网络不通
        | Unreachable
        /// 超时
        | TimedOut
        /// 200 但响应不是模型清单（多半是 baseUrl 指到了网页而不是 API）
        | BadResponse
        /// 其他 HTTP 状态
        | Rejected
        /// 调用方取消
        | Cancelled

    module ProbeFailure =

        let all =
            [ InvalidKey; EndpointNotFound; RateLimited; ServerError; Unreachable; TimedOut; BadResponse; Rejected; Cancelled ]

        /// 稳定错误码：随 `provider.probe-result.error` 的 `[code] ` 前缀下发
        /// （协议字段仍是自由文本，旧客户端只会多看到一个方括号前缀）。
        let code (f: ProbeFailure) =
            match f with
            | InvalidKey -> "invalid-key"
            | EndpointNotFound -> "endpoint-not-found"
            | RateLimited -> "rate-limited"
            | ServerError -> "server-error"
            | Unreachable -> "unreachable"
            | TimedOut -> "timeout"
            | BadResponse -> "bad-response"
            | Rejected -> "rejected"
            | Cancelled -> "cancelled"

        let ofCode (value: string) : ProbeFailure option =
            all |> List.tryFind (fun f -> code f = value)

        /// 把类型与说明编码成线上字符串：`[code] 说明`。
        let encode (f: ProbeFailure) (message: string) = sprintf "[%s] %s" (code f) message

        /// 解码 `[code] 说明`；没有前缀（旧主机）返回 None 与原文。
        let decode (text: string) : ProbeFailure option * string =
            if not (isNull text) && text.StartsWith("[", StringComparison.Ordinal) then
                let close = text.IndexOf ']'
                if close > 1 then
                    ofCode (text.Substring(1, close - 1)), text.Substring(close + 1).TrimStart()
                else
                    None, text
            else
                None, text

    /// 失败类型 → (发生了什么 · 为什么，下一步)。固定三段：发生了什么 / 为什么 / 下一步。
    let private describeFailure (p: ProviderConfig) (f: ProbeFailure) (detail: string) : string =
        let nativeKind = p.kind = "anthropic" || p.kind = "gemini"
        match f with
        | InvalidKey ->
            sprintf "%s 拒绝了这把密钥（%s）。密钥可能填错、已过期或没有权限。请重新复制完整密钥后再试。" (ProviderConfig.displayName p) detail
        | EndpointNotFound ->
            if nativeKind then
                sprintf "找不到模型清单端点（%s）。原生接口的地址只需填到域名；如果你用的是中转或网关，请改用兼容端点。" detail
            else
                sprintf "找不到模型清单端点（%s）。兼容接口的地址通常以 /v1 结尾，请检查路径；原生 Claude 或 Gemini 请改用原生接口。" detail
        | RateLimited ->
            sprintf "%s 暂时限制了请求（%s）。可能是额度用尽或请求太频繁，请稍后再试或检查账户额度。" (ProviderConfig.displayName p) detail
        | ServerError ->
            sprintf "%s 返回了内部错误（%s）。这通常是对方服务的临时故障，请稍后再试。" (ProviderConfig.displayName p) detail
        | Unreachable ->
            sprintf "连不上 %s（%s）。可能是网络不通、地址写错或本机服务没有启动，请检查地址与网络。" p.baseUrl detail
        | TimedOut ->
            sprintf "等待 %s 超时（%s）。网络较慢或对方没有响应，请稍后再试，或检查地址是否正确。" p.baseUrl detail
        | BadResponse ->
            sprintf "%s 有响应，但返回的不是模型清单（%s）。地址可能指到了网页而不是接口，请确认端点路径。" p.baseUrl detail
        | Rejected ->
            sprintf "%s 拒绝了请求（%s）。请检查地址与密钥是否匹配这个接口。" (ProviderConfig.displayName p) detail
        | Cancelled -> "探测已取消。"

    /// 向 provider 的模型清单端点探活，失败带类型。
    /// 只在用户点击后触发；密钥只进请求头，错误文案里不出现密钥（决策 41）。
    let probeModelsTyped (p: ProviderConfig) (ct: CancellationToken) : Task<Result<string list, ProbeFailure * string>> =
        task {
            try
                let url, authHeaders = probeRequest p
                use request = new HttpRequestMessage(HttpMethod.Get, url)
                for (name, value) in authHeaders do
                    request.Headers.TryAddWithoutValidation(name, value) |> ignore
                for KeyValue(name, value) in p.headers do
                    request.Headers.TryAddWithoutValidation(name, value) |> ignore
                use client = new HttpClient()
                client.Timeout <- TimeSpan.FromSeconds(float (min p.timeoutSeconds 30))
                let! response = client.SendAsync(request, ct)
                let! body = response.Content.ReadAsStringAsync ct
                if not response.IsSuccessStatusCode then
                    let status = int response.StatusCode
                    let detail = sprintf "HTTP %d" status
                    let failure =
                        match status with
                        | 401 | 403 -> InvalidKey
                        | 404 -> EndpointNotFound
                        | 429 -> RateLimited
                        | s when s >= 500 -> ServerError
                        | _ -> Rejected
                    return Error(failure, describeFailure p failure detail)
                else
                    match modelIdsFromProbe body with
                    | Ok ids ->
                        let fromList = ModelCapabilities.ofModelsBody body
                        let fromShow = Collections.Generic.List<string * bool>()
                        let root = ModelCapabilities.ollamaRoot p
                        let! isOllama =
                            task {
                                if ModelCapabilities.isOllamaKind p then return true
                                elif p.kind <> "openai" then return false
                                else
                                    try
                                        use cts = CancellationTokenSource.CreateLinkedTokenSource ct
                                        cts.CancelAfter(TimeSpan.FromSeconds 3.0)
                                        let! r = client.GetAsync(root + "/api/version", cts.Token)
                                        if not r.IsSuccessStatusCode then return false
                                        else
                                            let! b = r.Content.ReadAsStringAsync cts.Token
                                            return ModelCapabilities.ofOllamaVersion b
                                    with _ -> return false
                            }
                        if isOllama then
                            // 逐个问 Ollama 的 /api/show；失败或旧版本没有 capabilities 就跳过，不影响探测结果。
                            for m in ids |> List.truncate 50 do
                                try
                                    use show = new HttpRequestMessage(HttpMethod.Post, root + "/api/show")
                                    let payload = JsonObject()
                                    payload["model"] <- m
                                    show.Content <- new StringContent(payload.ToJsonString(), Text.Encoding.UTF8, "application/json")
                                    use cts = CancellationTokenSource.CreateLinkedTokenSource ct
                                    cts.CancelAfter(TimeSpan.FromSeconds 5.0)
                                    let! r = client.SendAsync(show, cts.Token)
                                    if r.IsSuccessStatusCode then
                                        let! b = r.Content.ReadAsStringAsync cts.Token
                                        match ModelCapabilities.ofOllamaShow b with
                                        | Some v -> fromShow.Add((m, v))
                                        | None -> ()
                                with _ -> ()
                        ModelCapabilities.replaceFor p (fromList @ List.ofSeq fromShow)
                        return Ok ids
                    | Error reason -> return Error(BadResponse, describeFailure p BadResponse reason)
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return Error(Cancelled, describeFailure p Cancelled "")
            | :? OperationCanceledException ->
                return Error(TimedOut, describeFailure p TimedOut "请求超时")
            | :? HttpRequestException as ex ->
                return Error(Unreachable, describeFailure p Unreachable ex.Message)
            | ex ->
                return Error(Unreachable, describeFailure p Unreachable ex.Message)
        }

    /// 向 provider 的模型清单端点探活（线上形态）。
    /// 失败文案为 `[code] 说明`，设置界面据 code 决定下一步，说明本身固定三段式。
    let probeModels (p: ProviderConfig) (ct: CancellationToken) : Task<Result<string list, string>> =
        task {
            let! result = probeModelsTyped p ct
            match result with
            | Ok ids -> return Ok ids
            | Error(failure, message) -> return Error(ProbeFailure.encode failure message)
        }
