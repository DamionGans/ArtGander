using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ArtGander.Core.Integrations;

/// <summary>
/// Sends API requests with polite retries: on 429 (rate limited) and temporary server errors it waits
/// (honoring <c>Retry-After</c>) and tries again, a few times at most.
/// </summary>
public sealed class ApiHttp(HttpClient http, string userAgent = "ArtGander")
{
    public static TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Lets tests skip real waiting.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task<JsonNode?> SendJsonAsync(HttpMethod method, string url, JsonNode? body, Action<HttpRequestHeaders> headers, CancellationToken ct)
    {
        var text = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.UserAgent.ParseAdd(userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            headers(request.Headers);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return request;
        }, ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.TrimStart().StartsWith('<')) throw new NotJsonException(new Uri(url).Host);
        return JsonNode.Parse(text);
    }

    public async Task<string> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = makeRequest();
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Delay(Backoff(attempt), ct);
                continue;
            }

            using (response)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) return content;

                var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError;
                if (retryable && attempt < MaxAttempts)
                {
                    var wait = response.Headers.RetryAfter?.Delta
                               ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                               ?? Backoff(attempt);
                    await Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxWait ? MaxWait : wait, ct);
                    continue;
                }
                throw new ApiException(response.StatusCode, request.RequestUri?.Host ?? "", Shorten(content));
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));

    private static string Shorten(string s) => s.Length > 400 ? s[..400] + "…" : s;
}

/// <summary>The server answered with a web page instead of data (usually a wrong address).</summary>
public sealed class NotJsonException(string host) : Exception($"{host} answered with a web page instead of data. Check the address.")
{
    public string Host { get; } = host;
}

public sealed class ApiException(HttpStatusCode status, string host, string body)
    : Exception($"{host} answered {(int)status} {status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;
}

/// <summary>Small helpers for reading API JSON without a fixed schema.</summary>
internal static class Json
{
    public static string Str(this JsonNode? n, string name) =>
        n?[name] is JsonValue v ? v.TryGetValue(out string? s) ? s ?? "" : v.ToJsonString().Trim('"') : "";

    public static long? Long(this JsonNode? n, string name) =>
        n?[name] is JsonValue v ? v.TryGetValue(out long l) ? l : v.TryGetValue(out string? s) && long.TryParse(s, out var p) ? p : null : null;

    public static decimal? Dec(this JsonNode? n, string name) =>
        n?[name] is JsonValue v
            ? v.TryGetValue(out decimal d) ? d
              : v.TryGetValue(out string? s) && decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null
            : null;

    public static bool Bool(this JsonNode? n, string name, bool fallback = false) =>
        n?[name] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    public static IEnumerable<JsonNode> Arr(this JsonNode? n, string name) =>
        n?[name] is JsonArray a ? a.OfType<JsonNode>() : [];

    /// <summary>Printify amounts are whole cents.</summary>
    public static decimal? Cents(this JsonNode? n, string name) => n.Long(name) is { } c ? c / 100m : null;
}
