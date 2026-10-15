# wanxiang / wanxiang2 源码与本机状态交接

## 任务和基线

用户要求参照 wanxiangshu / wanxiangli：正式 submodule、显式跟踪最新版本、状态退出 Git、
清理旧数据 refs、不向 Git 提交任何 `.tgz`。本项目是 .NET/NuGet，不复制 npm/Fable 方案。

接手时为 `assist/deletion-cutover@cc35785`，根 `wanxiang2/` 已登记为 submodule，但 feed、
NuGet 和 CI 部分路径仍是 `vendor/wanxiang2`。锁定子模块为 `7a13032`，最初仅本机拥有，
远端 main 是 `c3a2e74`。未 reset、rebase 或覆盖该本地提交。

施工期间协作者提交并推进了两边分支：宿主先后包含 `f40b6ef`、`d9708b2`、`e253e63`、
`dece2e2`，并切到 main；最后一次远端查询时宿主 main 为 `dece2e2`，新库 main 已发布
`7a13032f202c78bbca0ed411c98a567038f0e29a`，与宿主 gitlink 一致。以上提交/推送不是本轮助手执行。

## 已落地的修改

- feed、NuGet 和 CI 路径改为根 `wanxiang2/`。保留完整包集、逐包摘要、构建回执和失败停机。
- `tools/wanxiang2-feed.sh --source-check` 不产包，只核对两仓索引、gitlink、main 跟踪关系及
  源码干净性；忽略和索引检查均覆盖 `.tgz`、`.nupkg`、`.snupkg`（含大小写变体）。
- `--update` 显式 fetch 最新 main，仅快进；拒绝未发布提交、脏工作区、远端地址不符、
  网络等待期间发生的源码变化及包含二进制包的目标提交。不暂存、提交、推送或构建。
- 宿主 `LocalState` 定位主工作树 `.Herdchestra/`，拒绝 Git 路径与链接误入。Program 在
  LaunchPlan 读取配置前检查路径；默认配置、数据、偏好和草稿走新根。显式目录参数保留。
- `start.sh` 不再强制 XDG 默认目录，交给真实程序推导；显式相对路径先按调用目录展开。
- PWA 的既有浏览器存储与 C/S 协议保留；库的日志格式和存储语义未改。
- README、主 SSOT 与 `docs/contracts/source-and-local-state.md` 记录新的边界，旧交接不改历史数字。

## 实际清理

已删除当前仓库 `.git/wanxiangshu/`，未读取其中日志内容；已移除 `.git/info/exclude`
里旧 diagnostic bridge 的两行，保留其他内容。最后核对目录不存在，未安装新 hooks 或 refspec。

远端 `refs/wanxiang/store` 仍指向 `376ad7c702a9262f50e4ffb53ad2b1a39c26a511`。
按该精确旧值约束的删除操作被工具要求二次确认，未执行，也未改用另一通道绕过。
源码 branches、tags、objects 和 `.git/modules/wanxiang2` 均未清理；没有历史改写或 GC。

去掉旧 exclude 后显露出原有的根 `.wanxiangshu/` 未跟踪目录。本轮未删除该目录，不能
将它当作本轮新生成物提交。运行旧程序可能继续产生旧诊断；本轮没有停止或重启任何应用。

## 尚未落地或验证

- 工具拒绝编辑 `CredentialStore.fs`，文件未改；正常 App 入口给现有凭据路径逻辑传入
  已检查的 `WANXIANG_HOME`。绕开 App 入口的独立宿主仍需检查其默认路径，不能声称已覆盖。
- 对独立新库 `.gitignore` / `x` / 根规则的追加被工具拦截，未落地。宿主 gate 已检查
  两仓索引，但不能把它说成新库自己也安装了门禁；新库维护者还需补齐独立检查。
- 本轮没有编译、执行测试、产包、启动应用、浏览器冒烟或真实模型调用。
- 仅执行 Bash 语法、Python AST、XML 及显式 Compile 路径检查和 Git diff 检查。
  五项 LocalState 反例和新增包源回归均是测试源码，不是通过证据。

## 后续验收入口

先复核当前 HEAD 和工作区，勿按上述中途快照 reset。保留并提交本轮尚未被协作者纳入的
相关改动；新库保持干净后再运行以下验收。不要使用旧构建回执跳过应用源码重建。

```sh
tools/wanxiang2-feed.sh --source-check
python3 -B -m unittest discover -s tools/tests -p test_wanxiang2_feed.py -v
tools/wanxiang2-feed.sh
./run-tests.sh -j 4
```

以上命令仅为交接，不表示本轮已经运行。远端旧状态引用的删除另需工具二次确认。
