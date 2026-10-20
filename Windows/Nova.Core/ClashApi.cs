using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nova.Core;

/// Minimal client for sing-box's Clash-compatible API.
public sealed class ClashApi
{
    public sealed record Traffic([property: JsonPropertyName("up")] long Up, [property: JsonPropertyName("down")] long Down);

    public sealed class ConnectionMetadata
    {
        [JsonPropertyName("network")] public string? Network { get; init; }
        [JsonPropertyName("host")] public string? Host { get; init; }
        [JsonPropertyName("destinationIP")] public string? DestinationIp { get; init; }
        [JsonPropertyName("destinationPort")] public string? DestinationPort { get; init; }
        [JsonPropertyName("processPath")] public string? ProcessPath { get; init; }
    }

    public sealed class Connection
    {
        [JsonPropertyName("id"), JsonRequired] public string Id { get; init; } = "";
        [JsonPropertyName("metadata"), JsonRequired] public ConnectionMetadata Metadata { get; init; } = new();
        [JsonPropertyName("upload")] public long Upload { get; init; }
        [JsonPropertyName("download")] public long Download { get; init; }
        [JsonPropertyName("chains"), JsonRequired] public List<string> Chains { get; init; } = [];
        [JsonPropertyName("rule")] public string? Rule { get; init; }
    }

    public sealed class Connections
    {
        [JsonPropertyName("downloadTotal")] public long DownloadTotal { get; init; }
        [JsonPropertyName("uploadTotal")] public long UploadTotal { get; init; }
        [JsonPropertyName("connections")] public List<Connection>? List { get; init; }

        public static Connections Parse(string json) =>
            JsonSerializer.Deserialize(json, ClashJson.Default.Connections) ?? throw new JsonException("null");
    }

    public sealed class ApiException(string message) : Exception(message);

    readonly string _base;
    readonly string _secret;
    // One handler for the app's lifetime; requests never go through a system proxy.
    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public ClashApi(int port, string secret)
    {
        _base = $"http://127.0.0.1:{port}";
        _secret = secret;
    }

    HttpRequestMessage Request(string path, HttpMethod? method = null, string? query = null, string? jsonBody = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, _base + path + (query != null ? "?" + query : ""));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _secret);
        if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    static string Escape(string name) => Uri.EscapeDataString(name);

    async Task<string> Send(HttpRequestMessage req, int timeoutMs = 8000, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode is < 200 or >= 300) throw new ApiException($"HTTP {(int)resp.StatusCode}");
            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (ApiException) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            throw new ApiException("API недоступен");
        }
        finally
        {
            req.Dispose();
        }
    }

    public async Task<bool> IsAlive()
    {
        try
        {
            await Send(Request("/version"), 2000).ConfigureAwait(false);
            return true;
        }
        catch (ApiException) { return false; }
    }

    /// Delay in ms through `outbound`, null if the server didn't answer.
    public async Task<int?> Delay(string outbound, int timeoutMs = 5000, string url = "https://www.gstatic.com/generate_204")
    {
        try
        {
            var body = await Send(Request($"/proxies/{Escape(outbound)}/delay", query: $"timeout={timeoutMs}&url={Uri.EscapeDataString(url)}"),
                                  timeoutMs + 3000).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("delay", out var d) && d.TryGetInt32(out var ms) && ms > 0 ? ms : null;
        }
        catch (Exception e) when (e is ApiException or JsonException) { return null; }
    }

    public async Task<string?> Selected(string selector)
    {
        try
        {
            var body = await Send(Request($"/proxies/{Escape(selector)}")).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("now", out var n) ? n.GetString() : null;
        }
        catch (Exception e) when (e is ApiException or JsonException) { return null; }
    }

    public async Task Select(string selector, string outbound) =>
        await Send(Request($"/proxies/{Escape(selector)}", HttpMethod.Put, jsonBody: JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = outbound }, ClashJson.Default.DictionaryStringString))).ConfigureAwait(false);

    public async Task<Connections> GetConnections()
    {
        var body = await Send(Request("/connections")).ConfigureAwait(false);
        try { return Connections.Parse(body); }
        catch (JsonException) { throw new ApiException("непонятный ответ"); }
    }

    public async Task CloseAllConnections()
    {
        try { await Send(Request("/connections", HttpMethod.Delete)).ConfigureAwait(false); }
        catch (ApiException) { }
    }

    /// Per-second traffic stream. Ends when the core stops.
    public async IAsyncEnumerable<Traffic> TrafficStream([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = Request("/traffic");
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException) { yield break; }
        using (resp)
        {
            if (resp.StatusCode != HttpStatusCode.OK) yield break;
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or HttpRequestException) { yield break; }
                if (line == null) yield break;
                Traffic? t = null;
                try { t = JsonSerializer.Deserialize(line, ClashJson.Default.Traffic); }
                catch (JsonException) { }
                if (t != null) yield return t;
            }
        }
    }
}

[JsonSerializable(typeof(ClashApi.Connections))]
[JsonSerializable(typeof(ClashApi.Traffic))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ClashJson : JsonSerializerContext;
