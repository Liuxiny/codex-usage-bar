# Codex Usage Bar v0.6.2

## 变化

- 悬浮窗现在读取 Codex `config.toml` 中的 UI 字体家族、具体字体面/字重及 `desktop.sansFontSize`。
- 修复 Windows 当前用户字体无法正确枚举、字体名称带引号以及字体面匹配错误的问题。
- 收起态字号与 Codex 菜单层一致为 16px；展开态跟随 UI 字号，并限制在 Codex 支持的 11–16px。
- 收起态圆环外径不变，线宽向内增加 2px；零额度仍保留最小颜色弧段。
- 托盘菜单明确跟随 Windows 系统菜单字体，不受 Codex 外观字体设置影响。

## 验证

- .NET Framework 编译及内置自测通过。
- 字体配置解析、11px/16px 边界、收起/展开宽度、圆环线宽和窗口 owner 层级均由自测覆盖。
- 0.6.1 可原位覆盖升级。
