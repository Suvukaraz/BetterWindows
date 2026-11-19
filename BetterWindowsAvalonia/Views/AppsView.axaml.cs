using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using BetterWindows.Core;
using FluentAvalonia.UI.Controls;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Views;

public partial class AppsView : UserControl
{
    private readonly List<RemovableApp> _apps = AppCatalog.Create();
    private bool _loaded, _busy;

    public AppsView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshAsync();
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Busy.IsVisible = busy;
        UpdateButton();
    }

    private async Task RefreshAsync()
    {
        SetBusy(true);
        try
        {
            var installed = await AppService.GetInstalledAsync();
            foreach (var app in _apps)
            {
                app.IsInstalled = installed.Contains(app.Id);
                app.IsSelected = app.IsInstalled && app.Recommended;
            }
            Render();
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not read apps", "Fehler beim Lesen der Apps"), ex.Message); }
        finally { SetBusy(false); }
    }

    private void Render()
    {
        InstalledPanel.Children.Clear();
        MissingPanel.Children.Clear();

        foreach (var app in _apps.Where(a => a.IsInstalled))
        {
            var check = new CheckBox { IsChecked = app.IsSelected, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            check.IsCheckedChanged += (_, _) => { app.IsSelected = check.IsChecked == true; UpdateButton(); };

            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(Ui.Text(app.Name, "strong"));
            if (!app.Recommended) title.Children.Add(Ui.Badge(T("Optional", "Optional")));
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(Ui.Text(app.Description, "caption", "secondary"));

            var id = Ui.Text(app.Id, "caption", "tertiary");
            id.VerticalAlignment = VerticalAlignment.Center;
            id.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(check);
            text.Margin = new Thickness(8, 0, 12, 0);
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            Grid.SetColumn(id, 2); grid.Children.Add(id);
            InstalledPanel.Children.Add(Ui.Card(grid));
        }

        foreach (var app in _apps.Where(a => !a.IsInstalled))
            MissingPanel.Children.Add(Ui.Text($"{app.Name}  ({app.Id})", "secondary"));

        NoneText.IsVisible = !_apps.Any(a => a.IsInstalled);
        UpdateButton();
    }

    private void UpdateButton()
    {
        int n = _apps.Count(a => a.IsInstalled && a.IsSelected);
        RemoveButtonText.Text = n > 0 ? T($"Remove {n} selected", $"{n} ausgewählte entfernen") : T("Remove selected", "Ausgewählte entfernen");
        RemoveButton.IsEnabled = n > 0 && !_busy;
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) { if (!_busy) await RefreshAsync(); }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (!Admin.IsElevated)
        {
            Ui.ShowAdminRequired(Status,
                T("To remove apps for all users, please restart the app as administrator.", "Zum Entfernen von Apps für alle Benutzer bitte die App als Administrator neu starten."));
            return;
        }

        var selected = _apps.Where(a => a.IsInstalled && a.IsSelected).ToList();
        string list = string.Join("\n", selected.Select(a => "• " + a.Name));

        // OneDrive: vor reinen Cloud-Dateien warnen (sind danach lokal nicht mehr oeffnbar)
        if (selected.Any(a => a.Id == AppCatalog.OneDriveId))
        {
            var (cloudOnly, examples) = await Task.Run(AppService.ScanOneDriveCloudOnlyFiles);
            if (cloudOnly > 0)
                list += T($"\n\n⚠ OneDrive: {cloudOnly} files in your OneDrive folder exist only in the cloud, e.g.:\n",
                          $"\n\n⚠ OneDrive: {cloudOnly} Dateien in deinem OneDrive-Ordner liegen nur in der Cloud, z. B.:\n")
                        + string.Join("\n", examples.Select(e => "   " + e))
                        + T("\n\nThey stay safe in your OneDrive account, but can no longer be opened on this PC afterwards. "
                            + "To keep local copies, first right-click the OneDrive folder → “Always keep on this device” and wait for the download.",
                            "\n\nSie bleiben in deinem OneDrive-Konto erhalten, lassen sich danach auf diesem PC aber nicht mehr öffnen. "
                            + "Für lokale Kopien vorher Rechtsklick auf den OneDrive-Ordner → „Immer auf diesem Gerät beibehalten“ und den Download abwarten.");
        }

        if (!await Ui.ConfirmAsync(T($"Remove {selected.Count} apps?", $"{selected.Count} Apps entfernen?"),
                new ScrollViewer { MaxHeight = 440, Content = new TextBlock { Text = list, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 520 } },
                T("Remove", "Entfernen"))) return;

        SetBusy(true);
        LogBox.Text = "";
        LogBox.IsVisible = true;
        Ui.Show(Status, FAInfoBarSeverity.Informational, T("Removing apps", "Apps werden entfernt"), T("This can take a minute or two …", "Das kann ein bis zwei Minuten dauern …"));

        void Log(string line) => Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });

        try
        {
            await AppService.RemoveAsync(selected.Select(a => a.Id).ToList(), Log);
            Ui.Show(Status, FAInfoBarSeverity.Success, T("Done", "Fertig"), T("Details are in the log below.", "Details stehen im Protokoll unten."));
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Error", "Fehler"), ex.Message); }
        finally { SetBusy(false); }

        await RefreshAsync();
    }
}
