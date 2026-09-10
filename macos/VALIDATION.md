# 0.7.6 macOS 验证记录

日期：2026-09-10。开发环境：Windows x64。没有可用的 macOS SDK、Swift 编译器或 Mac 测试机。

## 已执行

- 14 个 Swift 文件经 tree-sitter-swift 解析，无语法错误节点。
- `build.sh` 和 `Build-and-Test.command` 经 tree-sitter-bash 解析，无语法错误节点。
- 两份 plist 和 GitHub Actions YAML 解析通过，目标版本 0.7.6、macOS 13+、arm64。
- Windows/macOS 共用套餐脚本的 Node/V8 契约测试通过：PRO 单周、非 PRO 双窗口、零余额、零估算、过期、已重置、缺失估算、接口失败。
- 对照本地 CC Switch 源码确认默认目录、目录覆盖键、数据库字段和当前服务商优先级。
- 手动对照 Windows 0.7.6 绘制代码迁移收缩/展开、日期格式、字号、数字字重、余额、估算和列宽分配规则。
- 源码交付 ZIP 校验 CRC、必需资源、脚本 LF 换行及 Unix 可执行位；排除用户配置、`.git`、Windows 二进制和临时依赖。

## 已实现、尚未执行的 Mac 检查

`bash macos/build.sh` 会依次运行：

1. `swift test`：现代额度桶优先、空值、百分比边界、UTC 昨日 Token、K/M/B 进位、零值/过期/套餐元数据、TOML 当前服务商隔离、与 Windows 一致的日期显示。
2. `swift build`：macOS arm64 原生编译。
3. plist、arm64 架构、应用签名检查。
4. 打包应用的 `--self-test`：只读 SQLite 选中/回退、不写数据库；模拟 App Server 握手、错误与停止时取消请求；本地 HTTP 请求头、重定向和响应大小限制；JavaScriptCore 超时与恢复；真实套餐模板提取；生成 8 张中英文深浅色收起/展开预览。
5. 全部成功后生成应用 ZIP、压缩只读 DMG（应用与 Applications 快捷方式）和各自 SHA256；通过 `hdiutil verify` 检查磁盘映像。配置正式签名/公证时也签名和公证 DMG。

DMG 流程已增加 Bash 语法检查；实际 `hdiutil` 创建、挂载安装及公证尚未在 Mac 执行。

**未执行 Swift 类型检查、链接、原生单元测试、应用运行、签名验证、UI 截图目视验收或 GitHub Actions。语法解析不能验证 Apple API 调用或替代编译。**

实机验收请按根目录 `INSTALL-MACOS.md` 的表格操作。重点核对 AX 主窗口识别、文件选择弹窗、跨应用层级、多屏/Spaces/全屏、Keychain、登录启动和真实 Mac CC Switch 查询。

## 构建约束

- 当前交付物为 source ZIP，不是预编译 `.app`；朋友首次使用需要先运行构建。
- 默认 ad-hoc 签名，无 Developer ID 公证。正式签名/公证入口已经预留，但未执行。
- JavaScriptCore 和 Chakra 引擎不同，系统菜单使用 macOS 原生控件；这些差异不能通过静态检查保证行为完全等价。
- 本轮未改动 Windows 功能源码、版本号或已有安装包。
