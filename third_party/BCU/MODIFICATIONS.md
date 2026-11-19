# Bulk Crap Uninstaller – eingebundene Teile und Änderungen

Quelle: https://github.com/BCUninstaller/Bulk-Crap-Uninstaller (Branch `master`, Stand 18.11.2026)
Lizenz: Apache License 2.0 – siehe `Licence.txt` und `NOTICE` in diesem Ordner.
Urheber: Marcin Szeniak und BCU-Mitwirkende.

## Übernommene Projekte (`source/`)
`UninstallTools`, `KlocTools`, `HelperTools`, `SteamHelper`, `StoreAppHelper`,
dazu `GlobalAssemblyInfo.cs`, `Licence.licenseheader` und `app.manifest`.

**Nicht enthalten: `ObjectListView`.** BCU bringt diese Komponente mit, sie steht aber unter der **GPL-3.0**
(Copyright Phillip Piper). BetterWindows hat sie entfernt (siehe Änderung 7), damit der gesamte Code
Apache-2.0-kompatibel bleibt.

Nicht übernommen (in BetterWindows nicht genutzt): BCU-Oberfläche, `UninstallerAutomatizer`,
`UniversalUninstaller`, `ScriptHelper`, `WinUpdateHelper`, `OculusHelper`, `es.exe`, Tests.

## Änderungen durch BetterWindows
1. `source/Directory.Build.props`: angepasst. Gemeinsamer Ausgabeordner entfernt, Warnungen und Analyzer für
   Fremdcode stummgeschaltet.
2. `UninstallTools/UninstallTools.csproj`: `COMReference` auf „Scripting“ entfernt, weil `dotnet build` kein
   tlbimp unterstützt.
3. `UninstallTools/Factory/InfoAdders/FastSizeGenerator.cs`: `Scripting.FileSystemObject` wird per später
   COM-Bindung (`dynamic`) erzeugt statt über die Interop-Assembly.
4. `KlocTools/Forms/Tools/PremadeDialogs.cs`: neue Eigenschaft `ErrorHandler`. Ist sie gesetzt, landen Fehler
   dort statt in einem modalen WinForms-Dialog, der sonst den Programm-Scan blockiert.
5. `source/helper.manifest` (neu) und `SteamHelper.csproj` / `StoreAppHelper.csproj`: Die Hilfsprogramme laufen
   mit `asInvoker` statt `requireAdministrator`. Sie erben so die Rechte von BetterWindows und lassen sich
   auch ohne Admin starten.
6. `UninstallTools/UninstallToolsGlobalConfig.cs`: Ist `Assembly.Location` leer (Single-File-Veröffentlichung),
   kommt der Ordner der Hilfsprogramme aus dem AppContext-Wert `BCU.HelperDirectory`, den BetterWindows beim
   Start setzt. Als Rückfall dient `AppContext.BaseDirectory`.
7. `UninstallTools/UninstallTools.csproj`: Verweis auf `ObjectListView` (GPL-3.0) entfernt. Gelöscht wurden der
   Ordner `ObjectListView` und das WinForms-Fenster `UninstallTools/Dialogs/StartupManagerWindow*`, die einzige
   Stelle, die ObjectListView nutzte. BetterWindows verwendet dieses Fenster nicht.
