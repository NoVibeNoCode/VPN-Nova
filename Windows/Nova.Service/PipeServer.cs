using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Nova.Core;

namespace Nova.Service;

/// Serves the app's commands on \\.\pipe\nova-vpn. Only the owner (plus SYSTEM and Administrators) may
/// connect, and never over the network. Commands run one at a time, in arrival order.
sealed class PipeServer(ServiceLayout layout, CoreHost core, string version)
{
    // Commands run strictly one after another (like the macOS core queue).
    readonly SemaphoreSlim _serial = new(1, 1);

    public async Task Run(CancellationToken ct)
    {
        // A listening instance always exists, so nothing else can take the pipe name while the service runs.
        // (Even if it could, it would run as the same user, who can read profile.json anyway.)
        var next = Create(first: true);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await next.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                next.Dispose();
                return;
            }
            var current = next;
            next = await CreateNext(ct).ConfigureAwait(false);
            _ = Task.Run(() => Serve(current, ct), CancellationToken.None);
        }
        next.Dispose();
    }

    async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            await _serial.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await Handle(pipe, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Client went away or stalled.
            }
            finally
            {
                _serial.Release();
                try { if (pipe.IsConnected) pipe.Disconnect(); } catch (IOException) { }
            }
        }
    }

    async Task Handle(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var req = await PipeProtocol.ReadRequest(pipe, ct).ConfigureAwait(false);
        if (req is not { } r)
        {
            await PipeProtocol.WriteReply(pipe, false, "непонятный запрос", ct).ConfigureAwait(false);
            return;
        }
        string reply;
        try
        {
            // Core work blocks (process start/stop, sing-box check): keep it off the pipe's async path.
            reply = await Task.Run(() => r.Command switch
            {
                "apply" when r.Payload != null => core.Apply(r.Payload),
                "start" => core.Start(),
                "stop" => core.Stop(),
                "status" => core.Status(),
                "version" => version,
                _ => throw new CoreException($"неизвестная команда {r.Command}"),
            }, ct).ConfigureAwait(false);
        }
        catch (CoreException e)
        {
            await PipeProtocol.WriteReply(pipe, false, e.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await PipeProtocol.WriteReply(pipe, false, $"служба: {e.Message}", ct).ConfigureAwait(false);
            return;
        }
        await PipeProtocol.WriteReply(pipe, true, reply, ct).ConfigureAwait(false);
    }

    const int Instances = 4;

    /// All instances can be busy for a moment (commands queue behind a slow start/stop): wait for one.
    async Task<NamedPipeServerStream> CreateNext(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return Create(first: false); }
            catch (IOException) when (attempt < 300 && !ct.IsCancellationRequested)
            {
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    NamedPipeServerStream Create(bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        if (!OperatingSystem.IsWindows() || layout.OwnerSid == null)
            return new NamedPipeServerStream(layout.PipeName, PipeDirection.InOut, Instances, PipeTransmissionMode.Byte,
                                             options | PipeOptions.CurrentUserOnly);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(layout.OwnerSid), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(layout.PipeName, PipeDirection.InOut, Instances, PipeTransmissionMode.Byte,
                                               options, 0, 0, security);
    }
}
