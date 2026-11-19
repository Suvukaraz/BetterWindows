#Requires -RunAsAdministrator
#Requires -PSEdition Desktop
<#
  BetterWindows - macht die Aenderungen von Debloat.ps1 rueckgaengig.

  Aufruf (Windows PowerShell als Administrator):
      .\Undo-Debloat.ps1                     # nimmt das neueste Protokoll
      .\Undo-Debloat.ps1 -LogFile .\Backup\changes-2026-11-18_12-00-00.json

  Registry-Werte, Dienste und geplante Aufgaben werden exakt auf den alten
  Stand zurueckgesetzt. Entfernte Apps koennen ueber den Microsoft Store
  neu installiert werden (Liste wird am Ende ausgegeben).
#>
param(
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
$BackupDir = Join-Path $PSScriptRoot 'Backup'

if (-not $LogFile) {
    $latest = Get-ChildItem -Path $BackupDir -Filter 'changes-*.json' -ErrorAction SilentlyContinue |
              Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { Write-Host "Kein Protokoll in $BackupDir gefunden." -ForegroundColor Red; exit 1 }
    $LogFile = $latest.FullName
}
Write-Host "Verwende Protokoll: $LogFile" -ForegroundColor Cyan

$parsed  = Get-Content -LiteralPath $LogFile -Raw -Encoding UTF8 | ConvertFrom-Json
$changes = @($parsed | ForEach-Object { $_ })   # PS 5.1: JSON-Array korrekt entpacken
[array]::Reverse($changes)   # in umgekehrter Reihenfolge zuruecknehmen

$apps = @()
$oneDrive = $false

foreach ($c in $changes) {
    try {
        switch ($c.Kind) {
            'reg' {
                if ($c.Existed) {
                    $val = $c.OldValue
                    if ($c.OldType -eq 'Binary')      { $val = [byte[]]@($val) }
                    if ($c.OldType -eq 'MultiString') { $val = [string[]]@($val) }
                    New-ItemProperty -LiteralPath $c.Path -Name $c.Name -Value $val -PropertyType $c.OldType -Force | Out-Null
                    Write-Host "  [OK] $($c.Path)\$($c.Name) -> $($c.OldValue)" -ForegroundColor Green
                } else {
                    $hive = if ($c.Path -like 'HKCU:*') { [Microsoft.Win32.Registry]::CurrentUser }
                            else { [Microsoft.Win32.Registry]::LocalMachine }
                    $sub  = $c.Path -replace '^HK(CU|LM):\\', ''
                    $rk   = $hive.OpenSubKey($sub, $true)
                    if ($rk) {
                        $raw = if ($c.Name -eq '(default)') { '' } else { $c.Name }
                        $rk.DeleteValue($raw, $false)
                        $rk.Close()
                    }
                    Write-Host "  [OK] $($c.Path)\$($c.Name) entfernt" -ForegroundColor Green
                }
            }
            'key' {
                # Nur entfernen, wenn der vom Skript angelegte Schluessel wieder leer ist
                $k = Get-Item -LiteralPath $c.Path -ErrorAction SilentlyContinue
                if ($k -and $k.ValueCount -eq 0 -and $k.SubKeyCount -eq 0) {
                    Remove-Item -LiteralPath $c.Path
                    Write-Host "  [OK] Schluessel $($c.Path) entfernt" -ForegroundColor Green
                }
            }
            'task' {
                Enable-ScheduledTask -TaskPath $c.TaskPath -TaskName $c.TaskName | Out-Null
                Write-Host "  [OK] Aufgabe $($c.TaskName) wieder aktiviert" -ForegroundColor Green
            }
            'app'      { $apps += $c.Name }
            'onedrive' { $oneDrive = $true }
        }
    } catch {
        Write-Host "  [WARN] $($c.Kind) $($c.Path)$($c.Name)$($c.TaskName): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

if ($oneDrive) {
    Write-Host "`nOneDrive neu installieren..." -ForegroundColor Cyan
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) { & $winget.Source install --id Microsoft.OneDrive --silent --accept-source-agreements --accept-package-agreements | Out-Host }
    else { Write-Host '  winget nicht gefunden - OneDrive bitte von microsoft.com/onedrive installieren.' -ForegroundColor Yellow }
}

if ($apps.Count -gt 0) {
    Write-Host "`nFolgende Apps wurden entfernt und koennen bei Bedarf im Microsoft Store neu installiert werden:" -ForegroundColor Cyan
    $apps | Sort-Object | ForEach-Object { Write-Host "  - $_" }
}

Write-Host "`nFertig. Bitte den PC neu starten." -ForegroundColor Yellow
