namespace Nova.Core;

/// Downloads rule sets (`geosite-*.srs`, `geoip-*.srs`) into a local folder so the core
/// starts without network access and never depends on a server being up.
public sealed class RuleSetStore(string? directory = null)
{
    public string Directory { get; } = directory ?? Path.Combine(ProfileStore.DirectoryPath, "rule-sets");

    public string PathFor(string tag) => Path.Combine(Directory, $"{tag}.srs");

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// Copies rule sets shipped with the app into the store when they're missing,
    /// so connecting never waits for GitHub.
    public void Seed(string? bundled)
    {
        if (bundled == null || !System.IO.Directory.Exists(bundled)) return;
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var f in System.IO.Directory.GetFiles(bundled, "*.srs"))
        {
            var dst = Path.Combine(Directory, Path.GetFileName(f));
            if (!File.Exists(dst))
            {
                try { File.Copy(f, dst); }
                catch (IOException) { }
            }
        }
    }

    /// Tags with no file yet.
    public List<string> Missing(IEnumerable<string> tags) => tags.Where(t => !File.Exists(PathFor(t))).ToList();

    /// Tags whose file is older than `MaxAge`.
    public List<string> Stale(IEnumerable<string> tags) =>
        tags.Where(t => File.Exists(PathFor(t)) && DateTime.UtcNow - File.GetLastWriteTimeUtc(PathFor(t)) > MaxAge).ToList();

    /// Makes sure every tag has a file. Stale files are refreshed when `refresh` is set;
    /// a failed refresh keeps the old file. Returns tags that have no file and couldn't be fetched
    /// (their rules are skipped by the config builder), with the reason.
    public async Task<Dictionary<string, string>> Ensure(IEnumerable<string> tags, bool refresh)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var jobs = new List<Task<(string Tag, string? Error)>>();
        foreach (var tag in tags)
        {
            var file = PathFor(tag);
            var exists = File.Exists(file);
            if (exists && !(refresh && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > MaxAge)) continue;
            jobs.Add(Task.Run(async () =>
            {
                try
                {
                    await Download(tag, file).ConfigureAwait(false);
                    return (tag, (string?)null);
                }
                catch (Exception e)
                {
                    return (tag, exists ? null : e.Message);
                }
            }));
        }
        var failed = new Dictionary<string, string>();
        foreach (var (tag, err) in await Task.WhenAll(jobs)) if (err != null) failed[tag] = err;
        return failed;
    }

    static async Task Download(string tag, string file)
    {
        using var resp = await Http.GetAsync(ConfigBuilder.RuleSetUrl(tag)).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code != 200)
            throw new IOException($"Не удалось скачать набор {tag}: {(code == 404 ? "такого набора нет" : $"HTTP {code}")}");
        var tmp = file + ".download";
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
        File.Move(tmp, file, overwrite: true);
    }

    /// All rule-set tags a profile uses.
    public static List<string> Tags(Profile profile)
    {
        var tags = new List<string>();
        foreach (var r in profile.DomainRules.Where(r => r.Enabled && r.Kind == DomainRuleKind.Ruleset))
            foreach (var t in ConfigBuilder.RuleSetTags(r.Value))
                if (!tags.Contains(t)) tags.Add(t);
        return tags;
    }
}
