namespace Wanxiang.Server

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Wanxiang.Config

/// 路径解析与文件读取工具核心实现。
module BuiltinToolsCore =

    let maxReadBytes = 1024L * 1024L
    let maxListEntries = 500

    let resolveInRoots (roots: string list) (path: string) : Result<string, string> =
        if List.isEmpty roots then
            Error "文件工具未启用：服务端未配置 tools.fileReadRoots 沙箱根目录。"
        elif String.IsNullOrWhiteSpace path then
            Error "路径不能为空。"
        else
            try
                let full = Path.GetFullPath path
                let real = try Path.GetFullPath(File.ResolveLinkTarget(full, true) |> Option.ofObj |> Option.map (fun i -> i.FullName) |> Option.defaultValue full) with _ -> full
                let inside =
                    roots
                    |> List.exists (fun root ->
                        let normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
                        (real + string Path.DirectorySeparatorChar).StartsWith(normalizedRoot, StringComparison.Ordinal))
                if inside then Ok real
                else Error(sprintf "路径超出沙箱范围：%s" path)
            with ex ->
                Error(sprintf "路径无效：%s" ex.Message)

    let readFile (roots: string list) (path: string) : string =
        match resolveInRoots roots path with
        | Error e -> sprintf "error: %s" e
        | Ok real ->
            try
                let info = FileInfo real
                if not info.Exists then sprintf "error: 文件不存在：%s" path
                elif info.Length > maxReadBytes then sprintf "error: 文件超过 %d KiB 上限" (maxReadBytes / 1024L)
                else File.ReadAllText(real, Encoding.UTF8)
            with ex ->
                sprintf "error: %s" ex.Message

    let listDirectory (roots: string list) (path: string) : string =
        match resolveInRoots roots path with
        | Error e -> sprintf "error: %s" e
        | Ok real ->
            try
                if not (Directory.Exists real) then sprintf "error: 目录不存在：%s" path
                else
                    let entries =
                        Directory.EnumerateFileSystemEntries real
                        |> Seq.truncate maxListEntries
                        |> Seq.map (fun e ->
                            let name = Path.GetFileName e
                            if Directory.Exists e then name + "/"
                            else
                                try sprintf "%s (%d B)" name (FileInfo(e).Length)
                                with _ -> name)
                        |> List.ofSeq
                    if List.isEmpty entries then "（空目录）"
                    else String.Join("\n", entries)
            with ex ->
                sprintf "error: %s" ex.Message

type BuiltinEchoFunction() =
    inherit AIFunction()
    static let schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string","description":"要原样返回的文本"}},"required":["text"]}""").RootElement.Clone()
    override _.Name = "builtin_echo"
    override _.Description = "把输入原样返回。用于验证工具调用链路是否通畅。"
    override _.JsonSchema = schema
    override _.InvokeCoreAsync(args: AIFunctionArguments, _ct: CancellationToken) =
        let mutable text = ""
        if not (isNull args) then
            for KeyValue(k, v) in args do
                if not (isNull v) then
                    let s = string v
                    if text = "" || k.ToLowerInvariant().Contains "text" || k.ToLowerInvariant().Contains "input" || k.ToLowerInvariant().Contains "msg" then
                        text <- s
        ValueTask<obj>(box text)

type BuiltinTimeFunction() =
    inherit AIFunction()
    static let schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()
    override _.Name = "builtin_time"
    override _.Description = "返回当前 UTC 时间（ISO 8601）。"
    override _.JsonSchema = schema
    override _.InvokeCoreAsync(_args: AIFunctionArguments, _ct: CancellationToken) =
        ValueTask<obj>(box (DateTimeOffset.UtcNow.ToString "o"))

type BuiltinFileReadFunction(roots: string list) =
    inherit AIFunction()
    static let schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string","description":"要读取的文件绝对路径"}},"required":["path"]}""").RootElement.Clone()
    override _.Name = "builtin_file_read"
    override _.Description =
        sprintf "读取沙箱内的文本文件（最多 %d KiB）。允许的根目录：%s。path 需为绝对路径。"
            (BuiltinToolsCore.maxReadBytes / 1024L) (String.Join("、", roots))
    override _.JsonSchema = schema
    override _.InvokeCoreAsync(args: AIFunctionArguments, _ct: CancellationToken) =
        let mutable path = ""
        if not (isNull args) then
            for KeyValue(k, v) in args do
                if not (isNull v) then
                    let s = string v
                    if not (String.IsNullOrWhiteSpace s) && (path = "" || k.ToLowerInvariant().Contains "path" || k.ToLowerInvariant().Contains "file") then
                        path <- s
        ValueTask<obj>(box (BuiltinToolsCore.readFile roots path))

type BuiltinFileListFunction(roots: string list) =
    inherit AIFunction()
    static let schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string","description":"要列出条目的目录绝对路径"}},"required":["path"]}""").RootElement.Clone()
    override _.Name = "builtin_file_list"
    override _.Description =
        sprintf "列出沙箱内目录的条目（最多 %d 项）。允许的根目录：%s。"
            BuiltinToolsCore.maxListEntries (String.Join("、", roots))
    override _.JsonSchema = schema
    override _.InvokeCoreAsync(args: AIFunctionArguments, _ct: CancellationToken) =
        let mutable path = ""
        if not (isNull args) then
            for KeyValue(k, v) in args do
                if not (isNull v) then
                    let s = string v
                    if not (String.IsNullOrWhiteSpace s) && (path = "" || k.ToLowerInvariant().Contains "path" || k.ToLowerInvariant().Contains "dir") then
                        path <- s
        ValueTask<obj>(box (BuiltinToolsCore.listDirectory roots path))

/// 内置工具公开接口与元数据。
module BuiltinTools =

    let maxReadBytes = BuiltinToolsCore.maxReadBytes
    let maxListEntries = BuiltinToolsCore.maxListEntries

    let functionName (stableId: string) : string =
        "builtin_" + stableId.Substring("builtin:".Length).Replace(".", "_")

    let stableId (functionName: string) : string =
        "builtin:" + functionName.Substring("builtin_".Length).Replace("_", ".")

    let resolveInRoots (roots: string list) (path: string) : Result<string, string> =
        BuiltinToolsCore.resolveInRoots roots path

    let readFile (roots: string list) (path: string) : string =
        BuiltinToolsCore.readFile roots path

    let listDirectory (roots: string list) (path: string) : string =
        BuiltinToolsCore.listDirectory roots path

    let all (cfg: ToolsConfig) : AITool list =
        let echo = BuiltinEchoFunction() :> AITool
        let time = BuiltinTimeFunction() :> AITool
        let fileTools =
            if List.isEmpty cfg.fileReadRoots then []
            else
                [ BuiltinFileReadFunction(cfg.fileReadRoots) :> AITool
                  BuiltinFileListFunction(cfg.fileReadRoots) :> AITool ]
        [ echo; time ] @ fileTools

    let descriptors (cfg: ToolsConfig) : (string * string * string) list =
        all cfg
        |> List.map (fun t -> stableId t.Name, t.Name, (if isNull t.Description then "" else t.Description))
