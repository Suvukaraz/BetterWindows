using Avalonia;

namespace BetterWindows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // BCU-Hilfsprogramme (SteamHelper, StoreAppHelper) liegen im portablen Aufbau in "BetterWindows-Files"
        AppContext.SetData("BCU.HelperDirectory", Core.AppPaths.FilesDir);

        Core.Loc.Init();

        // BCU soll keine modalen WinForms-Dialoge zeigen (blockieren sonst den Scan), sondern ins Protokoll schreiben
        Klocman.Forms.Tools.PremadeDialogs.ErrorHandler = (message, details) =>
            Core.AppLog.Write("BCU error: " + message + Environment.NewLine + details);

        // Diagnosemodus ohne Oberflaeche: BetterWindows.exe --selftest [Ausgabedatei]
        int idx = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            string outFile = idx + 1 < args.Length ? args[idx + 1] : Path.Combine(AppContext.BaseDirectory, "selftest.txt");
            SelfTest.RunAsync(outFile).GetAwaiter().GetResult();
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
