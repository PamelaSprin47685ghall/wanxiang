# 第 28 轮：桌面 / PWA 生命周期一致性审计

方法：`ParityProbe`（决策 450，默认关闭）在每条依赖生命周期的路径上打点。PWA 用发布产物 + headless Chrome（`?wxprobe=1`，`tools/parity_pwa.js`）实测；桌面用发布产物 + Xvfb（`WANXIANG_PARITY_PROBE=1`）实测。

| 行为 | 桌面 | PWA（修复前） | PWA（修复后） | 说明 / 守护 |
|---|---|---|---|---|
| 视图挂载 → 启动计时器（投递确认、请求超时、草稿自动保存） | ✅ | ✅（第 27 轮已修，决策 449） | ✅ | parity_pwa: `timer.delivery` `timer.draft` |
| 浏览器视口轮询（断点、恢复信号、页面链接） | 不适用 | ✅ | ✅ | `timer.viewport` |
| ChatView 挂载、动效订阅 | ✅ | ✅ | ✅ | `lifecycle.chatAttached` |
| 系统主题变化订阅 | ✅ | ✅ | ✅ | `theme.systemSubscribed` |
| 自动连接 | ✅ | ✅ | ✅ | `connect.auto` |
| 读本机草稿 | ✅（文件） | ✅（IndexedDB） | ✅ | `drafts.loaded` |
| 关闭 / 离开时落草稿 | ✅ Window.Closing | ❌ 无对应事件，只靠 800 ms 定时器（后台页被节流） | ✅ visibilitychange/pagehide | 决策 451，`drafts.flushOnHide` |
| 界面偏好持久化（主题、字号、侧栏、回车发送、草稿开关） | ✅ ui.json | ❌ 写进内存文件系统，刷新即丢 | ✅ localStorage | 决策 452，parity_pwa 刷新后核对 |
| 命令面板「最近使用」 | ✅ | ❌ 同上 | ✅ | 决策 452 |
| 键盘快捷键（首屏、点空白处后） | ✅ | ❌ 焦点在 body，按键进不了应用 | ✅ | 决策 454，`shortcut.resolved` |
| 键盘快捷键（输入框有焦点） | ✅ | ✅ | ✅ | — |
| 网络恢复 / 回到前台 → 立即重连 | 不适用（桌面走退避） | ✅ | ✅ | `reconnect.resume` |
| 深链 | ✅ `wanxiang://`（决策 251） | ✅ `#open?…` | ✅ | `deeplink.open` |
| 输入框聚焦 | ✅ | ✅ | ✅ | `focus.composer`（脚本外实测） |
| 剪贴板复制 / 粘贴 | ✅ | ✅（第 22 轮实测） | ✅ | 已打点 `clipboard.*`，CI 未断言（需会话） |
| 文件选择 / 保存（附件、导入、导出） | ✅ | ✅（第 14/15 轮实测） | ✅ | 已打点 `file.*`，CI 未断言 |
| 拖放附件 | ✅ | 未实测（headless 无法合成系统拖放） | — | 已打点 `dragdrop.drop` |
| 消息右键菜单 | ✅ | ✅（第 8 轮修复） | ✅ | 已打点 `contextmenu.message`，CI 未断言 |
| 限流倒计时 / 搜索防抖 | ✅ | 控件构造时挂事件，挂载顺序无风险 | ✅ | 已打点 |
| 系统通知 | 无此功能 | 无此功能 | — | — |

结论：第 27 轮那类「挂载早于订阅」的问题只出现在 MainView（顶层），子控件都是先挂事件再加入可视树，已逐个核对。另外发现 3 个与挂载无关、但同样「桌面正常、PWA 静默失效」的缺口（离开时落草稿、偏好持久化、首屏键盘），均已修复并进 CI 冒烟。
