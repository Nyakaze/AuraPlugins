using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Aura.Presence.MyAnimeListSync;

public sealed class MalException(string message, int status) : Exception(message)
{
    public int Status => status;

    public bool Unauthorized => status is 401;

    public bool Transient => status is 408 or 429 or >= 500;
}

public sealed record MalAnime(int Id, string Title, string? English, int? Episodes = null, int? Watched = null, string? Status = null,
    bool Rewatching = false);

public sealed record MalTokens(string Access, string Refresh);

public sealed class MyAnimeListAccount(HttpClient http, string clientId, MalTokens tokens, Action<MalTokens> saved)
{
    private const string AnimeFields = "alternative_titles,num_episodes,my_list_status";

    private static readonly Uri Api = new("https://api.myanimelist.net/v2/");
    private static readonly Uri TokenEndpoint = new("https://myanimelist.net/v1/oauth2/token");

    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private volatile MalTokens _tokens = tokens;

    public static Uri SignInUrl(string clientId, string verifier) =>
        new($"https://myanimelist.net/v1/oauth2/authorize?response_type=code&client_id={clientId}&code_challenge={verifier}&code_challenge_method=plain");

    public static Task<MalTokens> SignInAsync(HttpClient http, string clientId, string code, string verifier, CancellationToken cancellationToken) =>
        TokensAsync(http, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier
        }, cancellationToken);

    public async Task<string> NameAsync(CancellationToken cancellationToken = default) =>
        Text(await SendAsync(HttpMethod.Get, "users/@me", null, cancellationToken), "name") ?? "";

    public async Task<IReadOnlyList<MalAnime>> SearchAsync(string search, CancellationToken cancellationToken = default)
    {
        var query = Uri.EscapeDataString(search[..Math.Min(search.Length, 64)]);
        var found = await SendAsync(HttpMethod.Get, $"anime?q={query}&limit=10&nsfw=true&fields=alternative_titles", null, cancellationToken);

        return [.. found.GetProperty("data").EnumerateArray().Select(item => ToAnime(item.GetProperty("node")))];
    }

    public async Task<MalAnime?> AnimeAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return ToAnime(await SendAsync(HttpMethod.Get, $"anime/{id}?nsfw=true&fields={AnimeFields}", null, cancellationToken));
        }
        catch (MalException exception) when (exception.Status is 404)
        {
            return null;
        }
    }

    public Task SaveAsync(int id, int watched, string status, bool rewatching, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, $"anime/{id}/my_list_status", new Dictionary<string, string>
        {
            ["status"] = status,
            ["is_rewatching"] = rewatching ? "true" : "false",
            ["num_watched_episodes"] = watched.ToString()
        }, cancellationToken);

    public static MalAnime ToAnime(JsonElement node)
    {
        var listed = Child(node, "my_list_status");

        return new MalAnime(
            node.GetProperty("id").GetInt32(),
            Text(node, "title") ?? "",
            Text(Child(node, "alternative_titles"), "en") is { Length: > 0 } english ? english : null,
            Number(node, "num_episodes") is > 0 and var episodes ? episodes : null,
            Number(listed, "num_episodes_watched"),
            Text(listed, "status"),
            Child(listed, "is_rewatching").ValueKind is JsonValueKind.True);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, Dictionary<string, string>? form,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var used = _tokens;
            using var request = new HttpRequestMessage(method, new Uri(Api, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", used.Access);

            if (form is not null)
                request.Content = new FormUrlEncodedContent(form);

            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized && attempt == 0)
            {
                await RefreshAsync(used, cancellationToken);
                continue;
            }

            return await ReadAsync(response, cancellationToken);
        }
    }

    private async Task RefreshAsync(MalTokens used, CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken);

        try
        {
            if (_tokens != used)
                return;

            _tokens = await TokensAsync(http, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = used.Refresh
            }, cancellationToken);
            saved(_tokens);
        }
        catch (MalException exception) when (exception.Status is 400 or 401)
        {
            throw new MalException($"MyAnimeList refused to renew the sign-in: {exception.Message}", 401);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private static async Task<MalTokens> TokensAsync(HttpClient http, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var answer = await ReadAsync(response, cancellationToken);

        return new MalTokens(
            Text(answer, "access_token") ?? throw new MalException("MyAnimeList sent no access token.", (int)response.StatusCode),
            Text(answer, "refresh_token") ?? "");
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
            throw new MalException($"MyAnimeList answered {status} {response.ReasonPhrase}{(body.StartsWith('{') ? $": {body}" : "")}.", status);

        if (body.Length == 0)
            return default;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new MalException($"MyAnimeList answered {status} with something that isn't JSON.", status);
        }
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : null;
}
