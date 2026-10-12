namespace Wanxiang.Server

open System
open System.IO
open System.Collections.Concurrent
open Wanxiang.Protocol

/// 决策 250：中途失败留下的半截回复由主机保存（每对话至多一份），
/// 刷新页面、换设备、甚至主机重启后都还能「继续生成」。
/// 它不进对话账本：只有用户点「继续生成」才把它作为助手消息落盘（决策 230 的“宁可不混进历史”不变）。
/// 存的是编码后的 `generation.partial` 事件本身，读回即可原样下发。
type PartialReplyStore(directory: string option) =
    let items = ConcurrentDictionary<Guid, WireEvent>()
    let pathOf (convId: Guid) = directory |> Option.map (fun d -> Path.Combine(d, convId.ToString("D") + ".json"))

    do
        match directory with
        | Some d ->
            try
                Directory.CreateDirectory d |> ignore
                for file in Directory.GetFiles(d, "*.json") do
                    try
                        match WireCodec.tryDecode (File.ReadAllText file) with
                        | Ok(GenerationPartial p as ev) -> items[p.conversationId] <- ev
                        | _ -> ()
                    with ex -> eprintfn "[wanxiang] 跳过无法读取的半截回复 %s：%s" file ex.Message
            with _ -> ()
        | None -> ()

    member _.Save(ev: WireEvent) =
        match ev with
        | GenerationPartial p ->
            items[p.conversationId] <- ev
            match pathOf p.conversationId with
            | Some path ->
                try
                    let tmp = path + ".tmp"
                    File.WriteAllText(tmp, WireCodec.encode ev)
                    File.Move(tmp, path, true)
                with ex ->
                    // 稳定性（r17）：落盘失败不能静默——否则重启后「继续生成」悄悄消失
                    eprintfn "[wanxiang] 半截回复落盘失败（%s）：%s" path ex.Message
            | None -> ()
        | _ -> ()

    member _.TryGet(convId: Guid) : WireEvent option =
        match items.TryGetValue convId with
        | true, ev -> Some ev
        | _ -> None

    member _.Clear(convId: Guid) =
        if items.TryRemove(convId) |> fst then
            match pathOf convId with
            | Some path -> (try File.Delete path with _ -> ())
            | None -> ()
