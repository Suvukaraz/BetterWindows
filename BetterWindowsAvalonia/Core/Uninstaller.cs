using Klocman.IO;
using UninstallTools;
using UninstallTools.Factory;
using UninstallTools.Junk;
using UninstallTools.Junk.Confidence;
using UninstallTools.Junk.Containers;
using UninstallTools.Uninstaller;
using Avalonia.Media.Imaging;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Core;

// WICHTIG: Diese beiden Anzeige-Klassen duerfen KEINE BCU-Typen in Signaturen enthalten
// (auch nicht internal/private). Der XAML-Compiler reflektiert sie und kann die
// BCU-Assemblies nicht aufloesen -> "WMC9999 Xaml Internal Error".
// Der BCU-Eintrag steckt deshalb untypisiert in Handle; Zugriff ueber UninstallerService.Entry()/Junk().

/// <summary>Ein installiertes Programm (fuer die Oberflaeche aufbereitet).</summary>
public sealed class InstalledProgram : ObservableObject
{
    internal object Handle { get; set; } = null!;

    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public long SizeKb { get; set; }
    public string SizeText { get; set; } = "";
    public DateTime InstallDate { get; set; }
    public bool IsSystem { get; set; }
    public bool IsUpdate { get; set; }
    public string Subtitle { get; set; } = "";
    public string Tags { get; set; } = "";
    public string ToolTipText { get; set; } = "";

    /// <summary>Erscheint in der Ansicht "Mittel" (keine Systemkomponenten, Updates, geschuetzten Eintraege).</summary>
    public bool InMediumView { get; set; }
    /// <summary>Erscheint in der Ansicht "Einfach" (wie "Programme und Features" von Windows, ohne Laufzeitbibliotheken/Treiber).</summary>
    public bool InSimpleView { get; set; }

    // Darstellung je nach Ansicht (wird beim Filtern gesetzt)
    private bool _showCheckBox, _showDetails, _showUninstallButton;
    public bool ShowCheckBox { get => _showCheckBox; set => Set(ref _showCheckBox, value); }
    public bool ShowDetails { get => _showDetails; set => Set(ref _showDetails, value); }
    public bool ShowUninstallButton { get => _showUninstallButton; set => Set(ref _showUninstallButton, value); }

    private bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => Set(ref _icon, value); }

    // Name bewusst nicht "Status": kollidiert sonst im XAML-Compiler mit x:Name="Status" der Seite
    private string _runState = "";
    public string RunState { get => _runState; set => Set(ref _runState, value); }
}

/// <summary>Ein gefundener Rest (Datei, Ordner, Registry-Eintrag, Autostart ...).</summary>
public sealed class LeftoverItem : ObservableObject
{
    internal object Handle { get; set; } = null!;

    public string Path { get; set; } = "";
    public string Category { get; set; } = "";
    public string AppName { get; set; } = "";
    public int Raw { get; set; }
    public string LevelText { get; set; } = "";
    public bool IsSafe { get; set; }
    public string Details { get; set; } = "";
    public string Subtitle => $"{Category}  ·  {AppName}";

    private bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>Bruecke zu den UninstallTools aus Bulk Crap Uninstaller.</summary>
public static class UninstallerService
{
    private static bool _configured;

    private static void Configure()
    {
        if (_configured) return;
        _configured = true;
        UninstallToolsGlobalConfig.EnableAppInfoCache = false;
        UninstallToolsGlobalConfig.AutoDetectCustomProgramFiles = true;  // auch "Program Files"-Ordner auf anderen Laufwerken
        UninstallToolsGlobalConfig.AutoDetectScanRemovable = false;
        UninstallToolsGlobalConfig.QuietAutomatization = false;          // UninstallerAutomatizer ist nicht enthalten
        UninstallToolsGlobalConfig.ScanOculus = false;                   // Helfer nicht enthalten
        UninstallToolsGlobalConfig.ScanWinUpdates = false;               // Helfer nicht enthalten
        UninstallToolsGlobalConfig.ScanWinFeatures = false;              // Windows-Features gehoeren nicht in einen Programm-Deinstaller
        UninstallToolsGlobalConfig.ScanStoreApps = true;
        UninstallToolsGlobalConfig.ScanSteam = true;
    }

    public static List<InstalledProgram> Load(Action<string, double>? progress = null)
    {
        Configure();
        var entries = ApplicationUninstallerFactory.GetUninstallerEntries(p =>
        {
            var msg = p.Inner?.Message is { Length: > 0 } inner ? $"{p.Message} – {inner}" : p.Message;
            progress?.Invoke(msg ?? "", p.TotalCount > 0 ? Math.Clamp((double)p.CurrentCount / p.TotalCount, 0, 1) : -1);
        });
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.DisplayName))
            .Select(ToProgram)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static ApplicationUninstallerEntry Entry(this InstalledProgram p) => (ApplicationUninstallerEntry)p.Handle;
    private static IJunkResult Junk(this LeftoverItem l) => (IJunkResult)l.Handle;

    // Laufzeitbibliotheken, Treiber und Entwickler-Komponenten: in der einfachen Ansicht ausgeblendet
    private static readonly System.Text.RegularExpressions.Regex RuntimePattern = new(
        @"redistributable|runtime|\.net (framework|core|desktop|host)|windows (sdk|software development kit)|webview2|visual c\+\+|vc_?redist|" +
        @"directx|vulkan|physx|driver|treiber|chipset|update health|bonjour|msxml|windows app runtime|windowsappruntime|" +
        @"application verifier|kits configuration|universal crt|sql server.*(native client|odbc|lp)|\bodbc\b|launcher prerequisites|" +
        @"ms[- ]?teams[- ]?meeting add-?in|edge webview|microsoft update|gaming services|" +
        @"\bsdk\b|gameinput|openal|redist|operating system|integritätsprüfung|pc health check|installationsassistent|installation assistant|" +
        @"tap-windows|winpcap|npcap|epic online services|^branding$|pawnio|xbox service|anti-?cheat|vigem|helper compact",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Interne Komponenten-Kennungen (z. B. Adobe "ACR_11_0_32", "CDEC_1_0_0")
    private static readonly System.Text.RegularExpressions.Regex ComponentCodePattern = new(
        @"^[A-Za-z]+(_[A-Za-z0-9]+){2,}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static InstalledProgram ToProgram(ApplicationUninstallerEntry e)
    {
        string name = e.DisplayName.Trim();
        string publisher = e.Publisher?.Trim() ?? "", version = e.DisplayVersion?.Trim() ?? "";
        string date = e.InstallDate.Year > 1990 ? e.InstallDate.ToString("d") : "";
        long kb = e.EstimatedSize.GetKbSize();

        var tags = new List<string>();
        var kind = KindName(e.UninstallerKind);
        if (kind != "Unknown") tags.Add(kind);
        if (e.QuietUninstallPossible) tags.Add(T("Silent possible", "Still möglich"));
        if (e.SystemComponent) tags.Add(T("System component", "Systemkomponente"));
        if (e.IsUpdate) tags.Add("Update");
        if (e.IsProtected) tags.Add(T("Protected", "Geschützt"));
        if (e.IsOrphaned) tags.Add(T("Orphaned", "Verwaist"));
        if (!e.IsValid) tags.Add(T("No uninstaller", "Kein Deinstaller"));

        bool isStoreApp = e.UninstallerKind == UninstallerType.StoreApp;
        bool isSteam = e.UninstallerKind == UninstallerType.Steam;
        bool isRuntime = RuntimePattern.IsMatch(name) || ComponentCodePattern.IsMatch(name);
        bool guidName = name.StartsWith('{') && Guid.TryParse(name, out _);
        bool inMedium = !e.SystemComponent && !e.IsUpdate && !e.IsProtected && !guidName;
        bool inSimple = inMedium && e.IsValid && !isRuntime && !isStoreApp && !guidName
                        && (e.IsRegistered || isSteam)
                        && e.UninstallerKind is not (UninstallerType.WindowsFeature or UninstallerType.WindowsUpdate or UninstallerType.SdbInst);

        return new InstalledProgram
        {
            Handle = e,
            Name = name,
            Publisher = publisher,
            Version = version,
            SizeKb = kb,
            SizeText = kb > 0 ? e.EstimatedSize.ToString() : "",
            InstallDate = e.InstallDate,
            IsSystem = e.SystemComponent,
            IsUpdate = e.IsUpdate,
            InMediumView = inMedium,
            InSimpleView = inSimple,
            Subtitle = string.Join("  ·  ", new[] { publisher, version.Length > 0 ? "Version " + version : "", date }.Where(s => s.Length > 0)),
            Tags = string.Join("  ·  ", tags),
            ToolTipText = e.UninstallString ?? e.InstallLocation ?? e.DisplayName,
        };
    }

    private static LeftoverItem ToLeftover(IJunkResult j)
    {
        static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
        var level = j.Confidence.GetConfidence();
        string path = Safe(j.GetDisplayName) ?? "?";
        return new LeftoverItem
        {
            Handle = j,
            Path = path,
            Category = Safe(() => j.Source?.CategoryName) ?? T("Other", "Sonstiges"),
            AppName = Safe(() => j.Application?.DisplayName) ?? "",
            Raw = j.Confidence.GetRawConfidence(),
            IsSafe = level >= ConfidenceLevel.Good,
            IsSelected = level >= ConfidenceLevel.Good,
            Details = Safe(j.ToLongString) ?? path,
            LevelText = level switch
            {
                ConfidenceLevel.VeryGood => T("Very safe", "Sehr sicher"),
                ConfidenceLevel.Good => T("Safe", "Sicher"),
                ConfidenceLevel.Questionable => T("Questionable", "Fraglich"),
                ConfidenceLevel.Bad => T("Risky", "Riskant"),
                _ => T("Unknown", "Unbekannt")
            }
        };
    }

    /// <summary>Programm-Icon als PNG (fuer die Oberflaeche), null wenn keins vorhanden.</summary>
    public static byte[]? GetIconPng(InstalledProgram p)
    {
        try
        {
            using var icon = p.Entry().GetIcon();
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public static string KindName(UninstallerType kind) => kind switch
    {
        UninstallerType.Msiexec => "MSI",
        UninstallerType.Nsis => "NSIS",
        UninstallerType.InnoSetup => "Inno Setup",
        UninstallerType.InstallShield => "InstallShield",
        UninstallerType.Steam => "Steam",
        UninstallerType.StoreApp => "Store-App",
        UninstallerType.SimpleDelete => T("Delete folder", "Ordner löschen"),
        UninstallerType.Chocolatey => "Chocolatey",
        UninstallerType.PowerShell => "PowerShell",
        UninstallerType.SdbInst => T("Compatibility DB", "Kompatibilitäts-DB"),
        _ => kind.ToString()
    };

    public static string StatusName(UninstallStatus s) => s switch
    {
        UninstallStatus.Uninstalling => T("Uninstalling …", "Wird deinstalliert …"),
        UninstallStatus.Waiting => T("Waiting", "Wartet"),
        UninstallStatus.Completed => T("Done", "Fertig"),
        UninstallStatus.Failed => T("Failed", "Fehlgeschlagen"),
        UninstallStatus.Skipped => T("Skipped", "Übersprungen"),
        UninstallStatus.Protected => T("Protected – skipped", "Geschützt – übersprungen"),
        UninstallStatus.Invalid => T("No uninstaller – leftovers only", "Kein Deinstaller – nur Reste"),
        UninstallStatus.Paused => T("Paused", "Pausiert"),
        _ => s.ToString()
    };

    public sealed record UninstallOutcome(int Completed, int Failed, List<InstalledProgram> LeftoverTargets);

    /// <summary>Fuehrt die Deinstallationen nacheinander aus (BCU BulkUninstallTask). Setzt InstalledProgram.Status laufend
    /// (onUi wird fuer jede Statusaktualisierung aufgerufen und muss auf den UI-Thread wechseln).</summary>
    public static async Task<UninstallOutcome> UninstallAsync(IReadOnlyList<InstalledProgram> targets, bool preferQuiet,
        Action<Action> onUi, CancellationToken cancel)
    {
        // Reihenfolge wie in BCU: richtige Deinstaller zuerst, "Ordner loeschen" zuletzt, laute vor stillen
        var ordered = targets
            .OrderBy(t => t.Entry().UninstallerKind == UninstallerType.SimpleDelete)
            .ThenBy(t => preferQuiet && t.Entry().QuietUninstallPossible)
            .ThenBy(t => t.IsUpdate)
            .ThenBy(t => t.IsSystem)
            .ToList();
        var byEntry = ordered.ToDictionary(t => t.Entry());

        var list = ordered.Select(t => new BulkUninstallEntry(t.Entry(), preferQuiet && t.Entry().QuietUninstallPossible,
            !t.Entry().IsValid ? UninstallStatus.Invalid
            : t.Entry().IsProtected ? UninstallStatus.Protected
            : UninstallStatus.Waiting)).ToList();

        void Report()
        {
            var snapshot = list.Select(r => (byEntry[r.UninstallerEntry], StatusName(r.CurrentStatus))).ToList();
            onUi(() => { foreach (var (p, s) in snapshot) p.RunState = s; });
        }

        var config = new BulkUninstallConfiguration(ignoreProtection: false, preferQuiet: preferQuiet, simulate: false,
            autoKillStuckQuiet: true, retryFailedQuiet: true);
        var task = UninstallManager.CreateBulkUninstallTask(list, config);
        task.OneLoudLimit = true;
        task.ConcurrentUninstallerCount = 1;
        task.Start();

        while (!task.Finished)
        {
            if (cancel.IsCancellationRequested) task.Aborted = true;
            Report();
            await Task.Delay(700);
        }
        Report();

        // Welche Programme danach nach Resten durchsucht werden (wie BCU)
        var leftoverTargets = list
            .Where(r => r.CurrentStatus is UninstallStatus.Completed or UninstallStatus.Invalid
                        || (r.CurrentStatus == UninstallStatus.Skipped && !r.UninstallerEntry.RegKeyStillExists()))
            .Select(r => byEntry[r.UninstallerEntry]).ToList();

        return new UninstallOutcome(
            list.Count(r => r.CurrentStatus == UninstallStatus.Completed),
            list.Count(r => r.CurrentStatus == UninstallStatus.Failed),
            leftoverTargets);
    }

    public static List<LeftoverItem> FindLeftovers(IEnumerable<InstalledProgram> targets,
        IEnumerable<InstalledProgram> all, Action<string>? progress = null)
    {
        Configure();
        var stillInstalled = all.Select(p => p.Entry())
            .Where(e => { try { return e.RegKeyStillExists(); } catch { return false; } }).ToList();
        return JunkManager.FindJunk(targets.Select(t => t.Entry()), stillInstalled, p =>
            {
                var msg = p.Inner?.Message is { Length: > 0 } inner ? $"{p.Message}: {inner}" : p.Message;
                progress?.Invoke(msg ?? "");
            })
            .Select(ToLeftover)
            .OrderByDescending(i => i.Raw)
            .ToList();
    }

    public sealed record RemoveResult(int Removed, List<string> Errors, string BackupDir);

    /// <summary>Sichert die Reste und loescht sie danach (Reihenfolge wie BCU).</summary>
    public static RemoveResult RemoveLeftovers(IReadOnlyList<LeftoverItem> items, Action<int, int>? progress = null)
    {
        var dir = System.IO.Path.Combine(BackupStore.Folder, "Deinstallations-Sicherungen", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(dir);
        var errors = new List<string>();
        var deletable = new List<LeftoverItem>();

        foreach (var item in items)
        {
            try { item.Junk().Backup(dir); deletable.Add(item); }
            catch (Exception ex) { errors.Add(T("Not backed up, so not deleted: ", "Nicht gesichert, daher nicht gelöscht: ") + $"{item.Path} ({ex.Message})"); }
        }

        // Erst Befehle ausfuehren, dann Dienste/Autostart entfernen, dann Dateien und Registry
        var sorted = deletable.OrderByDescending(i => i.Junk() is RunProcessJunk)
                              .ThenByDescending(i => i.Junk() is StartupJunkNode).ToList();
        int removed = 0, n = 0;
        foreach (var item in sorted)
        {
            progress?.Invoke(++n, sorted.Count);
            try { item.Junk().Delete(); removed++; }
            catch (Exception ex) { errors.Add($"{item.Path}: {ex.Message}"); }
        }
        return new RemoveResult(removed, errors, dir);
    }

    public static string FormatKb(long kb) => kb <= 0 ? "" : FileSize.FromKilobytes(kb).ToString();
}
