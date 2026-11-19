using System.Diagnostics;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Microsoft.Win32;
using Windows.Management.Deployment;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Core;

public enum StartupKind { Registry, Folder, StoreApp }

public sealed class StartupItem : ObservableObject
{
    // set statt init: der XAML-Typgenerator braucht normale Setter
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public StartupKind Kind { get; set; }
    public string? Publisher { get; set; }
    public string? TargetPath { get; set; }   // ausfuehrbare Datei (fuer Icon und "Speicherort oeffnen")
    public string? LogoPath { get; set; }     // Store-App-Logo
    public bool IsMachineWide { get; set; }

    internal string ApprovedKey { get; set; } = "";  // StartupApproved-Schluessel bzw. Store-Task-Schluessel
    internal string ValueName { get; set; } = "";
    internal string? RunKey { get; set; }
    internal string? FilePath { get; set; }
    internal bool PolicyLocked { get; set; }

    private bool _enabled;
    public bool IsEnabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) Raise(nameof(StatusText)); }
    }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => Set(ref _icon, value); }

    public bool NeedsAdmin => IsMachineWide;
    public bool CanModify => !PolicyLocked && (!NeedsAdmin || Admin.IsElevated);
    public bool CanDelete => CanModify && Kind != StartupKind.StoreApp;
    public bool HasLocation => TargetPath != null && File.Exists(TargetPath) || FilePath != null;
    public string Subtitle => string.IsNullOrWhiteSpace(Publisher) ? SourceLabel : $"{Publisher}  ·  {SourceLabel}";
    public string StatusText => IsEnabled ? T("Enabled", "Aktiv") : T("Disabled", "Deaktiviert");
    public string ToolTipText => CanModify ? Command
        : PolicyLocked ? T("Set by a policy", "Durch eine Richtlinie festgelegt")
        : T("Applies to all users – restart as administrator to change", "Gilt für alle Benutzer – zum Ändern als Administrator starten");
}

/// <summary>
/// Liest und aendert Autostart-Eintraege genauso wie der Task-Manager (StartupApproved),
/// d. h. Deaktivieren ist jederzeit verlustfrei umkehrbar.
/// </summary>
public static class StartupManager
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string StoreTasks = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private static (string Run, string Approved, string Label, bool Machine)[] RegistrySources =>
    [
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", $@"HKCU\{Approved}\Run", T("Registry · Current user", "Registry · Benutzer"), false),
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", $@"HKLM\{Approved}\Run", T("Registry · All users", "Registry · Alle Benutzer"), true),
        (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", $@"HKLM\{Approved}\Run32", T("Registry · All users (32-bit)", "Registry · Alle Benutzer (32-Bit)"), true),
    ];

    public static List<StartupItem> Enumerate()
    {
        var items = new List<StartupItem>();
        foreach (var src in RegistrySources) items.AddRange(SafeEnum(() => FromRegistry(src)));
        items.AddRange(SafeEnum(() => FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            $@"HKCU\{Approved}\StartupFolder", T("Startup folder · Current user", "Autostart-Ordner · Benutzer"), false)));
        items.AddRange(SafeEnum(() => FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            $@"HKLM\{Approved}\StartupFolder", T("Startup folder · All users", "Autostart-Ordner · Alle Benutzer"), true)));
        items.AddRange(SafeEnum(FromStoreApps));
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<StartupItem> SafeEnum(Func<IEnumerable<StartupItem>> f)
    {
        try { return f().ToList(); } catch { return []; }
    }

    private static IEnumerable<StartupItem> FromRegistry((string Run, string Approved, string Label, bool Machine) src)
    {
        foreach (var name in Reg.ValueNames(src.Run))
        {
            if (string.IsNullOrEmpty(name)) continue;
            var cmd = Reg.Get(src.Run, name)?.ToString() ?? "";
            var exe = ExtractExecutable(cmd);
            yield return new StartupItem
            {
                Name = FriendlyName(name, exe),
                Command = cmd,
                SourceLabel = src.Label,
                Kind = StartupKind.Registry,
                Publisher = GetPublisher(exe),
                TargetPath = exe,
                IsMachineWide = src.Machine,
                ApprovedKey = src.Approved,
                ValueName = name,
                RunKey = src.Run,
                IsEnabled = IsApprovedEnabled(src.Approved, name),
            };
        }
    }

    private static IEnumerable<StartupItem> FromFolder(string folder, string approved, string label, bool machine)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string? target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) : file;
            yield return new StartupItem
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = target ?? file,
                SourceLabel = label,
                Kind = StartupKind.Folder,
                Publisher = GetPublisher(target),
                TargetPath = target,
                IsMachineWide = machine,
                ApprovedKey = approved,
                ValueName = fileName,
                FilePath = file,
                IsEnabled = IsApprovedEnabled(approved, fileName),
            };
        }
    }

    private static IEnumerable<StartupItem> FromStoreApps()
    {
        var pm = new PackageManager();
        foreach (var family in Reg.SubKeyNames(StoreTasks))
        {
            var taskIds = Reg.SubKeyNames($@"{StoreTasks}\{family}")
                             .Where(id => Reg.Get($@"{StoreTasks}\{family}\{id}", "State") is int).ToList();
            foreach (var taskId in taskIds)
            {
                string taskKey = $@"{StoreTasks}\{family}\{taskId}";
                if (Reg.Get(taskKey, "State") is not int state) continue;

                Windows.ApplicationModel.Package? pkg = null;
                try { pkg = pm.FindPackagesForUser(string.Empty, family).FirstOrDefault(); } catch { }
                if (pkg == null) continue; // App nicht (mehr) installiert

                string name = family, publisher = "", logo = "";
                try { name = pkg.DisplayName; } catch { }
                try { publisher = pkg.PublisherDisplayName; } catch { }
                try { logo = pkg.Logo?.LocalPath ?? ""; } catch { }

                yield return new StartupItem
                {
                    Name = (string.IsNullOrWhiteSpace(name) ? family : name) + (taskIds.Count > 1 ? $" ({taskId})" : ""),
                    Command = $"{family} ! {taskId}",
                    SourceLabel = "Store-App",
                    Kind = StartupKind.StoreApp,
                    Publisher = publisher,
                    LogoPath = File.Exists(logo) ? logo : null,
                    ApprovedKey = taskKey,
                    ValueName = "State",
                    // 0/1 = deaktiviert (1 = vom Benutzer), 2 = aktiv, 3/4 = per Richtlinie
                    IsEnabled = state is 2 or 4,
                    PolicyLocked = state is 3 or 4,
                };
            }
        }
    }

    private static bool IsApprovedEnabled(string approvedKey, string name)
    {
        // Kein Eintrag = aktiv. Erstes Byte gerade (02, 06) = aktiv, ungerade (03, 07) = deaktiviert.
        return Reg.Get(approvedKey, name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    public static void SetEnabled(StartupItem item, bool enabled)
    {
        if (item.Kind == StartupKind.StoreApp)
        {
            Reg.Set(item.ApprovedKey, "State", enabled ? 2 : 1, RegistryValueKind.DWord);
        }
        else
        {
            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            Reg.Set(item.ApprovedKey, item.ValueName, data, RegistryValueKind.Binary);
        }
        item.IsEnabled = enabled;
    }

    /// <summary>Entfernt einen Eintrag dauerhaft. Registry-Eintraege werden in removed-startup.json gesichert,
    /// Verknuepfungen in den Ordner RemovedStartup verschoben.</summary>
    public static void Delete(StartupItem item)
    {
        Directory.CreateDirectory(BackupStore.Folder);
        if (item.Kind == StartupKind.Registry && item.RunKey != null)
        {
            var logFile = Path.Combine(BackupStore.Folder, "removed-startup.json");
            var list = File.Exists(logFile)
                ? JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(logFile)) ?? []
                : [];
            list.Add(new() { ["Key"] = item.RunKey, ["Name"] = item.ValueName, ["Command"] = item.Command,
                             ["Removed"] = DateTime.Now.ToString("s") });
            File.WriteAllText(logFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            Reg.Delete(item.RunKey, item.ValueName);
        }
        else if (item.Kind == StartupKind.Folder && item.FilePath != null)
        {
            var dir = Path.Combine(BackupStore.Folder, "RemovedStartup");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(item.FilePath)}");
            File.Move(item.FilePath, dest);
        }
        else throw new InvalidOperationException(T("Store apps can only be disabled.", "Store-Apps können nur deaktiviert werden."));

        Reg.Delete(item.ApprovedKey, item.ValueName);
    }

    // ---------------------------------------------------------------- Hilfsfunktionen

    public static string? ExtractExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        string? exe;
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            exe = end > 1 ? cmd[1..end] : null;
        }
        else
        {
            int idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = idx > 0 ? cmd[..(idx + 4)] : cmd.Split(' ')[0];
        }
        if (exe == null) return null;
        if (!Path.IsPathRooted(exe))
        {
            var sys = Path.Combine(Environment.SystemDirectory, exe);
            if (File.Exists(sys)) return sys;
            var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
            if (File.Exists(win)) return win;
        }
        return exe;
    }

    private static string FriendlyName(string valueName, string? exe)
    {
        // Wertnamen wie "MicrosoftEdgeAutoLaunch_F52A..." lesbarer machen
        try
        {
            if (exe != null && File.Exists(exe))
            {
                var desc = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && valueName.Length > 24) return desc.Trim();
            }
        }
        catch { }
        return valueName;
    }

    private static string? GetPublisher(string? exe)
    {
        try
        {
            if (exe != null && File.Exists(exe))
                return FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim();
        }
        catch { }
        return null;
    }

    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }
}
