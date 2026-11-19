using System.Globalization;
using System.Text.Json;

namespace BetterWindows.Core;

/// <summary>
/// Zweisprachigkeit (Englisch = Standard, Deutsch). Texte stehen direkt als Paar im Code:
/// <c>T("Optimizations", "Optimierungen")</c>, im XAML: <c>{l:Tr En='…', De='…'}</c>.
/// </summary>
public static class Loc
{
    public static string Language { get; private set; } = "en";
    public static bool IsGerman => Language == "de";

    /// <summary>Wird nach einem Sprachwechsel ausgeloest (Oberflaeche baut sich neu auf).</summary>
    public static event Action? LanguageChanged;

    public static string T(string en, string de) => IsGerman ? de : en;

    public static void Init()
    {
        Language = AppSettings.Current.Language == "de" ? "de" : "en";
        ApplyCulture();
    }

    public static void SetLanguage(string language)
    {
        language = language == "de" ? "de" : "en";
        if (language == Language) return;
        Language = language;
        AppSettings.Current.Language = language;
        AppSettings.Current.Save();
        ApplyCulture();
        LanguageChanged?.Invoke();
    }

    private static void ApplyCulture()
    {
        // Nur die Sprache der Texte (auch fuer die BCU-Meldungen); Zahlen-/Datumsformat bleibt wie im System
        var culture = new CultureInfo(IsGerman ? "de-DE" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}

/// <summary>Einstellungen unter %LOCALAPPDATA%\BetterWindows\settings.json</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "en";

    /// <summary>Ansicht der Programmliste: simple, medium, expert</summary>
    public string ProgramsView { get; set; } = "simple";

    private static string FilePath => Path.Combine(BackupStore.Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* beschaedigte Datei -> Standardwerte */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(BackupStore.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Einstellungen sind nicht kritisch */ }
    }
}
