using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Copit.Core.Services;

public class ApiException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

internal static class HttpFactory
{
    public static HttpClient Create(string baseUrl, bool ignoreCerts)
    {
        var handler = new HttpClientHandler();
        if (ignoreCerts)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Copit/1.0");
        return client;
    }

    public static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = body.Length > 600 ? body[..600] : body;
            throw new ApiException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {msg}", (int)resp.StatusCode);
        }
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body); }
        catch (JsonException) { throw new ApiException("Antwort ist kein JSON (Login-Seite? URL prüfen): " + body[..Math.Min(200, body.Length)]); }
    }

    public static string Str(this JsonNode? n, string prop) =>
        n?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?[prop]?.ToString() ?? "";

    public static long Long(this JsonNode? n, string prop)
    {
        var x = n?[prop];
        if (x is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l;
        }
        return 0;
    }
}
