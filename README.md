# Codex Usage Bar

**macOS Apple Silicon 移植：** 0.7.6 新增 [原生 macOS 源码与安装/测试说明](./INSTALL-MACOS.md)，位于 `macos/`。已通过云端 Mac 编译、原生自检和 DMG 校验；真实 Codex/CC Switch 交互仍需实机验收。下文为现有 Windows 版说明。

> Windows 版 Codex 用量伴随工具。支持官方 App Server、CC Switch，以及其他第三方 API 的自定义用量查询；未安装 CC Switch 也可独立配置。界面使用独立原生窗口。

![Version](https://img.shields.io/badge/version-0.7.6-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)
![Architecture](https://img.shields.io/badge/arch-x64-lightgrey)

> [!IMPORTANT]
> 本项目是第三方工具，不属于 OpenAI 官方产品。不修改 Codex 的 `WindowsApps` 文件、`app.asar`、签名或进程内存。

## 功能

- 官方数据源显示 5 小时与周额度；收起态使用圆环，展开态使用双列进度条，并按 Codex 官方 UTC 日界线显示昨日 Token 和累计 Token。
- 第三方数据源执行 CC Switch 格式的 `request + extractor` JavaScript；收缩时显示圆环、额度百分比、官方格式的重置时间，以及 `$ 金额 单位`。
- 第三方展开时显示条形额度与重置时间、独立余额栏；有效的余额折合周额度另占下一栏，并标注历史估算。估算无效时连同分隔线隐藏，数值 0 仍显示。第三方模式不混入官方 Token 汇总。
- 收缩提示文字整体居中；收缩文字为 16px，展开文字跟随外观 UI 字号。数字与 %、K/M/B 加粗，$、USD 和普通文字不加粗；余额数值使用强调色。
- 悬浮窗跟随显示器 DPI 缩放字体、圆环、间距与高度；展开百分比在空间允许时与下一行时分居中对齐，否则保持标签右侧间距，余额栏更紧凑。
- 托盘“数据源”支持自动跟随 CC Switch、仅官方、仅 CC Switch、独立配置；附带套餐查询模板和测试查询。
- 两种悬浮窗模式：
  - **独立展示**：桌面置顶小窗，可拖动并记住位置。
  - **跟随 Codex**：仅吸附 Codex 主窗口，排除文件选择对话框和工具弹窗；在 Codex 工具栏水平居中并保持在 Codex 上一层；Codex 失焦后继续显示，但可被其他应用正常遮挡。Codex 最小化或窗口丢失时隐藏。
- 托盘图标左键、右键均可打开菜单。设置窗口、托盘与子菜单跟随悬浮窗的明暗主题、字体与强调色；设置按连接参数、脚本和结果分区，CC Switch 模式不展示无关凭据字段。
- 托盘菜单显示当前连接状态，并提供：
  - 显示或隐藏悬浮窗。
  - 独立展示或跟随 Codex。
  - 语言：跟随系统、中文或 English。
  - 立即刷新。
  - 开启或关闭开机启动。
  - 退出。
- 官方连接失败时自动隐藏悬浮窗；第三方查询失败保留来源与失败状态，不回退成官方额度。
- 启动先使用内置 Light/Dark 设计 token；读取 `config.toml` 后覆盖主题、表面色、文字色、强调色、UI 字体家族、具体字体面/字重与 UI 字号。
- 收起态使用 Codex 菜单栏的 16px 字号；展开态跟随 `desktop.sansFontSize`，并限制在 Codex 支持的 11–16px。两种状态均使用配置的 UI 字体样式。
- 默认跟随 Windows UI 语言；中文族语言使用中文，其他语言使用英语。程序只内置中文和英语，其他翻译需自行维护。

## 架构

```text
CodexUsageBar.exe
  ├─ 系统托盘与用户设置
  ├─ 用量数据源选择
  │    ├─ CC Switch settings.json + 只读 cc-switch.db
  │    └─ 独立配置（Windows 当前用户加密）
  ├─ 第三方 request → HTTP → extractor → 多行用量
  ├─ Codex 进程、前台窗口和位置事件监听
  ├─ codex app-server --listen stdio://
  │    ├─ account/rateLimits/read
  │    ├─ account/rateLimits/updated
  │    └─ account/usage/read
  └─ WinForms 原生悬浮窗
```

0.7.6 不使用 `9335`、CDP、DOM selector、`renderer-inject.js`、UI Automation 取数或高频截图。

## 连接与刷新

- 默认自动跟随 CC Switch 当前 Codex 服务商。未找到 CC Switch 数据库或未选中服务商时使用官方；明确选择“仅 CC Switch”时不会回退。
- 第三方模式不启动 App Server。每 5 秒检查配置及选中服务商；修改脚本、凭据或服务商后重新查询，丢弃切换期间返回的旧结果。
- 第三方查询遵循 `autoQueryInterval`（分钟）；未配置时每 2 分钟，0 为仅初始/切换/手动刷新。请求超时 2–30 秒；失败等待下一次计划刷新或用户手动刷新。
- 每 15 秒重新执行缓存响应的 extractor，更新 `Date.now()` 驱动的过期判断与倒计时，不额外请求。缓存超过 `max(6 分钟, 2 × 刷新间隔)` 后失效。
- 以下 App Server 连接与 Token 刷新规则仅适用于官方数据源。
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

1. 从 Releases 下载 `CodexUsageBar-Setup-v0.7.6.exe`。
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
- `usage-source.json`：数据源模式、可选 CC Switch 数据目录，以及由 Windows DPAPI 当前用户加密的独立查询参数和脚本。
- `companion.log`：最多约 1 MB 的诊断日志。
- `companion-state.json`：当前进程信息，正常退出时删除。

CC Switch 参数只读、不复制至 CodexBar 设置；独立模式凭据加密保存。不会保存对话内容或额度历史，诊断日志不记录请求地址、请求头、凭据或响应正文。

## 数据源

### 已安装 CC Switch

1. 在 CC Switch 选中 Codex 服务商，启用并保存用量查询脚本。
2. CodexBar 托盘打开“数据源”，选择“自动跟随”或“仅 CC Switch”。
3. 默认自动查找并填入数据目录；也可点击“自动查找”或“浏览”。请选择包含 `cc-switch.db` 的文件夹，不是程序安装目录。
4. 点击“测试查询”，确认后保存。

默认读取 `%USERPROFILE%\.cc-switch`；支持 CC Switch 的 `app_paths.json` 目录覆盖和旧版 HOME 目录回退。自定义目录可直接在设置窗口填写，也可设置 `CODEXBAR_CC_SWITCH_HOME`。按 `currentProviderCodex` 优先、数据库 `is_current` 回退的规则选择服务商；不会修改 CC Switch 数据库。

### 未安装 CC Switch

1. 打开“数据源”，选择“独立配置”。
2. 填写显示名称、Base URL、API Key / Access Token / User ID（按接口需要）、刷新分钟和请求超时。
3. 粘贴 CC Switch 格式的完整 JavaScript，或点击“填入套餐模板”。模板查询 `/api/user/self/package-quota`，使用站点 Access Token。
4. 点击“测试查询”，确认返回内容后“保存”。

Base URL 应为用量接口所在站点，可能与 CPA 模型转发地址不同。API Key 与 Access Token 是独立参数。独立模式无需安装或运行 CC Switch；悬浮窗仍在检测到 Codex 后显示。

### 兼容范围

- 支持 `{{baseUrl}}`、`{{apiKey}}`、`{{accessToken}}`、`{{userId}}` 的 CC Switch 原样变量替换。脚本需返回 `request` 对象和同步 `extractor(response)` 函数；不是 JSON 配置。
- 支持字符串 HTTP body、普通请求头及 User-Agent/Accept/Content-Type；不跟随 HTTP 重定向，遇到重定向请填写最终查询地址。
- 支持单行或数组的 `planName/extra/isValid/invalidMessage/total/used/remaining/unit`；最多解析 32 行；悬浮窗按额度、余额、估算分区展示，测试查询可查看全部原始行。
- 内置套餐模板保留 PRO 仅周额度、6 分钟新鲜度、余额折合周额度规则；估算不是额外额度。已知套餐响应的原始 `reset_at` 用于悬浮窗官方格式的本地时间显示，测试查询仍保留脚本原来的 UTC+8 说明。
- 自定义脚本可为额度行补充 `kind: "quota"`、`windowSeconds`（18000/604800 等）、`resetAt`（带时区 ISO 字符串或 Unix 秒）；余额/估算可用 `kind: "balance"`/`"estimate"`。缺失结构化日期时显示重置时间未知，不反推格式化说明文字。
- HTTPS 查询明确启用 TLS 1.2，避免独立 .NET Framework EXE 默认旧协议造成 SecureChannelFailure；保留系统证书验证。
- 使用 Windows 自带 Chakra JSRT，支持本项目示例中的 `Array.find`、`Date` 等语法。与上游 QuickJS 并非所有较新 JS 语法都等价；不支持依赖 CC Switch 原生后端的专用查询模板、自动登录或签名服务。
- JS 无文件/网络/进程宿主对象，16 MiB 运行时内存上限和 5 秒执行中断；HTTP 响应上限约 1 MiB 文本。脚本主动返回的说明内容会展示在界面。
- 跟随 CC Switch 选中的服务商，不推断代理逐请求的自动故障转移落点。

源代码参考与许可证见 [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)。

## 已知限制

- 官方插件 UI 没有 Codex 顶栏常驻插槽，因此“吸附”仍是进程外窗口，不是真正插入 Codex DOM。
- 吸附模式使用原生 owner 层级：Codex 前台时悬浮窗高一层；Codex 失焦时不隐藏，也不主动提升层级，因此其他应用可以正常遮挡它。
- App Server 需要可启动且已登录的 Codex CLI；认证失败时托盘会显示连接失败，悬浮窗保持隐藏。

## License

[MIT](./LICENSE)
