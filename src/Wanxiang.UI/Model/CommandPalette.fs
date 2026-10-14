namespace Wanxiang.UI

open System

/// 命令面板「最近使用」的本地读写（宿主职责）；检索与排序归 Wanxiang2.CommandPalette。
module PaletteRecent =

    let private recentFile () =
        IO.Path.Combine(IO.Path.GetDirectoryName(UiPrefs.filePath ()), "palette-recent.json")

    let loadRecent () : string list =
        try
            let path = recentFile ()
            match LocalText.read path with
            | Some text ->
                Text.Json.JsonSerializer.Deserialize<string[]>(text)
                |> Option.ofObj |> Option.map (List.ofArray >> List.truncate CommandPalette.maxRecent) |> Option.defaultValue []
            | None -> []
        with _ -> []

    let saveRecent (recent: string list) =
        try
            let path = recentFile ()
            LocalText.write path (Text.Json.JsonSerializer.Serialize(Array.ofList recent)) |> ignore
        with _ -> ()
