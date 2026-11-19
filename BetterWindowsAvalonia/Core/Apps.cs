using System.Text;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Core;

public sealed class RemovableApp(string id, string name, string description, bool recommended = true) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool Recommended { get; } = recommended;

    private bool _installed;
    public bool IsInstalled { get => _installed; set => Set(ref _installed, value); }

    private bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

public static class AppCatalog
{
    public const string OneDriveId = "OneDrive";

    // Bewusst NICHT enthalten: Xbox/Game Pass, Smartphone-Link, Fotos, Paint, Rechner, Editor,
    // Kamera, Uhr, Medienwiedergabe, Snipping Tool, Store, Terminal, OneNote.
    public static List<RemovableApp> Create() =>
    [
        new(OneDriveId, "OneDrive", T("Cloud sync. Local files are kept – cloud-only files can then only be opened via onedrive.com.",
                                      "Cloud-Synchronisierung. Lokale Dateien bleiben erhalten – reine Cloud-Dateien sind danach nur noch über onedrive.com erreichbar.")),
        new("Microsoft.OutlookForWindows", T("Outlook (new)", "Outlook (neu)"), T("Mail app that shows ads in the inbox", "Mail-App, die Werbung im Posteingang zeigt")),
        new("microsoft.windowscommunicationsapps", T("Mail and Calendar", "Mail und Kalender"), T("Old mail app, discontinued by Microsoft", "Alte Mail-App, wird von Microsoft eingestellt")),
        new("MSTeams", "Microsoft Teams", T("Teams (new)", "Teams (neu)")),
        new("MicrosoftTeams", T("Microsoft Teams (personal)", "Microsoft Teams (privat)"), T("Teams chat for personal accounts", "Teams-Chat für private Konten")),
        new("4DF9E0F8.Netflix", "Netflix", T("Preinstalled promo app", "Vorinstallierte Werbe-App")),
        new("Clipchamp.Clipchamp", "Clipchamp", T("Online video editor", "Online-Videoeditor")),
        new("Microsoft.BingNews", "Microsoft News", T("News app (MSN)", "Nachrichten-App (MSN)")),
        new("Microsoft.BingWeather", T("Weather", "Wetter"), T("Weather app (MSN)", "Wetter-App (MSN)")),
        new("Microsoft.BingSearch", T("Bing Search", "Bing-Suche"), T("Bing web search component", "Bing-Websuche-Komponente")),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", T("Game collection with ads", "Spielesammlung mit Werbung")),
        new("Microsoft.Todos", "Microsoft To Do", T("Task lists", "Aufgabenlisten")),
        new("Microsoft.People", T("People", "Kontakte"), T("Old contacts app", "Alte Kontakte-App")),
        new("Microsoft.WindowsMaps", T("Maps", "Karten"), T("Maps app", "Karten-App")),
        new("Microsoft.ZuneVideo", T("Movies & TV", "Filme & TV"), T("Microsoft video store", "Video-Shop von Microsoft")),
        new("Microsoft.GetHelp", T("Get Help", "Hilfe anfordern"), T("Support app", "Support-App")),
        new("Microsoft.WindowsFeedbackHub", T("Feedback Hub", "Feedback-Hub"), T("Send feedback to Microsoft", "Feedback an Microsoft senden")),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", T("Automation tool", "Automatisierungs-Tool")),
        new("Microsoft.Windows.DevHome", "Dev Home", T("Developer dashboard (discontinued)", "Entwickler-Dashboard (eingestellt)")),
        new("Microsoft.MixedReality.Portal", T("Mixed Reality Portal", "Mixed Reality-Portal"), T("Only needed for VR headsets", "Nur für VR-Brillen nötig")),
        new("Microsoft.Print3D", "Print 3D", T("3D printing (discontinued)", "3D-Druck (eingestellt)")),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", T("Promo app for Microsoft 365", "Werbe-App für Microsoft 365")),
        new("Microsoft.549981C3F5F10", "Cortana", T("Voice assistant (discontinued)", "Sprachassistent (eingestellt)")),
        new("Microsoft.Copilot", "Copilot", T("Copilot app", "Copilot-App")),
        new("Microsoft.BingTranslator", "Translator", T("Translator app", "Übersetzer-App"), recommended: false),
        new("Microsoft.Office.OneNote", T("OneNote for Windows 10", "OneNote für Windows 10"),
            T("Old OneNote app – only remove if you do not use it", "Alte OneNote-App – nur entfernen, wenn du sie nicht nutzt"), recommended: false),
    ];
}

public static class AppService
{
    public static async Task<HashSet<string>> GetInstalledAsync()
    {
        string script = Admin.IsElevated
            ? "$n = @(Get-AppxPackage -AllUsers | ForEach-Object Name)\n$n += @(Get-AppxProvisionedPackage -Online | ForEach-Object DisplayName)\n$n | Sort-Object -Unique"
            : "Get-AppxPackage | ForEach-Object Name | Sort-Object -Unique";
        var result = await PowerShell.RunAsync(script);
        var set = new HashSet<string>(result.Lines.Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);
        if (IsOneDriveInstalled()) set.Add(AppCatalog.OneDriveId);
        return set;
    }

    public static bool IsOneDriveInstalled()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\OneDrive\OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft OneDrive\OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft OneDrive\OneDrive.exe"),
        ];
        return candidates.Any(File.Exists);
    }

    /// <summary>
    /// Zaehlt Dateien im OneDrive-Ordner, die nur in der Cloud liegen (Platzhalter ohne Inhalt).
    /// Diese lassen sich nach der Deinstallation lokal nicht mehr oeffnen. Liest nur Attribute, oeffnet keine Datei.
    /// </summary>
    public static (int CloudOnly, List<string> Examples) ScanOneDriveCloudOnlyFiles()
    {
        const int RecallOnDataAccess = 0x400000, RecallOnOpen = 0x40000, Offline = 0x1000;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");
        var examples = new List<string>();
        int count = 0;
        if (!Directory.Exists(root)) return (0, examples);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                int a = (int)f.Attributes;
                if ((a & (RecallOnDataAccess | RecallOnOpen | Offline)) == 0 || f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                if (examples.Count < 6) examples.Add(Path.GetRelativePath(root, f.FullName));
            }
        }
        catch { /* best effort */ }
        return (count, examples);
    }

    public static async Task RemoveAsync(IReadOnlyList<string> ids, Action<string> log)
    {
        // Texte fuer das PowerShell-Protokoll in der aktuellen Sprache
        string removed = T("removed", "entfernt"), notInstalled = T("was not installed", "war nicht installiert"), error = T("[Error]", "[Fehler]");

        var appx = ids.Where(i => i != AppCatalog.OneDriveId).ToList();
        if (appx.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Continue'");
            sb.AppendLine("$apps = @(" + string.Join(",", appx.Select(PowerShell.Quote)) + ")");
            sb.AppendLine($$"""
                $ok = @{}
                # 1) Installierte Pakete fuer alle Benutzer entfernen
                foreach ($app in $apps) {
                    $ok[$app] = $false
                    foreach ($p in @(Get-AppxPackage -AllUsers -Name $app)) {
                        try { Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction Stop; $ok[$app] = $true }
                        catch {
                            try { Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop; $ok[$app] = $true }
                            catch { "{{error}} ${app}: $($_.Exception.Message)" }
                        }
                    }
                }
                # 2) Image-Eintraege ERST JETZT einlesen: Remove-AppxPackage -AllUsers entfernt sie auf
                #    neueren Builds bereits mit, eine vorher gelesene Liste waere veraltet.
                $prov = @(Get-AppxProvisionedPackage -Online)
                # "Datei/Pfad/Paket nicht gefunden" = bereits entfernt, kein Fehler
                $gone = @(-2147024894, -2147024893, -2147009295)
                foreach ($app in $apps) {
                    foreach ($pp in @($prov | Where-Object DisplayName -eq $app)) {
                        try { Remove-AppxProvisionedPackage -Online -PackageName $pp.PackageName -AllUsers -ErrorAction Stop | Out-Null; $ok[$app] = $true }
                        catch {
                            if ($gone -contains $_.Exception.HResult) { $ok[$app] = $true }
                            else { "{{error}} $app (Image): $($_.Exception.Message)" }
                        }
                    }
                    if ($ok[$app]) { "[OK] $app {{removed}}" } else { "[--] $app {{notInstalled}}" }
                }
                """);
            await PowerShell.RunAsync(sb.ToString(), log);
        }

        if (ids.Contains(AppCatalog.OneDriveId))
        {
            log(T("Uninstalling OneDrive …", "OneDrive wird deinstalliert …"));
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");
            string ok = T($"[OK] OneDrive uninstalled (local files in {folder} are kept, cloud-only files stay in your OneDrive account)",
                          $"[OK] OneDrive deinstalliert (lokale Dateien in {folder} bleiben erhalten, reine Cloud-Dateien bleiben in deinem OneDrive-Konto)");
            string fail = T("[Error] OneDrive could not be uninstalled", "[Fehler] OneDrive konnte nicht deinstalliert werden");
            await PowerShell.RunAsync($$"""
                Get-Process OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force
                Start-Sleep -Seconds 2
                $done = $false
                $winget = Get-Command winget -ErrorAction SilentlyContinue
                if ($winget) {
                    & $winget.Source uninstall --id Microsoft.OneDrive --silent --accept-source-agreements | Out-Null
                    if ($LASTEXITCODE -eq 0) { $done = $true }
                }
                if (-not $done) {
                    $setup = @("$env:SystemRoot\System32\OneDriveSetup.exe", "$env:SystemRoot\SysWOW64\OneDriveSetup.exe") |
                             Where-Object { Test-Path $_ } | Select-Object -First 1
                    if ($setup) { Start-Process $setup -ArgumentList '/uninstall' -Wait; $done = $true }
                }
                if ($done) { {{PowerShell.Quote(ok)}} } else { {{PowerShell.Quote(fail)}} }
                """, log);
            try
            {
                // Verhindert, dass OneDrive sich wieder einrichtet (mit Backup, zuruecksetzbar)
                new RegAction(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 1).Apply();
                log(T("[OK] OneDrive blocked by policy", "[OK] OneDrive per Richtlinie gesperrt"));
            }
            catch (Exception ex) { log(T("[Error] OneDrive policy: ", "[Fehler] OneDrive-Richtlinie: ") + ex.Message); }
        }
    }
}
