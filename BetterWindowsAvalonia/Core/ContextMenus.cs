using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Media.Imaging;
using BetterWindows.ThirdParty.Win11ContextMenuManager;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Core;

// Eigenstaendige Umsetzung der Kontextmenue-Verwaltung fuer BetterWindows.
// Mechanismen (alle von Windows dokumentiert bzw. allgemein bekannt):
//  - Befehle (shell\<verb>):     Wert "LegacyDisable" blendet aus (ProgrammaticAccessOnly/CommandFlags werden erkannt)
//  - Shell-Erweiterungen:         CLSID unter "Shell Extensions\Blocked" sperren (wie beim Windows-11-Menue)
//  - "Senden an":                 Dateiattribut "Versteckt" im SendTo-Ordner
//  - "Neu":                       Schluessel "ShellNew" umbenennen
//  - "Oeffnen mit":               Wert "NoOpenWith" unter Applications\<exe>
// Ausblenden wird wenn moeglich in HKCU geschrieben (kein Admin noetig, nur fuer das eigene Konto).

public enum MenuEntryKind { Win11, Command, ShellExtension, SendTo, NewItem, OpenWith }

public sealed class MenuEntry : ObservableObject
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Note { get; set; } = "";
    public string ToolTipText { get; set; } = "";
    public MenuEntryKind Kind { get; set; }
    public string? RegPath { get; set; }      // HKEY_CLASSES_ROOT\... (zum Kopieren / Regedit)
    public string? FilePath { get; set; }     // Datei / Programm (Speicherort oeffnen)
    public bool CanToggle { get; set; } = true;
    public bool CanDelete { get; set; }
    public bool HasRegPath => RegPath != null;
    public bool HasFilePath => FilePath != null && (File.Exists(FilePath) || Directory.Exists(FilePath));
    public bool HasNote => Note.Length > 0;

    internal string? IconLocation { get; set; }     // "datei,index" oder Datei
    internal string? LogoPath { get; set; }         // PNG (Store-Apps)
    internal Action<bool>? Toggle { get; set; }
    internal Action? Delete { get; set; }

    private bool _visible;
    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => Set(ref _icon, value); }
}

public sealed record MenuScene(string Name, string Glyph, string Description, string[] Paths);

public static class ContextMenuService
{
    private const string BlockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string DisabledShellNew = "ShellNew_BetterWindowsAus";

    public static MenuScene[] Scenes =>
    [
        new(T("Files", "Dateien"), "", T("Right-click on any file", "Rechtsklick auf jede Datei"), ["*"]),
        new(T("All objects", "Alle Objekte"), "", T("Files and folders together", "Dateien und Ordner gemeinsam"), ["AllFilesystemObjects"]),
        new(T("Folders", "Ordner"), "", T("Right-click on a folder", "Rechtsklick auf einen Ordner"), ["Directory", "Folder"]),
        new(T("Folder background", "Ordner-Hintergrund"), "", T("Right-click on an empty area inside a folder", "Rechtsklick in einen leeren Bereich eines Ordners"), ["Directory\\Background"]),
        new("Desktop", "", T("Right-click on the desktop", "Rechtsklick auf den Desktop"), ["DesktopBackground"]),
        new(T("Drives", "Laufwerke"), "", T("Right-click on a drive", "Rechtsklick auf ein Laufwerk"), ["Drive"]),
        new(T("This PC", "Dieser PC"), "", T("Right-click on “This PC”", "Rechtsklick auf „Dieser PC“"), ["CLSID\\{20D04FE0-3AEA-1069-A2D8-08002B30309D}"]),
        new(T("Recycle Bin", "Papierkorb"), "", T("Right-click on the Recycle Bin", "Rechtsklick auf den Papierkorb"), ["CLSID\\{645FF040-5081-101B-9F08-00AA002F954E}"]),
        new(T("Libraries", "Bibliotheken"), "", T("Libraries and their background", "Bibliotheken und ihr Hintergrund"), ["LibraryFolder", "LibraryFolder\\Background"]),
        new(T("Shortcuts", "Verknüpfungen"), "", T("Right-click on .lnk files", "Rechtsklick auf .lnk-Dateien"), ["lnkfile"]),
        new(T("Programs (.exe)", "Programme (.exe)"), "", T("Right-click on executable files", "Rechtsklick auf ausführbare Dateien"), ["exefile", "SystemFileAssociations\\.exe"]),
        new(T("Images", "Bilder"), "", T("All image formats", "Alle Bildformate"), ["SystemFileAssociations\\image"]),
        new("Videos", "", T("All video formats", "Alle Videoformate"), ["SystemFileAssociations\\video"]),
        new(T("Music", "Musik"), "", T("All audio formats", "Alle Audioformate"), ["SystemFileAssociations\\audio"]),
        new(T("Text files", "Textdateien"), "", T("All text formats", "Alle Textformate"), ["SystemFileAssociations\\text"]),
        new(T("Unknown types", "Unbekannte Typen"), "", T("Files without an associated program", "Dateien ohne zugeordnetes Programm"), ["Unknown"]),
    ];

    // ================================================================ Neues Menue (Windows 11)

    public static async Task<List<MenuEntry>> LoadWin11Async()
    {
        Blocks.LoadAll();
        var extensions = await Packages.GetExtensions();
        return extensions
            .OrderBy(x => x.Package.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(x =>
            {
                var types = x.ContextMenus.Select(m => DescribeType(m.Type)).Distinct().ToList();
                var e = new MenuEntry
                {
                    Kind = MenuEntryKind.Win11,
                    Name = x.Package.DisplayName,
                    Detail = $"{x.Package.PublisherDisplayName}  ·  {string.Join(", ", types)}",
                    ToolTipText = $"{x.ComServer.DisplayName ?? x.Package.DisplayName}\nCLSID {{{x.Id}}}\n{x.Package.FamilyName}",
                    FilePath = x.Package.InstallPath,
                    LogoPath = x.Package.Logo,
                    IsVisible = !IsBlocked(x.Id),
                };
                e.CanToggle = Admin.IsElevated || !IsBlockedMachine(x.Id);
                e.Note = e.CanToggle ? "" : T("Blocked for all users – restart as administrator to change", "Für alle Benutzer gesperrt – zum Ändern als Administrator starten");
                e.Toggle = visible => SetBlocked(x.Id, !visible);
                return e;
            }).ToList();
    }

    private static string DescribeType(string? type) => type switch
    {
        null => "?",
        "*" or "File: *" => T("All files", "Alle Dateien"),
        "Directory" => T("Folders", "Ordner"),
        "Directory\\Background" => T("Folder background", "Ordner-Hintergrund"),
        "Drive" => T("Drives", "Laufwerke"),
        _ when type.StartsWith("File: ") => type[6..],
        _ => type
    };

    // ================================================================ Sperrliste (Shell Extensions\Blocked)

    private static bool ListContains(RegistryKey root, string clsid)
    {
        using var k = root.OpenSubKey(BlockedKey);
        return k?.GetValueNames().Any(n => n.Trim('{', '}').Equals(clsid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool IsBlockedMachine(string clsid) => ListContains(Registry.LocalMachine, clsid);
    private static bool IsBlocked(string clsid) => ListContains(Registry.CurrentUser, clsid) || IsBlockedMachine(clsid);

    private static void SetBlocked(string clsid, bool blocked)
    {
        string braced = "{" + clsid.Trim('{', '}').ToUpperInvariant() + "}";
        if (blocked)
        {
            using var k = Registry.CurrentUser.CreateSubKey(BlockedKey, true);
            k.SetValue(braced, "", RegistryValueKind.String);
            return;
        }
        RemoveFromList(Registry.CurrentUser, clsid);
        if (IsBlockedMachine(clsid))
        {
            if (!Admin.IsElevated) throw new UnauthorizedAccessException(T("Blocked for all users – restart as administrator to unblock.", "Für alle Benutzer gesperrt – zum Freigeben als Administrator starten."));
            RemoveFromList(Registry.LocalMachine, clsid);
        }
    }

    private static void RemoveFromList(RegistryKey root, string clsid)
    {
        using var k = root.OpenSubKey(BlockedKey, true);
        if (k == null) return;
        foreach (var n in k.GetValueNames().Where(n => n.Trim('{', '}').Equals(clsid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)))
            k.DeleteValue(n, false);
    }

    // ================================================================ Klassisches Menue

    public static List<MenuEntry> LoadScene(MenuScene scene)
    {
        var result = new List<MenuEntry>();
        var seenClsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var basePath in scene.Paths)
        {
            using (var shell = Registry.ClassesRoot.OpenSubKey($@"{basePath}\shell"))
                foreach (var verb in shell?.GetSubKeyNames() ?? [])
                    if (Safe(() => FromVerb(basePath, verb)) is { } e) result.Add(e);

            using (var handlers = Registry.ClassesRoot.OpenSubKey($@"{basePath}\shellex\ContextMenuHandlers"))
                foreach (var name in handlers?.GetSubKeyNames() ?? [])
                    if (Safe(() => FromHandler(basePath, name)) is { } e && seenClsids.Add(e.ToolTipText)) result.Add(e);
        }
        return result.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static MenuEntry? FromVerb(string basePath, string verb)
    {
        string sub = $@"{basePath}\shell\{verb}";
        using var k = Registry.ClassesRoot.OpenSubKey(sub);
        if (k == null) return null;

        string name = CleanLabel(ResolveIndirect(k.GetValue("MUIVerb") as string)
                                 ?? ResolveIndirect(k.GetValue("") as string)
                                 ?? KnownVerb(verb));
        bool hidden = k.GetValue("LegacyDisable") != null
                      || k.GetValue("ProgrammaticAccessOnly") != null
                      || (ToInt(k.GetValue("CommandFlags")) & 0x8) != 0
                      || ToInt(k.GetValue("HideBasedOnVelocityId")) == 0x639bc8;

        string? command;
        string? delegateExecute;
        using (var c = k.OpenSubKey("command"))
        {
            command = c?.GetValue("") as string;
            delegateExecute = c?.GetValue("DelegateExecute") as string;
        }
        bool hasSubMenu = k.GetValue("SubCommands") != null || k.GetValue("ExtendedSubCommandsKey") != null;
        bool system = command == null && (delegateExecute != null || k.GetValue("ExplorerCommandHandler") != null);
        string? exe = ExtractExe(command);

        var notes = new List<string>();
        if (k.GetValue("Extended") != null) notes.Add(T("only visible with Shift", "nur mit Umschalttaste sichtbar"));
        if (hasSubMenu) notes.Add(T("Submenu", "Untermenü"));
        if (system) notes.Add(T("Windows system command", "Windows-Systembefehl"));

        return new MenuEntry
        {
            Kind = MenuEntryKind.Command,
            Name = name.Length > 0 ? name : verb,
            Detail = T("Command", "Befehl") + "  ·  " + (command ?? (system ? "Windows" : verb)),
            Note = string.Join("  ·  ", notes),
            ToolTipText = $@"HKEY_CLASSES_ROOT\{sub}" + (command != null ? "\n" + command : ""),
            RegPath = $@"HKEY_CLASSES_ROOT\{sub}",
            FilePath = exe,
            IconLocation = k.GetValue("Icon") as string ?? exe,
            IsVisible = !hidden,
            CanDelete = true,
            Toggle = visible => SetVerbVisible(sub, visible),
            Delete = () => DeleteClassesKey(sub),
        };
    }

    private static MenuEntry? FromHandler(string basePath, string keyName)
    {
        string sub = $@"{basePath}\shellex\ContextMenuHandlers\{keyName}";
        using var k = Registry.ClassesRoot.OpenSubKey(sub);
        if (k == null) return null;
        string? value = k.GetValue("") as string;
        if (!Guid.TryParse(value, out var guid) && !Guid.TryParse(keyName, out guid)) return null;
        string clsid = guid.ToString("D").ToUpperInvariant();

        string? dll = null, clsName = null;
        using (var c = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}"))
        {
            clsName = ResolveIndirect(c?.GetValue("LocalizedString") as string) ?? (c?.GetValue("") as string);
            using var srv = c?.OpenSubKey("InprocServer32");
            dll = srv?.GetValue("") as string;
            if (dll != null) dll = Environment.ExpandEnvironmentVariables(dll.Trim('"'));
        }
        string? dllDesc = null, dllCompany = null;
        if (dll != null && !Path.IsPathRooted(dll)) dll = Path.Combine(Environment.SystemDirectory, dll);
        if (dll != null && File.Exists(dll))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(dll);
                dllDesc = vi.FileDescription?.Trim();
                dllCompany = vi.CompanyName?.Trim();
            }
            catch { }
        }

        string name = !Guid.TryParse(keyName, out _) ? keyName.TrimStart('-') : clsName ?? dllDesc ?? keyName;
        if (string.IsNullOrWhiteSpace(name)) name = dllDesc ?? clsid;

        var e = new MenuEntry
        {
            Kind = MenuEntryKind.ShellExtension,
            Name = name,
            Detail = T("Shell extension", "Shell-Erweiterung") + "  ·  " + (dllCompany is { Length: > 0 } ? dllCompany + "  ·  " : "") + (dll != null ? Path.GetFileName(dll) : "{" + clsid + "}"),
            Note = dll != null && !File.Exists(dll) ? T("DLL missing – probably a leftover of an uninstalled program", "DLL fehlt – vermutlich Rest eines deinstallierten Programms") : "",
            ToolTipText = "{" + clsid + "}",
            RegPath = $@"HKEY_CLASSES_ROOT\{sub}",
            FilePath = dll,
            IconLocation = dll,
            IsVisible = !IsBlocked(clsid),
            CanDelete = true,
        };
        e.CanToggle = Admin.IsElevated || !IsBlockedMachine(clsid);
        e.Toggle = visible => SetBlocked(clsid, !visible);
        e.Delete = () => DeleteClassesKey(sub);
        if (e.Note.Length == 0) e.Note = T("Hiding applies everywhere this extension is registered", "Ausblenden gilt überall, wo diese Erweiterung eingebunden ist");
        return e;
    }

    private static void SetVerbVisible(string sub, bool visible)
    {
        string user = $@"HKCU\Software\Classes\{sub}", machine = $@"HKLM\Software\Classes\{sub}";
        if (!visible)
        {
            // Ueberlagerung in HKCU: Windows fuehrt HKCU und HKLM zu HKEY_CLASSES_ROOT zusammen
            var created = MissingKeys(user);
            Reg.Set(user, "LegacyDisable", "", RegistryValueKind.String);
            if (created.Count > 0) BackupStore.RememberIfAbsent("ctxkeys|" + sub, new ValueBackup(true, string.Join("\n", created), "keys"));
            return;
        }

        foreach (var v in new[] { "LegacyDisable", "ProgrammaticAccessOnly", "HideBasedOnVelocityId" })
            if (Reg.TryGet(user, v, out _, out _)) Reg.Delete(user, v);

        // Von uns angelegte, jetzt leere HKCU-Schluessel wieder entfernen
        if (BackupStore.Take("ctxkeys|" + sub) is { Value: { } list })
            foreach (var key in list.Split('\n').Reverse())
                if (Reg.KeyExists(key) && Reg.ValueNames(key).Length == 0 && Reg.SubKeyNames(key).Length == 0)
                    Reg.DeleteKeyTree(key);

        var machineFlags = new[] { "LegacyDisable", "ProgrammaticAccessOnly", "HideBasedOnVelocityId" }
            .Where(v => Reg.TryGet(machine, v, out _, out _)).ToList();
        if (machineFlags.Count > 0)
        {
            if (!Admin.IsElevated)
                throw new UnauthorizedAccessException(T("This entry is hidden for all users – restart as administrator to show it.", "Dieser Eintrag ist für alle Benutzer ausgeblendet – zum Einblenden als Administrator starten."));
            foreach (var v in machineFlags) Reg.Delete(machine, v);
        }
    }

    // ================================================================ "Senden an"

    public static List<MenuEntry> LoadSendTo()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder)
            .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var attrs = File.GetAttributes(f);
                return new MenuEntry
                {
                    Kind = MenuEntryKind.SendTo,
                    Name = Native.DisplayName(f) ?? Path.GetFileNameWithoutExtension(f),
                    Detail = T("Send to", "Senden an") + "  ·  " + Path.GetFileName(f),
                    ToolTipText = f,
                    FilePath = f,
                    IconLocation = f,
                    IsVisible = (attrs & FileAttributes.Hidden) == 0,
                    CanDelete = true,
                    Toggle = visible =>
                    {
                        var a = File.GetAttributes(f);
                        File.SetAttributes(f, visible ? a & ~FileAttributes.Hidden : a | FileAttributes.Hidden);
                    },
                    Delete = () =>
                    {
                        var dir = Path.Combine(BackupStore.Folder, "Kontextmenue-Sicherungen", "SendTo");
                        Directory.CreateDirectory(dir);
                        File.Move(f, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(f)}"));
                    }
                };
            })
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ================================================================ "Neu"

    public static List<MenuEntry> LoadShellNew()
    {
        var result = new List<MenuEntry>();
        foreach (var ext in Registry.ClassesRoot.GetSubKeyNames().Where(n => n.StartsWith('.') || n == "Folder"))
        {
            using var extKey = Safe(() => Registry.ClassesRoot.OpenSubKey(ext));
            if (extKey == null) continue;
            string? progId = extKey.GetValue("") as string;

            // ShellNew direkt unter .ext oder unter .ext\<ProgID>
            var candidates = new List<(string parent, string keyName, string? progId)>();
            foreach (var name in extKey.GetSubKeyNames())
            {
                if (name is "ShellNew" or DisabledShellNew) candidates.Add((ext, name, progId));
                else
                {
                    using var sub = Safe(() => extKey.OpenSubKey(name));
                    foreach (var inner in sub?.GetSubKeyNames() ?? [])
                        if (inner is "ShellNew" or DisabledShellNew) candidates.Add(($@"{ext}\{name}", inner, name));
                }
            }

            foreach (var (parent, keyName, pid) in candidates)
            {
                if (Safe(() => FromShellNew(ext, parent, keyName, pid)) is { } e) result.Add(e);
            }
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static MenuEntry? FromShellNew(string ext, string parent, string keyName, string? progId)
    {
        using var k = Registry.ClassesRoot.OpenSubKey($@"{parent}\{keyName}");
        if (k == null || k.ValueCount == 0 && k.SubKeyCount == 0) return null;

        string? friendly = ResolveIndirect(k.GetValue("ItemName") as string) ?? ResolveIndirect(k.GetValue("MenuText") as string);
        string? icon = null;
        if (progId != null)
        {
            using var p = Registry.ClassesRoot.OpenSubKey(progId);
            friendly ??= ResolveIndirect(p?.GetValue("FriendlyTypeName") as string) ?? (p?.GetValue("") as string);
            using var di = p?.OpenSubKey("DefaultIcon");
            icon = di?.GetValue("") as string;
        }
        if (ext == "Folder") friendly ??= T("Folder", "Ordner");
        bool visible = keyName == "ShellNew";

        return new MenuEntry
        {
            Kind = MenuEntryKind.NewItem,
            Name = string.IsNullOrWhiteSpace(friendly) ? ext : friendly!,
            Detail = T("New", "Neu") + $"  ·  {ext}" + (progId != null && progId != ext ? $"  ·  {progId}" : ""),
            Note = ext == "Folder" ? T("System entry – hiding not recommended", "Systemeintrag – Ausblenden nicht empfohlen") : "",
            ToolTipText = $@"HKEY_CLASSES_ROOT\{parent}\{keyName}",
            RegPath = $@"HKEY_CLASSES_ROOT\{parent}\{keyName}",
            IconLocation = icon,
            IsVisible = visible,
            Toggle = v => SetShellNewVisible(parent, v),
        };
    }

    private static void SetShellNewVisible(string parent, bool visible)
    {
        string from = visible ? DisabledShellNew : "ShellNew", to = visible ? "ShellNew" : DisabledShellNew;
        bool any = false;
        foreach (var (hive, label) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            using var p = hive.OpenSubKey($@"Software\Classes\{parent}");
            if (p?.OpenSubKey(from) == null) continue;
            if (label == "HKLM" && !Admin.IsElevated)
                throw new UnauthorizedAccessException(T("This entry applies to all users – restart as administrator to change it.", "Dieser Eintrag gilt für alle Benutzer – zum Ändern als Administrator starten."));
            using var writable = hive.OpenSubKey($@"Software\Classes\{parent}", true)!;
            int rc = Native.RegRenameKey(writable.Handle, from, to);
            if (rc != 0) throw new IOException(T($"Renaming failed (error {rc}).", $"Umbenennen fehlgeschlagen (Fehler {rc})."));
            any = true;
        }
        if (!any) throw new InvalidOperationException(T("Key not found.", "Schlüssel nicht gefunden."));
        // Zwischenspeicher des Explorers fuer das "Neu"-Menue verwerfen
        Reg.Delete(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew", "Classes");
    }

    // ================================================================ "Oeffnen mit"

    public static List<MenuEntry> LoadOpenWith()
    {
        var result = new List<MenuEntry>();
        using var apps = Registry.ClassesRoot.OpenSubKey("Applications");
        foreach (var app in apps?.GetSubKeyNames() ?? [])
        {
            if (Safe(() => FromApplication(app)) is { } e) result.Add(e);
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static MenuEntry? FromApplication(string app)
    {
        string sub = $@"Applications\{app}";
        using var k = Registry.ClassesRoot.OpenSubKey(sub);
        if (k == null) return null;
        string? command = null;
        using (var shell = k.OpenSubKey("shell"))
        {
            foreach (var verb in shell?.GetSubKeyNames() ?? [])
            {
                using var c = shell!.OpenSubKey($@"{verb}\command");
                command = c?.GetValue("") as string;
                if (command != null) break;
            }
        }
        if (command == null) return null; // ohne Befehl erscheint es nicht in "Oeffnen mit"

        string? exe = ExtractExe(command);
        bool exists = exe != null && File.Exists(exe);
        string? desc = null;
        if (exists) try { desc = FileVersionInfo.GetVersionInfo(exe!).FileDescription?.Trim(); } catch { }
        string name = ResolveIndirect(k.GetValue("FriendlyAppName") as string) ?? desc ?? app;

        return new MenuEntry
        {
            Kind = MenuEntryKind.OpenWith,
            Name = string.IsNullOrWhiteSpace(name) ? app : name,
            Detail = T("Open with", "Öffnen mit") + "  ·  " + app,
            Note = exists ? "" : T("Program not found – probably a leftover of an uninstall", "Programm nicht gefunden – vermutlich Rest einer Deinstallation"),
            ToolTipText = command,
            RegPath = $@"HKEY_CLASSES_ROOT\{sub}",
            FilePath = exe,
            IconLocation = exe,
            IsVisible = k.GetValue("NoOpenWith") == null,
            CanDelete = !exists,
            Toggle = v => SetOpenWithVisible(sub, v),
            Delete = () => DeleteClassesKey(sub),
        };
    }

    private static void SetOpenWithVisible(string sub, bool visible)
    {
        string user = $@"HKCU\Software\Classes\{sub}", machine = $@"HKLM\Software\Classes\{sub}";
        if (!visible) { Reg.Set(user, "NoOpenWith", "", RegistryValueKind.String); return; }
        if (Reg.TryGet(user, "NoOpenWith", out _, out _)) Reg.Delete(user, "NoOpenWith");
        if (Reg.TryGet(machine, "NoOpenWith", out _, out _))
        {
            if (!Admin.IsElevated) throw new UnauthorizedAccessException(T("Hidden for all users – restart as administrator to show it.", "Für alle Benutzer ausgeblendet – zum Einblenden als Administrator starten."));
            Reg.Delete(machine, "NoOpenWith");
        }
    }

    // ================================================================ Gemeinsames

    /// <summary>Loescht einen Schluessel aus HKCU\Software\Classes und HKLM\Software\Classes, vorher .reg-Sicherung.</summary>
    private static void DeleteClassesKey(string sub)
    {
        string dir = Path.Combine(BackupStore.Folder, "Kontextmenue-Sicherungen");
        Directory.CreateDirectory(dir);
        bool any = false;
        foreach (var (hive, label) in new[] { ("HKCU", "Benutzer"), ("HKLM", "Alle") })
        {
            string path = $@"{hive}\Software\Classes\{sub}";
            if (!Reg.KeyExists(path)) continue;
            if (hive == "HKLM" && !Admin.IsElevated)
                throw new UnauthorizedAccessException(T("This entry applies to all users – restart as administrator to remove it.", "Dieser Eintrag gilt für alle Benutzer – zum Entfernen als Administrator starten."));
            string file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{label}_{string.Concat(sub.Split(Path.GetInvalidFileNameChars())).Replace('\\', '_')}.reg");
            using (var p = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{path}\" \"{file}\" /y") { CreateNoWindow = true, UseShellExecute = false }))
            {
                p?.WaitForExit(15000);
                if (p == null || p.ExitCode != 0 || !File.Exists(file))
                    throw new IOException(T("Backup failed – entry was not deleted.", "Sicherung fehlgeschlagen – Eintrag wurde nicht gelöscht."));
            }
            Reg.DeleteKeyTree(path);
            any = true;
        }
        if (!any) throw new InvalidOperationException(T("Key not found.", "Schlüssel nicht gefunden."));
    }

    public static void OpenInRegedit(string regPath)
    {
        Reg.Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey", "Computer\\" + regPath, RegistryValueKind.String);
        foreach (var p in Process.GetProcessesByName("regedit")) { try { p.CloseMainWindow(); } catch { } }
        Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
    }

    public static Bitmap? LoadIcon(MenuEntry e)
    {
        if (e.LogoPath != null && File.Exists(e.LogoPath))
            try { return new Bitmap(e.LogoPath); } catch { }
        return e.IconLocation != null ? Native.IconFromLocation(e.IconLocation) : null;
    }

    private static List<string> MissingKeys(string path)
    {
        var missing = new List<string>();
        for (string? p = path; p != null && p.Count(ch => ch == '\\') > 2 && !Reg.KeyExists(p); p = Path.GetDirectoryName(p))
            missing.Insert(0, p);
        return missing;
    }

    private static string? ResolveIndirect(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!s.StartsWith('@')) return s;
        var sb = new StringBuilder(1024);
        return Native.SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0 ? sb.ToString() : null;
    }

    private static string CleanLabel(string s) => s.Replace("&&", "\u0001").Replace("&", "").Replace('\u0001', '&').Trim();

    private static string KnownVerb(string verb) => verb.ToLowerInvariant() switch
    {
        "open" => T("Open", "Öffnen"),
        "edit" => T("Edit", "Bearbeiten"),
        "print" => T("Print", "Drucken"),
        "printto" => T("Print to", "Drucken auf"),
        "runas" => T("Run as administrator", "Als Administrator ausführen"),
        "runasuser" => T("Run as different user", "Als anderer Benutzer ausführen"),
        "find" => T("Search", "Suchen"),
        "explore" => T("Explore", "Durchsuchen"),
        "play" => T("Play", "Wiedergeben"),
        "preview" => T("Preview", "Vorschau"),
        _ => verb
    };

    private static int ToInt(object? v) => v switch { int i => i, long l => (int)l, string s when int.TryParse(s, out var x) => x, _ => 0 };

    private static string? ExtractExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        string? exe;
        if (cmd.StartsWith('"')) { int end = cmd.IndexOf('"', 1); exe = end > 1 ? cmd[1..end] : null; }
        else { int idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); exe = idx > 0 ? cmd[..(idx + 4)] : cmd.Split(' ')[0]; }
        if (exe != null && !Path.IsPathRooted(exe))
        {
            var sys = Path.Combine(Environment.SystemDirectory, exe);
            if (File.Exists(sys)) return sys;
            var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
            if (File.Exists(win)) return win;
        }
        return exe;
    }

    private static T? Safe<T>(Func<T?> f) where T : class { try { return f(); } catch { return null; } }
}

internal static class Native
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegRenameKey(SafeRegistryHandle hKey, string? lpSubKeyName, string lpNewKeyName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    private const uint SHGFI_ICON = 0x100, SHGFI_DISPLAYNAME = 0x200, SHGFI_LARGEICON = 0x0;

    internal static string? DisplayName(string path)
    {
        var info = new SHFILEINFO();
        return SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME) != IntPtr.Zero
               && !string.IsNullOrWhiteSpace(info.szDisplayName) ? info.szDisplayName : null;
    }

    /// <summary>Icon aus "Datei,Index" (Index negativ = Ressourcen-ID) oder aus einer beliebigen Datei.</summary>
    internal static Bitmap? IconFromLocation(string location)
    {
        try
        {
            string loc = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
            int index = 0;
            int comma = loc.LastIndexOf(',');
            if (comma > 0 && int.TryParse(loc[(comma + 1)..].Trim(), out var idx)) { index = idx; loc = loc[..comma].Trim().Trim('"'); }
            if (!Path.IsPathRooted(loc)) loc = Path.Combine(Environment.SystemDirectory, loc);
            if (!File.Exists(loc)) return null;

            IntPtr h = IntPtr.Zero;
            string ext = Path.GetExtension(loc).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".ico" or ".cpl" or ".ocx" or ".mun")
            {
                var large = new IntPtr[1];
                if (ExtractIconEx(loc, index, large, null, 1) > 0) h = large[0];
            }
            if (h == IntPtr.Zero)
            {
                var info = new SHFILEINFO();
                if (SHGetFileInfo(loc, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero)
                    h = info.hIcon;
            }
            if (h == IntPtr.Zero) return null;
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(h);
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                return new Bitmap(ms);
            }
            finally { DestroyIcon(h); }
        }
        catch { return null; }
    }
}
