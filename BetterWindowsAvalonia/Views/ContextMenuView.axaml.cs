using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using BetterWindows.Core;
using FluentAvalonia.UI.Controls;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Views;

public partial class ContextMenuView : UserControl
{
    private readonly Tweak? _classicMenuTweak = TweakCatalog.Create().FirstOrDefault(t => t.Id == TweakCatalog.ClassicContextMenuId);

    // Je Tab: alle geladenen Eintraege (fuer die Suche) und die Liste in der Oberflaeche
    private readonly Dictionary<ItemsControl, List<MenuEntry>> _data = new();
    private readonly HashSet<ItemsControl> _loadedLists = [];
    private bool _initialized, _updatingSwitch;

    public ContextMenuView()
    {
        InitializeComponent();
        SceneList.ItemsSource = ContextMenuService.Scenes;

        // optional fuer Tests: --tab 0..4
        var cli = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(cli, a => a.Equals("--tab", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < cli.Length && int.TryParse(cli[i + 1], out var tab)) Tabs.SelectedIndex = tab;
        AttachedToVisualTree += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            RefreshClassicSwitch();
            await LoadTabAsync(force: false);
        };
    }

    private ItemsControl CurrentList => Tabs.SelectedIndex switch
    {
        1 => ClassicList,
        2 => SendToList,
        3 => ShellNewList,
        4 => OpenWithList,
        _ => Win11List
    };

    private async Task LoadTabAsync(bool force)
    {
        var list = CurrentList;
        if (list == ClassicList)
        {
            if (SceneList.SelectedIndex < 0) { SceneList.SelectedIndex = 0; return; } // loest OnSceneChanged aus
            if (!force && _loadedLists.Contains(list)) return;
            await LoadSceneAsync();
            return;
        }
        if (!force && _loadedLists.Contains(list)) return;

        Busy.IsVisible = true;
        try
        {
            List<MenuEntry> entries = list == Win11List ? await ContextMenuService.LoadWin11Async()
                : list == SendToList ? await Task.Run(ContextMenuService.LoadSendTo)
                : list == ShellNewList ? await Task.Run(ContextMenuService.LoadShellNew)
                : await Task.Run(ContextMenuService.LoadOpenWith);
            await ShowEntriesAsync(list, entries);
            _loadedLists.Add(list);
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not read", "Fehler beim Lesen"), ex.Message); }
        finally { Busy.IsVisible = false; }
    }

    private async Task LoadSceneAsync()
    {
        if (SceneList.SelectedItem is not MenuScene scene) return;
        Busy.IsVisible = true;
        try
        {
            SceneHint.Text = scene.Description + "  ·  " + string.Join(", ", scene.Paths.Select(p => @"HKEY_CLASSES_ROOT\" + p));
            var entries = await Task.Run(() => ContextMenuService.LoadScene(scene));
            await ShowEntriesAsync(ClassicList, entries);
            _loadedLists.Add(ClassicList);
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not read", "Fehler beim Lesen"), ex.Message); }
        finally { Busy.IsVisible = false; }
    }

    private async Task ShowEntriesAsync(ItemsControl list, List<MenuEntry> entries)
    {
        _data[list] = entries;
        ApplySearch(list);
        var icons = await Task.Run(() => entries.Select(e => (e, ContextMenuService.LoadIcon(e))).ToList());
        foreach (var (e, icon) in icons) e.Icon = icon;
    }

    private void ApplySearch(ItemsControl list)
    {
        if (!_data.TryGetValue(list, out var entries)) return;
        string q = SearchBox.Text?.Trim() ?? "";
        list.ItemsSource = q.Length == 0 ? entries : entries.Where(e =>
            e.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
            e.Detail.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
            e.ToolTipText.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    // ------------------------------------------------------------ Klassisches Menue statt Win11-Menue

    private void RefreshClassicSwitch()
    {
        if (_classicMenuTweak == null) { ClassicMenuSwitch.IsEnabled = false; return; }
        _updatingSwitch = true;
        ClassicMenuSwitch.IsChecked = _classicMenuTweak.ComputeApplied();
        _updatingSwitch = false;
    }

    private void OnClassicMenuToggled(object? sender, RoutedEventArgs e)
    {
        if (_updatingSwitch || _classicMenuTweak == null) return;
        bool on = ClassicMenuSwitch.IsChecked == true;
        var errors = on ? _classicMenuTweak.Apply() : _classicMenuTweak.Revert();
        if (errors.Count > 0) Ui.Show(Status, FAInfoBarSeverity.Warning, T("Not changed", "Nicht geändert"), string.Join("\n", errors));
        else Ui.Show(Status, FAInfoBarSeverity.Success, on ? T("Classic menu enabled", "Klassisches Menü aktiviert") : T("Windows 11 menu enabled", "Windows-11-Menü aktiviert"),
            T("Takes effect after restarting Explorer (button above).", "Wirkt nach einem Explorer-Neustart (Schaltfläche oben)."));
        RefreshClassicSwitch();
    }

    // ------------------------------------------------------------ Eintraege

    private void OnEntryToggled(object? sender, RoutedEventArgs e)
    {
        // Eintrag ueber DataContext bestimmen, nicht ueber Tag (siehe StartupView.OnItemToggled)
        if (sender is not ToggleSwitch { DataContext: MenuEntry entry } ts) return;
        bool visible = ts.IsChecked == true;
        if (visible == entry.IsVisible || entry.Toggle == null) return; // Aenderung kam aus der Bindung
        try
        {
            entry.Toggle(visible);
            entry.IsVisible = visible;
            Ui.Show(Status, FAInfoBarSeverity.Success, visible ? T("Shown", "Eingeblendet") : T("Hidden", "Ausgeblendet"),
                T($"“{entry.Name}” – visible in new Explorer windows or after restarting Explorer.", $"„{entry.Name}“ – sichtbar in neuen Explorer-Fenstern bzw. nach einem Explorer-Neustart."));
        }
        catch (Exception ex)
        {
            ts.IsChecked = entry.IsVisible;
            Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not be changed", "Konnte nicht geändert werden"),
                ex is UnauthorizedAccessException or System.Security.SecurityException
                    ? (ex.Message.Length > 0 && !ex.Message.StartsWith("Access") ? ex.Message : T("Access denied – please run as administrator.", "Zugriff verweigert – bitte als Administrator starten."))
                    : ex.Message);
        }
    }

    private async void OnCopyRegPath(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not MenuEntry { RegPath: { } path }) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(path);
        Ui.Show(Status, FAInfoBarSeverity.Informational, T("Copied", "Kopiert"), path);
    }

    private void OnOpenRegedit(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not MenuEntry { RegPath: { } path }) return;
        try { ContextMenuService.OpenInRegedit(path); }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Registry Editor", "Registrierungs-Editor"), ex.Message); }
    }

    private void OnOpenLocation(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MenuEntry { FilePath: { } path }) SystemTools.ShowInExplorer(path);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not MenuEntry { Delete: { } delete } entry) return;
        string what = entry.Kind == MenuEntryKind.SendTo
            ? T("The file is moved to the backup folder.", "Die Datei wird in den Sicherungsordner verschoben.")
            : T("The registry key is backed up as a .reg file and then deleted.", "Der Registry-Schlüssel wird als .reg-Datei gesichert und dann gelöscht.");
        if (!await Ui.ConfirmAsync(T($"Remove “{entry.Name}”?", $"„{entry.Name}“ entfernen?"),
                what + T("\n\nHiding is usually enough. Removing is mainly meant for leftovers of uninstalled programs.\n\nBackup: ", "\n\nMeist reicht Ausblenden. Entfernen ist vor allem für Reste deinstallierter Programme gedacht.\n\nSicherung: ") + $"{BackupStore.Folder}\\Kontextmenue-Sicherungen",
                T("Remove", "Entfernen")))
            return;
        try
        {
            delete();
            var list = CurrentList;
            if (_data.TryGetValue(list, out var entries)) entries.Remove(entry);
            ApplySearch(list);
            Ui.Show(Status, FAInfoBarSeverity.Success, T("Removed", "Entfernt"), T($"“{entry.Name}” was removed (backup created).", $"„{entry.Name}“ wurde entfernt (Sicherung angelegt)."));
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not be removed", "Konnte nicht entfernt werden"), ex.Message); }
    }

    // ------------------------------------------------------------ Navigation / Werkzeugleiste

    private async void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || e.Source != Tabs) return;
        await LoadTabAsync(force: false);
        ApplySearch(CurrentList);
    }

    private async void OnSceneChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized) await LoadSceneAsync();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplySearch(CurrentList);

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        RefreshClassicSwitch();
        await LoadTabAsync(force: true);
    }

    private async void OnRestartExplorer(object? sender, RoutedEventArgs e)
    {
        Busy.IsVisible = true;
        try
        {
            await SystemTools.RestartExplorerAsync();
            Ui.Show(Status, FAInfoBarSeverity.Success, T("Explorer restarted", "Explorer neu gestartet"), T("Context menu changes are now visible.", "Änderungen am Kontextmenü sind jetzt sichtbar."));
        }
        finally { Busy.IsVisible = false; }
    }
}
