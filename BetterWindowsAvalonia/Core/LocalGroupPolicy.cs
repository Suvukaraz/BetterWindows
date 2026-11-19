using System.Diagnostics;
using System.Text;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Core;

/// <summary>
/// Setzt Computer-Richtlinien ueber die lokale Gruppenrichtlinie (wie gpedit.msc):
/// Eintrag in %SystemRoot%\System32\GroupPolicy\Machine\Registry.pol, Version in gpt.ini erhoehen, gpupdate.
/// Wird als Ausweichweg genutzt, wenn Windows den direkten Registry-Zugriff blockiert
/// (z. B. der Schutztreiber UCPD bei den Widgets-Richtlinien).
/// </summary>
public static class LocalGroupPolicy
{
    private const string RegistryCse = "{35378EAC-683F-11D2-A89A-00C04FBBCFA2}";
    private const string AdminTemplatesTool = "{D02B1F72-3407-48AE-BA88-E8213C6761F1}";
    private const uint RegDword = 4;

    private sealed record PolEntry(string Key, string Value, uint Type, byte[] Data);

    // Nur fuer den Selbsttest: auf eine Kopie umleiten und gpupdate auslassen
    internal static string? RootOverride { get; set; }
    internal static bool SkipRefresh { get; set; }

    private static string Root => RootOverride ?? Path.Combine(Environment.SystemDirectory, "GroupPolicy");
    private static string MachinePol => Path.Combine(Root, "Machine", "Registry.pol");
    private static string GptIni => Path.Combine(Root, "gpt.ini");

    /// <summary>Setzt einen DWORD-Richtlinienwert (key relativ zu HKLM, z. B. "SOFTWARE\Policies\Microsoft\Dsh").</summary>
    public static void SetDword(string key, string value, int data)
    {
        var entries = Read();
        entries.RemoveAll(e => Same(e, key, value));
        entries.Add(new PolEntry(key, value, RegDword, BitConverter.GetBytes(data)));
        Write(entries);
        BumpVersion();
        Refresh();
    }

    /// <summary>Entfernt einen Richtlinienwert (entspricht "Nicht konfiguriert" in gpedit).</summary>
    public static void Remove(string key, string value)
    {
        var entries = Read();
        if (entries.RemoveAll(e => Same(e, key, value)) == 0) return;
        Write(entries);
        BumpVersion();
        Refresh();
    }

    public static bool Contains(string key, string value) => Read().Any(e => Same(e, key, value));

    private static bool Same(PolEntry e, string key, string value) =>
        e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && e.Value.Equals(value, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ Registry.pol (Format "PReg", Version 1)

    private static List<PolEntry> Read()
    {
        var list = new List<PolEntry>();
        if (!File.Exists(MachinePol)) return list;
        var b = File.ReadAllBytes(MachinePol);
        if (b.Length < 8 || Encoding.ASCII.GetString(b, 0, 4) != "PReg")
            throw new InvalidDataException(T("Registry.pol has an unknown format.", "Registry.pol hat ein unbekanntes Format."));

        int i = 8;
        string ReadString()
        {
            int start = i;
            while (i + 1 < b.Length && !(b[i] == 0 && b[i + 1] == 0)) i += 2;
            string s = Encoding.Unicode.GetString(b, start, i - start);
            i += 2; // Null-Terminator
            return s;
        }
        void Expect(char c)
        {
            if (i + 1 >= b.Length || b[i] != (byte)c || b[i + 1] != 0)
                throw new InvalidDataException(T("Registry.pol is damaged.", "Registry.pol ist beschädigt."));
            i += 2;
        }

        while (i < b.Length)
        {
            Expect('[');
            string key = ReadString(); Expect(';');
            string value = ReadString(); Expect(';');
            uint type = BitConverter.ToUInt32(b, i); i += 4; Expect(';');
            uint size = BitConverter.ToUInt32(b, i); i += 4; Expect(';');
            var data = b.AsSpan(i, (int)size).ToArray(); i += (int)size;
            Expect(']');
            list.Add(new PolEntry(key, value, type, data));
        }
        return list;
    }

    private static void Write(List<PolEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MachinePol)!);
        // Sicherung der bisherigen Datei
        if (File.Exists(MachinePol))
        {
            var backupDir = Path.Combine(BackupStore.Folder, "GroupPolicy-Sicherungen");
            Directory.CreateDirectory(backupDir);
            File.Copy(MachinePol, Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_Registry.pol"), true);
        }

        using var ms = new MemoryStream();
        void Char(char c) { ms.WriteByte((byte)c); ms.WriteByte(0); }
        void Str(string s) { var bytes = Encoding.Unicode.GetBytes(s); ms.Write(bytes); ms.WriteByte(0); ms.WriteByte(0); }
        ms.Write(Encoding.ASCII.GetBytes("PReg"));
        ms.Write(BitConverter.GetBytes(1u));
        foreach (var e in entries)
        {
            Char('['); Str(e.Key); Char(';'); Str(e.Value); Char(';');
            ms.Write(BitConverter.GetBytes(e.Type)); Char(';');
            ms.Write(BitConverter.GetBytes((uint)e.Data.Length)); Char(';');
            ms.Write(e.Data); Char(']');
        }
        File.WriteAllBytes(MachinePol, ms.ToArray());
    }

    // ------------------------------------------------------------ gpt.ini + gpupdate

    private static void BumpVersion()
    {
        var lines = File.Exists(GptIni) ? File.ReadAllLines(GptIni).ToList() : ["[General]"];
        int vIdx = lines.FindIndex(l => l.StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
        long version = vIdx >= 0 && long.TryParse(lines[vIdx][8..], out var v) ? v : 0;
        // untere 16 Bit = Computer-Version
        long machine = (version & 0xFFFF) + 1, user = version >> 16;
        string newVersion = "Version=" + ((user << 16) | (machine & 0xFFFF));
        if (vIdx >= 0) lines[vIdx] = newVersion; else lines.Add(newVersion);

        int eIdx = lines.FindIndex(l => l.StartsWith("gPCMachineExtensionNames=", StringComparison.OrdinalIgnoreCase));
        if (eIdx < 0)
            lines.Insert(1, $"gPCMachineExtensionNames=[{RegistryCse}{AdminTemplatesTool}]");
        else if (!lines[eIdx].Contains(RegistryCse, StringComparison.OrdinalIgnoreCase))
            lines[eIdx] += $"[{RegistryCse}{AdminTemplatesTool}]";

        File.WriteAllLines(GptIni, lines, Encoding.ASCII);
    }

    private static void Refresh()
    {
        if (SkipRefresh) return;
        using var p = Process.Start(new ProcessStartInfo("gpupdate.exe", "/target:computer /force /wait:120")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (p == null) throw new InvalidOperationException(T("gpupdate could not be started.", "gpupdate konnte nicht gestartet werden."));
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(150_000);
    }
}
