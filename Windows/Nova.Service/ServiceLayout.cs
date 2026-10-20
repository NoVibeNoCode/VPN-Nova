using Microsoft.Win32;
using Nova.Core;

namespace Nova.Service;

/// Where the service keeps things. Production: C:\ProgramData\Nova and C:\Program Files\VPN Nova\Service;
/// console test mode points everything at a temp folder.
sealed class ServiceLayout
{
    public required string DataDir { get; init; }
    public required string SingBox { get; init; }
    /// The owner's own rule-set folder (%LOCALAPPDATA%\Nova\rule-sets): the only place local rule sets may come from.
    public required string OwnerRuleSetDir { get; init; }
    public string PipeName { get; init; } = SystemPaths.PipeName;
    /// SID allowed to use the pipe (null in console test mode: the pipe then keeps .NET's default, current user only).
    public string? OwnerSid { get; init; }

    public string WorkDir => Path.Combine(DataDir, "work");
    public string RuleSetDir => Path.Combine(DataDir, "rule-sets");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string LogFile => Path.Combine(LogDir, "sing-box.log");
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string OwnerFile => Path.Combine(SystemPaths.DataDir, "owner");

    /// Production layout from the installed files.
    public static ServiceLayout Installed()
    {
        var sid = File.ReadAllText(OwnerFile).Trim();
        return new ServiceLayout
        {
            DataDir = SystemPaths.DataDir,
            SingBox = Path.Combine(AppContext.BaseDirectory, "sing-box.exe"),
            OwnerRuleSetDir = Path.Combine(ProfileDir(sid), "AppData", "Local", "Nova", "rule-sets"),
            OwnerSid = sid,
        };
    }

    /// The owner's profile folder from the registry (the service runs as SYSTEM, not as the owner).
    public static string ProfileDir(string sid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        var path = key?.GetValue("ProfileImagePath") as string ?? throw new InvalidOperationException($"нет профиля пользователя {sid}");
        return Environment.ExpandEnvironmentVariables(path);
    }
}
