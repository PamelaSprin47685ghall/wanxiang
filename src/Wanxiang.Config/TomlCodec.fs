namespace Wanxiang.Config

open System
open System.Globalization
open System.Text.Json.Nodes
open Tomlyn
open Tomlyn.Model

/// TOML 编解码：AppConfig <-> TOML 文本。
/// 读取时未知字段拒绝整份配置（决策 183）；写回时按当前模型完整重写（决策 42）。
module TomlCodec =

    let private knownTopKeys =
        set [ "configVersion"; "instanceId"; "runtime"; "network"; "pairing"; "providers"; "mcp"; "auth" ]

    let private knownRuntimeKeys = set [ "server"; "client"; "pwa"; "fix" ]
    let private knownNetworkKeys = set [ "listen"; "maxAttachmentBytes"; "chunkSizeBytes" ]
    let private knownPairingKeys = set [ "failureWindowMinutes"; "maxFailures"; "freezeMinutes" ]
    let private knownProviderKeys = set [ "kind"; "baseUrl"; "apiKey"; "model"; "extra" ]
    let private knownMcpKeys = set [ "command"; "args"; "env"; "url"; "maxConcurrency" ]
    let private knownClientKeys = set [ "tokenHash"; "name"; "createdAt"; "lastSeen"; "revoked" ]

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
                        getInt64 "maxAttachmentBytes" (64L * 1024L * 1024L),
                        int (getInt64 "chunkSizeBytes" (256L * 1024L))
                    | None ->
                        errors.Add "network: expected table"
                        "127.0.0.1:8765", 64L * 1024L * 1024L, 256 * 1024
                | _ -> "127.0.0.1:8765", 64L * 1024L * 1024L, 256 * 1024

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
                                  let kind = getStr "kind" |> Option.defaultValue "openai"
                                  let baseUrl = getStr "baseUrl" |> Option.defaultValue ""
                                  let model = getStr "model" |> Option.defaultValue ""
                                  let extra =
                                      match pt.TryGetValue "extra" with
                                      | true, b -> Some(JsonNode.Parse(string b))
                                      | _ -> None
                                  yield
                                      kv.Key,
                                      { id = kv.Key
                                        kind = kind
                                        baseUrl = baseUrl
                                        apiKey = getStr "apiKey"
                                        model = model
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
                                        command = getStr "command"
                                        args = args
                                        env = env
                                        url = getStr "url"
                                        maxConcurrency = getInt "maxConcurrency" }
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
            if configVersion <> 1 then
                errors.Add(sprintf "configVersion %d not supported" configVersion)
            match instanceId with
            | None -> errors.Add "instanceId: required UUID"
            | Some _ -> ()
            let listen, maxAtt, chunk = network
            if String.IsNullOrWhiteSpace listen then errors.Add "network.listen: required"
            if maxAtt <= 0L then errors.Add "network.maxAttachmentBytes: must be positive"
            if chunk <= 0 then errors.Add "network.chunkSizeBytes: must be positive"
            for kv in providers do
                if kv.Value.kind <> "openai" then errors.Add(sprintf "providers.%s.kind: unsupported kind %s" kv.Key kv.Value.kind)
                if String.IsNullOrWhiteSpace kv.Value.baseUrl then errors.Add(sprintf "providers.%s.baseUrl: required" kv.Key)
                if String.IsNullOrWhiteSpace kv.Value.model then errors.Add(sprintf "providers.%s.model: required" kv.Key)
            // 决策 162：重复/冲突稳定标识在配置加载时直接判为无效
            let ids = providers.Keys |> Set.ofSeq
            for kv in mcpServers do
                if ids.Contains kv.Key then errors.Add(sprintf "mcp.%s: id conflicts with provider id" kv.Key)
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
                      maxAttachmentBytes = maxAtt
                      chunkSizeBytes = chunk
                      pairingFailureWindowMinutes = let a, _, _ = pairing in a
                      pairingMaxFailures = let _, b, _ = pairing in b
                      pairingFreezeMinutes = let _, _, c = pairing in c
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
        network.Add("maxAttachmentBytes", cfg.maxAttachmentBytes)
        network.Add("chunkSizeBytes", int64 cfg.chunkSizeBytes)
        top.Add("network", network)

        let pairing = TomlTable()
        pairing.Add("failureWindowMinutes", int64 cfg.pairingFailureWindowMinutes)
        pairing.Add("maxFailures", int64 cfg.pairingMaxFailures)
        pairing.Add("freezeMinutes", int64 cfg.pairingFreezeMinutes)
        top.Add("pairing", pairing)

        let providers = TomlTable()
        for kv in cfg.providers do
            let p = TomlTable()
            p.Add("kind", kv.Value.kind)
            p.Add("baseUrl", kv.Value.baseUrl)
            match kv.Value.apiKey with Some k -> p.Add("apiKey", k) | None -> ()
            p.Add("model", kv.Value.model)
            providers.Add(kv.Key, p)
        top.Add("providers", providers)

        let mcp = TomlTable()
        for kv in cfg.mcpServers do
            let m = TomlTable()
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
