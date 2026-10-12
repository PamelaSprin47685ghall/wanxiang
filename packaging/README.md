# 打包：`wanxiang://` 消息链接（决策 251）

| 平台 | 注册方式 | 运行时 |
|---|---|---|
| Linux | 桌面模式启动时写 `~/.local/share/applications/wanxiang-url-handler.desktop` 并 `xdg-mime default`；发行包可直接装 `linux/wanxiang-url-handler.desktop` | 链接作为位置参数传入 |
| Windows | 桌面模式启动时写 `HKCU\Software\Classes\wanxiang`；安装器可用 `windows/wanxiang-url.reg.template` | `"wanxiang.exe" "%1"` |
| macOS | `.app` 内 `Contents/Info.plist` 的 `CFBundleURLTypes`（`macos/Info.plist`） | `IActivatableLifetime` 的 OpenUri 激活 |

已在运行时，第二次启动（系统打开链接就是这样）经配置目录下的 `ui.sock` 把链接交给已开的窗口并唤起它，然后退出。
开发 / 测试环境设 `WANXIANG_NO_URL_SCHEME=1` 不改写本机协议关联。
