# wanxiang（已退役）

本仓库已物理退役。万象（wanxiang）的产品本体是 `wanxiang2/`——一个独立的 Git 仓库（本目录下的物理副本不再被本仓库跟踪）：

- 远程：`git@github.com:PamelaSprin47685ghall/wanxiang2.git`

## 迁移简史

1. `bfdda85` 宿主壳净化：移除旧 UI 实现与旧测试，登记退役总纲 PLAN.md。
2. `fc3e28f`（wanxiang2）九桥补齐：DraftBackend、StartupTrace 执行侧、PaletteRecentFile、KeyShortcut、UnansweredReply 等宿主桥与 (b) 类资产合流收束，双入口构建与全量测试通过。
3. `1a25b35`（wanxiang2）产品文档与历史档案迁入 docs/，并建立独立 NuGet 源配置。
4. 本提交：物理废弃——删除 src/ 入口链、tests/、tools/、CI、品牌与截图资产、本地参考克隆；解除子模块挂载；产品文档已迁 wanxiang2/docs/。

退役总纲见 PLAN.md（历史记录）；最终状态见 docs/STATUS.md。全部删除均有提交基线可回滚（本仓库自 `bfdda85` 起）。
