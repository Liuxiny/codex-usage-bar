# Codex Usage Bar v0.6.0

0.6.0 用原生 Windows companion 替换 CDP renderer 注入。

## 主要变化

- 删除 `9335`、CDP、renderer DOM 注入和 Codex 自动重启。
- 使用官方 Codex App Server stdio 接口读取额度和 Token。
- 新增独立展示、吸附 Codex 两种悬浮窗模式。
- 新增托盘左键菜单。
- 托盘显示连接状态、展示方式、显示偏好、立即刷新和退出。
- 接口未连接或无有效额度数据时自动隐藏悬浮窗。
- 使用事件更新、2 分钟额度兜底、10 分钟 Token 读取和指数退避重连。
- 从 `config.toml` 读取主题；缺失字段使用内置设计规范。
- 悬浮窗只展示 5 小时和周额度，收起为圆环、展开为双列进度条；Token 改读昨日汇总。
- 悬浮窗改为单击展开/收起，鼠标离开后自动收起，不再悬停自动展开。
- 吸附模式在 Codex 工具栏水平居中，并以原生 owner 层级保持在 Codex 上方。
- Codex 失焦后吸附窗继续存在，但允许其他应用正常遮挡。
- 新增“跟随 Codex / 中文 / English”语言菜单；中文族语言默认中文，其余默认英语。
- 0.4.x 安装可原位升级，安装器清理旧 injector。

## 验证

- .NET Framework 编译通过。
- 内置主题、额度解析、Token 格式和退避策略自检通过。
- Codex App Server 真实连接通过：initialize、额度读取、Token 读取。
- companion 内置退出信号验证通过，退出码为 `0`。
