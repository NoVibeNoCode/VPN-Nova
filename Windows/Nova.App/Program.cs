using Avalonia;
using Avalonia.Media;

namespace Nova.App;

static class Program
{
    /// Command line: `--background` (autostart: tray only), `--demo [section]` (fake data, no service),
    /// `--snapshot <dir>` (render every screen to PNG and exit; used to check the UI on any OS).
    public static string[] Args { get; private set; } = [];

    public static bool Has(string flag) => Args.Contains(flag);
    public static string? Arg(string flag) => Array.IndexOf(Args, flag) is var i and >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        var tool = Has("--demo") || Has("--snapshot");
        if (!tool && !SingleInstance.Claim()) return 0;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Static weights cut from the variable Unbounded/Manrope (Avalonia doesn't apply the weight axis).
            .ConfigureFonts(fonts => fonts.AddFontCollection(new Avalonia.Media.Fonts.EmbeddedFontCollection(
                new Uri("fonts:Nova"), new Uri("avares://Nova/Assets/Fonts"))))
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Nova#Manrope" })
            .With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface] })
            .LogToTrace();
}

/// One app per Windows session; launching it again brings up the running one.
static class SingleInstance
{
    static Mutex? _mutex;
    const string Name = @"Local\NovaVPN-App";
    public static event Action? ShowRequested;

    public static bool Claim()
    {
        if (!OperatingSystem.IsWindows()) return true;
        _mutex = new Mutex(true, Name, out var created);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name + "-Show");
        if (!created)
        {
            signal.Set();
            return false;
        }
        new Thread(() =>
        {
            while (signal.WaitOne()) Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowRequested?.Invoke());
        }) { IsBackground = true, Name = "nova-show" }.Start();
        return true;
    }
}
