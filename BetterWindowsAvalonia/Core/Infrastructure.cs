using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace BetterWindows.Core;

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Admin
{
    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static bool TryRelaunchElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; } // UAC abgelehnt
    }
}

/// <summary>Registry-Zugriff mit Pfaden der Form "HKCU\..." / "HKLM\..." (immer 64-Bit-Ansicht).</summary>
public static class Reg
{
    private static (RegistryKey Root, string Sub) Split(string path)
    {
        int i = path.IndexOf('\\');
        var hive = path[..i] switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            _ => throw new ArgumentException("Unbekannter Hive: " + path)
        };
        return (RegistryKey.OpenBaseKey(hive, RegistryView.Registry64), path[(i + 1)..]);
    }

    private static string Raw(string name) => name == "(default)" ? "" : name;

    public static bool TryGet(string path, string name, out object? value, out RegistryValueKind kind)
    {
        value = null; kind = RegistryValueKind.Unknown;
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.OpenSubKey(sub))
        {
            if (key == null) return false;
            string raw = Raw(name);
            if (!key.GetValueNames().Contains(raw, StringComparer.OrdinalIgnoreCase)) return false;
            value = key.GetValue(raw, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            kind = key.GetValueKind(raw);
            return true;
        }
    }

    public static object? Get(string path, string name) => TryGet(path, name, out var v, out _) ? v : null;

    public static void Set(string path, string name, object value, RegistryValueKind kind)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.CreateSubKey(sub, true))
            key.SetValue(Raw(name), value, kind);
    }

    public static void Delete(string path, string name)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.OpenSubKey(sub, true))
            key?.DeleteValue(Raw(name), false);
    }

    public static bool KeyExists(string path)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.OpenSubKey(sub))
            return key != null;
    }

    public static void DeleteKeyTree(string path)
    {
        var (root, sub) = Split(path);
        using (root) root.DeleteSubKeyTree(sub, false);
    }

    public static string[] ValueNames(string path)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.OpenSubKey(sub))
            return key?.GetValueNames() ?? [];
    }

    public static string[] SubKeyNames(string path)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.OpenSubKey(sub))
            return key?.GetSubKeyNames() ?? [];
    }

    public static bool ValueEquals(object? current, object target, RegistryValueKind kind)
    {
        if (current == null) return false;
        return kind switch
        {
            RegistryValueKind.DWord or RegistryValueKind.QWord =>
                current is int or long && Convert.ToInt64(current) == Convert.ToInt64(target),
            RegistryValueKind.Binary => current is byte[] a && target is byte[] b && a.SequenceEqual(b),
            _ => string.Equals(current.ToString(), target.ToString(), StringComparison.Ordinal)
        };
    }
}

public sealed record ValueBackup(bool Existed, string? Value, string? Kind);

/// <summary>Speichert die Originalwerte vor jeder Aenderung (%LOCALAPPDATA%\BetterWindows\backup.json).</summary>
public static class BackupStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, ValueBackup>? _data;

    /// <summary>Datenordner: portabel in "BetterWindows-Files\Data", sonst %LOCALAPPDATA%\BetterWindows.</summary>
    public static string Folder { get; } = DataLocation.Resolve();

    private static string FilePath => Path.Combine(Folder, "backup.json");

    private static Dictionary<string, ValueBackup> Data
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                _data = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, ValueBackup>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch { _data = new(); }
            return _data;
        }
    }

    public static void RememberIfAbsent(string key, ValueBackup backup)
    {
        lock (Gate)
        {
            if (Data.ContainsKey(key)) return;
            Data[key] = backup;
            Save();
        }
    }

    public static ValueBackup? Take(string key)
    {
        lock (Gate)
        {
            if (!Data.Remove(key, out var b)) return null;
            Save();
            return b;
        }
    }

    private static void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Encode(object? value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])value!),
        RegistryValueKind.MultiString => string.Join("\n", (string[])value!),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    public static object Decode(string value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(value, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(value, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromBase64String(value),
        RegistryValueKind.MultiString => value.Split('\n'),
        _ => value
    };
}

/// <summary>
/// Portabler Aufbau: BetterWindows.exe (Einzeldatei) und daneben der Ordner "BetterWindows-Files"
/// mit Hilfsprogrammen und Lizenzen. Im Entwicklungs-Build liegt alles direkt im Ausgabeordner.
/// </summary>
public static class AppPaths
{
    public const string FilesFolderName = "BetterWindows-Files";

    public static string FilesDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, FilesFolderName);
            return Directory.Exists(dir) ? dir : AppContext.BaseDirectory;
        }
    }
}

/// <summary>
/// Bestimmt, wo BetterWindows seine Daten ablegt (Einstellungen, Rueckgaengig-Daten, Sicherungen, Protokoll).
/// Portabel: "BetterWindows-Files\Data" neben der EXE, sofern der Ordner existiert und beschreibbar ist.
/// Sonst: %LOCALAPPDATA%\BetterWindows. Beim ersten portablen Start werden vorhandene Daten aus
/// %LOCALAPPDATA%\BetterWindows uebernommen, damit z. B. backup.json zum Zuruecksetzen erhalten bleibt.
/// </summary>
public static class DataLocation
{
    public static string LocalAppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterWindows");

    public static bool IsPortable { get; private set; }

    /// <summary>Ergebnis der Uebernahme beim Start (fuer einen Hinweis in den Einstellungen), sonst null.</summary>
    public static string? MigrationNote { get; private set; }

    internal static string Resolve()
    {
        // Fester Datenordner (fuer Tests): keine Uebernahme, keine Portabel-Erkennung
        var forced = Environment.GetEnvironmentVariable("BETTERWINDOWS_DATA");
        if (!string.IsNullOrWhiteSpace(forced)) return forced;

        // Portabel nur in der veroeffentlichten Einzeldatei-EXE (dort ist Assembly.Location leer).
        // Entwicklungs-Builds nutzen immer %LOCALAPPDATA%, damit sie nie echte Daten "uebernehmen".
#pragma warning disable IL3000
        bool singleFile = string.IsNullOrEmpty(typeof(DataLocation).Assembly.Location);
#pragma warning restore IL3000
        string filesDir = Path.Combine(AppContext.BaseDirectory, AppPaths.FilesFolderName);
        if (!singleFile || !Directory.Exists(filesDir)) return LocalAppDataFolder;

        string portable = Path.Combine(filesDir, "Data");
        try
        {
            Directory.CreateDirectory(portable);
            // Schreibtest (z. B. unter C:\Program Files ohne Adminrechte nicht moeglich)
            string probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch { return LocalAppDataFolder; }

        IsPortable = true;
        MigrationNote = Migrate(LocalAppDataFolder, portable);
        return portable;
    }

    private const string MigratedMarker = "MIGRATED-TO-PORTABLE.txt";

    /// <summary>
    /// Fuehrt die Daten aus source in target zusammen (nichts wird ueberschrieben; backup.json wird Eintrag fuer
    /// Eintrag vereinigt) und entfernt danach source. Liefert eine Notiz oder null, wenn nichts zu tun war.
    /// </summary>
    internal static string? Migrate(string source, string target)
    {
        try
        {
            if (!Directory.Exists(source)) return null;
            if (File.Exists(Path.Combine(source, MigratedMarker))) return null;      // schon uebernommen
            if (!Directory.EnumerateFileSystemEntries(source).Any()) return null;

            MergeDirectory(source, target);
        }
        catch (Exception ex)
        {
            // Zusammenfuehren fehlgeschlagen: Quelle bleibt vollstaendig erhalten
            return "Migration failed: " + ex.Message;
        }

        // Erst nach vollstaendiger Uebernahme die alten Daten entfernen
        try { Directory.Delete(source, true); }
        catch
        {
            try { File.WriteAllText(Path.Combine(source, MigratedMarker), $"Data was moved to {target} on {DateTime.Now:g}."); } catch { }
        }
        return $"{source} -> {target}";
    }

    private static void MergeDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file), dest = Path.Combine(target, name);
            if (name.Equals(MigratedMarker, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(dest)) File.Copy(file, dest);
            else if (name.Equals("backup.json", StringComparison.OrdinalIgnoreCase)) MergeBackupJson(file, dest);
            // alle anderen vorhandenen Dateien (z. B. settings.json) bleiben wie sie sind
        }
        foreach (var dir in Directory.GetDirectories(source))
            MergeDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    /// <summary>Vereinigt zwei backup.json. Bei gleichem Schluessel gewinnt source (aelter = echter Originalwert).</summary>
    private static void MergeBackupJson(string source, string target)
    {
        var src = JsonSerializer.Deserialize<Dictionary<string, ValueBackup>>(File.ReadAllText(source)) ?? new();
        var dst = JsonSerializer.Deserialize<Dictionary<string, ValueBackup>>(File.ReadAllText(target)) ?? new();
        foreach (var (k, v) in src) dst[k] = v;
        File.WriteAllText(target, JsonSerializer.Serialize(dst, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Einfaches Protokoll unter %LOCALAPPDATA%\BetterWindows\log.txt (u. a. fuer BCU-Fehlermeldungen).</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(BackupStore.Folder, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(BackupStore.Folder);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { /* Protokoll darf nie stoeren */ }
    }
}

public sealed record PsResult(int ExitCode, List<string> Lines);

/// <summary>Fuehrt Windows PowerShell 5.1 unsichtbar aus (fuer Appx und Wiederherstellungspunkte).</summary>
public static class PowerShell
{
    public static async Task<PsResult> RunAsync(string script, Action<string>? onLine = null)
    {
        string full = "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n$ProgressPreference = 'SilentlyContinue'\n"
                    + "& {\n" + script + "\n} *>&1 | ForEach-Object { \"$_\" }";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var psi = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var lines = new List<string>();
        void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#< CLIXML") || line.StartsWith("<Objs")) return;
            lock (lines) lines.Add(line);
            onLine?.Invoke(line);
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => Add(e.Data);
        p.ErrorDataReceived += (_, e) => Add(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return new PsResult(p.ExitCode, lines);
    }

    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}

public static class SystemTools
{
    public static void RunHidden(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(15000);
        }
        catch { /* best effort */ }
    }

    public static async Task RestartExplorerAsync()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { }
        }
        await Task.Delay(2500);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }

    public static void ShowInExplorer(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static Task<PsResult> CreateRestorePointAsync() => PowerShell.RunAsync("""
        $ErrorActionPreference = 'Stop'
        try {
            Enable-ComputerRestore -Drive "$env:SystemDrive\"
            $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $prev = (Get-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
            Checkpoint-Computer -Description ("BetterWindows " + (Get-Date -Format 'yyyy-MM-dd HH:mm')) -RestorePointType MODIFY_SETTINGS
            if ($null -eq $prev) { Remove-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency }
            else { Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value $prev -Type DWord }
            'OK'
        } catch { 'ERROR: ' + $_.Exception.Message }
        """);
}
