using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterWindows.Core;
using BetterWindows.Views;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using static BetterWindows.Core.Loc;

namespace BetterWindows;

public partial class MainWindow : FAAppWindow
{
    private readonly Dictionary<string, Control> _pages = new();
    private string _current = "tweaks";

    public MainWindow()
    {
        InitializeComponent();
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 40;
        AdminBar.IsOpen = !Admin.IsElevated;
        Relabel();

        Loc.LanguageChanged += () =>
        {
            // Alle Seiten mit der neuen Sprache neu aufbauen
            _pages.Clear();
            Relabel();
            ShowPage(_current);
        };

        // optional: BetterWindows.exe --page apps|programs|contextmenu|startup|settings
        var cli = Environment.GetCommandLineArgs();
        int i = Array.FindIndex(cli, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        string? start = i >= 0 && i + 1 < cli.Length ? cli[i + 1] : null;
        Nav.SelectedItem = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<FANavigationViewItem>()
                               .FirstOrDefault(m => (string?)m.Tag == start)
                           ?? NavTweaks;
    }

    private void Relabel()
    {
        NavTweaks.Content = T("Optimizations", "Optimierungen");
        NavApps.Content = "Bloat Apps";
        NavPrograms.Content = T("Programs", "Programme");
        NavContextMenu.Content = T("Context menu", "Kontextmenü");
        NavStartup.Content = T("Startup apps", "Autostart");
        NavBackups.Content = T("Backups", "Sicherungen");
        NavSettings.Content = T("Settings", "Einstellungen");
        ModeText.Text = Admin.IsElevated ? T("· Administrator", "· Administrator") : T("· Limited", "· Eingeschränkt");
        AdminBar.Title = T("Limited mode", "Eingeschränkter Modus");
        AdminBar.Message = T("Without administrator rights, only settings of your own user account can be changed.",
                             "Ohne Administratorrechte können nur Einstellungen deines Benutzerkontos geändert werden.");
        RelaunchButton.Content = T("Restart as administrator", "Als Administrator neu starten");
    }

    private void OnNavSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        var tag = (e.SelectedItem as FANavigationViewItem)?.Tag as string ?? "tweaks";
        ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        _current = tag;
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "apps" => new AppsView(),
                "programs" => new UninstallerView(),
                "contextmenu" => new ContextMenuView(),
                "startup" => new StartupView(),
                "backups" => new BackupsView(),
                "settings" => new SettingsView(),
                _ => new TweaksView()
            };
            _pages[tag] = page;
        }
        PageHost.Content = page;
    }

    private void OnRelaunchAdmin(object? sender, RoutedEventArgs e)
    {
        if (Admin.TryRelaunchElevated()) Close();
    }
}
