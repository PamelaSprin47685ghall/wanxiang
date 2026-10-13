# 万象 @VERSION@（@RID@）

自包含发行包：不需要另装 .NET。

## 启动

- 桌面 + 主机（默认）：运行 `@EXE@`。首次启动会在本机建立配置与数据目录，并自动配对本机窗口。
- 只当主机给手机 / 其他设备用：`@EXE@ --client=false --pwa`，在设备浏览器里打开主机地址，按屏幕上的配对码配对。
- 诊断：`@EXE@ --server=false --client=false --pwa=false`（只读 doctor）。

## wanxiang:// 消息链接

桌面模式启动时会自动为当前用户注册（设 `WANXIANG_NO_URL_SCHEME=1` 可关闭）。
`packaging/` 里是给安装器 / 手工注册用的模板，说明见 `packaging/README.md`。

## 说明

- macOS 包未签名、未公证；首次打开需在「系统设置 → 隐私与安全性」里允许，或 `xattr -dr com.apple.quarantine <目录>`。
- 校验：每个包旁有同名 `.sha256`。
