using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using BetterWindows.Core;
using FluentAvalonia.UI.Controls;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Views;

public partial class UninstallerView : UserControl
{
    private List<InstalledProgram> _all = [];
    private readonly ObservableCollection<InstalledProgram> _programs = [];
    private List<LeftoverItem> _leftovers = [];
    private CancellationTokenSource? _cancel;
    private bool _loaded, _busy;
    private string _mode = AppSettings.Current.ProgramsView is "medium" or "expert" ? AppSettings.Current.ProgramsView : "simple";

    public UninstallerView()
    {
        InitializeComponent();
        ProgramList.ItemsSource = _programs;
        ApplyModeUi();
        AttachedToVisualTree += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            if (!Admin.IsElevated)
                Ui.ShowAdminRequired(Status,
                    T("You can browse the list. To uninstall programs and remove leftovers, please restart as administrator.",
                      "Die Liste kannst du ansehen. Zum Deinstallieren und Entfernen von Resten bitte als Administrator neu starten."));
            await LoadAsync();
        };
    }

    // ------------------------------------------------------------ Ansicht (Einfach / Erweitert / Experte)

    private bool IsSimple => _mode == "simple";
    private bool IsExpert => _mode == "expert";

    private void OnModeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string mode }) return;
        _mode = mode;
        AppSettings.Current.ProgramsView = mode;
        AppSettings.Current.Save();
        if (IsSimple) foreach (var p in _all) p.IsSelected = false;
        ApplyModeUi();
        ApplyFilter();
    }

    private void ApplyModeUi()
    {
        SimpleMode.IsChecked = _mode == "simple";
        MediumMode.IsChecked = _mode == "medium";
        ExpertMode.IsChecked = _mode == "expert";
        ModeHint.Text = _mode switch
        {
            "medium" => T("Also shows Store apps, runtimes and orphaned entries. Select several programs to uninstall them in one go.",
                          "Zeigt zusätzlich Store-Apps, Laufzeitbibliotheken und verwaiste Einträge. Mehrere Programme auswählen und gemeinsam deinstallieren."),
            "expert" => T("Everything BetterWindows finds – including system components, updates and leftover search without uninstalling.",
                          "Alles, was BetterWindows findet – inklusive Systemkomponenten, Updates und Restesuche ohne Deinstallation."),
            _ => T("Installed programs, like “Programs and Features” in Windows – without runtimes, drivers and system components.",
                   "Installierte Programme wie unter „Programme und Features“ in Windows – ohne Laufzeitbibliotheken, Treiber und Systemkomponenten.")
        };
        UninstallButton.IsVisible = !IsSimple;
        LeftoversOnlyButton.IsVisible = IsExpert;
        ShowSystemBox.IsVisible = IsExpert;
    }

    // ------------------------------------------------------------ Liste

    private async Task LoadAsync()
    {
        SetBusy(true, T("Looking for installed programs …", "Installierte Programme werden gesucht …"));
        try
        {
            _all = await Task.Run(() => UninstallerService.Load((msg, frac) => Dispatcher.UIThread.Post(() =>
            {
                LoadingText.Text = msg;
                LoadingBar.IsIndeterminate = frac < 0;
                if (frac >= 0) LoadingBar.Value = frac * 100;
            })));
            ApplyFilter();
            _ = LoadIconsAsync(_all);
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not read programs", "Programme konnten nicht gelesen werden"), ex.Message); }
        finally { SetBusy(false); }
    }

    private static async Task LoadIconsAsync(List<InstalledProgram> items)
    {
        foreach (var p in items)
        {
            var icon = await Task.Run(() => Ui.FromPng(UninstallerService.GetIconPng(p)));
            if (icon != null) p.Icon = icon;
        }
    }

    private void ApplyFilter()
    {
        string q = SearchBox.Text?.Trim() ?? "";
        bool showSystem = ShowSystemBox.IsChecked == true;
        IEnumerable<InstalledProgram> view = _all.Where(p =>
            (_mode switch
            {
                "simple" => p.InSimpleView,
                "medium" => p.InMediumView,
                _ => showSystem || (!p.IsSystem && !p.IsUpdate)
            }) &&
            (q.Length == 0 || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                           || p.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase)));

        view = SortBox.SelectedIndex switch
        {
            1 => view.OrderByDescending(p => p.SizeKb),
            2 => view.OrderByDescending(p => p.InstallDate),
            3 => view.OrderBy(p => p.Publisher, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Name),
            _ => view.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        _programs.Clear();
        foreach (var p in view)
        {
            p.ShowCheckBox = !IsSimple;
            p.ShowDetails = !IsSimple;
            p.ShowUninstallButton = IsSimple;
            _programs.Add(p);
        }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var sel = _all.Where(p => p.IsSelected).ToList();
        long totalKb = _programs.Sum(p => Math.Max(0, p.SizeKb));
        SummaryText.Text = T($"{_programs.Count} programs", $"{_programs.Count} Programme")
                           + (totalKb > 0 ? "  ·  " + UninstallerService.FormatKb(totalKb) : "")
                           + (sel.Count > 0 ? T($"  ·  {sel.Count} selected", $"  ·  {sel.Count} ausgewählt") : "");
        UninstallButtonText.Text = sel.Count > 0 ? T($"Uninstall {sel.Count}", $"{sel.Count} deinstallieren") : T("Uninstall", "Deinstallieren");
        bool can = sel.Count > 0 && !_busy && Admin.IsElevated;
        UninstallButton.IsEnabled = can;
        LeftoversOnlyButton.IsEnabled = can;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        LoadingPanel.IsVisible = busy;
        LoadingBar.IsIndeterminate = true;
        ModePanel.IsEnabled = !busy;
        if (text != null) LoadingText.Text = text;
        UpdateSelection();
    }

    private void ShowView(Grid view)
    {
        ProgramsView.IsVisible = view == ProgramsView;
        RunView.IsVisible = view == RunView;
        LeftoverView.IsVisible = view == LeftoverView;
    }

    private void OnProgramChecked(object? sender, RoutedEventArgs e)
    {
        // Eintrag ueber DataContext bestimmen, nicht ueber Tag (siehe StartupView.OnItemToggled)
        if (sender is CheckBox { DataContext: InstalledProgram p } cb) p.IsSelected = cb.IsChecked == true;
        UpdateSelection();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) { if (_loaded) ApplyFilter(); }
    private void OnSortChanged(object? sender, SelectionChangedEventArgs e) { if (_loaded) ApplyFilter(); }
    private void OnFilterChanged(object? sender, RoutedEventArgs e) { if (_loaded) ApplyFilter(); }
    private async void OnRefresh(object? sender, RoutedEventArgs e) { if (!_busy) await LoadAsync(); }

    // ------------------------------------------------------------ Deinstallation

    private async void OnUninstallSelected(object? sender, RoutedEventArgs e) =>
        await UninstallAsync(_all.Where(p => p.IsSelected).ToList());

    private async void OnUninstallSingle(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is InstalledProgram p) await UninstallAsync([p]);
    }

    private async Task UninstallAsync(List<InstalledProgram> targets)
    {
        if (targets.Count == 0 || _busy) return;
        if (!Admin.IsElevated)
        {
            Ui.ShowAdminRequired(Status,
                T("Please restart BetterWindows as administrator to uninstall programs.", "Bitte BetterWindows als Administrator neu starten, um Programme zu deinstallieren."));
            return;
        }

        var quietBox = new CheckBox { Content = T("Uninstall silently if the program supports it", "Still deinstallieren, wenn das Programm es unterstützt"), IsChecked = true };
        var restoreBox = new CheckBox { Content = T("Create a restore point first", "Vorher Wiederherstellungspunkt erstellen"), IsChecked = true };
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = string.Join("\n", targets.Take(12).Select(t => "• " + t.Name))
                   + (targets.Count > 12 ? T($"\n… and {targets.Count - 12} more", $"\n… und {targets.Count - 12} weitere") : ""),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(quietBox);
        panel.Children.Add(restoreBox);
        panel.Children.Add(Ui.Text(T("Afterwards BetterWindows searches for leftovers automatically. You decide what gets deleted.",
                                     "Danach wird automatisch nach Resten gesucht. Du entscheidest, was gelöscht wird."), "caption", "secondary"));

        string title = targets.Count == 1
            ? T($"Uninstall “{targets[0].Name}”?", $"„{targets[0].Name}“ deinstallieren?")
            : T($"Uninstall {targets.Count} programs?", $"{targets.Count} Programme deinstallieren?");
        if (!await Ui.ConfirmAsync(title, new ScrollViewer { Content = panel, MaxHeight = 420 }, T("Uninstall", "Deinstallieren"))) return;

        SetBusy(true);
        try
        {
            if (restoreBox.IsChecked == true)
            {
                LoadingText.Text = T("Creating restore point …", "Wiederherstellungspunkt wird erstellt …");
                var rp = await SystemTools.CreateRestorePointAsync();
                if (!rp.Lines.Any(l => l.Trim() == "OK") &&
                    !await Ui.ConfirmAsync(T("Restore point failed", "Wiederherstellungspunkt fehlgeschlagen"),
                        string.Join("\n", rp.Lines) + T("\n\nContinue anyway?", "\n\nTrotzdem fortfahren?"), T("Continue", "Fortfahren")))
                    return;
            }

            foreach (var t in targets) t.RunState = T("Waiting", "Wartet");
            RunList.ItemsSource = targets;
            ShowView(RunView);
            CancelButton.IsEnabled = true;
            LoadingText.Text = T("Follow the uninstaller windows if any appear.", "Folge den Fenstern der Deinstallationsprogramme, falls welche erscheinen.");
            Status.IsOpen = false;

            _cancel = new CancellationTokenSource();
            var outcome = await UninstallerService.UninstallAsync(targets, quietBox.IsChecked == true,
                a => Dispatcher.UIThread.Post(a), _cancel.Token);
            CancelButton.IsEnabled = false;

            Ui.Show(Status, outcome.Failed > 0 ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success,
                T("Uninstall finished", "Deinstallation abgeschlossen"),
                T($"{outcome.Completed} successful", $"{outcome.Completed} erfolgreich")
                + (outcome.Failed > 0 ? T($", {outcome.Failed} failed", $", {outcome.Failed} fehlgeschlagen") : "")
                + T(". Now searching for leftovers …", ". Jetzt wird nach Resten gesucht …"));

            if (outcome.LeftoverTargets.Count == 0)
            {
                Ui.Show(Status, FAInfoBarSeverity.Informational, T("No leftover search", "Keine Restesuche"),
                    T("No program was removed completely.", "Kein Programm wurde vollständig entfernt."));
                await BackToListAsync(keepStatus: true);
                return;
            }
            await SearchLeftoversAsync(outcome.LeftoverTargets);
        }
        catch (Exception ex)
        {
            Ui.Show(Status, FAInfoBarSeverity.Error, T("Uninstall error", "Fehler bei der Deinstallation"), ex.Message);
            await BackToListAsync(keepStatus: true);
        }
        finally { SetBusy(false); }
    }

    private async void OnLeftoversOnly(object? sender, RoutedEventArgs e)
    {
        var targets = _all.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) return;
        if (!await Ui.ConfirmAsync(T("Search for leftovers only?", "Nur nach Resten suchen?"),
                T("No uninstaller is started. Files and registry entries that are found are listed; you decide what gets deleted.\n\n"
                  + "Caution: if the program is still installed, many findings belong to the running program.",
                  "Es wird kein Deinstaller gestartet. Gefundene Dateien und Registry-Einträge werden aufgelistet; du entscheidest, was gelöscht wird.\n\n"
                  + "Achtung: Ist das Programm noch installiert, gehören viele Funde zum laufenden Programm."),
                T("Search", "Suchen")))
            return;

        SetBusy(true);
        try { await SearchLeftoversAsync(targets); }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Search error", "Fehler bei der Suche"), ex.Message); }
        finally { SetBusy(false); }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        CancelButton.IsEnabled = false;
        LoadingText.Text = T("Cancelling after the current program …", "Wird nach dem aktuellen Programm abgebrochen …");
    }

    // ------------------------------------------------------------ Reste

    private async Task SearchLeftoversAsync(List<InstalledProgram> targets)
    {
        _busy = true;
        LoadingPanel.IsVisible = true;
        LoadingBar.IsIndeterminate = true;
        LoadingText.Text = T("Searching for leftovers …", "Suche nach Resten …");
        var all = _all.ToList();
        _leftovers = await Task.Run(() => UninstallerService.FindLeftovers(targets, all,
            msg => Dispatcher.UIThread.Post(() => LoadingText.Text = T("Searching for leftovers: ", "Suche nach Resten: ") + msg)));

        LeftoverTitle.Text = _leftovers.Count == 0
            ? T("No leftovers found", "Keine Reste gefunden")
            : T($"{_leftovers.Count} possible leftovers found", $"{_leftovers.Count} mögliche Reste gefunden");
        ShowRiskyBox.IsChecked = false;
        ApplyLeftoverFilter();
        ShowView(LeftoverView);
        if (_leftovers.Count == 0)
            Ui.Show(Status, FAInfoBarSeverity.Success, T("Clean", "Sauber"), T("No leftovers were found.", "Es wurden keine Reste gefunden."));
    }

    private void ApplyLeftoverFilter()
    {
        bool risky = ShowRiskyBox.IsChecked == true;
        foreach (var l in _leftovers.Where(l => !risky && l.Raw < 0)) l.IsSelected = false;
        LeftoverList.ItemsSource = _leftovers.Where(l => risky || l.Raw >= 0).ToList();
        UpdateLeftoverButton();
    }

    private void UpdateLeftoverButton()
    {
        int n = _leftovers.Count(l => l.IsSelected);
        RemoveLeftoversText.Text = n > 0 ? T($"Remove {n} leftovers", $"{n} Reste entfernen") : T("Remove selected leftovers", "Ausgewählte Reste entfernen");
        RemoveLeftoversButton.IsEnabled = n > 0 && Admin.IsElevated;
    }

    private void OnLeftoverChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: LeftoverItem l } cb) l.IsSelected = cb.IsChecked == true;
        UpdateLeftoverButton();
    }

    private void OnLeftoverFilterChanged(object? sender, RoutedEventArgs e) => ApplyLeftoverFilter();

    private async void OnRemoveLeftovers(object? sender, RoutedEventArgs e)
    {
        var selected = _leftovers.Where(l => l.IsSelected).ToList();
        if (selected.Count == 0) return;
        int unsafeCount = selected.Count(l => !l.IsSafe);
        string msg = T($"{selected.Count} entries are backed up first and then deleted.\n\nBackup: ",
                       $"{selected.Count} Einträge werden zuerst gesichert und dann gelöscht.\n\nSicherung: ")
                     + $"{BackupStore.Folder}\\Deinstallations-Sicherungen"
                     + (unsafeCount > 0 ? T($"\n\nCaution: {unsafeCount} of them are rated questionable or risky.",
                                            $"\n\nAchtung: {unsafeCount} davon sind als fraglich oder riskant eingestuft.") : "");
        if (!await Ui.ConfirmAsync(T("Remove leftovers?", "Reste entfernen?"), msg, T("Back up and delete", "Sichern und löschen"))) return;

        SetBusy(true, T("Backing up and removing leftovers …", "Reste werden gesichert und entfernt …"));
        RemoveLeftoversButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => UninstallerService.RemoveLeftovers(selected,
                (i, n) => Dispatcher.UIThread.Post(() => LoadingText.Text = T($"Removing {i} of {n} …", $"Entferne {i} von {n} …"))));
            if (result.Errors.Count == 0)
                Ui.Show(Status, FAInfoBarSeverity.Success, T("Leftovers removed", "Reste entfernt"),
                    T($"{result.Removed} entries removed. Backup: ", $"{result.Removed} Einträge entfernt. Sicherung: ") + result.BackupDir);
            else
                Ui.Show(Status, FAInfoBarSeverity.Warning,
                    T($"{result.Removed} removed, {result.Errors.Count} problems", $"{result.Removed} entfernt, {result.Errors.Count} Probleme"),
                    string.Join("\n", result.Errors.Take(8)) + (result.Errors.Count > 8 ? "\n…" : "")
                    + T("\n\nBackup: ", "\n\nSicherung: ") + result.BackupDir);
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Error while removing", "Fehler beim Entfernen"), ex.Message); }
        finally { SetBusy(false); }
        await BackToListAsync(keepStatus: true);
    }

    private async void OnSkipLeftovers(object? sender, RoutedEventArgs e) => await BackToListAsync();

    private async Task BackToListAsync(bool keepStatus = false)
    {
        _leftovers = [];
        LeftoverList.ItemsSource = null;
        RunList.ItemsSource = null;
        ShowView(ProgramsView);
        if (!keepStatus && Status.Severity != FAInfoBarSeverity.Error) Status.IsOpen = false;
        await LoadAsync();
    }
}
