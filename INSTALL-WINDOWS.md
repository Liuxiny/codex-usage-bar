# Codex Usage Bar 0.6.0 Windows 安装说明

## 安装

1. 完全退出旧版 Codex Usage Bar；安装器也会尝试自动停止它。
2. 运行 `CodexUsageBar-Setup-v0.6.0.exe`。
3. 保留“登录 Windows 时启动”可让托盘常驻；不需要时可取消。
4. 安装结束后启动 Codex。

companion 检测到 Codex 后启动 App Server。连接成功且返回有效额度数据时，悬浮窗才会出现。

## 托盘操作

左键或右键托盘图标均可打开菜单：

- `Codex 连接：成功/失败/正在连接/未检测到 Codex`：当前连接状态。
- `显示悬浮窗`：保留或取消显示偏好。
- `展示方式 > 独立展示`：桌面置顶，可拖动。
- `展示方式 > 吸附 Codex 窗口`：贴附在 Codex 工具栏；Codex 失焦后保留，但可被其他应用遮挡。
- `语言`：可选择跟随 Codex、中文或 English。
- `立即刷新`：立即读取额度和 Token。
- `退出`：退出 companion，不卸载、不删除启动项。

连接失败时悬浮窗强制隐藏，但“显示悬浮窗”偏好不变；连接恢复后自动重新显示。

## 升级

0.6.0 使用与 0.4.x 相同的安装 AppId，可直接覆盖安装。升级过程会：

1. 停止旧 watcher、injector 和新 companion。
2. 删除旧 CDP engine 与 `9335` 状态文件。
3. 保留 `settings.json`。
4. 安装并启动 `CodexUsageBar.exe`。

不修改或重启 Codex。

## 卸载

在 Windows“设置 > 应用 > 已安装的应用”中卸载 Codex Usage Bar。卸载会停止 companion，删除程序文件、启动项、设置和日志；不修改 Codex。

## 排错

托盘显示连接失败时：

1. 确认 Codex Windows 应用正在运行。
2. 确认 Codex CLI 已登录同一账户。
3. 点击托盘“立即刷新”。
4. 查看 `%LOCALAPPDATA%\CodexUsageBar\companion.log`。

悬浮窗没有出现时，同时检查：

- 托盘“显示悬浮窗”是否勾选。
- 托盘连接状态是否成功。
- 吸附模式下 Codex 是否仍有可用窗口且未最小化。
