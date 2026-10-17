# 状态：已退役（终态）

**本仓库已完成物理退役（2026-10-17）。wanxiang2 是唯一产品本体。**

- 旧 `src/` 宿主壳、`tests/`、`tools/`、`.github/workflows/` CI、品牌页（site/）、打包资产（packaging/）、截图资产（shots/ 与 13 个 shots-r\* 目录）、本地参考克隆（kelivo/）已全部删除；`bfdda85` 基线保证可回滚。
- 产品文档（contracts / security / ux / adr）与历史档案（11 篇 handoffs、品牌页、packaging 的 URL scheme 平台知识）已迁入 `wanxiang2/docs/`（wanxiang2 提交 `1a25b35`，删除即永久丢失的记录均已保全）。
- 子模块挂载已解除（`git rm --cached wanxiang2`，`.gitmodules` 已删除）；`wanxiang2/` 目录保留为独立仓库的本地副本，不再被本仓库跟踪。
- wanxiang2 构建链已完全独立：自有 `NuGet.config` 指向其 `artifacts/feed`，restore 不再依赖本仓库的任何文件；终验（清缓存后 restore + 双入口 build + `./x check`）凭据见当次运维记录。

本文件是本仓库的终态声明，不再更新。
