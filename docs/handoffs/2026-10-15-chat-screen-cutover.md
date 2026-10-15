# 工程师交接：聊天屏接回与旧视图退出

## 项目与起点

- 项目：`vibe/wanxiang`；当前分支 `assist/deletion-cutover`。
- 本轮父提交：`3d631b2`（上一轮 DeletionDesk 接回）；上一轮记录见 `2026-10-15-deletion-cutover.md`。
- 工程师分支保持 `extract-ledger@8f39e84`；`main` 保持 `f873c40`。
- vendor 仍锁定 `wanxiang2@c3a2e74abafd0daf98db28b6b8ede750f331e53c`，本轮没有改 pin、没有改任何新库源码。
- 开工已有的未跟踪 `wanxiang2/` 是工程师开发目录，本轮没有改动或纳入提交。
- 本轮只提交到本地协助分支，不推送、不合并、不发布。

## 已完成

1. `MainView.chat` 直接改为 `Wanxiang2.ChatScreen.ChatScreen`，UI 项目新增对应 PackageReference。生产路径不经过旧类型或一个兼容包装。
2. 删除 `src/Wanxiang.UI/Views/ChatView.fs` 的 1,109 行旧实现及其编译项。卡片身份、对账缓存、流式原位更新、挂载窗口、分页锚点、跳转高亮、标题、面包屑、菜单和版本条均由现有交付包承担。
3. AppShell 以 `ScreenFrame` 传入一帧事实。宿主保留消息/配置来源、导航、真实命令、着色/公式后端与高亮停留计时；不再维护另一份聊天屏状态。
4. 审批面板插槽与 `AwaitingChanged` 重绘由 ChatScreen 组合负责，删除宿主 `SyncAwaitingApproval` 和重复的审批接线。真实审批对象仍是应用已有的那个实例。
5. 去掉已被决策 350 淘汰的隐藏顶栏模型入口。快捷键和错误恢复统一使用 `composer.ModelAnchor()`，没有为了旧测试把不可见按钮补回去。
6. `BuildComposer` 先于 `BuildChat`，再把真实 `composer.Desk` 交给 ChatScreen；局部构造组件的测试使用同一次序。浏览器探针 `lifecycle.chatAttached` 绑定在新屏幕的真实根控件上。

生产源码合计净减少 1,119 行。这个数字不包含测试与交接文档，也不代表系统总复杂度降低相同比例。

## 测试如何承接

没有删除任何原有测试或降低行为、性能阈值。原有 561 项应用用例先全部通过，再增加两项切换验收。

- `ChatFixture.fs`：147 行、只编入 `Wanxiang.Tests`。给原有场景提供装配便利，所有渲染、滚动、菜单、审批均调用真实 ChatScreen / ChatPane / ChatHeader 及 Cards；不复制旧规则，不保存卡片、滚动、帧或审批的第二份状态。它不是生产兼容层。
- 检查真实 AppShell 的用例直接读取 ChatScreen，不把生产对象强转成旧夹具。控件层级检查改读公开 `Header.Host` / `Pane`，未放宽几何或滚动断言。
- 旧的隐藏模型按钮测试改为验证唯一的输入区入口可操作；无障碍与外观源码检查转向仍存在的宿主/锁定库，而不是保留已删除文件。
- `ChatScreenCutoverTests.fs` 新增两项：应用程序集没有旧 ChatView/CardKey，且实际字段类型是 ChatScreen；真实 ApprovalDesk 的事件无需再次调用 AppShell.Render/Screen.Render，就能更新现有工具卡的等待批准状态，并确实位于输入区附属位。
- 用例下限从 UI 340 / NonUI 221 上调到 UI 341 / NonUI 222；LargeHistory 仍为 3。没有修改测试执行器或放宽门禁。

## 验证结果

环境：Linux，.NET SDK 10.0.111；使用上一轮从锁定源码生成的交付包和仓库内隔离 NuGet 缓存。本轮未重跑 316 个包的全部打包；锁定库源码完全不变。

| 验证 | 结果 |
| --- | --- |
| ChatScreen 自身测试（含整树金样、长对话流式性能） | 6 通过，0 失败、0 skip |
| 新增应用切换专项 | 2 通过，0 失败、0 skip；包含在下方全量中，不重复累计 |
| `./run-tests.sh -j 4 --large-history` | UI 341；非 UI 含大历史 225；合计 566 个用例，565 通过、0 失败、1 个既有 skip |
| Release 解决方案构建 | 通过，0 错误；11 条既有测试编译警告 |
| App + PWA publish | 通过，发布目录内 avalonia.js 补丁核验通过 |
| 发布产物真实 Chrome 冒烟与 PWA parity | 通过：画布有内容、DPR 2、文本上屏、无页面异常、运行时探针、online 重连入口、快捷键、偏好写入/刷新恢复、pagehide 草稿、深链接 |
| UI trait 检查 | 通过：57 个文件含 UI 测试，286 个方法标记 |
| `dotnet format --verify-no-changes` | 退出码 0；仍提示工作区加载警告，不能声称无警告 |
| `git diff --check` | 通过 |

唯一 skip 是既有的 10^6 提交冷启动手动基准；本轮没有新增 skip。11 条测试警告与上一轮相同，没有通过禁用警告消除。没有远端 Actions 结果，没有真机移动端或真实输入法结论。

## 复验与证据位置

从仓库根运行；全新检出时先按 README 初始化 vendor 并运行 `tools/wanxiang2-feed.sh`。

```sh
dotnet test --project vendor/wanxiang2/compose/ChatScreen/tests/ChatScreen.Tests.fsproj \
  -p:RestorePackagesPath="$PWD/vendor/wanxiang2/artifacts/packages"
python3 tools/tag_ui_tests.py --check
./run-tests.sh -j 4 --large-history
dotnet build wanxiang.slnx -c Release
dotnet publish src/Wanxiang.App/Wanxiang.App.fsproj -c Release \
  -o "$PWD/.scratch/assist-chat-screen-2026-10-15/publish"
SMOKE_OUT="$PWD/.scratch/assist-chat-screen-2026-10-15/shots" \
  tools/smoke_published.sh "$PWD/.scratch/assist-chat-screen-2026-10-15/publish"
dotnet format wanxiang.slnx --verify-no-changes
```

本机日志：`.scratch/chat-screen-{tests-build,cutover-tests,library-tests,full-suite,release-build,publish,smoke,format}.log`。发布物与截图在 `.scratch/assist-chat-screen-2026-10-15/`；这些都是未提交的可再生证据。冒烟主机已由脚本关闭。

## 下一步与边界

下一步仍是 Workspace，不是再造一个聊天屏。此次没有改整个布局组合，也没有把输入区的宿主职责删掉。

- `Composer.fs` 仍有真实职责：输入历史召回、IME/键盘适配、图像字节解码、上下文摘要。不要为了消灭文件，把这些行为悄悄漏掉。
- 接 Workspace 时要处理 `Composer` 与 `composer.Desk.Host` 的父控件归属；不能让同一控件同时挂在两个父节点。分隔条、浏览器 CSS 视口、设置页切换和焦点归宿各有既有测试，接线后继续保留。
- 测试夹具可以在以后将场景逐个改为新公开接口时缩减；它没有赋予旧生产 API 继续存在的理由。不要把它引用进应用，也不要往里面实现任何聊天规则。
- 这次证明的是锁定库在当前应用中的真实消费，不是整个抽吸已经完成，更不是所有平台已经验收。
