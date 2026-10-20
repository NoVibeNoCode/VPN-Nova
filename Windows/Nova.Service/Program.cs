using System.Reflection;
using System.ServiceProcess;
using System.Text;
using Nova.Core;
using Nova.Service;

// VPN Nova service. Modes:
//   service                       run as the Windows service (SCM starts it like this)
//   install --from <app dir> --owner <SID> --version <v> [--log <file>]   elevated, from the app
//   uninstall [--log <file>]      elevated, from the app
//   console --data <dir> --singbox <exe> --rulesets <dir> [--pipe <name>]  test mode (any OS, no admin)

Console.OutputEncoding = Encoding.UTF8;
var version = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "NovaServiceVersion")?.Value ?? "dev";

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

switch (args.FirstOrDefault())
{
    case "service":
        ServiceBase.Run(new NovaService(version));
        return 0;

    case "install":
    case "uninstall":
        try
        {
            if (args[0] == "install")
                Installer.Install(Arg("--from") ?? throw new ArgumentException("нет --from"),
                                  Arg("--owner") ?? throw new ArgumentException("нет --owner"),
                                  Arg("--version") ?? version);
            else
                Installer.Uninstall();
            return 0;
        }
        catch (Exception e)
        {
            // The app (not elevated) shows this line to the user.
            if (Arg("--log") is { } log) File.WriteAllText(log, e.Message);
            Console.Error.WriteLine(e.Message);
            return 1;
        }

    case "console":
    {
        var layout = new ServiceLayout
        {
            DataDir = Arg("--data") ?? throw new ArgumentException("нет --data"),
            SingBox = Arg("--singbox") ?? throw new ArgumentException("нет --singbox"),
            OwnerRuleSetDir = Arg("--rulesets") ?? throw new ArgumentException("нет --rulesets"),
            PipeName = Arg("--pipe") ?? "nova-vpn-test",
        };
        Directory.CreateDirectory(layout.DataDir);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var core = new CoreHost(layout);
        Console.WriteLine($"nova service (console) on pipe {layout.PipeName}");
        await new PipeServer(layout, core, version).Run(cts.Token);
        return 0;
    }

    default:
        Console.Error.WriteLine("usage: Nova.Service service | install --from <dir> --owner <SID> --version <v> | uninstall | console --data <dir> --singbox <exe> --rulesets <dir> [--pipe <name>]");
        return 2;
}

sealed class NovaService : ServiceBase
{
    readonly string _version;
    CancellationTokenSource? _cts;
    CoreHost? _core;
    Task? _server;

    public NovaService(string version)
    {
        _version = version;
        ServiceName = SystemPaths.ServiceName;
        CanShutdown = true;
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        Native.PrepareConsole();
        var layout = ServiceLayout.Installed();
        _core = new CoreHost(layout);
        _cts = new CancellationTokenSource();
        _server = Task.Run(() => new PipeServer(layout, _core, _version).Run(_cts.Token));
    }

    protected override void OnStop() => Shutdown();
    protected override void OnShutdown() => Shutdown();

    void Shutdown()
    {
        _cts?.Cancel();
        _core?.Dispose();
        try { _server?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }
}
