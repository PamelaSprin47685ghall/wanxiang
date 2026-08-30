namespace Wanxiang.Server

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Wanxiang.Config

/// 内置工具（决策 95：随万象编译发布，由代码注册）。
///
/// 稳定标识：`builtin:echo` / `builtin:time` / `builtin:file.read` /
/// `builtin:file.list`。
///
/// `file.read` / `file.list` **必须**配置沙箱根目录才可用
/// （`[tools] fileReadRoots`）。没有沙箱时它们等于把整台机器的文件
/// （包括存着 apiKey 的 config.toml）交给模型，因此默认完全不注册。
module BuiltinTools =

    /// 单文件读取上限。
    let maxReadBytes = 1024L * 1024L

    /// 目录列举条目上限。
    let maxListEntries = 500

    /// 稳定标识 ↔ 函数名：`builtin:file.read` ↔ `builtin_file_read`。
    let functionName (stableId: string) : string =
        "builtin_" + stableId.Substring("builtin:".Length).Replace(".", "_")

    let stableId (functionName: string) : string =
        "builtin:" + functionName.Substring("builtin_".Length).Replace("_", ".")

    /// 路径是否落在任一沙箱根内（解析符号链接后比较，避免 `..` 与软链穿越）。
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

    let private readFile (roots: string list) (path: string) : string =
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

    let private listDirectory (roots: string list) (path: string) : string =
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

    /// 构建全部内置工具。`roots` 为空时不注册文件类工具。
    let all (cfg: ToolsConfig) : AITool list =
        let echo =
            AIFunctionFactory.Create(
                Func<string, string>(fun text -> text),
                name = "builtin_echo",
                description = "把输入原样返回。用于验证工具调用链路是否通畅。")
        let time =
            AIFunctionFactory.Create(
                Func<string>(fun () -> DateTimeOffset.UtcNow.ToString "o"),
                name = "builtin_time",
                description = "返回当前 UTC 时间（ISO 8601）。")
        let fileTools =
            if List.isEmpty cfg.fileReadRoots then []
            else
                let rootHint = String.Join("、", cfg.fileReadRoots)
                let fileRead =
                    AIFunctionFactory.Create(
                        Func<string, string>(readFile cfg.fileReadRoots),
                        name = "builtin_file_read",
                        description =
                            sprintf
                                "读取沙箱内的文本文件（最多 %d KiB）。允许的根目录：%s。path 需为绝对路径。"
                                (maxReadBytes / 1024L)
                                rootHint)
                let fileList =
                    AIFunctionFactory.Create(
                        Func<string, string>(listDirectory cfg.fileReadRoots),
                        name = "builtin_file_list",
                        description = sprintf "列出沙箱内目录的条目（最多 %d 项）。允许的根目录：%s。" maxListEntries rootHint)
                [ fileRead :> AITool; fileList :> AITool ]
        [ echo :> AITool; time :> AITool ] @ fileTools

    /// 工具描述（用于目录快照：客户端据此渲染可勾选的工具清单）。
    let descriptors (cfg: ToolsConfig) : (string * string * string) list =
        all cfg
        |> List.map (fun t -> stableId t.Name, t.Name, (if isNull t.Description then "" else t.Description))
