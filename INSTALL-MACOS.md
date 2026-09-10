# Codex Usage Bar 0.7.6 · macOS Apple Silicon

这是 0.7.6 的原生 macOS 移植源码，目标为 M1/M2/M3/M4 及后续 Apple Silicon、macOS 13 或更高版本，不依赖 Rosetta、.NET、Node.js 或 CC Switch 运行时。界面交互和数据展示以 Windows 0.7.6 为准，底层使用 AppKit、Accessibility、JavaScriptCore、URLSession、SQLite 和 Keychain。

**交付状态：源码与构建/自检流程已提供；当前开发机为 Windows，尚未执行 macOS 编译、原生自检或实机验收。源码 ZIP 不是可直接启动的 `.app`。** 不应把语法检查通过视为 macOS 编译通过。

## 给测试者：先构建，再安装

如果拿到的是已构建的 `CodexUsageBar-v0.7.6-macOS-arm64.dmg`，直接双击挂载，将应用拖到旁边的 `Applications` 快捷方式，再从“应用程序”启动即可，无需安装开发工具。下面的构建步骤仅适用于源码包。构建脚本现在同时产出 DMG 和应用 ZIP；当前 Windows 开发机尚未生成真实 DMG。

需要 Apple Silicon Mac，以及 Swift 5.9+ 的 Xcode Command Line Tools（推荐当前稳定版 Xcode/Command Line Tools）。仅运行构建好的 `.app` 不需要这些开发工具。

1. 解压 `CodexUsageBar-v0.7.6-macOS-arm64-source.zip`。
2. 在终端执行 `xcode-select --install`，如果已安装可跳过。macOS 13 的较旧工具链可能低于 Swift 5.9，需要更新工具链或使用较新 macOS 构建。
3. 双击 `macos/Build-and-Test.command`。也可以在终端输入 `bash `（末尾留空格），将该文件拖进终端，再按回车。
4. 等待自动执行 Swift 单元测试、arm64 编译、应用组装、签名、原生自检和截图生成。
5. 成功后会打开 `dist/macos`。将 `Codex Usage Bar.app` 拖入“应用程序”后启动。测试失败时先回传 `dist/macos/build-test.log`，不要把半成品当作可用版本。

构建默认使用本机 ad-hoc 签名，不含 Developer ID 公证。从其他机器传来的未公证构建包可能被 Gatekeeper 拦截；优先在测试 Mac 本地构建。若系统提示无法验证开发者，可按 macOS“系统设置 → 隐私与安全性”提供的“仍要打开”流程处理。无需关闭系统安全设置。

第一次使用“跟随 Codex”时，在“系统设置 → 隐私与安全性 → 辅助功能”中允许 **Codex Usage Bar**。此权限只用于主窗口类型、位置和尺寸；用量仍从 App Server 或配置的 API 获取，不读取对话内容。未授权时可在菜单中切换“独立展示”使用。

## 官方数据源

- 启动 macOS 版 Codex。应用仅在检测到 Codex 时显示悬浮窗。
- 自动查找 `Codex.app/Contents/Resources/codex`、`/opt/homebrew/bin/codex`、`/usr/local/bin/codex`、`~/.local/bin/codex` 和 PATH。
- 若自动连接失败，在“数据源 → 仅官方”填写可执行的 Codex CLI 路径，并在终端完成该 CLI 的 `codex login`。不会读取或迁移 Windows 登录凭据。
- 使用 `initialize`、`account/rateLimits/read`、`account/rateLimits/updated` 和 `account/usage/read`。额度每 2 分钟兜底读取，Token 每 10 分钟读取；昨日 Token 按 UTC 日界线。
- CLI 版本未提供 `account/usage/read` 时，Token 显示 `—`，不编造数值；额度读取失败时隐藏官方悬浮窗。

## Mac 版 CC Switch

1. 在 CC Switch 中选择 Codex 服务商，启用并保存完整用量脚本。
2. 菜单“数据源”选择“自动跟随 CC Switch”或“仅 CC Switch”。
3. 默认读取 `~/.cc-switch/cc-switch.db` 和同目录 `settings.json`。
4. 支持 `~/Library/Application Support/com.ccswitch.desktop/app_paths.json` 中的 `app_config_dir_override`、环境变量 `CODEXBAR_CC_SWITCH_HOME`，也可浏览选择包含数据库的目录。
5. “测试查询”成功后保存。

数据库通过系统 SQLite 以 **只读** 模式打开；优先 `currentProviderCodex`，找不到对应记录时回退 `is_current=1`。不会写回 CC Switch、复制服务商到独立配置，也不会在明确选择“仅 CC Switch”但配置失败时偷偷切回官方。

第三方脚本沿用 Windows 的 `request + extractor(response)` 契约与 `{{baseUrl}}` / `{{apiKey}}` / `{{accessToken}}` / `{{userId}}` 替换。套餐模板直接打包 Windows 同一份 `installer/package-quota.js`，并依据原始 `reset_at` 补充重置时间。支持单行或 1–32 行结果；不支持依赖 CC Switch 原生后端的自动登录、签名及专用模板。

刷新分钟为 0 时只在初始、切换、手动刷新时请求；普通配置默认 2 分钟。每 15 秒重新运行缓存响应的 extractor，更新过期判断，不额外发 HTTP 请求；缓存超过 `max(6 分钟, 2 × 刷新间隔)` 后失效。切换服务商后丢弃旧结果。

## 独立配置、偏好与隐私

- 没有安装 CC Switch 时，选择“独立配置”，填写连接参数和脚本，也可填入套餐模板。
- 独立配置完整保存于 macOS Keychain：service `com.codexusagebar.macos`、account `standalone-provider`。不写明文 API Key 文件。
- 显示、位置、语言、数据源模式等非凭据偏好：`~/Library/Application Support/CodexUsageBar/settings.json`。
- 读取 `CODEX_HOME/config.toml`，默认 `~/.codex/config.toml`，沿用 Windows 的 `desktop.appearanceTheme`、Chrome theme 颜色和字体字段、`desktop.sansFontSize`。
- HTTP 使用系统证书校验，不跟随重定向，响应上限 1 MiB；支持用户配置的 HTTP 和 HTTPS 地址。
- JavaScript 在一次性子进程中运行，无文件、网络和进程宿主对象。父进程 5 秒终止超时脚本，子进程检查 256 MiB RSS 上限。此内存边界针对整个 macOS worker，不等同于 Windows Chakra 的 16 MiB JS 堆限制。
- 程序不写 API 请求/响应、凭据或对话日志。构建自检只使用合成数据，不查询你的服务商、不访问你的 Keychain、不修改真实 CC Switch 数据库。
- 登录启动使用 macOS `SMAppService`，在应用放入“应用程序”后通过菜单启用。卸载前可取消登录启动，再删除应用；手动删除上述偏好目录及 Keychain 项可清空个人设置。

## UI 对照验收

请测试下表，问题回传系统版本、芯片型号、Codex/CC Switch 版本，以及脱敏后的截图；不要发送真实 API Key、Access Token、数据库或完整配置文件。

| 场景 | 应与 Windows 0.7.6 一致的结果 |
|---|---|
| 单击收起条、顶部、移出鼠标 | 单击展开；展开顶部单击收起；鼠标离开收起；悬停不自动展开 |
| 官方额度 | 收起圆环，展开双列条形、额度、重置日期，底部昨日/累计 Token |
| 第三方套餐 | 额度与余额分栏；有效历史估算独占下一栏；无效估算连同分隔线隐藏 |
| 余额/估算为 0 | 保留显示；`$ 数值 USD` 单空格，美元符号与 USD 常规字重，余额数字强调色 |
| 字体/主题 | 收起 16px，展开使用 11–16px；配置变更后 5 秒内更新；中英文、深浅色无裁切 |
| 独立展示 | 置顶、拖动、重启记住位置；移除显示器后窗口回到可见区域 |
| 跟随 Codex | 主窗口顶栏居中；移动、缩放、最小化、还原、切换桌面和多屏时跟随 |
| Codex 文件选择对话框 | 不把文件对话框或 sheet 当成新吸附目标，不提升悬浮条遮挡弹窗 |
| 切换至其他应用 | Codex 失焦后可保留悬浮条，但其他应用应正常遮挡；不抢焦点 |
| 切换 CC Switch 服务商 | 5 秒内发现；切换期间旧请求不覆盖新服务商；失败显示第三方失败状态 |
| 网络/脚本异常 | 超时、失败、无效行、手动刷新均可恢复；菜单和 Codex 保持响应 |
| 菜单/登录启动 | 左右键菜单；显示开关、语言、立即刷新、登录启动和退出均工作 |

**需要重点实测的系统差异：** macOS 不允许直接使用 Win32 跨进程 owner 关系；跟随实现使用 AX 标准主窗口及公开 WindowServer 相对排序。跨应用遮挡、Spaces、全屏、多显示器坐标和辅助功能权限的实际表现，必须在 Mac 实测后才能确认等价。系统菜单保留 macOS 原生外观，菜单功能与 Windows 对齐。

## CI 与发布者

仓库包含 `.github/workflows/macos-arm64.yml`，在 Apple Silicon runner 上执行 `bash macos/build.sh`，上传 DMG、ZIP、SHA256 和中英文深浅色预览。不自动创建 Release，也不会上传个人配置。可通过该云端 macOS 环境完成构建，让测试者只安装 DMG；本轮未推送代码或运行远程 CI。

如配置 Developer ID，可用 `CODE_SIGN_IDENTITY` 进行正式签名；配置 `NOTARY_PROFILE` 时，构建脚本使用 `notarytool` 公证并 staple 后重新生成 ZIP。默认不依赖 Apple 开发者账户。
