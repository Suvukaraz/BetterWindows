# BetterWindows

**BetterWindows** is a small Windows 11 tool that removes ads, unnecessary internet requests and bloat, and makes Windows feel snappier. Every change is shown with its current state and **can be undone**.

The app is available in **English** (default) and **German**. You can switch the language under *Settings*.

---

## Features

| Page | What it does |
|---|---|
| **Optimizations** | 31 switches in five groups: ads & suggestions, search (no Bing), privacy & telemetry, Microsoft Edge, responsiveness. Each switch shows the *real* state of your system. Turning one off restores the exact previous value. “Apply all recommended” creates a system restore point first. |
| **Bloat Apps** | Removes preinstalled Microsoft apps (Netflix, Clipchamp, News, Weather, Teams, Outlook (new), OneDrive, …) for all users and from the Windows image, so they do not come back after updates. Xbox/Game Pass, Phone Link, Photos, Paint, Calculator, Notepad, Store etc. are deliberately **not** on the list. |
| **Programs** | Thorough uninstaller based on [Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller). First the program’s own uninstaller runs, then BetterWindows searches for leftover folders, registry keys, shortcuts, services and startup entries. Only leftovers that definitely belong to the program are preselected. Everything is **backed up before deletion**. Three views: <br>• **Simple** – installed programs like Windows “Programs and Features”, without runtimes, drivers and system components, with an *Uninstall* button per row <br>• **Advanced** – also Store apps, runtimes and orphaned entries, multi-select <br>• **Expert** – everything, including system components, updates and “find leftovers only” |
| **Context menu** | Clean up the right-click menu: <br>• **New menu (Windows 11)** – entries added by app packages (logic from [Windows 11 Context Menu Manager](https://github.com/branhill/windows-11-context-menu-manager)), plus a switch for the classic menu <br>• **Classic menu** – commands and shell extensions for files, folders, folder background, desktop, drives, This PC, Recycle Bin, libraries, shortcuts, .exe, images, videos, music, text <br>• **“Send to”**, **“New”** and **“Open with”** <br>Hiding is stored for your user account where possible (no admin rights needed) and can always be undone. |
| **Startup apps** | Lists all programs that start at sign-in (registry, startup folders, Store apps) with icon and publisher. Turning one off works exactly like Task Manager. Removing an entry backs it up first. |
| **Backups** | Shows every place where BetterWindows stores backups (program leftovers, context menu, startup entries, Group Policy, log) with size and date. Open each folder or empty it – emptying moves the files to the Recycle Bin. The undo data for optimizations (`backup.json`) is shown but protected. Also opens the Windows restore point settings. |
| **Settings** | Language (English/German), data folder (backups, log, settings), version and licenses. |

## Safety

- **Everything is reversible.** Original registry values are stored in `%LOCALAPPDATA%\BetterWindows\backup.json` before they are changed.
- **Backups before deleting.** Removed leftovers, context menu keys and startup entries are saved as `.reg` files or moved to a backup folder under `%LOCALAPPDATA%\BetterWindows`.
- **System restore points** are offered before applying recommended optimizations and before uninstalling programs.
- **Limited mode:** Without administrator rights, the app only changes settings of your own user account. A button lets you restart it as administrator.

## Requirements

- Windows 11 (Windows 10 21H2+ should work but is not the focus)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64)
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) – only for the uninstaller’s Steam and Store helpers
- Administrator rights for system-wide changes (recommended)

## Usage

BetterWindows is **portable** – no installation needed. Copy the whole `BetterWindows-App` folder anywhere (e.g. a USB stick):

```
BetterWindows-App\
├─ BetterWindows.exe          the app (single file)
└─ BetterWindows-Files\       helper programs for the uninstaller + licenses – keep next to the exe
   └─ Data\                   your settings, undo data, backups and log
```

Run `BetterWindows.exe`, ideally via *right-click → Run as administrator*. The window opens maximized.

**Data is stored inside the portable folder** (`BetterWindows-Files\Data`). If that folder is not writable (e.g. under `C:\Program Files` without admin rights), BetterWindows falls back to `%LOCALAPPDATA%\BetterWindows`. On the first portable start, existing data from `%LOCALAPPDATA%\BetterWindows` is merged into the portable folder automatically. `publish.ps1` never replaces the `Data` folder.

Command-line options (mainly for testing):

| Option | Effect |
|---|---|
| `--page tweaks\|apps\|programs\|contextmenu\|startup\|backups\|settings` | Opens the given page |
| `--selftest <file>` | Read-only diagnostics without UI, written to `<file>` |

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

`publish.ps1` builds the single-file `BetterWindows.exe`, publishes the BCU helper programs into `BetterWindows-Files` and only replaces `BetterWindows-App` once everything succeeded. For development, a normal `dotnet build` in `BetterWindowsAvalonia` puts everything into one output folder.

## Project structure

```
BetterWindows/
├─ BetterWindowsAvalonia/      App source (Avalonia + FluentAvalonia)
│  ├─ Core/                    Logic: tweaks, apps, startup, uninstaller, context menus, localization
│  ├─ Views/                   Pages (XAML + code-behind)
│  ├─ ThirdParty/              Win11ContextMenuManager files (MIT)
│  └─ Assets/                  App icon (+ make-icon.ps1 generator)
├─ third_party/BCU/            Bulk Crap Uninstaller sources (Apache-2.0), see MODIFICATIONS.md
├─ BetterWindows-App/          Ready-to-run portable build (BetterWindows.exe + BetterWindows-Files)
├─ publish.ps1                 Creates BetterWindows-App
├─ Debloat.ps1                 Original standalone PowerShell script (German output)
└─ Undo-Debloat.ps1            Reverts changes made by Debloat.ps1
```

The PowerShell scripts are the starting point of the project and work independently of the app. Use **either** the scripts **or** the app – the app does not know about backups made by the scripts.

## Third-party components

| Component | License | Usage |
|---|---|---|
| [Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller) (UninstallTools, KlocTools, SteamHelper, StoreAppHelper) | Apache-2.0 | Program detection, uninstalling, leftover search. Modified – see `third_party/BCU/MODIFICATIONS.md` |
| [Windows 11 Context Menu Manager](https://github.com/branhill/windows-11-context-menu-manager) (`Blocks.cs`, `Packages.cs`) | MIT | Windows 11 context menu entries |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | UI framework |
| [FluentAvalonia](https://github.com/amwx/FluentAvalonia) | MIT | Windows 11 style controls |

The classic context menu management is an independent implementation. [ContextMenuManager](https://github.com/BluePointLilac/ContextMenuManager) served as inspiration for scope and concept; its GPL-3.0 source code was **not** used.

Bulk Crap Uninstaller ships with the GPL-3.0 licensed component *ObjectListView*; BetterWindows does **not** include it (it was only used by a WinForms dialog that BetterWindows does not need).

Full license texts ship with the app in `BetterWindows-Files/Licenses/` and `BetterWindows-Files/THIRD-PARTY-NOTICES.txt`.

## License

BetterWindows is licensed under the [Apache License 2.0](LICENSE). See [NOTICE](NOTICE) for attributions.

## Disclaimer

BetterWindows changes system settings and uninstalls software. Every change is backed up and reversible, but use it at your own risk and create a restore point before larger changes.
