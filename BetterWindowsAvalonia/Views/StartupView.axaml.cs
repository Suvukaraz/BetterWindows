using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using BetterWindows.Core;
using FluentAvalonia.UI.Controls;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Views;

public partial class StartupView : UserControl
{
    private List<StartupItem> _all = [];
    private readonly ObservableCollection<StartupItem> _items = [];
    private bool _loaded;

    public StartupView()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;

        // optional (u. a. fuer Tests): BetterWindows.exe --page startup --search <Text>
        var cli = Environment.GetCommandLineArgs();
        int si = Array.FindIndex(cli, a => a.Equals("--search", StringComparison.OrdinalIgnoreCase));
        if (si >= 0 && si + 1 < cli.Length) SearchBox.Text = cli[si + 1];
        AttachedToVisualTree += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshAsync();
        };
    }

    private async Task RefreshAsync()
    {
        try
        {
            _all = await Task.Run(StartupManager.Enumerate);
            ApplyFilter();
            // Icons im Hintergrund erzeugen
            var icons = await Task.Run(() => _all.Select(i => (i, i.LogoPath != null ? Ui.ImageFile(i.LogoPath) : Ui.FileIcon(i.TargetPath))).ToList());
            foreach (var (item, icon) in icons) item.Icon = icon;
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not read", "Fehler beim Lesen"), ex.Message); }
    }

    private void ApplyFilter()
    {
        string q = SearchBox.Text?.Trim() ?? "";
        int mode = FilterBox.SelectedIndex;
        var view = _all.Where(i =>
                (mode <= 0 || (mode == 1 && i.IsEnabled) || (mode == 2 && !i.IsEnabled)) &&
                (q.Length == 0
                 || i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                 || (i.Publisher?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
                 || i.Command.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();

        _items.Clear();
        foreach (var i in view) _items.Add(i);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int active = _all.Count(i => i.IsEnabled);
        SummaryText.Text = T($"{_all.Count} entries  ·  {active} enabled  ·  {_all.Count - active} disabled", $"{_all.Count} Einträge  ·  {active} aktiv  ·  {_all.Count - active} deaktiviert");
    }

    private void OnItemToggled(object? sender, RoutedEventArgs e)
    {
        // WICHTIG: Eintrag ueber DataContext bestimmen, NICHT ueber Tag. Beim Scrollen werden Zeilen wiederverwendet;
        // der DataContext wechselt dabei zuerst, die Tag-Bindung evtl. erst nach IsChecked -> sonst wird der
        // Zustand des neuen Eintrags auf den alten angewendet (Fehler "Scrollen schaltet Eintraege um").
        if (sender is not ToggleSwitch { DataContext: StartupItem item } ts) return;
        bool on = ts.IsChecked == true;
        if (on == item.IsEnabled) return; // Aenderung kam aus der Bindung/Wiederverwendung, nicht vom Benutzer
        try
        {
            StartupManager.SetEnabled(item, on);
            Ui.Show(Status, FAInfoBarSeverity.Success, on ? T("Enabled", "Aktiviert") : T("Disabled", "Deaktiviert"),
                on ? T($"“{item.Name}” starts automatically at sign-in again.", $"„{item.Name}“ startet wieder automatisch bei der Anmeldung.") : T($"“{item.Name}” no longer starts automatically at sign-in.", $"„{item.Name}“ startet nicht mehr automatisch bei der Anmeldung."));
            UpdateSummary();
        }
        catch (Exception ex)
        {
            ts.IsChecked = item.IsEnabled;
            Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not be changed", "Konnte nicht geändert werden"),
                ex is UnauthorizedAccessException ? T("Access denied – please run as administrator.", "Zugriff verweigert – bitte als Administrator starten.") : ex.Message);
        }
    }

    private void OnOpenLocation(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not StartupItem item) return;
        var path = item.TargetPath != null && File.Exists(item.TargetPath) ? item.TargetPath : item.FilePath;
        if (path != null) SystemTools.ShowInExplorer(path);
    }

    private async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not StartupItem item) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(item.Command);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not StartupItem item) return;
        string where = item.Kind == StartupKind.Folder
            ? T("The shortcut is moved to the backup folder.", "Die Verknüpfung wird in den Sicherungsordner verschoben.")
            : T("The registry entry is backed up and then deleted.", "Der Registry-Eintrag wird gesichert und dann gelöscht.");
        bool ok = await Ui.ConfirmAsync(T($"Remove “{item.Name}”?", $"„{item.Name}“ entfernen?"),
            where + T("\n\nTip: Turning it off is usually enough. Removing only makes sense for leftovers of uninstalled programs.\n\nBackup: ", "\n\nTipp: Meist reicht Ausschalten. Entfernen ist nur für Reste deinstallierter Programme sinnvoll.\n\nSicherung: ") + BackupStore.Folder,
            T("Remove", "Entfernen"));
        if (!ok) return;

        try
        {
            StartupManager.Delete(item);
            _all.Remove(item);
            _items.Remove(item);
            UpdateSummary();
            Ui.Show(Status, FAInfoBarSeverity.Success, T("Removed", "Entfernt"), T($"“{item.Name}” was removed from startup.", $"„{item.Name}“ wurde aus dem Autostart entfernt."));
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not be removed", "Konnte nicht entfernt werden"), ex.Message); }
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) { if (_loaded) ApplyFilter(); }
    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) { if (_loaded) ApplyFilter(); }
    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();
}
