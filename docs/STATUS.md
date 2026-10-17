# 万象 · 退役状态（2026-10-17）

本文原为旧仓库（wanxiang）的收敛状态说明（2026-10-13 版）。项目本体已迁移至
`wanxiang2/` 子模块并完成扶正：原收敛目标已在 wanxiang2 落地，本文据此改为
退役状态表述。

## 1. 收敛目标在 wanxiang2 的落地

- **唯一入口**：桌面 `wanxiang2/app/Desktop/`、PWA `wanxiang2/app/Pwa/`，两工程已纳入
  `wanxiang2/wanxiang2.slnx`；旧仓库 `src/` 宿主壳退役中（已净化为过渡入口薄壳，待物理删除）。
- **独立启动**：`wanxiang2/run-desktop.sh`（产包 → 构建 → 启动桌面）、`wanxiang2/run-pwa.sh`
  （产包 → 构建 PWA → 本机静态托管，默认端口 8000）；脚本明示不依赖宿主仓库的 start.sh 与包源脚本。
- **独立构建/测试**：`wanxiang2/x`（`test` 逐库、`check` 架构合规、`pack` 交付包、`compose`
  干净消费测组合者、`all` 依次全部）；slnx 实测含 254 个库目录，每库带自有 tests 工程；
  自有 CI `wanxiang2/.github/workflows/ci.yml`。
- **自包含发布闭环**：`wanxiang2/publish.sh`——四 RID（linux-x64 / win-x64 / osx-arm64 / osx-x64）
  自包含 publish → tar.gz/zip + SHA-256 → `--draft` 挂 GitHub 草稿 Release，并校验发布目录内嵌的
  PWA bundle；Avalonia 浏览器补丁工具链（`tools/patch_avalonia_js.py` + 校验脚本）已在 wanxiang2。
- **运行验证**：双入口 0 错误编译、publish 全通过（EXIT=0）、`x check` 全绿、571 项基线通过，
  来自 DevOps 执行记录（2026-10-17）；571 项基线的分支记录另见 `wanxiang2/AGENTS.md` 迁移状态注。

## 2. 原收敛期内容的处置

2026-10-13 版的功能清单、已知局限、发版步骤与测试说明不再在旧仓库维护，避免双真源；
现行功能与质量以 wanxiang2 为准（见 `wanxiang2/AGENTS.md` 与 `README.md` 文首声明），
原文可在本仓库 git 历史中查阅。

## 3. 遗留与待处置（未完成）

- 宿主壳 `src/`（三工程薄壳、`wanxiang.slnx`、`src/Directory.Build.props`）尚未物理删除。
- 根目录 `start.sh`、`run-tests.sh`、`tests/`、`tools/`、`packaging/` 的处置尚未逐项执行
  （kelivo/、site/、shots*/、docs/ 其余文档的「保留 / 归档 / 删除」判定亦未完成）。
- 退役完成的判定以 `PLAN.md` §1.2 为准；本次执行进展与出处见其 §6。