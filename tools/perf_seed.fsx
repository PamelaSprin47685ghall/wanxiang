// 决策 290：感知性能基准用的数据目录生成器。
// 用法：dotnet fsi tools/perf_seed.fsx <dataDir> [conversations=1500] [longMessages=3000]
#r "../src/Wanxiang.Core/bin/Debug/net10.0/Wanxiang.Core.dll"
open System
open System.IO
open System.Text.Json.Nodes
open Wanxiang.Core

let args = fsi.CommandLineArgs |> Array.skip 1
let dir = args[0]
let convs = if args.Length > 1 then int args[1] else 1500
let longN = if args.Length > 2 then int args[2] else 3000
let msg (role: string) (text: string) : JsonNode =
    let o = JsonObject()
    o["role"] <- role
    let a = JsonArray()
    let t = JsonObject()
    t["text"] <- text
    a.Add t
    o["contents"] <- a
    o
let cfg = { provider = "openai"; model = "test-model"; instructions = None; tools = []; temperature = None; topP = None; maxTokens = None; thinkingBudget = None; extraJson = None }
let reply i =
    sprintf "### 第 %d 段\n\n一段中文正文，混 `行内代码` 与 [链接](https://example.com)，以及公式 $E = mc^2$。\n\n- 列表一\n- 列表二\n\n```fsharp\nlet f%d x =\n    x * %d + 1\n```\n\n$$\\int_0^1 x^2 \\, dx = \\frac{1}{3}$$" i i i
let day = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
let path = Path.Combine(dir, "events", "2026-09-01.jsonseq")
Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
let fs = new BufferedStream(new FileStream(path, FileMode.Create), 1 <<< 20)
let mutable id = 0UL
let write evs =
    id <- id + 1UL
    let c = Events.Commit.create id (DateTimeOffset(day).AddSeconds(float id)) evs
    let b = Ledger.Frame.encode (CommitCodec.commitToJsonLine c)
    fs.Write(b, 0, b.Length)
for n in 1 .. convs do
    let c = Guid.NewGuid()
    write [ ConversationCreated { conversationId = c; title = sprintf "对话 %d：关于项目计划的讨论" n; config = cfg } ]
    write [ AgentMessageRecorded { conversationId = c; payloadJson = msg "user" (sprintf "问题 %d needle%d" n n) } ]
    write [ AgentMessageRecorded { conversationId = c; payloadJson = msg "assistant" (reply n) } ]
let lc = Guid.NewGuid()
write [ ConversationCreated { conversationId = lc; title = "超长对话"; config = cfg } ]
for i in 1 .. longN do
    write [ AgentMessageRecorded { conversationId = lc; payloadJson = msg (if i % 2 = 1 then "user" else "assistant") (if i % 2 = 1 then sprintf "追问 %d" i else reply i) } ]
fs.Flush(); fs.Dispose()
printfn "seeded %s: %d commits, long=%O" dir id lc
