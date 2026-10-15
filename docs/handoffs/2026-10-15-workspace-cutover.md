# 工程师交接：工作区接回与输入区唯一挂载

## 项目与起点

- 项目：`vibe/wanxiang`，施工分支 `assist/deletion-cutover`。
- 父提交：`c1f3b2d`（ChatScreen 接回）；此前还有 `3d631b2`（DeletionDesk 与包消费验收）。两轮各有同目录交接文件。
- 工程师分支在开工时为 `extract-ledger@8f39e84`，`main@f873c40`；本轮不移动它们。
- vendor 继续锁定 `wanxiang2@c3a2e74abafd0daf98db28b6b8ede750f331e53c`。本轮不更新 pin，不修改 vendor 或未跟踪 `wanxiang2/` 开发目录的源码，不重打全部 316 个包。
- 只在本地协助分支提交，不推送、不合并、不发布。

## 已完成的职责迁移

1. UI 项目经 `PackageReference` 消费现有 `Wanxiang2.Workspace`。`MainView.workspace` 直接是库对象，不新增生产转发壳。
2. 移除宿主的 `shellDesk`、`shellGrid`、`sidebarSplitter`、`hostViewportWidth` 四个字段，以及 `ApplyResponsiveLayout(width)`。分隔条、列布局、紧凑模式、起步页和 CSS 视口优先级由 Workspace 组织，不在宿主保留第二份状态。
3. `BuildWorkspace` 只把已构造的 SidebarDesk、ChatScreen、ComposerDesk 和浮层根组合起来，并接真实输入来源、侧栏宽度偏好与设置页。Workspace 将自身放进浮层根最底层，宿主不重复插入。
4. 浏览器的视口轮询仍在宿主，读到 CSS 宽度即发给 `workspace.ObserveViewport`；去重、取档与应用归库。桌面尺寸变化由库自身的 Bounds 订阅处理。恢复检测、页面链接与计时器停启未移走。
5. 分隔条的真实 `SidebarResized` 事件写回原有 `UiPrefsFile`，焦点来源追踪仍接实际分隔条。抽屉开合不覆盖桌面折叠偏好，紧凑模式不覆盖保存的桌面宽度。
6. 设置页切换只隐藏/显示 `workspace.Host`；回到原触发点或输入框的焦点逻辑留在宿主。

## Composer 的父节点问题怎样解决

旧 Composer 继承 Border，并在 Build 中把 `Desk.Host` 挂成自己的 Child。Workspace 也要挂同一个 `Desk.Host`，不能让两者同时充当父节点。

本轮将 Composer 改为普通宿主适配对象，公开 `Host = Desk.Host`，移除 Border 继承、重复的 compact 标记及 Build 方法。原来的初始化在构造时完成；没有在接线时临时从另一父节点夺走控件，也没有新增兼容标志、空控件或测试专用生产接口。

输入历史召回、IME 检查、键盘钩子、图像字节解码、模型视觉提示和上下文摘要仍由同一个适配对象承担。输入框、门控、附件与控件树仍是同一个 ComposerDesk 的产物。宿主设置已保存的发送键偏好仍在构造后立即进行。

调用者要挂载或遍历界面时使用 `composer.Host`；读写文本、提交、历史与焦点继续调用 Composer。`composer.Build()` 已不存在。测试改为直接使用真实 Host，没有再造第二个 Composer 测试包装。

## 测试承接与新反例

原有测试全部保留，行为和性能阈值未放宽，没有新 skip。

- 原来将 Composer 当作 Control 的挂载、尺寸、自动化查找改为读取 Host；焦点断言改读实际 Input。
- 使用局部 MainView 构造的测试在参与者建好后调用真实 `BuildWorkspace`，仍不调用 AutoConnect 或读取用户凭据。
- 两处反馈断言原先把空 TextBlock.Text 当非空字符串；挂入完整工作区后暴露 NullReference。现在文本提取使用 `Option.ofObj`，仍要求出现原来的删除反馈和不可撤销提示。

新增 `WorkspaceCutoverTests.fs` 的五项证明：

1. 实际应用字段类型为 Workspace，四个旧状态字段与旧布局方法不存在；Composer 不是 Control，也没有 Build。
2. 真实窗口四次宽窄切换，不手调响应式方法，模式自动切换；同一个输入框、草稿、父节点和唯一挂载保持不变。
3. 窄 CSS 视口压过较宽物理窗口；打开/关闭真实设置页后草稿、输入实例、父节点、模式和焦点都保留。
4. 真实顶栏按钮驱动桌面折叠与窄屏抽屉；键盘收起回输入框，触屏方式打开不自动抢焦点；桌面折叠偏好不被窄屏操作覆盖。
5. 通过无头平台的真实 MouseDown/MouseMove/MouseUp 拖动分隔条 48 像素，实际列宽变化并写回偏好；进入/退出紧凑模式不丢失该宽度。不是手动触发一个假 SidebarResized 回调。

测试修改偏好时恢复原始字节，使用既有 Headless 测试目录，不触及真实用户配置。

## 实际验收

环境：Linux，.NET SDK 10.0.111。消费上一轮从锁定库提交生成的包。本轮没有重新声称完成整套包构建或依赖图检查。

| 验证 | 结果 |
| --- | --- |
| Workspace 自身测试（含整树金样） | 3 通过，0 失败、0 skip |
| 新切换测试与受影响的 InteractionReliability 定向验证 | 28 通过；当时尚未加入最后的鼠标拖动用例，全部包含在下方全量中 |
| `./run-tests.sh -j 4 --large-history` | UI 345；非 UI 含大历史 226；总计 571，570 通过、0 失败、1 个既有 skip |
| 默认应用用例（上述全量的子集） | UI 345 + NonUI 223 = 568 项，全部通过；大历史另有 2 项通过和 1 项 skip |
| Release 解决方案构建 | 通过，0 错误；仍有 11 条既有测试警告 |
| App + PWA 发布及 avalonia.js 补丁核验 | 通过 |
| 发布物真实 Chrome 冒烟及 PWA parity | 通过：真实画布、DPR 2、文字输入桥、无页面异常、启动与计时器、online 恢复、快捷键、偏好刷新恢复、pagehide 草稿与深链接 |
| UI trait 检查 | 通过：58 个文件含 UI 测试，290 个方法已标注 |
| `dotnet format wanxiang.slnx --verify-no-changes` | 退出码 0；仍提示工作区加载警告 |

唯一 skip 是原有的 10^6 提交冷启动手动基准。基线提高为 UI 345 / NonUI 223 / LargeHistory 3。没有改测试执行器或门禁。一次包含额外状态检查的工具调用被安全检查拦截，其结果不计作证据；上述验证均以实际成功返回为准。

## 复验与证据

```sh
dotnet test --project vendor/wanxiang2/compose/Workspace/tests/Workspace.Tests.fsproj \
  -p:RestorePackagesPath="$PWD/vendor/wanxiang2/artifacts/packages"
python3 tools/tag_ui_tests.py --check
./run-tests.sh -j 4 --large-history
dotnet build wanxiang.slnx -c Release
dotnet publish src/Wanxiang.App/Wanxiang.App.fsproj -c Release \
  -o "$PWD/.scratch/assist-workspace-2026-10-15/publish"
SMOKE_OUT="$PWD/.scratch/assist-workspace-2026-10-15/shots" \
  tools/smoke_published.sh "$PWD/.scratch/assist-workspace-2026-10-15/publish"
dotnet format wanxiang.slnx --verify-no-changes
```

日志在 `.scratch/assist-workspace-2026-10-15/`：`library-tests.log`、`cutover-tests.log`、`full-suite.log`、`release-build.log`、`publish.log`、`smoke.log`、`format.log`。同目录的 `publish/` 与 `shots/` 是可再生产物，未纳入提交。最初迁移测试装配时的失败日志也保留，没有替换成成功日志。

## 接手边界

这一轮完成的是工作区的真实包消费，不是整个抽吸已经结束。协议、持久格式、输入历史规则、网络重连与宿主计时策略未改；沒有真实移动设备、输入法或远端 Actions 结论。

接手时先审阅本提交，再与前两轮一起决定如何纳入 extract-ledger。不要复活空 Composer Border 或宿主的布局副本来迁就旧调用者。AppShell 剩下的消息、连接、草稿和命令协作应按真实职责另选一个完整单元；不要仅为减少文件行数继续拆控制流程。
