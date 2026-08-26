# Codex Usage Bar

> Windows 版 Codex 用量伴随工具。数据使用 Codex App Server，界面使用独立原生窗口，不再注入 renderer。

![Version](https://img.shields.io/badge/version-0.6.2-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)
![Architecture](https://img.shields.io/badge/arch-x64-lightgrey)

> [!IMPORTANT]
> 本项目是第三方工具，不属于 OpenAI 官方产品。不修改 Codex 的 `WindowsApps` 文件、`app.asar`、签名或进程内存。

## 功能

- 只显示 5 小时与周额度；收起态使用圆环，展开态使用双列进度条，并显示昨日 Token 和累计 Token。
- 两种悬浮窗模式：
  - **独立展示**：桌面置顶小窗，可拖动并记住位置。
  - **跟随 Codex**：在 Codex 工具栏水平居中并保持在 Codex 上一层；Codex 失焦后继续显示，但可被其他应用正常遮挡。Codex 最小化或窗口丢失时隐藏。
- 托盘图标左键、右键均可打开菜单。
- 托盘菜单显示当前连接状态，并提供：
  - 显示或隐藏悬浮窗。
  - 独立展示或跟随 Codex。
  - 语言：跟随系统、中文或 English。
  - 立即刷新。
  - 开启或关闭开机启动。
  - 退出。
- App Server 未连接或额度数据无效时自动隐藏悬浮窗；连接恢复后按用户显示偏好自动恢复。
- 启动先使用内置 Light/Dark 设计 token；读取 `config.toml` 后覆盖主题、表面色、文字色、强调色、UI 字体家族、具体字体面/字重与 UI 字号。
- 收起态使用 Codex 菜单栏的 16px 字号；展开态跟随 `desktop.sansFontSize`，并限制在 Codex 支持的 11–16px。两种状态均使用配置的 UI 字体样式。
- 默认跟随 Windows UI 语言；中文族语言使用中文，其他语言使用英语。程序只内置中文和英语，其他翻译需自行维护。

## 架构

```text
CodexUsageBar.exe
  ├─ 系统托盘与用户设置
  ├─ Codex 进程、前台窗口和位置事件监听
  ├─ codex app-server --listen stdio://
  │    ├─ account/rateLimits/read
  │    ├─ account/rateLimits/updated
  │    └─ account/usage/read
  └─ WinForms 原生悬浮窗
```

0.6.2 不使用 `9335`、CDP、DOM selector、`renderer-inject.js`、UI Automation 取数或高频截图。

## 连接与刷新

- 只在检测到官方 Codex Windows 进程时启动 App Server。
- 启动后必须成功完成 `initialize` 和 `account/rateLimits/read`，托盘才显示“连接成功”。
- 已连接：额度更新事件驱动；每 2 分钟做一次额度兜底读取；每 10 分钟读取一次 Token 汇总。
- 未连接：按 `1s、2s、5s、15s、30s` 退避重试，最长每 30 秒一次。
- Codex 未运行：每 3 秒做一次轻量进程检查，不启动 App Server。
- 窗口位置主要由 Win32 事件驱动；5 秒检查只用于漏事件恢复。
- 悬浮窗宽度随语言和重置日期动态测量；收起、展开宽度保持一致。收起态比当前 Codex 工具栏约低 2px，并按工具栏中轴垂直居中；不显示鼠标提示框。
- 单击悬浮窗展开，单击展开态顶部区域收起；悬停不会自动展开，鼠标离开后自动收起。
- 托盘“立即刷新”会合并重复请求，不按鼠标次数并发访问接口。

## 主题

默认值和配置映射见 [CODEX-THEME-SPEC.md](./CODEX-THEME-SPEC.md)。读取顺序：

1. 内置规范 token。
2. `%USERPROFILE%\.codex\config.toml`，或 `CODEX_HOME\config.toml`。
3. 配置中存在且有效的字段覆盖默认值。

`config.toml` 变化时自动重新读取，无需重启 companion。

## 安装

1. 从 Releases 下载 `CodexUsageBar-Setup-v0.6.2.exe`。
2. 运行安装器。
3. 安装完成后托盘出现 Codex Usage Bar 图标。
4. 左键或右键托盘图标选择展示方式。

安装器可覆盖升级 0.4.x；升级时会停止并移除旧 CDP injector。默认注册当前用户登录启动，可在安装向导中取消。

## 从源码构建

只构建并运行自检后的 companion：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\build-release.ps1 -SkipInstaller
```

输出：`dist\CodexUsageBar.exe`。

构建 Setup.exe 还需要 Inno Setup 6：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\build-release.ps1
```

## 本地数据

保存在 `%LOCALAPPDATA%\CodexUsageBar`：

- `settings.json`：显示偏好、展示方式、独立窗口位置。
- `companion.log`：最多约 1 MB 的诊断日志。
- `companion-state.json`：当前进程信息，正常退出时删除。

不保存登录令牌、对话内容或额度历史。

## 已知限制

- 官方插件 UI 没有 Codex 顶栏常驻插槽，因此“吸附”仍是进程外窗口，不是真正插入 Codex DOM。
- 吸附模式使用原生 owner 层级：Codex 前台时悬浮窗高一层；Codex 失焦时不隐藏，也不主动提升层级，因此其他应用可以正常遮挡它。
- App Server 需要可启动且已登录的 Codex CLI；认证失败时托盘会显示连接失败，悬浮窗保持隐藏。

## License

[MIT](./LICENSE)
