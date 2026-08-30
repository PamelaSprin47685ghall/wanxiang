namespace Wanxiang.Config

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Tomlyn
open Tomlyn.Model

/// TOML 编解码：AppConfig <-> TOML 文本。
/// 读取时未知字段拒绝整份配置（决策 183）；写回时按当前模型完整重写（决策 42）。
module TomlCodec =

    let private knownTopKeys =
        set [ "configVersion"; "instanceId"; "runtime"; "network"; "pairing"; "generation"; "tools"; "providers"; "mcp"; "auth" ]

    let private knownRuntimeKeys = set [ "server"; "client"; "pwa"; "fix" ]
    let private knownNetworkKeys =
        set [ "listen"; "tlsCertPath"; "tlsKeyPath"; "maxAttachmentBytes"; "chunkSizeBytes" ]
    let private knownPairingKeys = set [ "failureWindowMinutes"; "maxFailures"; "freezeMinutes" ]

    let private knownGenerationKeys =
        set [ "temperature"; "topP"; "maxTokens"; "instructions"; "maxContextMessages"
              "autoTitle"; "maxToolRounds"; "thinkingBudget" ]

    let private knownToolsKeys = set [ "fileReadRoots"; "callTimeoutSeconds" ]

    let private knownProviderKeys =
        set [ "kind"; "label"; "baseUrl"; "apiKey"; "models"; "defaultModel"
              "timeoutSeconds"; "maxRetries"; "enabled"; "headers"; "extra"; "promptCaching" ]

    let private knownMcpKeys =
        set [ "label"; "command"; "args"; "env"; "url"; "maxConcurrency"; "callTimeoutSeconds"; "enabled" ]

    let private knownClientKeys = set [ "tokenHash"; "name"; "createdAt"; "lastSeen"; "revoked" ]

    /// 支持的 provider 传输类型。扩展原生协议时在此登记。
    let supportedProviderKinds = set [ "openai"; "anthropic"; "gemini" ]

    let private asTable (v: obj) : TomlTable option =
        match v with
        | :? TomlTable as t -> Some t
        | _ -> None

    let private asString (v: obj) : string option =
        match v with
        | :? string as s -> Some s
        | _ -> None

    let private asBool (v: obj) : bool option =
        match v with
        | :? bool as b -> Some b
        | _ -> None

    let private asInt (v: obj) : int option =
        match v with
        | :? int64 as i -> Some(int i)
        | :? int as i -> Some i
        | _ -> None

    let private asFloat (v: obj) : float option =
        match v with
        | :? float as f -> Some f
        | :? int64 as i -> Some(float i)
        | _ -> None

    let private asGuid (v: obj) : Guid option =
        match asString v with
        | Some s ->
            match Guid.TryParse s with
            | true, g -> Some g
            | _ -> None
        | None -> None

    let private asUtc (v: obj) : DateTimeOffset option =
        match asString v with
        | Some s ->
            match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, d -> Some d
            | _ -> None
        | None -> None

    let private asStringList (v: obj) : string list option =
        match v with
        | :? TomlArray as arr -> Some [ for item in arr -> string item ]
        | _ -> None

    let private asStringMap (v: obj) : Map<string, string> option =
        match asTable v with
        | Some t -> Some(Map.ofSeq [ for kv in t -> kv.Key, string kv.Value ])
        | None -> None

    /// 解析并校验 TOML 文本。未知字段/类型错误返回错误列表。
    let tryParse (text: string) : Result<AppConfig, string list> =
        let parsed =
            try
                let mutable result: System.Collections.Generic.Dictionary<string, obj> = null
                let mutable options = TomlSerializerOptions()
                let ok = TomlSerializer.TryDeserialize<System.Collections.Generic.Dictionary<string, obj>>(text, &result, options)
                if ok && not (isNull result) then
                    Ok result
                else
                    Error [ "config: TOML syntax error" ]
            with e ->
                Error [ sprintf "config: %s" e.Message ]
        match parsed with
        | Error errs -> Error errs
        | Ok top ->
            let errors = System.Collections.Generic.List<string>()

            let checkKeys (table: System.Collections.Generic.IDictionary<string, obj>) (known: Set<string>) (path: string) =
                for key in table.Keys do
                    if not (known.Contains key) then
                        errors.Add(sprintf "%s.%s: unknown field" path key)

            checkKeys top knownTopKeys "config"

            let configVersion =
                match top.TryGetValue "configVersion" with
                | true, v -> asInt v |> Option.defaultValue 0
                | _ -> 1

            let instanceId =
                match top.TryGetValue "instanceId" with
                | true, v -> asGuid v
                | _ -> None

            let runtime =
                match top.TryGetValue "runtime" with
                | true, v ->
                    match asTable v with
                    | Some t ->
                        checkKeys t knownRuntimeKeys "runtime"
                        let getBool k def =
                            match t.TryGetValue k with
                            | true, b -> asBool b |> Option.defaultValue def
                            | _ -> def
                        { server = getBool "server" true
                          client = getBool "client" true
                          pwa = getBool "pwa" true
                          fix = getBool "fix" false }
                    | None ->
                        errors.Add "runtime: expected table"
                        { server = true; client = true; pwa = true; fix = false }
                | _ -> { server = true; client = true; pwa = true; fix = false }

            let network =
                match top.TryGetValue "network" with
                | true, v ->
                    match asTable v with
                    | Some t ->
                        checkKeys t knownNetworkKeys "network"
                        let getStr k def =
                            match t.TryGetValue k with
                            | true, b -> asString b |> Option.defaultValue def
                            | _ -> def
                        let getInt64 k def =
                            match t.TryGetValue k with
                            | true, b -> (asInt b |> Option.map int64) |> Option.defaultValue def
                            | _ -> def
                        getStr "listen" "127.0.0.1:8765",
                        getStr "tlsCertPath" "",
                        getStr "tlsKeyPath" "",
                        getInt64 "maxAttachmentBytes" (64L * 1024L * 1024L),
                        int (getInt64 "chunkSizeBytes" (256L * 1024L))
                    | None ->
                        errors.Add "network: expected table"
                        "127.0.0.1:8765", "", "", 64L * 1024L * 1024L, 256 * 1024
                | _ -> "127.0.0.1:8765", "", "", 64L * 1024L * 1024L, 256 * 1024

            let pairing =
                match top.TryGetValue "pairing" with
                | true, v ->
                    match asTable v with
                    | Some t ->
                        checkKeys t knownPairingKeys "pairing"
                        let getInt k def =
                            match t.TryGetValue k with
                            | true, b -> asInt b |> Option.defaultValue def
                            | _ -> def
                        getInt "failureWindowMinutes" 1, getInt "maxFailures" 5, getInt "freezeMinutes" 5
                    | None ->
                        errors.Add "pairing: expected table"
                        1, 5, 5
                | _ -> 1, 5, 5

            // generation 默认值
            let generation =
                match top.TryGetValue "generation" with
                | true, v ->
                    match asTable v with
                    | Some t ->
                        checkKeys t knownGenerationKeys "generation"
                        let getFloatOpt k =
                            match t.TryGetValue k with
                            | true, b -> asFloat b
                            | _ -> None
                        let getIntOpt k =
                            match t.TryGetValue k with
                            | true, b -> asInt b
                            | _ -> None
                        let d = GenerationDefaults.defaults
                        { temperature = getFloatOpt "temperature"
                          topP = getFloatOpt "topP"
                          maxTokens = getIntOpt "maxTokens"
                          instructions =
                            match t.TryGetValue "instructions" with
                            | true, b -> asString b |> Option.filter (String.IsNullOrWhiteSpace >> not)
                            | _ -> None
                          maxContextMessages = getIntOpt "maxContextMessages" |> Option.defaultValue d.maxContextMessages
                          autoTitle =
                            (match t.TryGetValue "autoTitle" with
                             | true, b -> asBool b |> Option.defaultValue d.autoTitle
                             | _ -> d.autoTitle)
                          maxToolRounds = getIntOpt "maxToolRounds" |> Option.defaultValue d.maxToolRounds
                          thinkingBudget = getIntOpt "thinkingBudget" |> Option.defaultValue d.thinkingBudget }
                    | None ->
                        errors.Add "generation: expected table"
                        GenerationDefaults.defaults
                | _ -> GenerationDefaults.defaults

            // tools 配置
            let toolsCfg =
                match top.TryGetValue "tools" with
                | true, v ->
                    match asTable v with
                    | Some t ->
                        checkKeys t knownToolsKeys "tools"
                        let d = ToolsConfig.defaults
                        { fileReadRoots =
                            (match t.TryGetValue "fileReadRoots" with
                             | true, b -> asStringList b |> Option.defaultValue []
                             | _ -> [])
                          callTimeoutSeconds =
                            (match t.TryGetValue "callTimeoutSeconds" with
                             | true, b -> asInt b |> Option.defaultValue d.callTimeoutSeconds
                             | _ -> d.callTimeoutSeconds) }
                    | None ->
                        errors.Add "tools: expected table"
                        ToolsConfig.defaults
                | _ -> ToolsConfig.defaults

            // providers
            let providers =
                match top.TryGetValue "providers" with
                | true, v ->
                    match asTable v with
                    | Some t ->
                        [ for kv in t do
                              match asTable kv.Value with
                              | Some pt ->
                                  checkKeys pt knownProviderKeys (sprintf "providers.%s" kv.Key)
                                  let getStr k =
                                      match pt.TryGetValue k with
                                      | true, b -> asString b
                                      | _ -> None
                                  let getInt k =
                                      match pt.TryGetValue k with
                                      | true, b -> asInt b
                                      | _ -> None
                                  let kind = getStr "kind" |> Option.defaultValue "openai"
                                  let baseUrl = getStr "baseUrl" |> Option.defaultValue ""
                                  let models =
                                      match pt.TryGetValue "models" with
                                      | true, b ->
                                          asStringList b
                                          |> Option.defaultValue []
                                          |> List.map (fun s -> s.Trim())
                                          |> List.filter (String.IsNullOrWhiteSpace >> not)
                                          |> List.distinct
                                      | _ -> []
                                  let defaultModel =
                                      match getStr "defaultModel" with
                                      | Some m when not (String.IsNullOrWhiteSpace m) -> m.Trim()
                                      | _ -> models |> List.tryHead |> Option.defaultValue ""
                                  let headers =
                                      match pt.TryGetValue "headers" with
                                      | true, b -> asStringMap b |> Option.defaultValue Map.empty
                                      | _ -> Map.empty
                                  let extra =
                                      match pt.TryGetValue "extra" with
                                      | true, b -> Some(JsonNode.Parse(string b))
                                      | _ -> None
                                  yield
                                      kv.Key,
                                      { id = kv.Key
                                        kind = kind
                                        label = getStr "label" |> Option.defaultValue ""
                                        baseUrl = baseUrl
                                        apiKey = getStr "apiKey"
                                        models = models
                                        defaultModel = defaultModel
                                        timeoutSeconds = getInt "timeoutSeconds" |> Option.defaultValue ProviderConfig.defaultTimeoutSeconds
                                        maxRetries = getInt "maxRetries" |> Option.defaultValue ProviderConfig.defaultMaxRetries
                                        enabled =
                                          (match pt.TryGetValue "enabled" with
                                           | true, b -> asBool b |> Option.defaultValue true
                                           | _ -> true)
                                        headers = headers
                                        promptCaching =
                                          (match pt.TryGetValue "promptCaching" with
                                           | true, b -> asBool b |> Option.defaultValue false
                                           | _ -> false)
                                        extraJson = extra }
                              | None -> errors.Add(sprintf "providers.%s: expected table" kv.Key) ]
                        |> Map.ofList
                    | None ->
                        errors.Add "providers: expected table"
                        Map.empty
                | _ -> Map.empty

            // mcp
            let mcpServers =
                match top.TryGetValue "mcp" with
                | true, v ->
                    match asTable v with
                    | Some t ->
                        [ for kv in t do
                              match asTable kv.Value with
                              | Some mt ->
                                  checkKeys mt knownMcpKeys (sprintf "mcp.%s" kv.Key)
                                  let getStr k =
                                      match mt.TryGetValue k with
                                      | true, b -> asString b
                                      | _ -> None
                                  let getInt k =
                                      match mt.TryGetValue k with
                                      | true, b -> asInt b
                                      | _ -> None
                                  let args = match mt.TryGetValue "args" with true, b -> asStringList b |> Option.defaultValue [] | _ -> []
                                  let env = match mt.TryGetValue "env" with true, b -> asStringMap b |> Option.defaultValue Map.empty | _ -> Map.empty
                                  yield
                                      kv.Key,
                                      { id = kv.Key
                                        label = getStr "label" |> Option.defaultValue ""
                                        command = getStr "command"
                                        args = args
                                        env = env
                                        url = getStr "url"
                                        maxConcurrency = getInt "maxConcurrency"
                                        callTimeoutSeconds = getInt "callTimeoutSeconds" |> Option.defaultValue McpServerConfig.defaultCallTimeoutSeconds
                                        enabled =
                                          (match mt.TryGetValue "enabled" with
                                           | true, b -> asBool b |> Option.defaultValue true
                                           | _ -> true) }
                              | None -> errors.Add(sprintf "mcp.%s: expected table" kv.Key) ]
                        |> Map.ofList
                    | None ->
                        errors.Add "mcp: expected table"
                        Map.empty
                | _ -> Map.empty

            // auth.clients（数组）
            let authClients =
                match top.TryGetValue "auth" with
                | true, v ->
                    match asTable v with
                    | Some at ->
                        // Q183：auth 表未知字段（如 clientSecret、漏写 s 的 [[auth.client]]）拒绝整份配置，
                        // 不能静默接受拼写错误形成的假配置
                        checkKeys at (set [ "clients" ]) "auth"
                        match at.TryGetValue "clients" with
                        | true, c ->
                            match c with
                            | :? TomlTableArray as arr ->
                                [ for item in arr do
                                      checkKeys item knownClientKeys "auth.clients[]"
                                      let getStr k =
                                          match item.TryGetValue k with
                                          | true, b -> asString b
                                          | _ -> None
                                      let getBool k def =
                                          match item.TryGetValue k with
                                          | true, b -> asBool b |> Option.defaultValue def
                                          | _ -> def
                                      match getStr "tokenHash" with
                                      | Some hash ->
                                          yield
                                              { tokenHash = hash
                                                name = getStr "name" |> Option.defaultValue ""
                                                createdAtUtc = match getStr "createdAt" with Some s -> asUtc s |> Option.defaultValue DateTimeOffset.UtcNow | None -> DateTimeOffset.UtcNow
                                                lastSeenUtc = match getStr "lastSeen" with Some s -> asUtc s | None -> None
                                                revoked = getBool "revoked" false }
                                      | None -> errors.Add "auth.clients[]: missing tokenHash" ]
                            | _ ->
                                errors.Add "auth.clients: expected array of tables"
                                []
                        | _ -> []
                    | None ->
                        errors.Add "auth: expected table"
                        []
                | _ -> []

            // 基础校验
            if configVersion <> AppConfig.CurrentVersion then
                errors.Add(sprintf "configVersion %d not supported (expected %d)" configVersion AppConfig.CurrentVersion)
            match instanceId with
            | None -> errors.Add "instanceId: required UUID"
            | Some _ -> ()
            let listen, tlsCert, tlsKey, maxAtt, chunk = network
            if String.IsNullOrWhiteSpace listen then errors.Add "network.listen: required"
            if maxAtt <= 0L then errors.Add "network.maxAttachmentBytes: must be positive"
            if chunk <= 0 then errors.Add "network.chunkSizeBytes: must be positive"
            // 只填一半是配置事故：会静默退回明文，而运维以为已经加密
            match String.IsNullOrWhiteSpace tlsCert, String.IsNullOrWhiteSpace tlsKey with
            | false, true -> errors.Add "network.tlsKeyPath: required when tlsCertPath is set"
            | true, false -> errors.Add "network.tlsCertPath: required when tlsKeyPath is set"
            | false, false ->
                if not (IO.File.Exists tlsCert) then errors.Add $"network.tlsCertPath: file not found: {tlsCert}"
                if not (IO.File.Exists tlsKey) then errors.Add $"network.tlsKeyPath: file not found: {tlsKey}"
            | true, true -> ()
            match generation.temperature with
            | Some t when t < 0.0 || t > 2.0 -> errors.Add "generation.temperature: must be within [0, 2]"
            | _ -> ()
            match generation.topP with
            | Some p when p <= 0.0 || p > 1.0 -> errors.Add "generation.topP: must be within (0, 1]"
            | _ -> ()
            match generation.maxTokens with
            | Some m when m <= 0 -> errors.Add "generation.maxTokens: must be positive"
            | _ -> ()
            if generation.maxContextMessages < 0 then errors.Add "generation.maxContextMessages: must not be negative"
            if generation.maxToolRounds <= 0 then errors.Add "generation.maxToolRounds: must be positive"
            if generation.thinkingBudget < 0 then errors.Add "generation.thinkingBudget: must not be negative"
            if toolsCfg.callTimeoutSeconds <= 0 then errors.Add "tools.callTimeoutSeconds: must be positive"
            for root in toolsCfg.fileReadRoots do
                if not (Path.IsPathRooted root) then
                    errors.Add(sprintf "tools.fileReadRoots: %s must be an absolute path" root)
            for kv in providers do
                let p = kv.Value
                if not (supportedProviderKinds.Contains p.kind) then
                    errors.Add(sprintf "providers.%s.kind: unsupported kind %s" kv.Key p.kind)
                if String.IsNullOrWhiteSpace p.baseUrl then
                    errors.Add(sprintf "providers.%s.baseUrl: required" kv.Key)
                elif not (Uri.IsWellFormedUriString(p.baseUrl, UriKind.Absolute)) then
                    errors.Add(sprintf "providers.%s.baseUrl: must be an absolute URL" kv.Key)
                if List.isEmpty p.models then
                    errors.Add(sprintf "providers.%s.models: at least one model required" kv.Key)
                elif not (ProviderConfig.hasModel p.defaultModel p) then
                    errors.Add(sprintf "providers.%s.defaultModel: %s is not in models" kv.Key p.defaultModel)
                if p.timeoutSeconds <= 0 then errors.Add(sprintf "providers.%s.timeoutSeconds: must be positive" kv.Key)
                if p.maxRetries < 0 then errors.Add(sprintf "providers.%s.maxRetries: must not be negative" kv.Key)
            // 决策 162：重复/冲突稳定标识在配置加载时直接判为无效
            let ids = providers.Keys |> Set.ofSeq
            for kv in mcpServers do
                if ids.Contains kv.Key then errors.Add(sprintf "mcp.%s: id conflicts with provider id" kv.Key)
                let m = kv.Value
                match m.command, m.url with
                | None, None -> errors.Add(sprintf "mcp.%s: either command or url is required" kv.Key)
                | Some _, Some _ -> errors.Add(sprintf "mcp.%s: command and url are mutually exclusive" kv.Key)
                | _ -> ()
                match m.url with
                | Some u when not (Uri.IsWellFormedUriString(u, UriKind.Absolute)) ->
                    errors.Add(sprintf "mcp.%s.url: must be an absolute URL" kv.Key)
                | _ -> ()
                if m.callTimeoutSeconds <= 0 then errors.Add(sprintf "mcp.%s.callTimeoutSeconds: must be positive" kv.Key)
                match m.maxConcurrency with
                | Some c when c <= 0 -> errors.Add(sprintf "mcp.%s.maxConcurrency: must be positive" kv.Key)
                | _ -> ()
            // 令牌哈希格式校验（决策 187）
            for c in authClients do
                if c.tokenHash.Length <> 64 || not (c.tokenHash |> Seq.forall (fun ch -> Uri.IsHexDigit ch)) then
                    errors.Add "auth.clients[]: tokenHash must be lowercase hex sha256"
                elif c.tokenHash <> c.tokenHash.ToLowerInvariant() then
                    errors.Add "auth.clients[]: tokenHash must be lowercase"

            if errors.Count > 0 then
                Error(List.ofSeq errors)
            else
                Ok
                    { configVersion = configVersion
                      instanceId = instanceId.Value
                      runtime = runtime
                      listen = listen
                      tlsCertPath = tlsCert
                      tlsKeyPath = tlsKey
                      maxAttachmentBytes = maxAtt
                      chunkSizeBytes = chunk
                      pairingFailureWindowMinutes = let a, _, _ = pairing in a
                      pairingMaxFailures = let _, b, _ = pairing in b
                      pairingFreezeMinutes = let _, _, c = pairing in c
                      generation = generation
                      tools = toolsCfg
                      providers = providers
                      mcpServers = mcpServers
                      authClients = authClients }

    /// 将配置完整重写为 TOML 文本（决策 42：不保留注释/未知字段/布局）。
    let serialize (cfg: AppConfig) : string =
        let top = TomlTable()
        top.Add("configVersion", int64 cfg.configVersion)
        top.Add("instanceId", cfg.instanceId.ToString("D"))

        let runtime = TomlTable()
        runtime.Add("server", cfg.runtime.server)
        runtime.Add("client", cfg.runtime.client)
        runtime.Add("pwa", cfg.runtime.pwa)
        runtime.Add("fix", cfg.runtime.fix)
        top.Add("runtime", runtime)

        let network = TomlTable()
        network.Add("listen", cfg.listen)
        if not (String.IsNullOrWhiteSpace cfg.tlsCertPath) then network.Add("tlsCertPath", cfg.tlsCertPath)
        if not (String.IsNullOrWhiteSpace cfg.tlsKeyPath) then network.Add("tlsKeyPath", cfg.tlsKeyPath)
        network.Add("maxAttachmentBytes", cfg.maxAttachmentBytes)
        network.Add("chunkSizeBytes", int64 cfg.chunkSizeBytes)
        top.Add("network", network)

        let pairing = TomlTable()
        pairing.Add("failureWindowMinutes", int64 cfg.pairingFailureWindowMinutes)
        pairing.Add("maxFailures", int64 cfg.pairingMaxFailures)
        pairing.Add("freezeMinutes", int64 cfg.pairingFreezeMinutes)
        top.Add("pairing", pairing)

        let generation = TomlTable()
        match cfg.generation.temperature with Some t -> generation.Add("temperature", t) | None -> ()
        match cfg.generation.topP with Some p -> generation.Add("topP", p) | None -> ()
        match cfg.generation.maxTokens with Some m -> generation.Add("maxTokens", int64 m) | None -> ()
        match cfg.generation.instructions with Some s -> generation.Add("instructions", s) | None -> ()
        generation.Add("maxContextMessages", int64 cfg.generation.maxContextMessages)
        generation.Add("autoTitle", cfg.generation.autoTitle)
        generation.Add("maxToolRounds", int64 cfg.generation.maxToolRounds)
        if cfg.generation.thinkingBudget > 0 then
            generation.Add("thinkingBudget", int64 cfg.generation.thinkingBudget)
        top.Add("generation", generation)

        let toolsTable = TomlTable()
        if not (List.isEmpty cfg.tools.fileReadRoots) then
            let arr = TomlArray()
            for r in cfg.tools.fileReadRoots do arr.Add r
            toolsTable.Add("fileReadRoots", arr)
        toolsTable.Add("callTimeoutSeconds", int64 cfg.tools.callTimeoutSeconds)
        top.Add("tools", toolsTable)

        let providers = TomlTable()
        // extra 是已识别字段（knownProviderKeys 含 "extra"）；写回必须保留，
        // 否则任何一次 Rewrite（配对落盘、吊销、配置修改）都会静默删除用户配置的 extra 内容（决策 42）
        let rec jsonToToml (node: JsonNode) : obj =
            match node with
            | :? JsonObject as o ->
                let t = TomlTable()
                for kv in o do
                    if not (isNull kv.Value) then t.Add(kv.Key, jsonToToml kv.Value)
                t :> obj
            | :? JsonArray as a ->
                let arr = TomlArray()
                for item in a do
                    if not (isNull item) then arr.Add(jsonToToml item)
                arr :> obj
            | :? JsonValue as v ->
                let mutable s = ""
                if v.TryGetValue<string>(&s) then s :> obj
                else
                    let mutable b = false
                    if v.TryGetValue<bool>(&b) then b :> obj
                    else
                        let mutable i = 0L
                        if v.TryGetValue<int64>(&i) then i :> obj
                        else
                            let mutable f = 0.0
                            if v.TryGetValue<double>(&f) then f :> obj
                            else string v :> obj
            | _ -> string node :> obj
        for kv in cfg.providers do
            let p = TomlTable()
            p.Add("kind", kv.Value.kind)
            if not (String.IsNullOrWhiteSpace kv.Value.label) then p.Add("label", kv.Value.label)
            p.Add("baseUrl", kv.Value.baseUrl)
            match kv.Value.apiKey with Some k -> p.Add("apiKey", k) | None -> ()
            let models = TomlArray()
            for m in kv.Value.models do models.Add m
            p.Add("models", models)
            p.Add("defaultModel", kv.Value.defaultModel)
            p.Add("timeoutSeconds", int64 kv.Value.timeoutSeconds)
            p.Add("maxRetries", int64 kv.Value.maxRetries)
            p.Add("enabled", kv.Value.enabled)
            if kv.Value.promptCaching then p.Add("promptCaching", true)
            if not (Map.isEmpty kv.Value.headers) then
                let h = TomlTable()
                for e in kv.Value.headers do h.Add(e.Key, e.Value)
                p.Add("headers", h)
            match kv.Value.extraJson with
            | Some n when not (isNull n) -> p.Add("extra", jsonToToml n)
            | _ -> ()
            providers.Add(kv.Key, p)
        top.Add("providers", providers)

        let mcp = TomlTable()
        for kv in cfg.mcpServers do
            let m = TomlTable()
            if not (String.IsNullOrWhiteSpace kv.Value.label) then m.Add("label", kv.Value.label)
            match kv.Value.command with Some c -> m.Add("command", c) | None -> ()
            if not (List.isEmpty kv.Value.args) then
                let arr = TomlArray()
                for a in kv.Value.args do arr.Add a
                m.Add("args", arr)
            if not (kv.Value.env |> Map.isEmpty) then
                let env = TomlTable()
                for e in kv.Value.env do env.Add(e.Key, e.Value)
                m.Add("env", env)
            match kv.Value.url with Some u -> m.Add("url", u) | None -> ()
            match kv.Value.maxConcurrency with Some c -> m.Add("maxConcurrency", int64 c) | None -> ()
            m.Add("callTimeoutSeconds", int64 kv.Value.callTimeoutSeconds)
            m.Add("enabled", kv.Value.enabled)
            mcp.Add(kv.Key, m)
        top.Add("mcp", mcp)

        let auth = TomlTable()
        let clients = TomlTableArray()
        for c in cfg.authClients do
            let ct = TomlTable()
            ct.Add("tokenHash", c.tokenHash)
            ct.Add("name", c.name)
            ct.Add("createdAt", c.createdAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            match c.lastSeenUtc with
            | Some t -> ct.Add("lastSeen", t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            | None -> ()
            ct.Add("revoked", c.revoked)
            clients.Add ct
        auth.Add("clients", clients)
        top.Add("auth", auth)

        TomlSerializer.Serialize(top)
