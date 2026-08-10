namespace Wanxiang.UI

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// 本机 UI 偏好（~/.config/wanxiang/ui.json；不经 NDJSON）。
type UiPrefs = {
    autoCollapseReasoning: bool
    fontScale: float
}

module UiPrefs =

    let defaultPrefs = { autoCollapseReasoning = true; fontScale = 14.0 }

    let filePath () =
        let home =
            match Environment.GetEnvironmentVariable "WANXIANG_HOME" with
            | s when not (String.IsNullOrWhiteSpace s) -> s
            | _ -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".config", "wanxiang")
        Path.Combine(home, "ui.json")

    let private readObject () =
        try
            let path = filePath ()
            if File.Exists path then
                let node = JsonNode.Parse(File.ReadAllText path)
                if isNull node then JsonObject() else node.AsObject()
            else JsonObject()
        with _ ->
            JsonObject()

    let load () : UiPrefs =
        try
            let o = readObject ()
            let getB k d =
                let mutable n: JsonNode = null
                if o.TryGetPropertyValue(k, &n) && n <> null && n.GetValueKind() = JsonValueKind.True then true
                elif o.TryGetPropertyValue(k, &n) && n <> null && n.GetValueKind() = JsonValueKind.False then false
                else d
            let getD k d =
                let mutable n: JsonNode = null
                if o.TryGetPropertyValue(k, &n) && n <> null then
                    let v = n.GetValue<double>()
                    if Double.IsNaN v then d else v
                else d
            { autoCollapseReasoning = getB "autoCollapseReasoning" defaultPrefs.autoCollapseReasoning
              fontScale =
                  let s = getD "fontScale" defaultPrefs.fontScale
                  if s < 12.0 then 12.0 elif s > 16.0 then 16.0 else s }
        with _ ->
            defaultPrefs

    let save (prefs: UiPrefs) =
        try
            let path = filePath ()
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let o = readObject ()
            o["autoCollapseReasoning"] <- prefs.autoCollapseReasoning
            o["fontScale"] <- prefs.fontScale
            File.WriteAllText(path, o.ToJsonString())
            try File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite) with _ -> ()
        with _ ->
            ()
