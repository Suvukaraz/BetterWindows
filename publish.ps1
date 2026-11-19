<#
  Erzeugt die portable Version von BetterWindows:

    BetterWindows-App\
    ├─ BetterWindows.exe          (Einzeldatei, enthaelt die App und alle Bibliotheken)
    └─ BetterWindows-Files\       (Hilfsprogramme fuer den Deinstaller + Lizenzen)
       └─ Data\                   (Einstellungen, Sicherungen, Protokoll – wird beim Aktualisieren NICHT ersetzt)

  Aufruf:  powershell -ExecutionPolicy Bypass -File .\publish.ps1 [-Output <Ordner>]
  Voraussetzung: .NET 10 SDK.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot 'BetterWindows-App')
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$work  = Join-Path ([IO.Path]::GetTempPath()) 'BetterWindowsPublish'
$stage = Join-Path $work 'stage'
$files = Join-Path $stage 'BetterWindows-Files'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }

function Publish([string]$project, [string]$dest, [string[]]$extra) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    # eigener Build-Ordner, damit ein laufender Entwicklungs-Build (bin\) nicht stoert
    $dotnetArgs = @('publish', $project, '-c', 'Release', '-p:Platform=x64', "-p:OutDir=$work\build\$name\", '-o', $dest,
                    '-p:SatelliteResourceLanguages=en%3Bde', '-v:quiet', '-nologo') + $extra
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen: $project" }
}

Write-Host 'BetterWindows (Einzeldatei) ...' -ForegroundColor Cyan
Publish (Join-Path $PSScriptRoot 'BetterWindowsAvalonia\BetterWindows.csproj') $stage @('-p:PublishSingleFile=true')

Write-Host 'Hilfsprogramme ...' -ForegroundColor Cyan
Publish (Join-Path $PSScriptRoot 'third_party\BCU\source\SteamHelper\SteamHelper.csproj') $files @()
Publish (Join-Path $PSScriptRoot 'third_party\BCU\source\StoreAppHelper\StoreAppHelper.csproj') $files @()

# Debug-/Hilfsdateien entfernen, die fuer den Betrieb nicht noetig sind
Get-ChildItem $stage -Recurse -Include *.pdb | Remove-Item -Force
# Sprachdateien der Hilfsprogramme: nur Englisch (eingebaut) und Deutsch behalten
Get-ChildItem $files -Directory | Where-Object { $_.Name -ne 'Licenses' -and $_.Name -ne 'de' -and
    (Get-ChildItem $_.FullName -Filter *.resources.dll).Count -gt 0 } | Remove-Item -Recurse -Force

if (-not (Test-Path (Join-Path $stage 'BetterWindows.exe'))) { throw 'BetterWindows.exe fehlt im Ergebnis.' }

# Laeuft die App gerade aus dem Zielordner? Dann nicht ueberschreiben.
$running = Get-Process BetterWindows -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Output\*" }
if ($running) { throw "BetterWindows laeuft noch aus $Output - bitte schliessen und erneut ausfuehren." }

# Programmdateien austauschen – die Benutzerdaten in "BetterWindows-Files\Data" bleiben unangetastet!
New-Item -ItemType Directory -Force $Output | Out-Null
Get-ChildItem $Output -Force | Where-Object Name -ne 'BetterWindows-Files' | Remove-Item -Recurse -Force
$outFiles = Join-Path $Output 'BetterWindows-Files'
if (Test-Path $outFiles) {
    Get-ChildItem $outFiles -Force | Where-Object Name -ne 'Data' | Remove-Item -Recurse -Force
}
Copy-Item (Join-Path $stage '*') $Output -Recurse -Force
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $outFiles 'Data')) { Write-Host 'Benutzerdaten in BetterWindows-Files\Data wurden beibehalten.' -ForegroundColor Green }

Write-Host "Fertig: $Output" -ForegroundColor Green
Get-ChildItem $Output | ForEach-Object { '  ' + $_.Name }
