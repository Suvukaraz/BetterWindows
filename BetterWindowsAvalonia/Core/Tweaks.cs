using Microsoft.Win32;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Core;

public abstract class TweakAction
{
    public abstract bool NeedsAdmin { get; }
    public virtual bool IsAvailable => true;
    public abstract bool IsApplied();
    public abstract void Apply();
    public abstract void Revert();
    public abstract string Describe();
}

/// <summary>Setzt einen Registry-Wert. Der Originalwert wird gesichert und beim Zuruecksetzen wiederhergestellt.</summary>
public sealed class RegAction(string path, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord) : TweakAction
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public object Value { get; } = value;
    public RegistryValueKind Kind { get; } = kind;

    /// <summary>Wert beim Zuruecksetzen ohne Backup (null = Wert loeschen = Windows-Standard).</summary>
    public object? DefaultValue { get; init; }

    /// <summary>Schluessel, der beim Zuruecksetzen komplett entfernt wird, wenn er vorher nicht existierte.</summary>
    public string? DeleteKeyOnRevert { get; init; }

    private string BackupKey => $"reg|{Path}|{Name}";
    public override bool NeedsAdmin => Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
    public override string Describe() => $"{Path}\\{Name} = {Value}";

    public override bool IsApplied() => Reg.TryGet(Path, Name, out var cur, out _) && Reg.ValueEquals(cur, Value, Kind);

    // Computer-Richtlinien (HKLM\SOFTWARE\Policies) koennen notfalls ueber die lokale Gruppenrichtlinie gesetzt werden
    private bool IsMachinePolicy => Path.StartsWith(@"HKLM\SOFTWARE\Policies\", StringComparison.OrdinalIgnoreCase) && Kind == RegistryValueKind.DWord;
    private string PolicyKey => Path[5..]; // ohne "HKLM\"
    private string GpoMarker => BackupKey + "|gpo";

    public override void Apply()
    {
        if (IsApplied()) return;
        if (Reg.TryGet(Path, Name, out var old, out var oldKind))
            BackupStore.RememberIfAbsent(BackupKey, new ValueBackup(true, BackupStore.Encode(old, oldKind), oldKind.ToString()));
        else
            BackupStore.RememberIfAbsent(BackupKey, new ValueBackup(false, null, null));
        try
        {
            Reg.Set(Path, Name, Value, Kind);
        }
        catch (UnauthorizedAccessException) when (IsMachinePolicy && Admin.IsElevated)
        {
            // Windows blockiert den direkten Zugriff (z. B. UCPD) -> offizieller Weg ueber die Gruppenrichtlinie
            AppLog.Write($"Direct write blocked, using local Group Policy: {Path}\\{Name}");
            LocalGroupPolicy.SetDword(PolicyKey, Name, Convert.ToInt32(Value));
            BackupStore.RememberIfAbsent(GpoMarker, new ValueBackup(true, "gpo", "gpo"));
            if (!IsApplied())
                throw new InvalidOperationException(T("The Group Policy was set, but Windows has not applied it yet. Please restart the PC.",
                                                      "Die Gruppenrichtlinie wurde gesetzt, aber von Windows noch nicht übernommen. Bitte den PC neu starten."));
        }
    }

    public override void Revert()
    {
        // Ueber die Gruppenrichtlinie gesetzt (von uns oder z. B. per gpedit)? Dann dort entfernen.
        if (IsMachinePolicy && Admin.IsElevated && (BackupStore.Take(GpoMarker) != null || SafeContains()))
        {
            BackupStore.Take(BackupKey);
            LocalGroupPolicy.Remove(PolicyKey, Name);
            return;
        }

        var b = BackupStore.Take(BackupKey);
        if (b != null)
        {
            if (b.Existed && b.Value != null && b.Kind != null)
            {
                var k = Enum.Parse<RegistryValueKind>(b.Kind);
                Reg.Set(Path, Name, BackupStore.Decode(b.Value, k), k);
            }
            else Reg.Delete(Path, Name);
        }
        else if (DefaultValue != null) Reg.Set(Path, Name, DefaultValue, Kind);
        else Reg.Delete(Path, Name);

        if (DeleteKeyOnRevert != null && (b == null || !b.Existed))
            Reg.DeleteKeyTree(DeleteKeyOnRevert);
    }

    private bool SafeContains()
    {
        try { return LocalGroupPolicy.Contains(PolicyKey, Name); } catch { return false; }
    }
}

/// <summary>Aendert den Starttyp eines Dienstes (2 = Automatisch, 3 = Manuell, 4 = Deaktiviert).</summary>
public sealed class ServiceAction(string service, int start, int windowsDefault) : TweakAction
{
    private readonly RegAction _reg = new($@"HKLM\SYSTEM\CurrentControlSet\Services\{service}", "Start", start)
    {
        DefaultValue = windowsDefault
    };

    public override bool NeedsAdmin => true;
    public override bool IsAvailable => Reg.KeyExists(_reg.Path);
    public override string Describe() => T($"Service {service} -> start type {start}", $"Dienst {service} -> Starttyp {start}");
    public override bool IsApplied() => !IsAvailable || _reg.IsApplied();

    public override void Apply()
    {
        _reg.Apply();
        if (start == 4) SystemTools.RunHidden("sc.exe", $"stop {service}");
    }

    public override void Revert() => _reg.Revert();
}

/// <summary>Deaktiviert eine geplante Aufgabe (ueber die Aufgabenplanung-COM-API).</summary>
public sealed class TaskAction(string folder, string name) : TweakAction
{
    private string BackupKey => $"task|{folder}|{name}";
    public override bool NeedsAdmin => true;
    public override bool IsAvailable => GetTask() != null;
    public override string Describe() => T($"Disable task {folder}{name}", $"Aufgabe {folder}{name} deaktivieren");

    private dynamic? GetTask()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return null;
            dynamic svc = Activator.CreateInstance(type)!;
            svc.Connect();
            string f = folder.TrimEnd('\\');
            dynamic dir = svc.GetFolder(f.Length == 0 ? "\\" : f);
            return dir.GetTask(name);
        }
        catch { return null; }
    }

    public override bool IsApplied()
    {
        var t = GetTask();
        return t is null || !(bool)t!.Enabled;
    }

    public override void Apply()
    {
        var t = GetTask();
        if (t is null || !(bool)t!.Enabled) return;
        BackupStore.RememberIfAbsent(BackupKey, new ValueBackup(true, "enabled", "task"));
        t!.Enabled = false;
    }

    public override void Revert()
    {
        // Nur wieder aktivieren, wenn die App sie selbst deaktiviert hat.
        if (BackupStore.Take(BackupKey) == null) return;
        var t = GetTask();
        if (t != null) t.Enabled = true;
    }
}

public sealed class Tweak : ObservableObject
{
    public string Id { get; init; } = "";
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool Recommended { get; init; } = true;
    public string? Hint { get; init; }
    public required TweakAction[] Actions { get; init; }

    public bool NeedsAdmin => Actions.Any(a => a.NeedsAdmin);

    private bool _isApplied;
    public bool IsApplied { get => _isApplied; set => Set(ref _isApplied, value); }

    public bool ComputeApplied()
    {
        var available = Actions.Where(a => a.IsAvailable).ToArray();
        return available.Length > 0 && available.All(a => a.IsApplied());
    }

    public List<string> Apply() => Run(Actions, a => a.Apply());
    public List<string> Revert() => Run(Enumerable.Reverse(Actions), a => a.Revert());

    private static List<string> Run(IEnumerable<TweakAction> actions, Action<TweakAction> op)
    {
        var errors = new List<string>();
        foreach (var a in actions)
        {
            if (!a.IsAvailable) continue;
            try { op(a); }
            catch (UnauthorizedAccessException) { errors.Add($"{a.Describe()}: {T("access denied", "Zugriff verweigert")}"); }
            catch (Exception ex) { errors.Add($"{a.Describe()}: {ex.Message}"); }
        }
        return errors;
    }
}

public static class TweakCatalog
{
    public const string ClassicContextMenuId = "classic-context-menu";

    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string CloudPol = @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";
    private const string SearchPol = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";
    private const string DataPol = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string SysPol = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string EdgePol = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string ClassicMenuKey = @"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private static RegAction Cu(string sub, string name, int v) => new($@"HKCU\{sub}", name, v);
    private static RegAction Lm(string sub, string name, int v) => new($@"HKLM\{sub}", name, v);

    public static string Ads => T("Ads & suggestions", "Werbung & Vorschläge");
    public static string Search => T("Search", "Suche");
    public static string Privacy => T("Privacy & telemetry", "Datenschutz & Telemetrie");
    public static string Edge => "Microsoft Edge";
    public static string Speed => T("Responsiveness", "Bediengefühl");

    public static string[] Categories => [Ads, Search, Privacy, Edge, Speed];

    public static List<Tweak> Create() =>
    [
        // ---------------- Werbung ----------------
        new() { Category = Ads,
            Title = T("No ad apps or silent installs", "Keine Werbe-Apps und stillen Installationen"),
            Description = T("Stops Windows from installing games and promo apps (e.g. Candy Crush) without asking.",
                            "Verhindert, dass Windows ungefragt Spiele und Promo-Apps (z. B. Candy Crush) installiert."),
            Actions = [ Cu(Cdm, "SilentInstalledAppsEnabled", 0), Cu(Cdm, "PreInstalledAppsEnabled", 0),
                        Cu(Cdm, "OemPreInstalledAppsEnabled", 0), Lm(CloudPol, "DisableWindowsConsumerFeatures", 1) ] },
        new() { Category = Ads,
            Title = T("No suggestions in the Start menu", "Keine Vorschläge im Startmenü"),
            Description = T("Removes app suggestions and recommendations for tips, shortcuts and new apps.",
                            "Entfernt App-Vorschläge und Empfehlungen für Tipps, Verknüpfungen und neue Apps."),
            Actions = [ Cu(Cdm, "SystemPaneSuggestionsEnabled", 0), Cu(Cdm, "SubscribedContent-338388Enabled", 0),
                        Cu(Adv, "Start_IrisRecommendations", 0) ] },
        new() { Category = Ads,
            Title = T("No account ads in the Start menu", "Keine Konto-Werbung im Startmenü"),
            Description = T("Hides Microsoft 365, OneDrive and Microsoft account notices next to your profile picture.",
                            "Blendet Hinweise zu Microsoft 365, OneDrive und Microsoft-Konto am Benutzerbild aus."),
            Actions = [ Cu(Adv, "Start_AccountNotifications", 0), Lm(CloudPol, "DisableConsumerAccountStateContent", 1) ] },
        new() { Category = Ads,
            Title = T("No tips, tricks or welcome pages", "Keine Tipps, Tricks und Willkommensseiten"),
            Description = T("No tip notifications, no “Welcome” after updates, no “Finish setting up your device” page.",
                            "Keine Tipp-Benachrichtigungen, kein „Willkommen“ nach Updates, keine „Einrichtung abschließen“-Seite."),
            Actions = [ Cu(Cdm, "SoftLandingEnabled", 0), Cu(Cdm, "SubscribedContent-338389Enabled", 0),
                        Cu(Cdm, "SubscribedContent-310093Enabled", 0),
                        Cu(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
                        Cu(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0) ] },
        new() { Category = Ads,
            Title = T("Lock screen without ad texts", "Sperrbildschirm ohne Werbetexte"),
            Description = T("Removes fun facts, tips and ads from the lock screen. The background image stays.",
                            "Entfernt „Wissenswertes“, Tipps und Werbung vom Sperrbildschirm. Das Hintergrundbild bleibt."),
            Actions = [ Cu(Cdm, "RotatingLockScreenOverlayEnabled", 0), Cu(Cdm, "SubscribedContent-338387Enabled", 0) ] },
        new() { Category = Ads,
            Title = T("No suggested content in Settings", "Keine empfohlenen Inhalte in den Einstellungen"),
            Description = T("Removes promo tiles and suggestions from the Settings app.",
                            "Entfernt Werbekacheln und Vorschläge aus der Einstellungen-App."),
            Actions = [ Cu(Cdm, "SubscribedContent-338393Enabled", 0), Cu(Cdm, "SubscribedContent-353694Enabled", 0),
                        Cu(Cdm, "SubscribedContent-353696Enabled", 0), Cu(Cdm, "SubscribedContent-353698Enabled", 0) ] },
        new() { Category = Ads,
            Title = T("No ads in File Explorer", "Keine Werbung im Explorer"),
            Description = T("Hides OneDrive and Microsoft 365 notices in File Explorer.",
                            "Blendet OneDrive- und Microsoft-365-Hinweise im Datei-Explorer aus."),
            Actions = [ Cu(Adv, "ShowSyncProviderNotifications", 0) ] },
        new() { Category = Ads,
            Title = T("Turn off advertising ID", "Werbe-ID deaktivieren"),
            Description = T("Apps can no longer track you with an advertising ID for personalized ads.",
                            "Apps können dich nicht mehr über eine Werbe-ID für personalisierte Werbung verfolgen."),
            Actions = [ Cu(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
                        Lm(@"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1) ] },
        new() { Category = Ads,
            Title = T("No “tailored experiences”", "Keine „maßgeschneiderten Erfahrungen“"),
            Description = T("Microsoft does not use your diagnostic data for personalized tips and offers.",
                            "Microsoft nutzt deine Diagnosedaten nicht für personalisierte Tipps und Angebote."),
            Actions = [ Cu(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
                        Cu(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
                        Lm(CloudPol, "DisableCloudOptimizedContent", 1) ] },
        new() { Category = Ads,
            Title = T("Turn off Widgets (news feed)", "Widgets (News-Feed) deaktivieren"),
            Description = T("Removes the Widgets board with MSN news and ads from the taskbar.",
                            "Entfernt das Widgets-Menü mit MSN-Nachrichten und Werbung aus der Taskleiste."),
            Hint = T("Windows protects the per-user widget setting, so this only works system-wide (administrator rights). Takes effect after restarting Explorer.",
                     "Windows schützt die Widget-Einstellung pro Benutzer, daher geht das nur systemweit (Administratorrechte). Wirkt nach einem Explorer-Neustart."),
            Actions = [ Lm(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0) ] },

        // ---------------- Suche ----------------
        new() { Category = Search,
            Title = T("Start menu search without Bing", "Startmenü-Suche ohne Bing"),
            Description = T("Search only finds local apps, files and settings. No web search in the background.",
                            "Die Suche findet nur noch lokale Apps, Dateien und Einstellungen. Keine Websuche im Hintergrund."),
            Actions = [ Cu(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                        Cu(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
                        Lm(SearchPol, "DisableWebSearch", 1), Lm(SearchPol, "ConnectedSearchUseWeb", 0),
                        Lm(SearchPol, "AllowCortana", 0) ] },
        new() { Category = Search,
            Title = T("No search highlights", "Keine Suchhighlights"),
            Description = T("Removes daily topics, images and trends from the search box.",
                            "Entfernt Tagesthemen, Bilder und Trends aus dem Suchfeld."),
            Actions = [ Cu(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0),
                        Lm(SearchPol, "EnableDynamicContentInWSB", 0) ] },

        // ---------------- Datenschutz ----------------
        new() { Category = Privacy,
            Title = T("Diagnostic data to minimum", "Diagnosedaten auf Minimum"),
            Description = T("Telemetry reduced to required data, telemetry service (DiagTrack) off. Windows Update keeps working.",
                            "Telemetrie auf die erforderlichen Daten reduziert, Telemetrie-Dienst (DiagTrack) aus. Windows Update funktioniert weiter."),
            Actions = [ Lm(DataPol, "AllowTelemetry", 0), Lm(DataPol, "LimitDiagnosticLogCollection", 1),
                        Lm(DataPol, "DisableDiagnosticDataViewer", 1),
                        new ServiceAction("DiagTrack", 4, 2), new ServiceAction("dmwappushservice", 4, 3) ] },
        new() { Category = Privacy,
            Title = T("Turn off telemetry tasks", "Telemetrie-Aufgaben deaktivieren"),
            Description = T("Disables scheduled tasks that collect and send compatibility and usage data.",
                            "Schaltet geplante Aufgaben ab, die Kompatibilitäts- und Nutzungsdaten sammeln und senden."),
            Actions = [ new TaskAction(@"\Microsoft\Windows\Application Experience\", "Microsoft Compatibility Appraiser"),
                        new TaskAction(@"\Microsoft\Windows\Application Experience\", "Microsoft Compatibility Appraiser Exp"),
                        new TaskAction(@"\Microsoft\Windows\Application Experience\", "ProgramDataUpdater"),
                        new TaskAction(@"\Microsoft\Windows\Application Experience\", "MareBackup"),
                        new TaskAction(@"\Microsoft\Windows\Autochk\", "Proxy"),
                        new TaskAction(@"\Microsoft\Windows\Customer Experience Improvement Program\", "Consolidator"),
                        new TaskAction(@"\Microsoft\Windows\Customer Experience Improvement Program\", "UsbCeip"),
                        new TaskAction(@"\Microsoft\Windows\DiskDiagnostic\", "Microsoft-Windows-DiskDiagnosticDataCollector"),
                        new TaskAction(@"\Microsoft\Windows\Feedback\Siuf\", "DmClient"),
                        new TaskAction(@"\Microsoft\Windows\Feedback\Siuf\", "DmClientOnScenarioDownload") ] },
        new() { Category = Privacy,
            Title = T("No feedback requests", "Keine Feedback-Abfragen"),
            Description = T("Windows no longer asks for ratings and feedback.",
                            "Windows fragt nicht mehr nach Bewertungen und Feedback."),
            Actions = [ Lm(DataPol, "DoNotShowFeedbackNotifications", 1), Cu(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0) ] },
        new() { Category = Privacy,
            Title = T("Activity history off", "Aktivitätsverlauf aus"),
            Description = T("Opened apps and files are neither recorded nor uploaded.",
                            "Geöffnete Apps und Dateien werden nicht protokolliert und nicht hochgeladen."),
            Actions = [ Lm(SysPol, "EnableActivityFeed", 0), Lm(SysPol, "PublishUserActivities", 0), Lm(SysPol, "UploadUserActivities", 0) ] },
        new() { Category = Privacy,
            Title = T("Do not collect typing and inking data", "Keine Eingabe- und Freihanddaten sammeln"),
            Description = T("Typed text and handwriting are not sent to Microsoft “to improve” products.",
                            "Getippter Text und Handschrift werden nicht zur „Verbesserung“ an Microsoft gesendet."),
            Actions = [ Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                        Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                        Cu(@"Software\Microsoft\Input\TIPC", "Enabled", 0) ] },
        new() { Category = Privacy,
            Title = T("Do not send language list to websites", "Sprachliste nicht an Websites senden"),
            Description = T("Websites do not get your installed languages for personalization.",
                            "Websites erhalten deine installierten Sprachen nicht zur Personalisierung."),
            Actions = [ Cu(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1) ] },
        new() { Category = Privacy,
            Title = T("Turn off Copilot", "Copilot deaktivieren"),
            Description = T("Turns off Windows Copilot and removes its taskbar button.",
                            "Schaltet Windows Copilot ab und entfernt die Schaltfläche aus der Taskleiste."),
            Actions = [ Cu(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                        Lm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                        Cu(Adv, "ShowCopilotButton", 0) ] },
        new() { Category = Privacy,
            Title = T("Turn off Recall", "Recall deaktivieren"),
            Description = T("Stops Windows from regularly saving screenshots for AI search.",
                            "Verhindert, dass Windows regelmäßig Bildschirmfotos für die KI-Suche speichert."),
            Actions = [ Lm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
                        Lm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0) ] },
        new() { Category = Privacy,
            Title = T("Do not share updates with other PCs", "Updates nicht mit fremden PCs teilen"),
            Description = T("Delivery Optimization without peer-to-peer: your PC does not upload updates to other PCs on the internet.",
                            "Übermittlungsoptimierung ohne Peer-to-Peer: dein PC lädt keine Updates für andere PCs im Internet hoch."),
            Actions = [ Lm(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0) ] },
        new() { Category = Privacy,
            Title = T("Maps and demo services off", "Karten- und Demo-Dienste aus"),
            Description = T("No automatic offline map updates; maps and retail demo services no longer start automatically.",
                            "Keine automatischen Offline-Karten-Updates; Karten- und Einzelhandels-Demo-Dienst starten nicht mehr automatisch."),
            Actions = [ Lm(@"SYSTEM\Maps", "AutoUpdateEnabled", 0), new ServiceAction("MapsBroker", 3, 2),
                        new ServiceAction("RetailDemo", 4, 3),
                        new TaskAction(@"\Microsoft\Windows\Maps\", "MapsUpdateTask"),
                        new TaskAction(@"\Microsoft\Windows\Maps\", "MapsToastTask") ] },

        // ---------------- Edge ----------------
        new() { Category = Edge,
            Title = T("Edge: no preloading or background mode", "Edge nicht vorladen und nicht im Hintergrund laufen lassen"),
            Description = T("Edge no longer starts invisibly with Windows and fully exits when closed.",
                            "Edge startet nicht mehr unsichtbar mit Windows und beendet sich beim Schließen vollständig."),
            Hint = T("Edge will then show “Managed by your organization” – that is expected.",
                     "Edge zeigt danach „Wird von Ihrer Organisation verwaltet“ an – das ist normal."),
            Actions = [ Lm(EdgePol, "StartupBoostEnabled", 0), Lm(EdgePol, "BackgroundModeEnabled", 0) ] },
        new() { Category = Edge,
            Title = T("Edge without ads and shopping assistant", "Edge ohne Werbung und Shopping-Assistent"),
            Description = T("No recommendations, spotlight notices or coupon pop-ups.",
                            "Keine Empfehlungen, Spotlight-Hinweise und Gutschein-Popups."),
            Actions = [ Lm(EdgePol, "ShowRecommendationsEnabled", 0), Lm(EdgePol, "EdgeShoppingAssistantEnabled", 0),
                        Lm(EdgePol, "SpotlightExperiencesAndRecommendationsEnabled", 0) ] },
        new() { Category = Edge,
            Title = T("Edge without diagnostic data and personalization", "Edge ohne Diagnosedaten und Personalisierung"),
            Description = T("Edge sends no optional diagnostic data and does not use your browsing history for ads.",
                            "Edge sendet keine optionalen Diagnosedaten und nutzt den Browserverlauf nicht für Werbung."),
            Actions = [ Lm(EdgePol, "DiagnosticData", 0), Lm(EdgePol, "PersonalizationReportingEnabled", 0) ] },

        // ---------------- Bediengefuehl ----------------
        new() { Category = Speed,
            Title = T("Faster menus", "Schnellere Menüs"),
            Description = T("Submenus open after 100 ms instead of 400 ms.", "Untermenüs öffnen nach 100 ms statt 400 ms."),
            Actions = [ new RegAction(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "100", RegistryValueKind.String) { DefaultValue = "400" } ] },
        new() { Category = Speed,
            Title = T("Startup apps without delay", "Autostart-Programme ohne Wartezeit"),
            Description = T("Windows does not artificially delay your startup apps after sign-in.",
                            "Windows verzögert den Start deiner Autostart-Programme nach der Anmeldung nicht künstlich."),
            Actions = [ Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0) ] },
        new() { Id = ClassicContextMenuId, Category = Speed,
            Title = T("Classic right-click menu", "Klassisches Rechtsklick-Menü"),
            Description = T("Shows the full context menu right away, without “Show more options”.",
                            "Zeigt sofort das vollständige Kontextmenü, ohne Umweg über „Weitere Optionen anzeigen“."),
            Hint = T("Takes effect after restarting Explorer.", "Wirkt nach einem Neustart des Explorers."),
            Actions = [ new RegAction(ClassicMenuKey + @"\InprocServer32", "(default)", "", RegistryValueKind.String)
                        { DeleteKeyOnRevert = ClassicMenuKey } ] },
        new() { Category = Speed,
            Title = T("No permanent background recording in games", "Keine dauerhafte Hintergrundaufnahme in Spielen"),
            Description = T("Game Bar no longer constantly records the last minutes. Game Bar and manual recording still work.",
                            "Die Game Bar nimmt nicht mehr ständig die letzten Minuten auf. Game Bar und manuelle Aufnahmen bleiben nutzbar."),
            Actions = [ Cu(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0) ] },
        new() { Category = Speed, Recommended = false,
            Title = T("Show file extensions", "Dateiendungen anzeigen"),
            Description = T("Shows extensions like .exe or .pdf in File Explorer. Handy and protects against disguised files.",
                            "Zeigt Endungen wie .exe oder .pdf im Explorer an. Praktisch und schützt vor getarnten Dateien."),
            Actions = [ new RegAction($@"HKCU\{Adv}", "HideFileExt", 0) { DefaultValue = 1 } ] },
        new() { Category = Speed, Recommended = false,
            Title = T("File Explorer opens “This PC”", "Explorer öffnet „Dieser PC“"),
            Description = T("File Explorer starts with your drives instead of “Home”.",
                            "Der Datei-Explorer startet mit deinen Laufwerken statt mit „Start“/„Zuletzt verwendet“."),
            Actions = [ new RegAction($@"HKCU\{Adv}", "LaunchTo", 1) { DefaultValue = 2 } ] },
    ];
}
