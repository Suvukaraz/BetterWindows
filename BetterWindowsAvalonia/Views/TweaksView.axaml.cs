using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using BetterWindows.Core;
using FluentAvalonia.UI.Controls;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Views;

public partial class TweaksView : UserControl
{
    private readonly List<Tweak> _tweaks = TweakCatalog.Create();
    private readonly Dictionary<Tweak, ToggleSwitch> _switches = new();
    private bool _updating;

    public TweaksView()
    {
        InitializeComponent();
        BuildUi();
        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private void BuildUi()
    {
        foreach (var category in TweakCatalog.Categories)
        {
            var header = Ui.Text(category, "subtitle");
            header.Margin = new Thickness(0, 20, 0, 4);
            CategoriesPanel.Children.Add(header);

            foreach (var tweak in _tweaks.Where(t => t.Category == category))
                CategoriesPanel.Children.Add(BuildCard(tweak));
        }
    }

    private Border BuildCard(Tweak tweak)
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(Ui.Text(tweak.Title, "strong"));
        if (tweak.Recommended) titleRow.Children.Add(Ui.Badge(T("Recommended", "Empfohlen"), accent: true));
        if (tweak.NeedsAdmin) titleRow.Children.Add(Ui.Badge("Admin"));

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleRow);
        text.Children.Add(Ui.Text(tweak.Description, "caption", "secondary"));
        if (tweak.Hint != null) text.Children.Add(Ui.Text(tweak.Hint, "caption", "tertiary"));

        var toggle = new ToggleSwitch
        {
            OnContent = T("On", "An"),
            OffContent = T("Off", "Aus"),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = false,
            Tag = tweak
        };
        if (tweak.NeedsAdmin && !Admin.IsElevated) ToolTip.SetTip(toggle, T("Requires administrator rights", "Benötigt Administratorrechte"));
        toggle.IsCheckedChanged += OnToggled;
        _switches[tweak] = toggle;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        toggle.Margin = new Thickness(16, 0, 0, 0);
        grid.Children.Add(toggle);
        return Ui.Card(grid);
    }

    private async Task RefreshAsync()
    {
        var states = await Task.Run(() => _tweaks.ToDictionary(t => t, t => t.ComputeApplied()));
        _updating = true;
        foreach (var (tweak, applied) in states)
        {
            tweak.IsApplied = applied;
            var ts = _switches[tweak];
            ts.IsChecked = applied;
            // Admin-Schalter bleiben klickbar: ohne Adminrechte erscheint ein Hinweis mit Neustart-Knopf
            ts.IsEnabled = true;
        }
        _updating = false;
        SummaryText.Text = T($"{states.Count(s => s.Value)} of {states.Count} optimizations active", $"{states.Count(s => s.Value)} von {states.Count} Optimierungen aktiv");
    }

    private async void OnToggled(object? sender, RoutedEventArgs e)
    {
        if (_updating || sender is not ToggleSwitch { Tag: Tweak tweak } ts) return;
        bool apply = ts.IsChecked == true;
        if (apply == tweak.IsApplied) return;

        if (tweak.NeedsAdmin && !Admin.IsElevated)
        {
            _updating = true;
            ts.IsChecked = tweak.IsApplied;   // Schalter zuruecksetzen
            _updating = false;
            Ui.ShowAdminRequired(Status, T($"“{tweak.Title}” changes a system-wide Windows setting. Restart BetterWindows as administrator to change it.",
                                           $"„{tweak.Title}“ ändert eine systemweite Windows-Einstellung. Starte BetterWindows als Administrator neu, um sie zu ändern."));
            Status.BringIntoView();
            return;
        }

        await RunAsync(async () =>
        {
            var errors = await Task.Run(() => apply ? tweak.Apply() : tweak.Revert());
            if (errors.Count == 0)
                Ui.Show(Status, FAInfoBarSeverity.Success, apply ? T("Applied", "Angewendet") : T("Reverted", "Zurückgesetzt"),
                    T($"“{tweak.Title}”. Some changes take effect after signing out, restarting Explorer or rebooting.", $"„{tweak.Title}“. Manche Änderungen wirken erst nach Abmelden, Explorer-Neustart oder Neustart."));
            else
                Ui.Show(Status, FAInfoBarSeverity.Warning, T("Partially failed", "Teilweise fehlgeschlagen"), string.Join("\n", errors));
        });
    }

    private async void OnApplyRecommended(object? sender, RoutedEventArgs e)
    {
        var todo = _tweaks.Where(t => t.Recommended && !t.IsApplied && (!t.NeedsAdmin || Admin.IsElevated)).ToList();
        if (todo.Count == 0)
        {
            Ui.Show(Status, FAInfoBarSeverity.Informational, T("Nothing to do", "Nichts zu tun"), T("All recommended optimizations are already active.", "Alle empfohlenen Optimierungen sind bereits aktiv."));
            return;
        }

        string msg = T($"{todo.Count} optimizations will be applied.", $"{todo.Count} Optimierungen werden angewendet.")
                     + (Admin.IsElevated ? T(" A system restore point is created first.", " Vorher wird ein Systemwiederherstellungspunkt erstellt.") : "")
                     + T("\n\nEvery change can be turned off again individually later.", "\n\nJede Änderung kann hier später einzeln wieder ausgeschaltet werden.");
        if (!await Ui.ConfirmAsync(T("Apply recommended optimizations?", "Empfohlene Optimierungen anwenden?"), msg, T("Apply", "Anwenden"))) return;

        await RunAsync(async () =>
        {
            if (Admin.IsElevated)
            {
                Ui.Show(Status, FAInfoBarSeverity.Informational, T("Please wait", "Bitte warten"), T("Creating restore point …", "Wiederherstellungspunkt wird erstellt …"));
                var rp = await SystemTools.CreateRestorePointAsync();
                if (!rp.Lines.Any(l => l.Trim() == "OK") &&
                    !await Ui.ConfirmAsync(T("Restore point failed", "Wiederherstellungspunkt fehlgeschlagen"), string.Join("\n", rp.Lines) + T("\n\nContinue anyway?", "\n\nTrotzdem fortfahren?"), T("Continue", "Fortfahren")))
                    return;
            }

            Ui.Show(Status, FAInfoBarSeverity.Informational, T("Please wait", "Bitte warten"), T("Applying optimizations …", "Optimierungen werden angewendet …"));
            var errors = await Task.Run(() => todo.SelectMany(t => t.Apply()).ToList());
            if (errors.Count == 0)
                Ui.Show(Status, FAInfoBarSeverity.Success, T("Done", "Fertig"), T($"{todo.Count} optimizations applied. Restart your PC for everything to take effect.", $"{todo.Count} Optimierungen angewendet. Starte den PC neu, damit alles wirkt."));
            else
                Ui.Show(Status, FAInfoBarSeverity.Warning, T("Finished with warnings", "Mit Warnungen abgeschlossen"), string.Join("\n", errors));
        });
    }

    private async void OnRestorePoint(object? sender, RoutedEventArgs e)
    {
        if (!Admin.IsElevated)
        {
            Ui.ShowAdminRequired(Status, T("Restart the app as administrator.", "Starte die App als Administrator neu."));
            return;
        }
        await RunAsync(async () =>
        {
            Ui.Show(Status, FAInfoBarSeverity.Informational, T("Please wait", "Bitte warten"), T("Creating restore point …", "Wiederherstellungspunkt wird erstellt …"));
            var rp = await SystemTools.CreateRestorePointAsync();
            bool ok = rp.Lines.Any(l => l.Trim() == "OK");
            Ui.Show(Status, ok ? FAInfoBarSeverity.Success : FAInfoBarSeverity.Error,
                ok ? T("Restore point created", "Wiederherstellungspunkt erstellt") : T("Failed", "Fehlgeschlagen"), ok ? "" : string.Join("\n", rp.Lines));
        });
    }

    private async void OnRestartExplorer(object? sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await SystemTools.RestartExplorerAsync();
            Ui.Show(Status, FAInfoBarSeverity.Success, T("Explorer restarted", "Explorer neu gestartet"), "");
        });
    }

    private async Task RunAsync(Func<Task> work)
    {
        Busy.IsVisible = true;
        ApplyRecommendedButton.IsEnabled = false;
        foreach (var ts in _switches.Values) ts.IsEnabled = false;
        try { await work(); }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Error", "Fehler"), ex.Message); }
        finally
        {
            ApplyRecommendedButton.IsEnabled = true;
            await RefreshAsync();
            Busy.IsVisible = false;
        }
    }
}
