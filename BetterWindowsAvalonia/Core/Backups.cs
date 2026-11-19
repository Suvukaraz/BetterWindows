using Microsoft.VisualBasic.FileIO;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Core;

/// <summary>Ein Ort, an dem BetterWindows Sicherungen oder Protokolle ablegt.</summary>
public sealed class BackupLocation
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    /// <summary>Ordner und/oder Dateien, die zu diesem Bereich gehoeren.</summary>
    public required string[] Paths { get; init; }
    /// <summary>Darf geleert werden (backup.json z. B. nicht – wird zum Zuruecksetzen gebraucht).</summary>
    public bool CanEmpty { get; init; } = true;
    public string? Warning { get; init; }

    public string OpenPath => Paths.FirstOrDefault(p => Directory.Exists(p) || File.Exists(p)) ?? BackupStore.Folder;

    public (int Files, long Bytes, DateTime? Newest) Measure()
    {
        int files = 0; long bytes = 0; DateTime? newest = null;
        foreach (var p in Paths)
        {
            IEnumerable<FileInfo> infos = Directory.Exists(p)
                ? new DirectoryInfo(p).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                : File.Exists(p) ? [new FileInfo(p)] : [];
            foreach (var f in infos)
            {
                files++;
                bytes += f.Length;
                if (newest == null || f.LastWriteTime > newest) newest = f.LastWriteTime;
            }
        }
        return (files, bytes, newest);
    }

    /// <summary>Verschiebt alle Inhalte in den Papierkorb (wiederherstellbar).</summary>
    public void EmptyToRecycleBin()
    {
        if (!CanEmpty) throw new InvalidOperationException(T("This item cannot be emptied.", "Dieser Bereich kann nicht geleert werden."));
        foreach (var p in Paths)
        {
            if (Directory.Exists(p))
                FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else if (File.Exists(p))
                FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }
}

public static class BackupLocations
{
    public static List<BackupLocation> All()
    {
        string root = BackupStore.Folder;
        return
        [
            new()
            {
                Title = T("Program leftovers", "Programm-Reste"),
                Description = T("Files, folders and registry keys that were backed up before removing leftovers of uninstalled programs.",
                                "Dateien, Ordner und Registry-Schlüssel, die vor dem Entfernen von Resten deinstallierter Programme gesichert wurden."),
                Glyph = "",
                Paths = [Path.Combine(root, "Deinstallations-Sicherungen")],
            },
            new()
            {
                Title = T("Context menu", "Kontextmenü"),
                Description = T("Registry exports (.reg) of removed context menu entries and removed “Send to” items.",
                                "Registry-Sicherungen (.reg) entfernter Kontextmenü-Einträge und entfernte „Senden an“-Einträge."),
                Glyph = "",
                Paths = [Path.Combine(root, "Kontextmenue-Sicherungen")],
            },
            new()
            {
                Title = T("Startup apps", "Autostart"),
                Description = T("Removed startup entries (registry values and shortcuts).",
                                "Entfernte Autostart-Einträge (Registry-Werte und Verknüpfungen)."),
                Glyph = "",
                Paths = [Path.Combine(root, "RemovedStartup"), Path.Combine(root, "removed-startup.json")],
            },
            new()
            {
                Title = T("Group Policy", "Gruppenrichtlinie"),
                Description = T("Copies of the local Group Policy file (Registry.pol) before BetterWindows changed it.",
                                "Kopien der lokalen Gruppenrichtlinien-Datei (Registry.pol), bevor BetterWindows sie geändert hat."),
                Glyph = "",
                Paths = [Path.Combine(root, "GroupPolicy-Sicherungen")],
            },
            new()
            {
                Title = T("Log", "Protokoll"),
                Description = T("Error and warning messages of BetterWindows.", "Fehler- und Warnmeldungen von BetterWindows."),
                Glyph = "",
                Paths = [AppLog.FilePath],
            },
            new()
            {
                Title = T("Undo data for optimizations", "Rückgängig-Daten der Optimierungen"),
                Description = T("Original values from before each optimization. Needed to restore them exactly – therefore it cannot be emptied here.",
                                "Ursprüngliche Werte vor jeder Optimierung. Werden zum exakten Zurücksetzen gebraucht – deshalb hier nicht leerbar."),
                Glyph = "",
                Paths = [Path.Combine(root, "backup.json")],
                CanEmpty = false,
            },
        ];
    }
}
