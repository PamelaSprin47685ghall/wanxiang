namespace Wanxiang.UI

open System

/// 命令面板「最近使用」的本地读写（宿主职责）；落盘格式归包内 PaletteRecent，检索与排序归 Wanxiang2.CommandPalette。
module PaletteRecent =

    let private recentFile () =
        IO.Path.Combine(IO.Path.GetDirectoryName(UiPrefsFile.filePath ()), PaletteRecent.fileName)

    let loadRecent () : string list =
        try
            let path = recentFile ()
            match LocalText.read path with
            | Some text -> PaletteRecent.decode text
            | None -> []
        with _ -> []

    let saveRecent (recent: string list) =
        try
            let path = recentFile ()
            LocalText.write path (PaletteRecent.encode recent) |> ignore
        with _ -> ()
