namespace Nova.Core;

/// Checks every server through the Clash API and applies the kill switch policy:
/// - kill switch ON: selectors always point at the server, a dead server blocks its traffic;
/// - kill switch OFF: a dead server's selector falls back to `direct` until it recovers.
public sealed class HealthMonitor(ClashApi api)
{
    public sealed record ServerHealth(int? DelayMs = null, int Failures = 0, bool FallbackActive = false)
    {
        public bool IsDown => Failures >= FailuresToMarkDown;
    }

    public const int FailuresToMarkDown = 2;

    public Dictionary<string, ServerHealth> Health { get; private set; } = [];

    /// One check round. Returns the new state per server id.
    public async Task<Dictionary<string, ServerHealth>> Tick(IReadOnlyList<string> serverIds, bool killSwitch)
    {
        var results = await Task.WhenAll(serverIds.Select(async id => (id, delay: await api.Delay(ConfigBuilder.ServerTag(id), 6000))));
        var health = new Dictionary<string, ServerHealth>();
        foreach (var (id, delay) in results)
        {
            var h = Health.GetValueOrDefault(id) ?? new ServerHealth();
            health[id] = h with { DelayMs = delay, Failures = delay == null ? h.Failures + 1 : 0 };
        }

        foreach (var id in serverIds)
        {
            if (!health.TryGetValue(id, out var h)) continue;
            var wantFallback = !killSwitch && h.IsDown;
            var selector = ConfigBuilder.SelectorTag(id);
            var want = wantFallback ? ConfigBuilder.DirectTag : ConfigBuilder.ServerTag(id);
            if (await api.Selected(selector) != want)
            {
                try { await api.Select(selector, want); }
                catch (ClashApi.ApiException) { }
            }
            health[id] = h with { FallbackActive = wantFallback };
        }
        Health = health;
        return new Dictionary<string, ServerHealth>(health);
    }
}
