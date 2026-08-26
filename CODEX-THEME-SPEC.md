# Codex Usage Bar 主题与视觉规范

## 1. 目的

本规范从 `codex 浅色 css相关.txt`、`codex 深色 css相关.txt` 提炼稳定视觉参数，供新承载界面使用。

运行时不解析这两份 CSS 快照，也不解析本 Markdown。实现应内置本规范中的默认 token：

1. `config.toml` 尚未读取、缺失或字段无效时，使用默认 token。
2. `config.toml` 读取成功后，只覆盖其中明确提供且有效的字段。
3. 配置未提供的字段继续保留默认值。

## 2. 主题选择

初始状态同时准备浅色、深色两组默认 token。

- 读取配置前：优先跟随系统明暗模式；无法判断时使用深色。
- `appearanceTheme = "light"`：使用浅色。
- `appearanceTheme = "dark"`：使用深色。
- `appearanceTheme = "system"`：跟随系统明暗模式。
- 值缺失或无效：保留读取配置前的选择。

配置覆盖应一次性生效，避免同一帧混用旧、新主题值。

## 3. 默认 token

### 3.1 颜色

| Token | 浅色默认值 | 深色默认值 | 用途 |
| --- | --- | --- | --- |
| `surface` | `#f6f6f6` | `#171717` | 状态栏详情层、弹出层背景 |
| `ink` | `#030303` | `#fefefe` | 主文字、图标 |
| `accent` | `#ff6363` | `#ff6363` | 进度、百分比、重点数据 |
| `secondaryInk` | `rgba(3,3,3,.73)` | `rgba(254,254,254,.699)` | 次要文字 |
| `tertiaryInk` | `rgba(3,3,3,.53)` | `rgba(254,254,254,.484)` | 弱提示、次级图标 |
| `hoverSurface` | `rgba(3,3,3,.064)` | `rgba(254,254,254,.075)` | 可交互项悬停、键盘焦点 |
| `softSurface` | `rgba(3,3,3,.056)` | `rgba(254,254,254,.05)` | 弱背景、轨道背景 |
| `separator` | `rgba(3,3,3,.137)` | `rgba(254,254,254,.15)` | 分隔线、环形进度轨道 |

`accent` 来自旧快照，只是无配置时的历史兜底值。读取到当前配置后必须优先使用配置值。

### 3.2 字体

| Token | 默认值 |
| --- | --- |
| `fontFamily` | `Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif` |
| `fontSize` | `13px` |
| `fontWeight` | `400` |
| `lineHeight` | `19.5px` |
| `letterSpacing` | `normal` |
| 重点数值字重 | `650` |
| 数值排版 | `font-variant-numeric: tabular-nums` |

悬浮窗不直接把菜单项的 `13px` 用作所有文字字号。收起态对应 Codex 菜单层的计算字号 `16px`；展开态读取 `desktop.sansFontSize`，并钳制在 Codex 支持的 `11–16px`。字体家族和字体面/字重在两种状态下保持一致。

### 3.3 弹出层

| Token | 默认值 |
| --- | --- |
| `padding` | `4px 0` |
| `border` | `0 solid transparent` |
| `borderRadius` | `7.5px` |
| `boxShadow` | `rgba(0,0,0,.42) 0 4px 12px 0` |
| `opacity` | `1` |

### 3.4 菜单项

| Token | 默认值 |
| --- | --- |
| `padding` | `5px 16px` |
| `margin` | `0 4px` |
| `borderRadius` | `7.5px` |
| 计算高度 | `29.5px` |
| 禁用态透明度 | `.60` |

`29.5px` 是 `19.5px` 行高加上下各 `5px` 内边距的结果；实现不必重复硬编码高度。

### 3.5 分隔线

| Token | 默认值 |
| --- | --- |
| `height` | `0.5px` |
| `margin` | `8px 0` |
| `padding` | `0` |
| `borderRadius` | `0` |

## 4. `config.toml` 覆盖规则

只接受类型正确、格式有效的值。单个字段无效时忽略该字段，不废弃整套配置。

| 配置路径 | 覆盖目标 | 规则 |
| --- | --- | --- |
| `desktop.appearanceTheme` | 当前主题 | 仅接受 `system`、`light`、`dark` |
| `desktop.appearanceLightChromeTheme.surface` | 浅色 `surface` | 有效 CSS 颜色才覆盖 |
| `desktop.appearanceDarkChromeTheme.surface` | 深色 `surface` | 有效 CSS 颜色才覆盖 |
| `desktop.appearanceLightChromeTheme.ink` | 浅色 `ink` | 覆盖后重算浅色派生颜色 |
| `desktop.appearanceDarkChromeTheme.ink` | 深色 `ink` | 覆盖后重算深色派生颜色 |
| `desktop.appearanceLightChromeTheme.accent` | 浅色 `accent` | 有效 CSS 颜色才覆盖 |
| `desktop.appearanceDarkChromeTheme.accent` | 深色 `accent` | 有效 CSS 颜色才覆盖 |
| `desktop.appearanceLightChromeTheme.fonts.ui` | 浅色 `fontFamily` | 配置字体在前，默认字体栈作回退 |
| `desktop.appearanceDarkChromeTheme.fonts.ui` | 深色 `fontFamily` | 配置字体在前，默认字体栈作回退 |
| `desktop.appearanceLightChromeTheme.fonts.uiFace` | 浅色具体字体面/字重 | 优先匹配 `postscriptName`、`fullName`，缺失时使用字体族默认面 |
| `desktop.appearanceDarkChromeTheme.fonts.uiFace` | 深色具体字体面/字重 | 优先匹配 `postscriptName`、`fullName`，缺失时使用字体族默认面 |
| `desktop.sansFontSize` | 展开态 UI 字号 | 有效数值钳制为 `11–16px`；收起态保持 `16px` |

`ink` 更新后的派生颜色：

- 浅色：`secondaryInk` 73%、`tertiaryInk` 53%、`hoverSurface` 6.4%、`softSurface` 5.6%、`separator` 13.7%。
- 深色：`secondaryInk` 69.9%、`tertiaryInk` 48.4%、`hoverSurface` 7.5%、`softSurface` 5%、`separator` 15%。

百分比表示配置 `ink` 的 alpha。这样自定义文字颜色变化时，弱文字、悬停背景和分隔线仍保持同一色相。

## 5. 已识别但不直接覆盖的配置

- `contrast`：缺少官方换算规则。保留为配置元数据，不凭经验修改颜色。
- `opaqueWindows`：控制窗口材质，不改变组件自身颜色 token。
- `fonts.code`：仅用于代码内容；Usage Bar 无代码内容时忽略。
- `semanticColors.diffAdded`、`diffRemoved`、`skill`：与用量展示无直接语义，忽略。
- `appearanceLightCodeThemeId`、`appearanceDarkCodeThemeId`：编辑器代码主题，不用于本组件。

## 6. 合并模型

逻辑结果等价于：

```text
resolvedLight = shallowCopy(defaultLight)
resolvedDark  = shallowCopy(defaultDark)

读取 config.toml
  选择 appearanceTheme
  覆盖对应主题中存在且有效的 surface、ink、accent、fonts.ui、fonts.uiFace
  将 sansFontSize 应用于展开态并限制在 11–16px；收起态使用 16px
  ink 被覆盖时重算该主题派生颜色
  其余 token 保留默认值

一次性应用当前 resolved theme
```

不需要通用深度合并器、主题继承系统或 Markdown 解析器。

## 7. 禁止作为运行时依据的数据

以下内容与旧 renderer DOM 强绑定，不进入新承载方案：

- `application-menu-trigger-help-menu`、`application-menu-content` 等 ID。
- Tailwind 类名、Radix 属性、元素层级、`outerHTML`。
- 快照中的菜单宽度、高度、最大宽度和 `z-index`。
- VS Code 变量全集和样式表全文。

宽高由内容、屏幕可用区域和新承载窗口决定。`z-index` 对独立原生窗口没有等价意义。

## 8. 验收条件

- `config.toml` 不存在、不可读或解析失败时，浅色和深色界面仍可完整显示。
- 配置仅含 `accent` 时，只改变强调色。
- 配置仅含 `surface` 时，只改变背景色。
- 配置仅含 `ink` 时，主文字及其派生颜色同步更新。
- 配置缺少浅色或深色主题块时，缺少的一侧继续使用默认 token。
- 无效单字段不会影响其他有效字段。
- 运行时不依赖旧 Codex DOM、CSS 类名或 `9335` CDP。

## 9. 来源与限制

默认值来自两份 Codex 菜单 computed-style 快照。它们描述当时版本的视觉结果，不代表当前 Codex 的稳定公开 API。

本规范用于视觉兜底和配置映射；当前 `config.toml` 始终拥有更高优先级。
