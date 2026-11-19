using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterWindows.Core;

namespace BetterWindows.Views;

public partial class SettingsView : UserControl
{
    private bool _ready;

    public SettingsView()
    {
        InitializeComponent();
        LanguageBox.SelectedIndex = Loc.IsGerman ? 1 : 0;
        DataFolderText.Text = BackupStore.Folder;
        DataModeText.Text = DataLocation.IsPortable
            ? Loc.T("Portable: stored next to BetterWindows.exe – take the whole folder with you.",
                    "Portabel: wird neben BetterWindows.exe gespeichert – nimm einfach den ganzen Ordner mit.")
            : Loc.T("Stored in your user profile (the app folder is not writable or this is not the portable version).",
                    "Wird im Benutzerprofil gespeichert (der App-Ordner ist nicht beschreibbar oder dies ist nicht die portable Version).");
        if (DataLocation.MigrationNote is { } note)
            DataModeText.Text += "\n" + Loc.T("Existing data was taken over: ", "Vorhandene Daten wurden übernommen: ") + note;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"BetterWindows {v?.ToString(3)}";
        _ready = true;
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string lang }) return;
        // Neuaufbau erst nach Abschluss des Auswahl-Ereignisses
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Loc.SetLanguage(lang));
    }

    private void OnOpenDataFolder(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(BackupStore.Folder);
        SystemTools.ShowInExplorer(BackupStore.Folder);
    }

    private void OnOpenLicenses(object? sender, RoutedEventArgs e)
    {
        var file = Path.Combine(AppPaths.FilesDir, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }
}
