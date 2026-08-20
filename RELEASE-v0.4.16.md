# Codex Usage Bar v0.4.16

首个面向公开发布整理的 Windows 版本。

## Highlights

- Codex 顶部原生风格 Usage Bar。
- 剩余额度圆环 / 横向进度条与原生强调色同步。
- 额度、重置时间、今日 Token、累计 Token 展示。
- 自动额度 / Token 刷新，支持托盘手动刷新。
- Windows 原生托盘 WatcherHost：刷新、退出。
- 标准 Inno Setup 安装、覆盖升级和卸载。
- 不修改 WindowsApps、`app.asar` 或 Codex 应用签名。
- CDP 仅接受本机 loopback，自动 attach 失败后带 restart fuse，避免重启循环。
- 内置中文 / English，根据 Codex `document.documentElement.lang` 自动匹配。
- 支持 `%LOCALAPPDATA%\CodexUsageBar\locales\` 中的用户自定义语言文件。

## Install

1. 完全退出 Codex。
2. 下载并运行 `CodexUsageBar-Setup-v0.4.16.exe`。
3. 安装后继续使用原来的 Codex 图标启动。

首次接管未启用 CDP 的 Codex 会话时，Codex 可能自动重启一次。

## SHA256
```
BBC12DEAF3AF39894FC2A8F5AB08579F4C5B5E6F63192487206EB5160D70C320
```
