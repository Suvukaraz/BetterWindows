#Requires -RunAsAdministrator
#Requires -PSEdition Desktop
<#
  BetterWindows - Debloat-Skript fuer Windows 11
  ------------------------------------------------
  Ziele: Werbung entfernen, unnoetige Internetabfragen/Telemetrie abschalten,
         schnelleres Bediengefuehl.

  Aufruf (in einer Windows PowerShell als Administrator):
      .\Debloat.ps1 -DryRun     # zeigt nur an, was geaendert wuerde
      .\Debloat.ps1             # fuehrt die Aenderungen aus

  Jede Aenderung wird in .\Backup\changes-<Datum>.json protokolliert.
  Rueckgaengig machen:  .\Undo-Debloat.ps1
  Zusaetzlich wird vorher ein Systemwiederherstellungspunkt erstellt.
#>
param(
    [switch]$DryRun
)

# ============================ KONFIGURATION ============================

# Apps, die entfernt werden (fuer alle Benutzer + aus dem Windows-Image,
# damit sie nach Feature-Updates nicht wiederkommen).
# BEHALTEN werden u.a.: Xbox/Game Pass, Smartphone-Link, Fotos, Paint,
# Rechner, Editor, Kamera, Uhr, Medienwiedergabe, Snipping Tool, Store, OneNote.
$RemoveApps = @(
    '4DF9E0F8.Netflix'                     # Netflix (Werbe-Vorinstallation)
    'Clipchamp.Clipchamp'                  # Clipchamp Videoeditor
    'Microsoft.BingNews'                   # Nachrichten
    'Microsoft.BingSearch'                 # Bing-Suche
    'Microsoft.BingWeather'                # Wetter
    'Microsoft.GetHelp'                    # Hilfe anfordern
    'Microsoft.MicrosoftSolitaireCollection'
    'Microsoft.MixedReality.Portal'
    'Microsoft.People'
    'Microsoft.PowerAutomateDesktop'
    'Microsoft.Print3D'
    'Microsoft.Todos'                      # Microsoft To Do
    'Microsoft.Windows.DevHome'            # Dev Home (eingestellt)
    'Microsoft.WindowsFeedbackHub'
    'Microsoft.WindowsMaps'
    'microsoft.windowscommunicationsapps'  # Mail & Kalender (alt)
    'Microsoft.OutlookForWindows'          # Outlook (neu)
    'Microsoft.ZuneVideo'                  # Filme & TV
    'MSTeams'                              # Teams (neu)
    'MicrosoftTeams'                       # Teams (privat)
    # nicht installiert, aber falls ein Update sie nachliefert:
    'Microsoft.Copilot'
    'Microsoft.MicrosoftOfficeHub'
    'Microsoft.549981C3F5F10'              # Cortana
    'Microsoft.WindowsAI.Copilot.Provider'
)

$UninstallOneDrive   = $true   # OneDrive deinstallieren (lokale Dateien bleiben erhalten)
$ClassicContextMenu  = $true   # Klassisches Rechtsklick-Menue (ohne "Weitere Optionen")
$DisableWidgets      = $true   # Widgets-Leiste (Bing-News-Feed) deaktivieren

# ======================================================================

$ErrorActionPreference = 'Stop'
$Stamp     = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$BackupDir = Join-Path $PSScriptRoot 'Backup'
$LogFile   = Join-Path $BackupDir "changes-$Stamp.json"
$Changes   = New-Object System.Collections.ArrayList

function Write-Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t)      { Write-Host "  [OK]   $t" -ForegroundColor Green }
function Write-Skip($t)    { Write-Host "  [--]   $t" -ForegroundColor DarkGray }
function Write-Warn2($t)   { Write-Host "  [WARN] $t" -ForegroundColor Yellow }
function Write-Dry($t)     { Write-Host "  [DRY]  $t" -ForegroundColor Magenta }

function Save-Log {
    if ($DryRun) { return }
    if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }
    ConvertTo-Json -InputObject $Changes.ToArray() -Depth 5 | Out-File -FilePath $LogFile -Encoding utf8
}

function Ensure-Key([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return }
    $parent = Split-Path -Path $Path -Parent
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { Ensure-Key $parent }
    New-Item -Path $Path -Force | Out-Null
    [void]$Changes.Add([pscustomobject]@{ Kind = 'key'; Path = $Path })
}

function Set-Reg {
    param(
        [string]$Path, [string]$Name, $Value,
        [ValidateSet('DWord','String','Binary','QWord','ExpandString')][string]$Type = 'DWord',
        [string]$Label
    )
    $display = if ($Label) { $Label } else { "$Path\$Name = $Value" }
    try {
        $key = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
        $rawName = if ($Name -eq '(default)') { '' } else { $Name }
        $existed = $false; $old = $null; $oldType = $null
        if ($key -and ($key.GetValueNames() -contains $rawName)) {
            $existed = $true
            $old     = $key.GetValue($rawName, $null, 'DoNotExpandEnvironmentNames')
            $oldType = $key.GetValueKind($rawName).ToString()
        }
        $same = $existed -and $oldType -eq $Type -and (
            ($Type -eq 'Binary' -and (@(Compare-Object @($old) @($Value) -SyncWindow 0).Count -eq 0)) -or
            ($Type -ne 'Binary' -and "$old" -eq "$Value"))
        if ($same) { Write-Skip "$display (bereits gesetzt)"; return }
        if ($DryRun) { Write-Dry "$display  (vorher: $(if($existed){$old}else{'nicht gesetzt'}))"; return }

        Ensure-Key $Path
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
        [void]$Changes.Add([pscustomobject]@{
            Kind = 'reg'; Path = $Path; Name = $Name
            Existed = $existed; OldValue = $old; OldType = $oldType
        })
        Write-Ok $display
    } catch {
        Write-Warn2 "$display -> $($_.Exception.Message)"
    }
}

function Disable-ServiceLogged([string]$Name, [int]$Start = 4) {
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Skip "Dienst $Name nicht vorhanden"; return }
    $mode = if ($Start -eq 4) { 'Deaktiviert' } else { 'Manuell' }
    Set-Reg -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name 'Start' -Value $Start `
            -Label "Dienst $Name -> $mode"
    if (-not $DryRun -and $Start -eq 4 -and $svc.Status -eq 'Running') {
        try { Stop-Service -Name $Name -Force -ErrorAction Stop; Write-Ok "Dienst $Name gestoppt" }
        catch { Write-Warn2 "Dienst $Name konnte nicht gestoppt werden (wirkt nach Neustart)" }
    }
}

function Disable-TaskLogged([string]$TaskPath, [string]$TaskName) {
    $task = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) { Write-Skip "Aufgabe $TaskName nicht vorhanden"; return }
    if ($task.State -eq 'Disabled') { Write-Skip "Aufgabe $TaskName (bereits deaktiviert)"; return }
    if ($DryRun) { Write-Dry "Aufgabe $TaskPath$TaskName deaktivieren"; return }
    try {
        Disable-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName | Out-Null
        [void]$Changes.Add([pscustomobject]@{ Kind = 'task'; TaskPath = $TaskPath; TaskName = $TaskName })
        Write-Ok "Aufgabe $TaskName deaktiviert"
    } catch { Write-Warn2 "Aufgabe $TaskName -> $($_.Exception.Message)" }
}

# ----------------------------------------------------------------------
if ($DryRun) { Write-Host "TESTLAUF: Es wird nichts veraendert." -ForegroundColor Magenta }

# ---------------------- 0. Wiederherstellungspunkt ---------------------
Write-Section 'Wiederherstellungspunkt'
if ($DryRun) { Write-Dry 'Systemschutz fuer C: aktivieren und Wiederherstellungspunkt erstellen' }
else {
    try {
        Enable-ComputerRestore -Drive "$env:SystemDrive\"
        # 24h-Sperre fuer neue Punkte kurz aufheben
        $srKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
        $prevFreq = (Get-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
        Set-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
        Checkpoint-Computer -Description "Vor BetterWindows $Stamp" -RestorePointType MODIFY_SETTINGS
        if ($null -eq $prevFreq) { Remove-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency }
        else { Set-ItemProperty -Path $srKey -Name SystemRestorePointCreationFrequency -Value $prevFreq -Type DWord }
        Write-Ok 'Wiederherstellungspunkt erstellt'
    } catch {
        Write-Warn2 "Wiederherstellungspunkt fehlgeschlagen: $($_.Exception.Message)"
        $answer = Read-Host '  Trotzdem fortfahren? (j/n)'
        if ($answer -ne 'j') { Write-Host 'Abgebrochen.'; exit 1 }
    }
}

try {
# ---------------------- 1. Werbung & Vorschlaege -----------------------
Write-Section 'Werbung, Tipps und Vorschlaege'
$cdm = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
Set-Reg $cdm 'SilentInstalledAppsEnabled'      0 -Label 'Keine automatisch installierten Werbe-Apps'
Set-Reg $cdm 'PreInstalledAppsEnabled'         0 -Label 'Keine vorinstallierten Promo-Apps'
Set-Reg $cdm 'OemPreInstalledAppsEnabled'      0 -Label 'Keine OEM-Promo-Apps'
Set-Reg $cdm 'SystemPaneSuggestionsEnabled'    0 -Label 'Keine Vorschlaege im Startmenue'
Set-Reg $cdm 'SoftLandingEnabled'              0 -Label 'Keine Tipps zu Windows'
Set-Reg $cdm 'RotatingLockScreenOverlayEnabled' 0 -Label 'Keine "Wissenswertes"-Texte auf dem Sperrbildschirm'
Set-Reg $cdm 'SubscribedContent-338387Enabled' 0 -Label 'Sperrbildschirm: Tipps/Tricks aus'
Set-Reg $cdm 'SubscribedContent-338388Enabled' 0 -Label 'Startmenue: App-Vorschlaege aus'
Set-Reg $cdm 'SubscribedContent-338389Enabled' 0 -Label 'Tipps und Vorschlaege bei Nutzung aus'
Set-Reg $cdm 'SubscribedContent-310093Enabled' 0 -Label 'Willkommens-Erlebnis nach Updates aus'
Set-Reg $cdm 'SubscribedContent-338393Enabled' 0 -Label 'Einstellungen-App: empfohlene Inhalte aus (1)'
Set-Reg $cdm 'SubscribedContent-353694Enabled' 0 -Label 'Einstellungen-App: empfohlene Inhalte aus (2)'
Set-Reg $cdm 'SubscribedContent-353696Enabled' 0 -Label 'Einstellungen-App: empfohlene Inhalte aus (3)'
Set-Reg $cdm 'SubscribedContent-353698Enabled' 0 -Label 'Zeitachsen-Vorschlaege aus'

$adv = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
Set-Reg $adv 'Start_IrisRecommendations'     0 -Label 'Startmenue: Empfehlungen fuer Tipps/neue Apps aus'
Set-Reg $adv 'Start_AccountNotifications'    0 -Label 'Startmenue: Konto-Werbung (Microsoft 365, OneDrive) aus'
Set-Reg $adv 'ShowSyncProviderNotifications' 0 -Label 'Explorer: OneDrive/Microsoft 365-Werbung aus'
Set-Reg $adv 'ShowCopilotButton'             0 -Label 'Taskleiste: Copilot-Schaltflaeche aus'

Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement' 'ScoobeSystemSettingEnabled' 0 `
        -Label '"Geraeteeinrichtung abschliessen"-Nachfrage aus'
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested' 'Enabled' 0 `
        -Label 'Vorschlags-Benachrichtigungen aus'
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo' 'Enabled' 0 -Label 'Werbe-ID aus'
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo' 'DisabledByGroupPolicy' 1 -Label 'Werbe-ID per Richtlinie aus'
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Privacy' 'TailoredExperiencesWithDiagnosticDataEnabled' 0 `
        -Label 'Massgeschneiderte Erfahrungen aus'
Set-Reg 'HKCU:\Software\Policies\Microsoft\Windows\CloudContent' 'DisableTailoredExperiencesWithDiagnosticData' 1 `
        -Label 'Massgeschneiderte Erfahrungen per Richtlinie aus'
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsConsumerFeatures' 1 -Label 'Consumer-Features (Promo-Apps) aus'
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableConsumerAccountStateContent' 1 -Label 'Konto-Promo-Inhalte aus'
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableCloudOptimizedContent' 1 -Label 'Cloud-optimierte Inhalte aus'

if ($DisableWidgets) {
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' 'AllowNewsAndInterests' 0 -Label 'Widgets (News-Feed) deaktivieren'
}

# ---------------------- 2. Suche ohne Bing ----------------------------
Write-Section 'Startmenue-Suche ohne Bing/Web'
Set-Reg 'HKCU:\Software\Policies\Microsoft\Windows\Explorer' 'DisableSearchBoxSuggestions' 1 -Label 'Keine Web-/Bing-Ergebnisse in der Suche'
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' 'BingSearchEnabled' 0 -Label 'Bing-Suche aus'
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\SearchSettings' 'IsDynamicSearchBoxEnabled' 0 -Label 'Suchhighlights aus'
$ws = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search'
Set-Reg $ws 'DisableWebSearch'          1 -Label 'Websuche per Richtlinie aus'
Set-Reg $ws 'ConnectedSearchUseWeb'     0 -Label 'Verbundene Websuche aus'
Set-Reg $ws 'EnableDynamicContentInWSB' 0 -Label 'Dynamische Inhalte in der Suchleiste aus'
Set-Reg $ws 'AllowCortana'              0 -Label 'Cortana aus'

# ---------------------- 3. Telemetrie & Datenuebertragung -------------
Write-Section 'Telemetrie und Datenuebertragung'
$dc = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection'
Set-Reg $dc 'AllowTelemetry'                   0 -Label 'Diagnosedaten auf Minimum'
Set-Reg $dc 'DoNotShowFeedbackNotifications'   1 -Label 'Feedback-Abfragen aus'
Set-Reg $dc 'LimitDiagnosticLogCollection'     1 -Label 'Diagnose-Log-Sammlung begrenzen'
Set-Reg $dc 'DisableDiagnosticDataViewer'      1 -Label 'Diagnosedaten-Viewer aus'
Set-Reg 'HKCU:\Software\Microsoft\Siuf\Rules' 'NumberOfSIUFInPeriod' 0 -Label 'Feedback-Haeufigkeit: nie'

$sys = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'
Set-Reg $sys 'EnableActivityFeed'    0 -Label 'Aktivitaetsverlauf aus'
Set-Reg $sys 'PublishUserActivities' 0 -Label 'Aktivitaeten nicht veroeffentlichen'
Set-Reg $sys 'UploadUserActivities'  0 -Label 'Aktivitaeten nicht hochladen'

Set-Reg 'HKCU:\Software\Microsoft\InputPersonalization' 'RestrictImplicitInkCollection'  1 -Label 'Freihand-Daten nicht sammeln'
Set-Reg 'HKCU:\Software\Microsoft\InputPersonalization' 'RestrictImplicitTextCollection' 1 -Label 'Eingabe-Daten nicht sammeln'
Set-Reg 'HKCU:\Software\Microsoft\Input\TIPC' 'Enabled' 0 -Label 'Tipp-/Freihand-Verbesserung aus'
Set-Reg 'HKCU:\Control Panel\International\User Profile' 'HttpAcceptLanguageOptOut' 1 -Label 'Sprachliste nicht an Websites senden'

# Copilot & Recall
Set-Reg 'HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot' 'TurnOffWindowsCopilot' 1 -Label 'Windows Copilot aus'
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot' 'TurnOffWindowsCopilot' 1 -Label 'Windows Copilot aus (alle Benutzer)'
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI' 'DisableAIDataAnalysis' 1 -Label 'Recall-Snapshots aus'
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI' 'AllowRecallEnablement' 0 -Label 'Recall nicht aktivierbar'

# Uebermittlungsoptimierung: Updates nicht mit fremden PCs im Internet teilen
Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization' 'DODownloadMode' 0 `
        -Label 'Update-Uebermittlung: kein Peer-to-Peer (spart Upload)'

# Karten-Autoupdate
Set-Reg 'HKLM:\SYSTEM\Maps' 'AutoUpdateEnabled' 0 -Label 'Offline-Karten nicht automatisch aktualisieren'

Disable-ServiceLogged 'DiagTrack'        4   # Benutzererfahrung und Telemetrie im verbundenen Modus
Disable-ServiceLogged 'dmwappushservice' 4   # WAP-Push (Telemetrie-Routing)
Disable-ServiceLogged 'RetailDemo'       4   # Einzelhandels-Demo
Disable-ServiceLogged 'MapsBroker'       3   # Karten-Manager -> Manuell

# Format: 'Ordner\|Aufgabenname'
$tasks = @(
    '\Microsoft\Windows\Application Experience\|Microsoft Compatibility Appraiser'
    '\Microsoft\Windows\Application Experience\|Microsoft Compatibility Appraiser Exp'   # Name in neueren Builds
    '\Microsoft\Windows\Application Experience\|ProgramDataUpdater'
    '\Microsoft\Windows\Application Experience\|MareBackup'
    '\Microsoft\Windows\Autochk\|Proxy'
    '\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator'
    '\Microsoft\Windows\Customer Experience Improvement Program\|UsbCeip'
    '\Microsoft\Windows\DiskDiagnostic\|Microsoft-Windows-DiskDiagnosticDataCollector'
    '\Microsoft\Windows\Feedback\Siuf\|DmClient'
    '\Microsoft\Windows\Feedback\Siuf\|DmClientOnScenarioDownload'
    '\Microsoft\Windows\Maps\|MapsUpdateTask'
    '\Microsoft\Windows\Maps\|MapsToastTask'
)
foreach ($t in $tasks) {
    $parts = $t.Split('|')
    Disable-TaskLogged $parts[0] $parts[1]
}

# ---------------------- 4. Microsoft Edge -----------------------------
Write-Section 'Microsoft Edge (bleibt installiert)'
$edge = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
Set-Reg $edge 'StartupBoostEnabled'               0 -Label 'Edge: kein Vorladen beim Windows-Start'
Set-Reg $edge 'BackgroundModeEnabled'             0 -Label 'Edge: nicht im Hintergrund weiterlaufen'
Set-Reg $edge 'ShowRecommendationsEnabled'        0 -Label 'Edge: keine Empfehlungen/Werbe-Hinweise'
Set-Reg $edge 'EdgeShoppingAssistantEnabled'      0 -Label 'Edge: Shopping-Assistent aus'
Set-Reg $edge 'PersonalizationReportingEnabled'   0 -Label 'Edge: keine Personalisierung durch Browserdaten'
Set-Reg $edge 'DiagnosticData'                    0 -Label 'Edge: Diagnosedaten aus'
Set-Reg $edge 'SpotlightExperiencesAndRecommendationsEnabled' 0 -Label 'Edge: Spotlight-Empfehlungen aus'

# Edge-Autostart deaktivieren (wie im Task-Manager -> Autostart)
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$runItem = Get-Item -LiteralPath $runKey -ErrorAction SilentlyContinue
if ($runItem) {
    $approvedItem = Get-Item -LiteralPath $approved -ErrorAction SilentlyContinue
    foreach ($n in $runItem.GetValueNames() | Where-Object { $_ -like 'MicrosoftEdgeAutoLaunch*' }) {
        $cur = if ($approvedItem) { $approvedItem.GetValue($n) } else { $null }
        # Erstes Byte ungerade (z.B. 03) = im Task-Manager bereits deaktiviert
        if ($cur -is [byte[]] -and $cur.Length -gt 0 -and ($cur[0] -band 1)) {
            Write-Skip "Autostart aus: $n (bereits deaktiviert)"; continue
        }
        Set-Reg $approved $n ([byte[]](3,0,0,0,0,0,0,0,0,0,0,0)) -Type Binary -Label "Autostart aus: $n"
    }
}

# ---------------------- 5. Bediengefuehl ------------------------------
Write-Section 'Schnelleres Bediengefuehl'
Set-Reg 'HKCU:\Control Panel\Desktop' 'MenuShowDelay' '100' -Type String -Label 'Menue-Verzoegerung 400 ms -> 100 ms'
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize' 'StartupDelayInMSec' 0 `
        -Label 'Autostart-Programme ohne kuenstliche Verzoegerung starten'
Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' 'HistoricalCaptureEnabled' 0 `
        -Label 'Game Bar: dauerhafte Hintergrundaufnahme aus (Game Bar bleibt nutzbar)'
if ($ClassicContextMenu) {
    Set-Reg 'HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32' '(default)' '' -Type String `
            -Label 'Klassisches Rechtsklick-Menue'
}

# ---------------------- 6. Apps entfernen -----------------------------
Write-Section 'Vorinstallierte Apps entfernen'
$provisioned = Get-AppxProvisionedPackage -Online
foreach ($app in $RemoveApps) {
    $pkgs = @(Get-AppxPackage -AllUsers -Name $app -ErrorAction SilentlyContinue)
    $prov = @($provisioned | Where-Object DisplayName -eq $app)
    if ($pkgs.Count -eq 0 -and $prov.Count -eq 0) { Write-Skip "$app nicht installiert"; continue }
    if ($DryRun) { Write-Dry "App entfernen: $app"; continue }
    $removed = $false
    foreach ($p in $pkgs) {
        try { Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction Stop; $removed = $true }
        catch {
            try { Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop; $removed = $true }
            catch { Write-Warn2 "$app -> $($_.Exception.Message)" }
        }
    }
    foreach ($pp in $prov) {
        try { Remove-AppxProvisionedPackage -Online -PackageName $pp.PackageName -AllUsers -ErrorAction Stop | Out-Null; $removed = $true }
        catch { Write-Warn2 "$app (Image) -> $($_.Exception.Message)" }
    }
    if ($removed) {
        [void]$Changes.Add([pscustomobject]@{ Kind = 'app'; Name = $app })
        Write-Ok "$app entfernt"
    }
}

# ---------------------- 7. OneDrive -----------------------------------
if ($UninstallOneDrive) {
    Write-Section 'OneDrive deinstallieren'
    if ($DryRun) { Write-Dry 'OneDrive beenden, deinstallieren und per Richtlinie sperren' }
    else {
        Get-Process OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 2
        $done = $false
        $winget = Get-Command winget -ErrorAction SilentlyContinue
        if ($winget) {
            & $winget.Source uninstall --id Microsoft.OneDrive --silent --accept-source-agreements | Out-Host
            if ($LASTEXITCODE -eq 0) { $done = $true }
        }
        if (-not $done) {
            $setup = @("$env:SystemRoot\System32\OneDriveSetup.exe", "$env:SystemRoot\SysWOW64\OneDriveSetup.exe") |
                     Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($setup) { Start-Process $setup -ArgumentList '/uninstall' -Wait; $done = $true }
        }
        if ($done) {
            [void]$Changes.Add([pscustomobject]@{ Kind = 'onedrive' })
            Write-Ok "OneDrive deinstalliert (Dateien in $env:USERPROFILE\OneDrive bleiben erhalten)"
        } else { Write-Warn2 'OneDrive-Deinstallation nicht moeglich' }
    }
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive' 'DisableFileSyncNGSC' 1 -Label 'OneDrive per Richtlinie sperren (kein Wiederauftauchen)'
}

} finally {
    Save-Log
}

Write-Section 'Fertig'
if ($DryRun) {
    Write-Host 'Testlauf beendet. Ohne -DryRun ausfuehren, um die Aenderungen anzuwenden.' -ForegroundColor Magenta
} else {
    Write-Host "Protokoll: $LogFile" -ForegroundColor Green
    Write-Host 'Bitte den PC jetzt NEU STARTEN, damit alle Aenderungen wirken.' -ForegroundColor Yellow
    Write-Host 'Rueckgaengig: .\Undo-Debloat.ps1  (oder Systemwiederherstellung)' -ForegroundColor Gray
}
