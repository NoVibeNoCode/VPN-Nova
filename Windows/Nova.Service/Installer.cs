using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Nova.Core;

namespace Nova.Service;

/// Install / update / uninstall of the service. Runs elevated (UAC) from the app's folder.
static class Installer
{
    static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static string Sc => Path.Combine(Environment.SystemDirectory, "sc.exe");

    public static void Install(string fromDir, string ownerSid, string version)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("служба ставится только на Windows");
        var owner = new SecurityIdentifier(ownerSid);
        if (!owner.IsAccountSid()) throw new InvalidOperationException($"{ownerSid} — не учётная запись пользователя");
        foreach (var f in new[] { "Nova.Service.exe", "sing-box.exe" })
            if (!File.Exists(Path.Combine(fromDir, f))) throw new FileNotFoundException($"нет {f} в {fromDir}");

        StopService();

        // Binaries go where only administrators can write: nothing running as the user can swap them.
        var serviceDir = SystemPaths.ServiceDir;
        Directory.CreateDirectory(serviceDir);
        CopyWithRetry(Path.Combine(fromDir, "Nova.Service.exe"), Path.Combine(serviceDir, "Nova.Service.exe"));
        CopyWithRetry(Path.Combine(fromDir, "sing-box.exe"), Path.Combine(serviceDir, "sing-box.exe"));
        var licenses = Path.Combine(fromDir, "Licenses");
        if (Directory.Exists(licenses))
        {
            Directory.CreateDirectory(Path.Combine(serviceDir, "Licenses"));
            foreach (var f in Directory.GetFiles(licenses)) File.Copy(f, Path.Combine(serviceDir, "Licenses", Path.GetFileName(f)), true);
        }

        // C:\ProgramData\Nova: SYSTEM and Administrators only. The owner may list the folder, read the
        // installed version and the logs; config.json (server keys), cache.db (FakeIP → domain) stay closed.
        var data = SystemPaths.DataDir;
        Directory.CreateDirectory(data);
        var ds = new DirectorySecurity();
        ds.SetAccessRuleProtection(true, false);
        ds.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(data).SetAccessControl(ds);

        foreach (var d in new[] { "work", "rule-sets" }) Directory.CreateDirectory(Path.Combine(data, d));

        // The log records which sites and apps were used: start clean, readable by the owner only.
        var logs = SystemPaths.LogDir;
        if (Directory.Exists(logs)) foreach (var f in Directory.GetFiles(logs)) File.Delete(f);
        Directory.CreateDirectory(logs);
        var ls = new DirectoryInfo(logs).GetAccessControl();
        ls.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(logs).SetAccessControl(ls);

        WriteOwnerReadable(ServiceLayout.OwnerFile, ownerSid, owner);

        var bin = $"\"{Path.Combine(serviceDir, "Nova.Service.exe")}\" service";
        if (ServiceExists())
            Run(Sc, "config", SystemPaths.ServiceName, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", SystemPaths.ServiceDisplayName);
        else
            Run(Sc, "create", SystemPaths.ServiceName, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", SystemPaths.ServiceDisplayName);
        Run(Sc, "description", SystemPaths.ServiceName, "Ядро VPN Nova (sing-box). Запускает VPN по команде приложения VPN Nova.");
        // If the service itself crashes, Windows restarts it (sing-box restarts are the service's job).
        Run(Sc, "failure", SystemPaths.ServiceName, "reset=", "86400", "actions=", "restart/2000/restart/5000/restart/10000");

        using (var sc = new ServiceController(SystemPaths.ServiceName))
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
        // Last: the app treats this file as "installed".
        WriteOwnerReadable(SystemPaths.VersionFile, version, owner);
    }

    public static void Uninstall()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        StopService();
        if (ServiceExists()) Run(Sc, "delete", SystemPaths.ServiceName);
        DeleteWithRetry(SystemPaths.ServiceDir);
        var parent = Path.GetDirectoryName(SystemPaths.ServiceDir)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        DeleteWithRetry(SystemPaths.DataDir);
    }

    static bool ServiceExists() =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(SystemPaths.ServiceName, StringComparison.OrdinalIgnoreCase));

    static void StopService()
    {
        if (!ServiceExists()) return;
        using var sc = new ServiceController(SystemPaths.ServiceName);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        try
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        catch (InvalidOperationException) { }
    }

    static void WriteOwnerReadable(string path, string text, SecurityIdentifier owner)
    {
        File.WriteAllText(path, text);
        var fs = new FileInfo(path).GetAccessControl();
        fs.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(fs);
    }

    static void Run(string exe, params string[] args)
    {
        var (code, output) = Shell.Run(exe, args, 30000);
        if (code != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} {args[0]}: {output.Trim()}");
    }

    /// The old service binary may stay locked for a moment after the service stops.
    static void CopyWithRetry(string src, string dst)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                File.Copy(src, dst, true);
                return;
            }
            catch (IOException) when (i < 50)
            {
                Thread.Sleep(200);
            }
        }
    }

    static void DeleteWithRetry(string dir)
    {
        for (var i = 0; Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < 50) { Thread.Sleep(200); }
        }
    }
}
