using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aura.Presence.SimklSync;

public sealed class SimklException(string message, int status, string? error) : Exception(message)
{
    public int Status => status;

    public string? Error => error;

    public bool Unauthorized => status is 401 || error is "invalid_grant";

    public bool Transient => status is 408 or 429 or >= 500 || string.Equals(error, "rate_limit", StringComparison.OrdinalIgnoreCase);
}

public sealed record SimklCode(string DeviceCode, string UserCode, Uri VerificationUrl, TimeSpan ExpiresIn, TimeSpan Interval);

public sealed record SimklTokens(string AccessToken, string RefreshToken);

public enum SimklResult
{
    Added,
    AlreadyWatched,
    NotFound
}

public sealed class SimklClient(HttpClient http, string clientId, SimklTokens? tokens, Action<SimklTokens> saved)
{
    public static readonly Uri PinPage = new("https://simkl.com/pin");

    private const string Api = "https://api.simkl.com/";
    private const string AppName = "aura";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly string AppVersion = typeof(SimklClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SimklTokens? _tokens = tokens;

    public async Task<SimklCode> RequestCodeAsync(CancellationToken cancellationToken)
    {
        var code = await SendAsync(HttpMethod.Post, "oauth2/device",
            Form(("client_id", clientId), ("scope", "media:read media:write")), null, cancellationToken);

        return new SimklCode(
            code.GetProperty("device_code").GetString()!,
            code.GetProperty("user_code").GetString()!,
            Text(code, "verification_uri_complete") is { } complete ? new Uri(complete) : PinPage,
            TimeSpan.FromSeconds(Number(code, "expires_in") ?? 900),
            TimeSpan.FromSeconds(Number(code, "interval") ?? 5));
    }

    public async Task<SimklTokens?> WaitForApprovalAsync(SimklCode code, CancellationToken cancellationToken)
    {
        var interval = code.Interval;
        var until = DateTimeOffset.UtcNow + code.ExpiresIn;

        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(interval, cancellationToken);

            try
            {
                return Keep(await SendAsync(HttpMethod.Post, "oauth2/token",
                    Form(("grant_type", DeviceGrant), ("client_id", clientId), ("device_code", code.DeviceCode)), null, cancellationToken));
            }
            catch (SimklException exception) when (exception.Error is "authorization_pending")
            {
            }
            catch (SimklException exception) when (exception.Error is "slow_down")
            {
                interval += TimeSpan.FromSeconds(5);
            }
            catch (SimklException exception) when (exception.Error is "expired_token")
            {
                return null;
            }
        }

        return null;
    }

    public async Task<string> UserNameAsync(CancellationToken cancellationToken) =>
        Text((await AuthorizedAsync(HttpMethod.Get, "users/settings", () => null, cancellationToken)).GetProperty("user"), "name") ?? "";

    public async Task<SimklResult> AddToHistoryAsync(JsonObject history, CancellationToken cancellationToken) =>
        ResultOf(await AuthorizedAsync(HttpMethod.Post, "sync/history",
            () => new StringContent(history.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken));

    public static SimklResult ResultOf(JsonElement response)
    {
        if (response.TryGetProperty("not_found", out var notFound) && notFound.ValueKind is JsonValueKind.Object &&
            notFound.EnumerateObject().Any(bucket => bucket.Value is { ValueKind: JsonValueKind.Array } items && items.GetArrayLength() > 0))
            return SimklResult.NotFound;

        return response.TryGetProperty("added", out var added) && added.ValueKind is JsonValueKind.Object &&
               added.EnumerateObject().Any(count => count.Value is { ValueKind: JsonValueKind.Number } number && number.GetInt32() > 0)
            ? SimklResult.Added
            : SimklResult.AlreadyWatched;
    }

    private async Task<JsonElement> AuthorizedAsync(HttpMethod method, string path, Func<HttpContent?> content,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var current = _tokens ?? throw new SimklException("Not signed in to Simkl.", 401, null);

            try
            {
                return await SendAsync(method, path, content(), current.AccessToken, cancellationToken);
            }
            catch (SimklException exception) when (exception.Status is 401 && current.RefreshToken.Length > 0)
            {
                var renewed = Keep(await SendAsync(HttpMethod.Post, "oauth2/token",
                    Form(("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", current.RefreshToken)), null,
                    cancellationToken));

                return await SendAsync(method, path, content(), renewed.AccessToken, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private SimklTokens Keep(JsonElement response)
    {
        var renewed = new SimklTokens(response.GetProperty("access_token").GetString()!,
            Text(response, "refresh_token") ?? _tokens?.RefreshToken ?? "");
        _tokens = renewed;
        saved(renewed);

        return renewed;
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent? content, string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method,
            $"{Api}{path}?client_id={Uri.EscapeDataString(clientId)}&app-name={AppName}&app-version={AppVersion}");
        request.Content = content;
        request.Headers.UserAgent.ParseAdd($"{AppName}/{AppVersion}");
        request.Headers.Accept.ParseAdd("application/json");

        if (accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SimklException($"Simkl answered {status} {response.ReasonPhrase}.", status, null);
        }

        if (response.IsSuccessStatusCode)
            return root;

        var error = root.ValueKind is JsonValueKind.Object ? Text(root, "error") : null;
        var message = root.ValueKind is JsonValueKind.Object ? Text(root, "error_description") ?? Text(root, "message") : null;

        throw new SimklException($"Simkl answered {status}: {message ?? error ?? response.ReasonPhrase}.", status, error);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(field => KeyValuePair.Create(field.Key, field.Value)));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number ? value.GetInt32() : null;
}
