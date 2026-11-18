using System.Text.Json;
using Aura.Presence.Sdk;

namespace Aura.Presence.Roblox;

public sealed record RobloxGame(string Name, string? Creator, long RootPlaceId, string? IconUrl);

public sealed class RobloxApi : IDisposable
{
    private const int MaxImageKey = 256;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<long?> UniverseAsync(long placeId, CancellationToken token) =>
        ParseUniverse(await GetAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", token));

    public async Task<RobloxGame?> GameAsync(long universeId, CancellationToken token)
    {
        var game = ParseGame(await GetAsync($"https://games.roblox.com/v1/games?universeIds={universeId}", token));

        if (game is null)
            return null;

        var icon = ParseThumbnail(await GetAsync(
            $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&returnPolicy=PlaceHolder&size=512x512&format=Png&isCircular=false",
            token));

        return game with { IconUrl = icon };
    }

    public async Task<string?> PlaceNameAsync(long placeId, CancellationToken token) =>
        ParsePlaceName(await GetAsync($"https://economy.roblox.com/v2/assets/{placeId}/details", token));

    public async Task<PresenceImage?> AvatarAsync(long userId, CancellationToken token)
    {
        var url = ParseThumbnail(await GetAsync(
            $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false",
            token));

        return url is null
            ? null
            : new PresenceImage(url, ParseDisplayName(await GetAsync($"https://users.roblox.com/v1/users/{userId}", token)));
    }

    public async Task<string?> AssetImageAsync(long assetId, CancellationToken token) =>
        ParseThumbnail(await GetAsync(
            $"https://thumbnails.roblox.com/v1/assets?assetIds={assetId}&returnPolicy=PlaceHolder&size=512x512&format=Png&isCircular=false",
            token));

    public static long? ParseUniverse(string? json) =>
        Read<long?>(json, root => root.GetProperty("universeId").GetInt64());

    public static RobloxGame? ParseGame(string? json) => Read(json, root =>
    {
        var game = root.GetProperty("data")[0];
        var creator = game.TryGetProperty("creator", out var owner) && owner.TryGetProperty("name", out var name)
            ? name.GetString()
            : null;

        return new RobloxGame(game.GetProperty("name").GetString()!, creator, game.GetProperty("rootPlaceId").GetInt64(), null);
    });

    public static string? ParseThumbnail(string? json) => Read(json, root =>
    {
        var icon = root.GetProperty("data")[0];

        return icon.GetProperty("state").GetString() == "Completed" &&
               icon.GetProperty("imageUrl").GetString() is { Length: > 0 and <= MaxImageKey } url
            ? url
            : null;
    });

    public static string? ParsePlaceName(string? json) =>
        Read(json, root => root.GetProperty("Name").GetString());

    public static string? ParseDisplayName(string? json) =>
        Read(json, root => root.GetProperty("displayName").GetString());

    public void Dispose() => _http.Dispose();

    private async Task<string?> GetAsync(string url, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, token);

        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null;
    }

    private static T? Read<T>(string? json, Func<JsonElement, T> read)
    {
        if (json is null)
            return default;

        try
        {
            using var document = JsonDocument.Parse(json);

            return read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
                                              or IndexOutOfRangeException or FormatException)
        {
            return default;
        }
    }
}
