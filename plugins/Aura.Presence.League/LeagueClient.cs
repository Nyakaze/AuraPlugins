using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aura.Presence.Sdk;

namespace Aura.Presence.League;

public sealed record LeaguePresence(
    string GameStatus,
    int QueueId,
    string GameMode,
    int ChampionId,
    int SkinId,
    string RankTier,
    string RankDivision,
    string PartyType)
{
    public string Phase => GameStatus switch
    {
        "inQueue" => "QUEUE",
        "championSelect" => "CHAMPSELECT",
        "inGame" => "INGAME",
        "spectating" => "SPECTATING",
        _ when GameStatus.StartsWith("hosting", StringComparison.OrdinalIgnoreCase) => "LOBBY",
        _ => "CLIENT"
    };

    public static LeaguePresence? Parse(string? json) => LeagueClient.Read(json, root =>
    {
        var status = LeagueClient.Text(root, "gameStatus");

        return status == ""
            ? null
            : new LeaguePresence(
                status,
                LeagueClient.Number(root, "queueId"),
                LeagueClient.Text(root, "gameMode"),
                LeagueClient.Number(root, "championId"),
                LeagueClient.Number(root, "skinVariant"),
                LeagueClient.Text(root, "rankedLeagueTier"),
                LeagueClient.Text(root, "rankedLeagueDivision"),
                LeagueClient.Text(root, "ptyType"));
    });
}

public sealed record LiveStats(int Kills, int Deaths, int Assists, int CreepScore, double GameTime);

public sealed record LeagueSkin(string Name, string Tile);

public sealed record LeagueChampion(string Name, LeagueSkin Base, IReadOnlyDictionary<int, LeagueSkin> Skins)
{
    public LeagueSkin Skin(int id) => Skins.GetValueOrDefault(id, Base);
}

public sealed class LeagueClient : IDisposable
{
    public const string GameData = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/";

    private const string AssetPrefix = "/lol-game-data/assets/";
    private const string LiveUrl = "https://127.0.0.1:2999/liveclientdata/allgamedata";

    public static readonly string RiotLockfile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config", "lockfile");

    private static readonly TimeSpan ContentRetry = TimeSpan.FromMinutes(1);

    private readonly HttpClient _local = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { IsLoopback: true }
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly HttpClient _web = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<int, LeagueChampion> _champions = [];
    private readonly Dictionary<string, DateTimeOffset> _triedAt = [];
    private Dictionary<int, string> _queues = [];
    private string? _lockfile;
    private string? _puuid;

    public static string? ReadLockfile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<LeaguePresence?> PresenceAsync(string lockfile, CancellationToken token)
    {
        if (lockfile != _lockfile)
        {
            _lockfile = lockfile;
            _puuid = null;
        }

        _puuid ??= Read(await LocalAsync(lockfile, "/chat/v1/session", token),
            root => root.GetProperty("puuid").GetString() is { Length: > 0 } puuid ? puuid : null);

        return _puuid is null ? null : OwnPresence(await LocalAsync(lockfile, "/chat/v4/presences", token), _puuid);
    }

    public async Task<PresenceParty?> LobbyAsync(string lockfile, CancellationToken token)
    {
        try
        {
            return ParseLobby(await LocalAsync(lockfile, "/lol-lobby/v2/lobby", token));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    public static PresenceParty? ParseLobby(string? json) => Read(json, root =>
        root.GetProperty("members").GetArrayLength() is var size and > 0 &&
        Number(root.GetProperty("gameConfig"), "maxLobbySize") is var max && max >= size
            ? new PresenceParty(size, max)
            : null);

    public async Task<LiveStats?> LiveAsync(CancellationToken token)
    {
        try
        {
            using var response = await _local.GetAsync(LiveUrl, token);

            return response.IsSuccessStatusCode ? ParseLive(await response.Content.ReadAsStringAsync(token)) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task LoadContentAsync(int championId, CancellationToken token)
    {
        if (_queues.Count == 0)
            _queues = ParseQueues(await ContentAsync($"{GameData}v1/queues.json", token));

        if (championId > 0 && !_champions.ContainsKey(championId) &&
            ParseChampion(await ContentAsync($"{GameData}v1/champions/{championId}.json", token)) is { } champion)
            _champions[championId] = champion;
    }

    public string? Queue(int id) => _queues.GetValueOrDefault(id);

    public LeagueChampion? Champion(int id) => _champions.GetValueOrDefault(id);

    public static LeaguePresence? OwnPresence(string? json, string puuid) => Read(json, root => root.GetProperty("presences")
        .EnumerateArray()
        .Where(entry => Text(entry, "puuid") == puuid && Text(entry, "product") == "league_of_legends")
        .Select(entry => Decode(Text(entry, "private")))
        .FirstOrDefault(presence => presence is not null));

    public static LiveStats? ParseLive(string? json) => Read(json, root =>
    {
        var active = root.GetProperty("activePlayer");
        var me = Text(active, "riotId") is { Length: > 0 } riotId ? riotId : Text(active, "summonerName");
        var player = root.GetProperty("allPlayers")
            .EnumerateArray()
            .FirstOrDefault(player => me != "" && (Text(player, "riotId") == me || Text(player, "summonerName") == me));

        if (player.ValueKind != JsonValueKind.Object)
            return null;

        var scores = player.GetProperty("scores");

        return new LiveStats(Number(scores, "kills"), Number(scores, "deaths"), Number(scores, "assists"), Number(scores, "creepScore"),
            root.GetProperty("gameData").GetProperty("gameTime").GetDouble());
    });

    public static Dictionary<int, string> ParseQueues(string? json) => Read(json, root =>
    {
        var queues = root.EnumerateArray()
            .Select(queue => (Id: Number(queue, "id"), Name: Text(queue, "name"), Description: Text(queue, "description"),
                Bots: Text(queue, "gameSelectCategory") == "kVersusAI"))
            .Where(queue => queue.Name != "")
            .DistinctBy(queue => queue.Id)
            .ToList();
        var shared = queues.CountBy(queue => queue.Name).Where(count => count.Value > 1).Select(count => count.Key).ToHashSet();

        return queues.ToDictionary(queue => queue.Id, queue => (queue.Bots ? "Co-op vs. AI: " : "") +
            (shared.Contains(queue.Name) && queue.Description != "" ? queue.Description : queue.Name));
    }) ?? [];

    public static LeagueChampion? ParseChampion(string? json) => Read(json, root =>
    {
        var skins = new Dictionary<int, LeagueSkin>();
        LeagueSkin? first = null;

        foreach (var skin in root.GetProperty("skins").EnumerateArray())
        {
            if (Tile(Text(skin, "tilePath")) is not { } tile)
                continue;

            var entry = new LeagueSkin(Text(skin, "name"), tile);
            first ??= entry;
            skins[Number(skin, "id")] = entry;

            if (skin.TryGetProperty("chromas", out var chromas) && chromas.ValueKind == JsonValueKind.Array)
                foreach (var chroma in chromas.EnumerateArray())
                    skins.TryAdd(Number(chroma, "id"), entry);

            if (skin.TryGetProperty("questSkinInfo", out var quest) && quest.ValueKind == JsonValueKind.Object &&
                quest.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
                foreach (var tier in tiers.EnumerateArray())
                    if (Tile(Text(tier, "tilePath")) is { } tierTile)
                        skins[Number(tier, "id")] = new LeagueSkin(Text(tier, "name"), tierTile);
        }

        var name = Text(root, "name");

        return name == "" || first is null ? null : new LeagueChampion(name, first, skins);
    });

    public static string? Tile(string path) =>
        path.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) ? GameData + path[AssetPrefix.Length..].ToLowerInvariant() : null;

    public static T? Read<T>(string? json, Func<JsonElement, T> read)
    {
        if (string.IsNullOrEmpty(json))
            return default;

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.String
                ? Read(document.RootElement.GetString(), read)
                : read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return default;
        }
    }

    public static string Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => ""
            }
            : "";

    public static int Number(JsonElement element, string key) =>
        int.TryParse(Text(element, key), CultureInfo.InvariantCulture, out var number) ? number : 0;

    public void Dispose()
    {
        _local.Dispose();
        _web.Dispose();
    }

    private static LeaguePresence? Decode(string encoded)
    {
        try
        {
            return encoded.Length == 0 ? null : LeaguePresence.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<string?> LocalAsync(string lockfile, string path, CancellationToken token)
    {
        var parts = lockfile.Split(':');

        if (parts.Length < 4 || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var port))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{parts[3]}")));
        using var response = await _local.SendAsync(request, token);

        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null;
    }

    private async Task<string?> ContentAsync(string url, CancellationToken token)
    {
        if (_triedAt.TryGetValue(url, out var triedAt) && DateTimeOffset.UtcNow - triedAt < ContentRetry)
            return null;

        _triedAt[url] = DateTimeOffset.UtcNow;
        using var response = await _web.GetAsync(url, token);

        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null;
    }
}
