# Codex Usage Bar v0.4.16 — Windows installation model

v0.4.16 keeps the renderer/injector and native tray model, and adds document-language-driven localization with built-in Chinese and English catalogs plus user-maintained locale files.

## Safety change from v0.4.13

v0.4.13 could repeatedly restart Codex if an automatic CDP attach failed after the restart. v0.4.16 is fail-closed:

- the watcher may request at most one automatic CDP restart for a Codex session;
- if that attach fails while Codex is still running, `watcher-fuse.json` is written;
- no further automatic restart is attempted until Codex has fully exited;
- the fuse survives watcher-host restarts, so restarting the watcher itself cannot recreate a reboot loop;
- a managed attach will never launch Codex if the user already closed the triggering Codex session.

The legacy PowerShell `-Watch` path remains only for compatibility/diagnostics and has the same fuse behavior. The Setup installer does not use it for persistent startup.

## Startup model

The installer deploys:

```text
%LOCALAPPDATA%\Programs\CodexUsageBar\
  CodexUsageBar.WatcherHost.exe
  setup-bootstrap.ps1
  payload\...

%LOCALAPPDATA%\CodexUsageBar\
  engine\...
  install.json
  watcher-host.json
  watcher-host.log
  watcher-fuse.json   # only present after a failed automatic attach
```

A normal per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value points directly at `CodexUsageBar.WatcherHost.exe`. There is no Startup-folder shortcut whose target is hidden PowerShell.

The native watcher listens for `ChatGPT.exe` process-start events, verifies that the executable path belongs to the registered `OpenAI.Codex` WindowsApps package pattern, and only then starts a one-shot Windows PowerShell attach worker. A 3-second process check is retained as a low-frequency recovery path and to detect when Codex has fully exited so the restart fuse can be cleared.

## Tray controls

`CodexUsageBar.WatcherHost.exe` stays visible in the Windows notification area while the integration is active. Right-click the icon to use:

- **刷新** — writes a lightweight refresh request consumed by the existing injector. It immediately re-reads rate limits and token usage without restarting Codex or tearing down the Usage Bar. If the injector is missing, the tray performs attach-only recovery and still never restarts Codex.
- **退出** — removes the live Usage Bar when CDP is available, stops the injector, stops the watcher host, and removes the tray icon. The installed HKCU Run entry is intentionally kept, so the integration starts again at the next Windows sign-in.

Double-clicking the tray icon performs the same action as **刷新**.

## Application icon

`assets/codex-usage-bar-logo.png` is converted without artwork changes to `assets/codex-usage-bar.ico`, a multi-resolution Windows icon containing 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixel entries. The `.ico` is embedded into `CodexUsageBar.WatcherHost.exe` and is also used by Setup.exe, the Start Menu shortcut, and the uninstall entry.

## PowerShell 5.1 compatibility

The one-shot attach worker is intentionally Windows PowerShell 5.1 compatible. v0.4.16 keeps the `.NET ProcessStartInfo.ArgumentList` compatibility fix from the CDP probe and no longer relies on the unused `Invoke-RestMethod -NoProxy` helper.

## Build

Install Inno Setup 6, then run from the project root:

```powershell
pwsh.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\installer\build-release.ps1 `
  -InnoCompiler 'D:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

The build script also uses the .NET Framework C# compiler already included with Windows to produce `CodexUsageBar.WatcherHost.exe`, then embeds it in the Setup package.

Output:

```text
dist\CodexUsageBar-Setup-v0.4.16.exe
```

## Install / update

Close Codex before installing. Running a newer Setup.exe performs an in-place managed-engine update. The installer does not modify WindowsApps, `app.asar`, or the Codex application signature.

## Uninstall

Use Windows **Settings → Apps → Installed apps → Codex Usage Bar → Uninstall**. The uninstall bootstrap stops the native watcher first, removes any live Usage Bar injection when CDP is available, stops the injector, clears the managed state directory, and then lets Inno Setup remove the installed program files and HKCU Run entry.

## Language matching

Renderer text follows `document.documentElement.lang` from Codex. All Chinese variants use the built-in `zh.json`. Other languages first try the exact locale, then the primary language, and finally English. Numeric percentages and token values are not localized or reformatted.

Built-in catalogs are `engine\locales\en.json` and `engine\locales\zh.json`. Additional user-maintained JSON catalogs belong in `%LOCALAPPDATA%\CodexUsageBar\locales\`; they survive normal upgrades. Tray **Refresh** reloads these locale files as well as usage data.
