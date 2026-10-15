# 工程师交接：锁定包源与验收产物

## 起点与范围

- 项目：`vibe/wanxiang`；协助分支 `assist/deletion-cutover`，父提交 `51fd759`。
- 工程师分支 `extract-ledger@8f39e84`、`main@f873c40` 不移动。
- vendor 仍为 `c3a2e74abafd0daf98db28b6b8ede750f331e53c`；不修改新库源码，不更新 pin，不动未跟踪的 `wanxiang2/` 开发目录。
- 前三轮完成 DeletionDesk、ChatScreen、Workspace 接回。本轮不新增业务库，也不改 AppShell；补实切换库版本后的构建、测试与启动边界。
- 只在本地协助分支提交，不推送、不合并、不发布。

## 为什么先做这一项

原 `run-tests.sh` 只检查一个 `Wanxiang2.LedgerStore.0.0.0-local.nupkg` 是否存在，不能据此确认整套包属于当前 pin。它又使用 `set -uo pipefail`，失败的产包命令之后没有显式退出。

修改前实际运行了受控反例：临时 Git 仓库里的产包命令写下一份包后退出 31，已有测试二进制仍被执行，整个测试入口退出 0。故障夹具只用来控制脚本路径，不是一个假的 NuGet/应用实现；它证明的是失败未阻断，不冒充生产库行为。

这会使抽吸验收失去归属：新的调用已经接回，但跑的可能仍是旧包与旧二进制。

## 已完成

### 包准备与完成回执

`tools/wanxiang2-feed.sh` 仍是唯一调用入口，具体核验由 `tools/wanxiang2_feed.py` 承担。

准备前检查 Git 索引中的 gitlink、vendor 实际 HEAD 和工作区状态。未初始化时沿用 `git submodule update --init`；pin 不符或源码有未提交改动时拒绝，不擅自移动已有 checkout。更新新库后须先明确 `git add vendor/wanxiang2`。

产包仍调用工程师原有的 `x pack`，没有另写依赖排序、增量编译或包管理器。成功回执记录 pin、完整主项目包集、逐包 SHA-256，位于忽略的 `vendor/wanxiang2/artifacts/feed/receipt.json`。

回执与当前实际字节一致时直接复用。缺少回执、缺包、多出旧包、包字节变化或 pin 更新时重新产包。先撤销旧回执，再执行构建；退出失败、包集不全或源码在构建期间变化，均不能留下新的成功回执。

成功后只清理本仓 `.nuget/packages/wanxiang2.*` 和应用/测试的 `obj/project.assets.json`，第三方包不清除。缓存目录被链接到别处，或 `NUGET_PACKAGES` 覆盖到其他位置时拒绝，不猜测该清哪个用户目录。包准备使用进程文件锁，第二个准备者明确失败。

### 测试与启动

`run-tests.sh` 和 `start.sh` 都先经过上述入口。产包失败立即停止，不运行旧二进制。

成功构建后，在各自输出目录记录 `wanxiang2-feed.sha256`。`--no-build` 与 `WANXIANG_SKIP_BUILD=1` 只接受仍匹配当前包源的完成回执；缺回执时必须正常构建。构建失败先删除旧回执，不能继续借用上次成功。

测试开始、构建结束和测试结束均复核包源。测试期间换 pin、改包或改 vendor 源码，即使已有测试打印了通过，也不能把结果归给另一个依赖版本。

CI 的 PR 门禁增加脚本故障回归；相关文件也进入大历史路径触发范围。原有应用用例、基线下限、性能阈值和 skip 均未修改。

## 反例与实际消费分开验收

新增 `tools/tests/test_wanxiang2_feed.py` 的 17 项脚本级测试，使用临时仓库、受控产包/构建命令及退出码，覆盖：

- 产包失败、成功退出但缺包、失败重建撤销旧完成标记。
- 完整包集与字节变化、无变化复用、未提交源码、pin 不符、构建中源码变化。
- 同时准备、只清指定缓存、拒绝缓存重定向及外部缓存覆盖。
- 测试与启动两条入口不执行旧产物；正常构建后的复用；构建失败撤销旧二进制回执；测试期间依赖变化不能被通过汇总掩盖。

这些测试不算进 F# 应用用例数。真实产包和应用验收另做如下：

| 验证 | 实际结果 |
| --- | --- |
| `x pack` 经新入口完整产包 | 316 个包，退出 0，源码 pin 未变 |
| 再次运行准备入口 | 不重写任何包；本机单次观测 0.082 秒，不作跨机器承诺 |
| 实际还原后的包字节核对 | 应用缓存中的 315 个 Wanxiang2 包逐一匹配本轮 feed SHA-256 |
| 旧应用 `WANXIANG_SKIP_BUILD=1` | 因没有当前构建回执而被明确拒绝 |
| 实际 `start.sh` 正常构建，再跳过构建 | 两次均运行真实主程序 `--help` 成功；只用 `.scratch` 下路径，不启动桌面或模型服务 |
| `./run-tests.sh -j 4 --large-history` | UI 345，非 UI 含大历史 226；总计 571，570 通过，0 失败，1 个既有 skip |
| 实际 `--no-build --no-ui` 复用 | 223 项通过，0 失败、0 skip；是上项子集，不重复累计 |
| 脚本故障回归 | 17 项通过 |
| Release 解决方案构建 | 通过，0 错误；11 条既有测试警告 |
| Bash 语法、UI trait 与差异检查 | 通过；测试清点基线未修改 |

唯一 skip 仍是 10^6 提交的手动基准。默认应用用例仍为 568 项，大历史额外两项通过。本轮未重跑各库全部测试、浏览器冒烟、真机 IME 或远端 Actions，也没有生成新发布包。

## 回执的边界

这是本机交付完成记录，不是签名或供应链认证，也不证明可跨任意 SDK/操作系统重复构建。包内部是否合法仍由 NuGet 和真实消费验证；本工具不另写 NuGet 解析器。

跳过构建的选项只核对库依赖，不检查应用源码是否在上次构建后变化。修改应用代码后照常构建。直接调用任意 `dotnet` 命令仍应先运行包准备入口；这些命令不会自动取得本入口的构建回执。文件锁约束的是本工具，不是任意外部构建命令。

## 复验与本机证据

```sh
python3 -B -m unittest discover -s tools/tests -p test_wanxiang2_feed.py -v
tools/wanxiang2-feed.sh --check
tools/wanxiang2-feed.sh             # 已核验的包可直接复用
tools/wanxiang2-feed.sh --rebuild   # 需要完整重建时使用
./run-tests.sh -j 4 --large-history
./run-tests.sh --no-build --no-ui
dotnet build wanxiang.slnx -c Release
```

日志在 `.scratch/assist-feed-2026-10-15/`：`prepare.log`、`gate-tests.log`、`consumption-audit.json`、`old-launch-refused.log`、`launch-build.log`、`launch-reuse.log`、`full-suite.log`、`no-build-tests.log`、`release-build.log`。这些是可再生本机证据，未纳入提交。

后续可以继续抽吸业务职责；接手时不要删除这道检查来绕过 pin 不符，也不要手工抄写构建回执。先提交要消费的新库，明确 vendor pin，再运行真实构建与验收。
