namespace Wanxiang.UI

open System

/// 命令面板「最近使用」的本地读写（宿主职责）；落盘格式归包内 PaletteRecent，检索与排序归 Wanxiang2.CommandPalette。
module PaletteRecent =

    let private recentFile () =
        IO.Path.Combine(IO.Path.GetDirectoryName(UiPrefsFile.filePath ()), PaletteRecent.fileName)

    let loadRecent () : string list =
        try
            let path = recentFile ()
            if IO.File.Exists path then PaletteRecent.decode (IO.File.ReadAllText path) else []
        with _ -> []

    let saveRecent (recent: string list) =
        try
            let path = recentFile ()
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName path) |> ignore
            let tmp = path + ".tmp"
            IO.File.WriteAllText(tmp, PaletteRecent.encode recent)
            IO.File.Move(tmp, path, true)
        with _ -> ()
