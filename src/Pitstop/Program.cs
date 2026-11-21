using Avalonia;
using Pitstop.Services;

namespace Pitstop;

internal static class Program
{
    /// <summary>--minimized (at Windows sign-in): start in the tray.</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>--smoke-test: the release build opens every page and theme, then exits 0. A crash exits non-zero.</summary>
    public static bool SmokeTest { get; private set; }
    public static int SmokeExitCode { get; set; } = 3;

    /// <summary>--after-update &lt;pid&gt;: started by the previous version's updater.</summary>
    public static int? AfterUpdatePid { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Paths.Ensure();

        // Admin helper: runs one job (install / profile writes) with no window, then exits.
        int ei = Array.IndexOf(args, "--elevated");
        if (ei >= 0 && ei + 1 < args.Length) return ElevatedHelper.Run(args[ei + 1]);

        // CI self-test of the driver pipeline (lookup, download, verify, unpack). No window.
        int si = Array.IndexOf(args, "--selftest");
        if (si >= 0) return SelfTest.Run(si + 1 < args.Length ? args[si + 1] : Path.Combine(Paths.Data, "selftest"));

        if (args.Contains("--quit"))
        {
            SingleInstance.SignalQuit();
            return 0;
        }

        StartMinimized = args.Contains("--minimized");
        SmokeTest = args.Contains("--smoke-test");
        int ai = Array.IndexOf(args, "--after-update");
        if (ai >= 0 && ai + 1 < args.Length && int.TryParse(args[ai + 1], out var pid)) AfterUpdatePid = pid;

        // One Pitstop at a time; a second launch brings the first forward. The copy started by an
        // update takes the lock once the old copy has exited.
        if (!SmokeTest && AfterUpdatePid is null && !SingleInstance.TryAcquire(TimeSpan.FromMilliseconds(200)))
            return 0;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("unhandled", e.ExceptionObject as Exception);
            if (SmokeTest) Environment.Exit(5);
        };
        if (SmokeTest)
        {
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error("unobserved task", e.Exception);
                Environment.Exit(6);
            };
        }

        try
        {
            int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return SmokeTest ? SmokeExitCode : code;
        }
        catch (Exception ex)
        {
            Log.Error("startup", ex);
            return 2;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
