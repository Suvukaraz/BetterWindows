using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using BetterWindows.Core;
using FluentAvalonia.UI.Controls;
using static BetterWindows.Core.Loc;

namespace BetterWindows.Views;

public partial class BackupsView : UserControl
{
    public BackupsView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var locations = BackupLocations.All();
        var stats = await Task.Run(() => locations.Select(l => (l, l.Measure())).ToList());

        CardsPanel.Children.Clear();
        foreach (var (loc, m) in stats)
            CardsPanel.Children.Add(BuildCard(loc, m));

        long total = stats.Where(s => s.l.CanEmpty).Sum(s => s.Item2.Bytes);
        int files = stats.Where(s => s.l.CanEmpty).Sum(s => s.Item2.Files);
        SummaryText.Text = T($"{files} files · {FormatSize(total)} in backups", $"{files} Dateien · {FormatSize(total)} in Sicherungen");
    }

    private Border BuildCard(BackupLocation loc, (int Files, long Bytes, DateTime? Newest) m)
    {
        var icon = Ui.Text(loc.Glyph, "icon");
        icon.FontSize = 20;
        icon.Margin = new Thickness(0, 0, 16, 0);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(loc.Title, "strong"));
        text.Children.Add(Ui.Text(loc.Description, "caption", "secondary"));
        string stats = m.Files == 0
            ? T("empty", "leer")
            : T($"{m.Files} files · {FormatSize(m.Bytes)}", $"{m.Files} Dateien · {FormatSize(m.Bytes)}")
              + (m.Newest is { } d ? T($" · last change {d:g}", $" · zuletzt geändert {d:g}") : "");
        text.Children.Add(Ui.Text(stats, "caption", "tertiary"));
        var path = Ui.Text(loc.OpenPath, "caption", "tertiary", "mono");
        path.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        path.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        text.Children.Add(path);

        var open = new Button { Content = Ui.IconText("", T("Open", "Öffnen")), IsEnabled = m.Files > 0 };
        open.Click += (_, _) => SystemTools.ShowInExplorer(loc.OpenPath);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        buttons.Children.Add(open);
        if (loc.CanEmpty)
        {
            var empty = new Button { Content = Ui.IconText("", T("Empty", "Leeren")), IsEnabled = m.Files > 0 };
            empty.Click += async (_, _) => await EmptyAsync(loc, m);
            buttons.Children.Add(empty);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(buttons, 2); grid.Children.Add(buttons);
        return Ui.Card(grid);
    }

    private async Task EmptyAsync(BackupLocation loc, (int Files, long Bytes, DateTime? Newest) m)
    {
        string msg = T($"{m.Files} files ({FormatSize(m.Bytes)}) will be moved to the Recycle Bin.\n\nAfter that, the backed up items can only be restored from the Recycle Bin – and no longer once it has been emptied.",
                       $"{m.Files} Dateien ({FormatSize(m.Bytes)}) werden in den Papierkorb verschoben.\n\nDanach lassen sich die gesicherten Einträge nur noch aus dem Papierkorb wiederherstellen – und nach dem Leeren des Papierkorbs gar nicht mehr.");
        if (!await Ui.ConfirmAsync(T($"Empty “{loc.Title}”?", $"„{loc.Title}“ leeren?"), msg, T("Move to Recycle Bin", "In den Papierkorb"))) return;
        try
        {
            await Task.Run(loc.EmptyToRecycleBin);
            Ui.Show(Status, FAInfoBarSeverity.Success, T("Emptied", "Geleert"),
                T($"“{loc.Title}” was moved to the Recycle Bin.", $"„{loc.Title}“ wurde in den Papierkorb verschoben."));
        }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not be emptied", "Konnte nicht geleert werden"), ex.Message); }
        await RefreshAsync();
    }

    private void OnOpenRoot(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(BackupStore.Folder);
        SystemTools.ShowInExplorer(BackupStore.Folder);
    }

    private void OnRestorePoints(object? sender, RoutedEventArgs e)
    {
        // Systemeigenschaften -> Computerschutz (Wiederherstellungspunkte anzeigen, erstellen, loeschen)
        try { Process.Start(new ProcessStartInfo("SystemPropertiesProtection.exe") { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Show(Status, FAInfoBarSeverity.Error, T("Could not open", "Konnte nicht geöffnet werden"), ex.Message); }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
    };
}
