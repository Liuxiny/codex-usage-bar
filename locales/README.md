# Codex Usage Bar locales

Codex Usage Bar matches `document.documentElement.lang` from Codex.

Matching rules:

1. Any Chinese locale (`zh`, `zh-CN`, `zh-TW`, `zh-Hans`, `zh-Hant`, etc.) uses `zh.json`.
2. Other locales first try an exact file name, for example `pt-BR` -> `pt-br.json` / `pt-BR.json` on Windows.
3. If no exact file exists, the primary language is tried, for example `pt-PT` -> `pt.json`.
4. Missing languages and missing individual keys fall back to `en.json`.

Built-in files are `en.json` and `zh.json`. To maintain additional languages without editing the plugin core, place JSON files in:

`%LOCALAPPDATA%\CodexUsageBar\locales\`

User locale files override files with the same locale code in the managed engine. After editing or adding a locale file, use the tray **Refresh** command (or restart Codex Usage Bar) to reload the locale catalog.

Keep placeholders such as `{year}`, `{month}`, and `{day}` unchanged. Numeric usage/token values are not localized or reformatted by the locale system.
