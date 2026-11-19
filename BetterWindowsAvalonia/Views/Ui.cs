using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FluentAvalonia.UI.Controls;

namespace BetterWindows.Views;

internal static class Ui
{
    public static Border Card(Control child)
    {
        var b = new Border { Child = child };
        b.Classes.Add("card");
        return b;
    }

    public static TextBlock Text(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.Classes.AddRange(classes);
        return t;
    }

    public static Border Badge(string text, bool accent = false)
    {
        var b = new Border { Child = new TextBlock { Text = text } };
        b.Classes.Add("badge");
        if (accent) b.Classes.Add("accent");
        return b;
    }

    public static StackPanel IconText(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(Text(glyph, "icon"));
        p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    public static async Task<bool> ConfirmAsync(string title, object content, string primary)
    {
        var dlg = new FAContentDialog
        {
            Title = title,
            Content = content is string s ? new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 } : content,
            PrimaryButtonText = primary,
            CloseButtonText = BetterWindows.Core.Loc.T("Cancel", "Abbrechen"),
            DefaultButton = FAContentDialogButton.Primary
        };
        return await dlg.ShowAsync() == FAContentDialogResult.Primary;
    }

    public static void Show(FAInfoBar bar, FAInfoBarSeverity severity, string title, string message)
    {
        bar.ActionButton = null;
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;

        if (severity is FAInfoBarSeverity.Error or FAInfoBarSeverity.Warning)
        {
            // Fehler/Warnungen: kopierbar und im Protokoll festgehalten
            BetterWindows.Core.AppLog.Write($"{severity}: {title}{Environment.NewLine}{message}");
            bar.ActionButton = CopyButton(bar, title, message);
        }
    }

    private static Button CopyButton(Control owner, string title, string message)
    {
        var label = new TextBlock { Text = BetterWindows.Core.Loc.T("Copy", "Kopieren"), VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Text("", "icon"));
        content.Children.Add(label);
        var button = new Button { Content = content };
        ToolTip.SetTip(button, BetterWindows.Core.Loc.T("Copy message to clipboard", "Meldung in die Zwischenablage kopieren"));
        button.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard == null) return;
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
            await clipboard.SetTextAsync($"{title}\n{message}\n\n(BetterWindows {version}, Windows {Environment.OSVersion.Version}, " +
                                         $"Admin: {BetterWindows.Core.Admin.IsElevated})");
            label.Text = BetterWindows.Core.Loc.T("Copied", "Kopiert");
        };
        return button;
    }

    /// <summary>Hinweis "Adminrechte noetig" mit Schaltflaeche zum Neustart als Administrator.</summary>
    public static void ShowAdminRequired(FAInfoBar bar, string what)
    {
        Show(bar, FAInfoBarSeverity.Warning, BetterWindows.Core.Loc.T("Administrator rights required", "Administratorrechte nötig"), what);
        var button = new Button { Content = BetterWindows.Core.Loc.T("Restart as administrator", "Als Administrator neu starten") };
        button.Click += (_, _) =>
        {
            if (BetterWindows.Core.Admin.TryRelaunchElevated() &&
                Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime app)
                app.Shutdown();
        };
        bar.ActionButton = button;
    }

    /// <summary>Icon einer Datei (exe/lnk/ico) als Avalonia-Bitmap.</summary>
    public static Bitmap? FileIcon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return icon == null ? null : FromIcon(icon);
        }
        catch { return null; }
    }

    public static Bitmap? FromIcon(System.Drawing.Icon icon)
    {
        using var bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    public static Bitmap? FromPng(byte[]? png)
    {
        if (png == null) return null;
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); }
        catch { return null; }
    }

    public static Bitmap? ImageFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { return new Bitmap(path); } catch { return null; }
    }
}
