using System.Text;
using BetterWindows.Core;

namespace BetterWindows;

/// <summary>Nur lesender Diagnosemodus: BetterWindows.exe --selftest [Ausgabedatei]</summary>
internal static class SelfTest
{
    public static async Task RunAsync(string outFile)
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine($"Admin: {Admin.IsElevated}");
            if (Environment.GetCommandLineArgs().Contains("--probe-widgets"))
            {
                // Schreibt den AKTUELLEN Wert unveraendert zurueck -> prueft nur, ob UCPD diesen Prozess blockiert
                const string adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
                var cur = Reg.Get(adv, "TaskbarDa");
                sb.AppendLine($"TaskbarDa aktuell: {cur ?? "(nicht gesetzt)"}");
                try
                {
                    if (cur is int v) { Reg.Set(adv, "TaskbarDa", v, Microsoft.Win32.RegistryValueKind.DWord); sb.AppendLine("TaskbarDa schreiben: ERLAUBT"); }
                    else sb.AppendLine("TaskbarDa nicht vorhanden – kein Test");
                }
                catch (Exception ex) { sb.AppendLine($"TaskbarDa schreiben: BLOCKIERT ({ex.GetType().Name}: {ex.Message})"); }
                await File.WriteAllTextAsync(outFile, sb.ToString());
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--probe-migration"))
            {
                void Check(string what, bool ok) => sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}");
                string root = Path.Combine(Path.GetTempPath(), "BetterWindowsMigrationTest");
                if (Directory.Exists(root)) Directory.Delete(root, true);
                string src = Path.Combine(root, "local"), dst = Path.Combine(root, "portable");
                Directory.CreateDirectory(Path.Combine(src, "Kontextmenue-Sicherungen"));
                Directory.CreateDirectory(dst);
                File.WriteAllText(Path.Combine(src, "backup.json"), "{\"reg|HKCU\\\\X|Y\":{}}");
                File.WriteAllText(Path.Combine(src, "settings.json"), "{\"Language\":\"de\"}");
                File.WriteAllText(Path.Combine(src, "Kontextmenue-Sicherungen", "a.reg"), "REGEDIT");
                try
                {
                    var note = DataLocation.Migrate(src, dst);
                    Check("Uebernahme meldet Erfolg", note != null && !note.StartsWith("Migration failed"));
                    Check("backup.json kopiert", File.ReadAllText(Path.Combine(dst, "backup.json")).Contains("HKCU"));
                    Check("Unterordner kopiert", File.Exists(Path.Combine(dst, "Kontextmenue-Sicherungen", "a.reg")));
                    Check("Quelle danach entfernt", !Directory.Exists(src));

                    // Fall aus der Praxis: Ziel hat schon eine (leere) backup.json, Quelle echte Eintraege -> zusammenfuehren
                    Directory.Delete(dst, true); Directory.CreateDirectory(dst);
                    File.WriteAllText(Path.Combine(dst, "backup.json"), "{}");
                    File.WriteAllText(Path.Combine(dst, "settings.json"), "{\"Language\":\"en\"}");
                    Directory.CreateDirectory(src);
                    File.WriteAllText(Path.Combine(src, "backup.json"),
                        "{\"reg|HKCU\\\\A|B\":{\"Existed\":true,\"Value\":\"1\",\"Kind\":\"DWord\"}}");
                    File.WriteAllText(Path.Combine(src, "settings.json"), "{\"Language\":\"de\"}");
                    var note2 = DataLocation.Migrate(src, dst);
                    Check("Zusammenfuehren trotz vorhandener leerer backup.json", note2 != null && File.ReadAllText(Path.Combine(dst, "backup.json")).Contains("HKCU\\\\A|B"));
                    Check("vorhandene settings.json im Ziel nicht ueberschrieben", File.ReadAllText(Path.Combine(dst, "settings.json")).Contains("\"en\""));

                    // Quelle laesst sich nicht loeschen -> Markierung verhindert wiederholtes Uebernehmen
                    Directory.CreateDirectory(src);
                    File.WriteAllText(Path.Combine(src, "MIGRATED-TO-PORTABLE.txt"), "x");
                    File.WriteAllText(Path.Combine(src, "backup.json"), "{\"reg|HKCU\\\\Z|Z\":{\"Existed\":false}}");
                    var note3 = DataLocation.Migrate(src, dst);
                    Check("Markierung verhindert erneutes Uebernehmen", note3 == null && !File.ReadAllText(Path.Combine(dst, "backup.json")).Contains("HKCU\\\\Z"));
                }
                catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
                finally { Directory.Delete(root, true); }
                sb.AppendLine($"Datenordner dieses Laufs: {BackupStore.Folder} (portabel: {DataLocation.IsPortable})");
                await File.WriteAllTextAsync(outFile, sb.ToString());
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--probe-onedrive"))
            {
                var (cloudOnly, examples) = AppService.ScanOneDriveCloudOnlyFiles();
                sb.AppendLine($"OneDrive: {cloudOnly} reine Cloud-Dateien");
                foreach (var e in examples) sb.AppendLine("  " + e);
                await File.WriteAllTextAsync(outFile, sb.ToString());
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--probe-gpo"))
            {
                // Rundlauf an einer KOPIE der lokalen Gruppenrichtlinie (Systemdateien bleiben unberuehrt)
                void Check(string what, bool ok) => sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}");
                string copy = Path.Combine(Path.GetTempPath(), "BetterWindowsGpoTest");
                if (Directory.Exists(copy)) Directory.Delete(copy, true);
                Directory.CreateDirectory(Path.Combine(copy, "Machine"));
                string sys = Path.Combine(Environment.SystemDirectory, "GroupPolicy");
                File.Copy(Path.Combine(sys, "Machine", "Registry.pol"), Path.Combine(copy, "Machine", "Registry.pol"));
                File.Copy(Path.Combine(sys, "gpt.ini"), Path.Combine(copy, "gpt.ini"));
                var original = File.ReadAllBytes(Path.Combine(copy, "Machine", "Registry.pol"));
                string Version() => File.ReadAllLines(Path.Combine(copy, "gpt.ini")).First(l => l.StartsWith("Version="));

                LocalGroupPolicy.RootOverride = copy;
                LocalGroupPolicy.SkipRefresh = true;
                try
                {
                    string v0 = Version();
                    Check("Widgets-Richtlinie anfangs nicht gesetzt", !LocalGroupPolicy.Contains(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests"));
                    LocalGroupPolicy.SetDword(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0);
                    Check("nach SetDword vorhanden", LocalGroupPolicy.Contains(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests"));
                    string v1 = Version();
                    Check($"gpt.ini-Version erhoeht ({v0} -> {v1})", v0 != v1);
                    LocalGroupPolicy.Remove(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests");
                    var after = File.ReadAllBytes(Path.Combine(copy, "Machine", "Registry.pol"));
                    Check("nach Remove: Datei byte-identisch zum Original", after.SequenceEqual(original));
                    Check("bestehende Eintraege erhalten", LocalGroupPolicy.Contains(@"Software\Policies\Microsoft\Windows\DeviceInstall\Settings", ""));
                    sb.AppendLine("gpt.ini danach: " + string.Join(" | ", File.ReadAllLines(Path.Combine(copy, "gpt.ini"))));
                }
                catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
                finally { LocalGroupPolicy.RootOverride = null; LocalGroupPolicy.SkipRefresh = false; Directory.Delete(copy, true); }
                await File.WriteAllTextAsync(outFile, sb.ToString());
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--contextmenu"))
            {
                await ContextMenuTest(sb);
                await File.WriteAllTextAsync(outFile, sb.ToString());
                return;
            }
            sb.AppendLine("== Tweaks ==");
            foreach (var t in TweakCatalog.Create())
            {
                var avail = t.Actions.Count(a => a.IsAvailable);
                sb.AppendLine($"[{(t.ComputeApplied() ? "X" : " ")}] {t.Category} | {t.Title} ({avail}/{t.Actions.Length} verfuegbar)");
            }

            sb.AppendLine("== Autostart ==");
            foreach (var i in StartupManager.Enumerate())
                sb.AppendLine($"[{(i.IsEnabled ? "an " : "aus")}] {i.Name} | {i.Publisher} | {i.SourceLabel} | {i.TargetPath} | logo={i.LogoPath != null}");

            sb.AppendLine("== Roundtrip (Wegwerf-Schluessel HKCU\\Software\\BetterWindowsSelfTest) ==");
            sb.AppendLine(Roundtrip());

            sb.AppendLine("== Deinstaller (BCU) ==");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progs = UninstallerService.Load();
            sb.AppendLine($"{progs.Count} Programme in {sw.Elapsed.TotalSeconds:F1}s, davon {progs.Count(p => !p.IsSystem && !p.IsUpdate)} sichtbar");
            sb.AppendLine($"Ansichten: Einfach {progs.Count(p => p.InSimpleView)}, Erweitert {progs.Count(p => p.InMediumView)}, Experte {progs.Count(p => !p.IsSystem && !p.IsUpdate)} (+System/Updates {progs.Count})");
            foreach (var p in progs.Where(p => !p.IsSystem && !p.IsUpdate).Take(400))
                sb.AppendLine($"  [{(p.InSimpleView ? "E" : p.InMediumView ? "M" : "X")}] {p.Name} | {p.Publisher} | {p.SizeText} | {p.Tags}");
            int icons = progs.Take(30).Count(p => UninstallerService.GetIconPng(p) != null);
            sb.AppendLine($"Icons (erste 30): {icons}");

            // Nur lesende Restesuche fuer ein installiertes Programm (es wird nichts geloescht)
            var probe = progs.FirstOrDefault(p => p.Name.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                        ?? progs.First(p => !p.IsSystem);
            sw.Restart();
            var junk = UninstallerService.FindLeftovers([probe], progs);
            sb.AppendLine($"Restesuche (nur lesen) fuer '{probe.Name}': {junk.Count} Funde in {sw.Elapsed.TotalSeconds:F1}s");
            foreach (var j in junk.Take(15)) sb.AppendLine($"  [{j.LevelText}/{j.Raw}] {j.Category} | {j.Path}");

            sb.AppendLine("== Apps ==");
            var installed = await AppService.GetInstalledAsync();
            foreach (var a in AppCatalog.Create())
                sb.AppendLine($"[{(installed.Contains(a.Id) ? "X" : " ")}] {a.Id}");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        await File.WriteAllTextAsync(outFile, sb.ToString());
    }

    private static async Task ContextMenuTest(StringBuilder sb)
    {
        void Check(string what, bool ok) => sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        sb.AppendLine("== Kontextmenue: Windows 11 ==");
        var w11 = await ContextMenuService.LoadWin11Async();
        sb.AppendLine($"{w11.Count} Eintraege ({sw.ElapsedMilliseconds} ms), Logos: {w11.Count(e => ContextMenuService.LoadIcon(e) != null)}");
        foreach (var e in w11) sb.AppendLine($"  [{(e.IsVisible ? "an " : "aus")}] {e.Name} | {e.Detail}");

        sb.AppendLine("== Kontextmenue: klassisch ==");
        foreach (var scene in ContextMenuService.Scenes)
        {
            sw.Restart();
            var list = ContextMenuService.LoadScene(scene);
            int icons = list.Count(e => ContextMenuService.LoadIcon(e) != null);
            sb.AppendLine($"{scene.Name}: {list.Count(e => e.Kind == MenuEntryKind.Command)} Befehle, {list.Count(e => e.Kind == MenuEntryKind.ShellExtension)} Erweiterungen, " +
                          $"{list.Count(e => !e.IsVisible)} ausgeblendet, {icons} Icons ({sw.ElapsedMilliseconds} ms)");
            foreach (var e in list.Take(12)) sb.AppendLine($"  [{(e.IsVisible ? "an " : "aus")}] {e.Name} | {e.Detail}{(e.HasNote ? " | " + e.Note : "")}");
        }

        sw.Restart(); var sendTo = ContextMenuService.LoadSendTo();
        sb.AppendLine($"== Senden an: {sendTo.Count} ({sw.ElapsedMilliseconds} ms) ==");
        foreach (var e in sendTo) sb.AppendLine($"  [{(e.IsVisible ? "an " : "aus")}] {e.Name}");
        sw.Restart(); var shellNew = ContextMenuService.LoadShellNew();
        sb.AppendLine($"== Neu: {shellNew.Count} ({sw.ElapsedMilliseconds} ms) ==");
        foreach (var e in shellNew) sb.AppendLine($"  [{(e.IsVisible ? "an " : "aus")}] {e.Name} | {e.Detail}");
        sw.Restart(); var openWith = ContextMenuService.LoadOpenWith();
        sb.AppendLine($"== Oeffnen mit: {openWith.Count} ({sw.ElapsedMilliseconds} ms), davon {openWith.Count(e => e.CanDelete)} verwaist ==");
        foreach (var e in openWith.Take(15)) sb.AppendLine($"  [{(e.IsVisible ? "an " : "aus")}] {e.Name} | {e.Detail}{(e.HasNote ? " | " + e.Note : "")}");

        // ---------- Rundlauf an Wegwerf-Eintraegen (nur HKCU, wird danach entfernt) ----------
        sb.AppendLine("== Rundlauf (Wegwerf-Eintraege in HKCU) ==");
        const string type = "BetterWindowsTestType", ext = ".bwtest", app = "bwtest.exe";
        try
        {
            Reg.Set($@"HKCU\Software\Classes\{type}\shell\bwverb", "MUIVerb", "BW Testbefehl", Microsoft.Win32.RegistryValueKind.String);
            Reg.Set($@"HKCU\Software\Classes\{type}\shell\bwverb\command", "(default)", "notepad.exe \"%1\"", Microsoft.Win32.RegistryValueKind.String);
            var scene = new MenuScene("Test", "", "", [type]);
            var verb = ContextMenuService.LoadScene(scene).Single(e => e.Name == "BW Testbefehl");
            Check("Befehl gefunden und sichtbar", verb.IsVisible);
            verb.Toggle!(false);
            Check("Ausblenden setzt LegacyDisable", Reg.TryGet($@"HKCU\Software\Classes\{type}\shell\bwverb", "LegacyDisable", out _, out _));
            Check("Neu eingelesen: ausgeblendet", !ContextMenuService.LoadScene(scene).Single(e => e.Name == "BW Testbefehl").IsVisible);
            verb.Toggle!(true);
            Check("Einblenden entfernt LegacyDisable", !Reg.TryGet($@"HKCU\Software\Classes\{type}\shell\bwverb", "LegacyDisable", out _, out _));

            Reg.Set($@"HKCU\Software\Classes\{ext}", "(default)", type, Microsoft.Win32.RegistryValueKind.String);
            Reg.Set($@"HKCU\Software\Classes\{ext}\ShellNew", "NullFile", "", Microsoft.Win32.RegistryValueKind.String);
            var nw = ContextMenuService.LoadShellNew().Single(e => e.Detail.Contains(ext));
            Check("Neu-Eintrag gefunden und sichtbar", nw.IsVisible);
            nw.Toggle!(false);
            Check("Ausblenden benennt ShellNew um", !Reg.KeyExists($@"HKCU\Software\Classes\{ext}\ShellNew") && Reg.KeyExists($@"HKCU\Software\Classes\{ext}\ShellNew_BetterWindowsAus"));
            Check("Neu eingelesen: ausgeblendet", !ContextMenuService.LoadShellNew().Single(e => e.Detail.Contains(ext)).IsVisible);
            nw.Toggle!(true);
            Check("Einblenden stellt ShellNew wieder her", Reg.KeyExists($@"HKCU\Software\Classes\{ext}\ShellNew"));

            Reg.Set($@"HKCU\Software\Classes\Applications\{app}\shell\open\command", "(default)", @"C:\BetterWindowsGibtEsNicht\bwtest.exe ""%1""", Microsoft.Win32.RegistryValueKind.String);
            var ow = ContextMenuService.LoadOpenWith().Single(e => e.Detail.Contains(app));
            Check("Öffnen-mit-Eintrag gefunden, als verwaist erkannt", ow.IsVisible && ow.CanDelete);
            ow.Toggle!(false);
            Check("Ausblenden setzt NoOpenWith", Reg.TryGet($@"HKCU\Software\Classes\Applications\{app}", "NoOpenWith", out _, out _));
            ow.Toggle!(true);
            Check("Einblenden entfernt NoOpenWith", !Reg.TryGet($@"HKCU\Software\Classes\Applications\{app}", "NoOpenWith", out _, out _));
            ow.Delete!();
            Check("Entfernen loescht Schluessel (mit .reg-Sicherung)", !Reg.KeyExists($@"HKCU\Software\Classes\Applications\{app}")
                && Directory.GetFiles(Path.Combine(BackupStore.Folder, "Kontextmenue-Sicherungen"), "*bwtest.exe*.reg").Length > 0);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            foreach (var k in new[] { type, ext, $@"Applications\{app}" })
                try { Reg.DeleteKeyTree($@"HKCU\Software\Classes\{k}"); } catch { }
            foreach (var f in Directory.Exists(Path.Combine(BackupStore.Folder, "Kontextmenue-Sicherungen"))
                         ? Directory.GetFiles(Path.Combine(BackupStore.Folder, "Kontextmenue-Sicherungen"), "*bwtest.exe*.reg") : [])
                File.Delete(f);
        }
        Check("Wegwerf-Eintraege wieder entfernt", !Reg.KeyExists($@"HKCU\Software\Classes\{type}") && !Reg.KeyExists($@"HKCU\Software\Classes\{ext}"));
    }

    private static string Roundtrip()
    {
        const string root = @"HKCU\Software\BetterWindowsSelfTest";
        var log = new StringBuilder();
        void Check(string what, bool ok) => log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        try
        {
            Reg.Set(root, "Existing", "400", Microsoft.Win32.RegistryValueKind.String);
            var t = new Tweak
            {
                Category = "Test", Title = "Test", Description = "",
                Actions =
                [
                    new RegAction(root, "Existing", "100", Microsoft.Win32.RegistryValueKind.String),
                    new RegAction(root + @"\Neu\Tief", "Wert", 1),
                    new RegAction(root + @"\{clsid}\InprocServer32", "(default)", "", Microsoft.Win32.RegistryValueKind.String)
                        { DeleteKeyOnRevert = root + @"\{clsid}" },
                ]
            };
            Check("vorher nicht angewendet", !t.ComputeApplied());
            Check("Apply ohne Fehler", t.Apply().Count == 0);
            Check("danach angewendet", t.ComputeApplied());
            Check("Revert ohne Fehler", t.Revert().Count == 0);
            Check("Originalwert 400 zurueck", (string?)Reg.Get(root, "Existing") == "400");
            Check("neuer Wert entfernt", Reg.Get(root + @"\Neu\Tief", "Wert") == null);
            Check("CLSID-Schluessel entfernt", !Reg.KeyExists(root + @"\{clsid}"));

            var item = new StartupItem { Name = "Test", ApprovedKey = root + @"\Approved", ValueName = "App", Kind = StartupKind.Registry };
            StartupManager.SetEnabled(item, false);
            var bytes = Reg.Get(item.ApprovedKey, "App") as byte[];
            Check("Autostart aus -> 03 + Zeitstempel", bytes is { Length: 12 } && bytes[0] == 3 && BitConverter.ToInt64(bytes, 4) > 0);
            StartupManager.SetEnabled(item, true);
            bytes = Reg.Get(item.ApprovedKey, "App") as byte[];
            Check("Autostart an -> 02", bytes is { Length: 12 } && bytes[0] == 2);
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION: " + ex.Message); }
        finally { try { Reg.DeleteKeyTree(root); } catch { } }
        return log.ToString();
    }
}
