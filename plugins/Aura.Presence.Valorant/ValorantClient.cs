using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Aura.Presence.Valorant;

public sealed record ValorantPresence(
    string SessionState,
    string MatchMap,
    string QueueId,
    string ProvisioningFlow,
    string PartyState,
    string PartyAccessibility,
    int PartySize,
    int MaxPartySize,
    DateTimeOffset? QueueEntryTime,
    int ScoreAlly,
    int ScoreEnemy,
    int CompetitiveTier,
    bool IsIdle)
{
    public string Phase => SessionState != "MENUS" ? SessionState : PartyState == "MATCHMAKING" ? "QUEUE" : "LOBBY";

    public bool IsCustomGame => ProvisioningFlow == "CustomGame" || PartyState == "CUSTOM_GAME_SETUP";

    public string MapCode => MatchMap.TrimEnd('/').Split('/')[^1];

    public bool IsRange => MapCode.StartsWith("Range", StringComparison.OrdinalIgnoreCase);

    public static ValorantPresence? Parse(string json) => ValorantClient.Read(json, root =>
    {
        JsonElement[] sources =
        [
            .. new[] { "matchPresenceData", "partyPresenceData", "playerPresenceData" }
                .Select(name => root.TryGetProperty(name, out var part) ? part : default)
                .Where(part => part.ValueKind == JsonValueKind.Object),
            root
        ];

        string Text(string key) => sources
            .Select(source => source.TryGetProperty(key, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                    _ => null
                }
                : null)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";

        int Number(string key, int fallback) => int.TryParse(Text(key), CultureInfo.InvariantCulture, out var number) ? number : fallback;

        var state = Text("sessionLoopState");

        return state == ""
            ? null
            : new ValorantPresence(
                state,
                Text("matchMap"),
                Text("queueId"),
                Text("provisioningFlow"),
                Text("partyState"),
                Text("partyAccessibility"),
                Math.Max(1, Number("partySize", 1)),
                Math.Max(1, Number("maxPartySize", 5)),
                QueueTime(Text("queueEntryTime")),
                Number("partyOwnerMatchScoreAllyTeam", 0),
                Number("partyOwnerMatchScoreEnemyTeam", 0),
                Number("competitiveTier", 0),
                bool.TryParse(Text("isIdle"), out var idle) && idle);
    });

    private static DateTimeOffset? QueueTime(string text) =>
        DateTimeOffset.TryParseExact(text, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var time) && time.Year > 2000
            ? time
            : null;
}

public sealed record ValorantMap(string Name, string? Splash);

public sealed record ValorantAgent(string Name, string Icon);

public sealed class ValorantClient : IDisposable
{
    private static readonly string LockfilePath = Path.Combine(
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
    private Dictionary<string, ValorantMap> _maps = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ValorantAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _contentTriedAt = DateTimeOffset.MinValue;
    private string? _lockfile;
    private string? _puuid;

    public static string? ReadLockfile()
    {
        try
        {
            using var stream = new FileStream(LockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<ValorantPresence?> PresenceAsync(string lockfile, CancellationToken token)
    {
        var parts = lockfile.Split(':');

        if (parts.Length < 4 || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var port))
            return null;

        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{parts[3]}")));

        if (lockfile != _lockfile)
        {
            _lockfile = lockfile;
            _puuid = null;
        }

        _puuid ??= Read(await LocalAsync(port, auth, "/chat/v1/session", token),
            root => root.GetProperty("puuid").GetString() is { Length: > 0 } puuid ? puuid : null);

        return _puuid is null ? null : OwnPresence(await LocalAsync(port, auth, "/chat/v4/presences", token), _puuid);
    }

    public static ValorantPresence? OwnPresence(string? json, string puuid) => Read(json, root => root.GetProperty("presences")
        .EnumerateArray()
        .Where(entry => entry.TryGetProperty("puuid", out var id) && id.GetString() == puuid)
        .Select(entry => entry.TryGetProperty("private", out var data) && data.ValueKind == JsonValueKind.String ? Decode(data.GetString()!) : null)
        .FirstOrDefault(presence => presence is not null));

    public async Task LoadContentAsync(CancellationToken token)
    {
        if (_maps.Count > 0 && _agents.Count > 0 || DateTimeOffset.UtcNow - _contentTriedAt < ContentRetry)
            return;

        _contentTriedAt = DateTimeOffset.UtcNow;

        if (_maps.Count == 0)
            _maps = ParseMaps(await WebAsync("https://valorant-api.com/v1/maps", token));

        if (_agents.Count == 0)
            _agents = ParseAgents(await WebAsync("https://valorant-api.com/v1/agents?isPlayableCharacter=true", token));
    }

    public ValorantMap? Map(string code) =>
        code == "" ? null : _maps.TryGetValue(code, out var map) ? map : new ValorantMap(code, null);

    public ValorantAgent? Agent(string? uuid) => uuid is null ? null : _agents.GetValueOrDefault(uuid);

    public static Dictionary<string, ValorantMap> ParseMaps(string? json) => Read(json, root => root.GetProperty("data")
        .EnumerateArray()
        .Select(map => (Code: Text(map, "mapUrl")?.TrimEnd('/').Split('/')[^1], Name: Text(map, "displayName"), Splash: Text(map, "splash")))
        .Where(map => !string.IsNullOrEmpty(map.Code) && !string.IsNullOrEmpty(map.Name))
        .DistinctBy(map => map.Code, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(map => map.Code!, map => new ValorantMap(map.Name!, map.Splash), StringComparer.OrdinalIgnoreCase))
        ?? new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, ValorantAgent> ParseAgents(string? json) => Read(json, root => root.GetProperty("data")
        .EnumerateArray()
        .Select(agent => (Id: Text(agent, "uuid"), Name: Text(agent, "displayName"), Icon: Text(agent, "displayIcon")))
        .Where(agent => !string.IsNullOrEmpty(agent.Id) && !string.IsNullOrEmpty(agent.Name) && !string.IsNullOrEmpty(agent.Icon))
        .DistinctBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(agent => agent.Id!, agent => new ValorantAgent(agent.Name!, agent.Icon!), StringComparer.OrdinalIgnoreCase))
        ?? new(StringComparer.OrdinalIgnoreCase);

    public static T? Read<T>(string? json, Func<JsonElement, T> read)
    {
        if (json is null)
            return default;

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object ? read(document.RootElement) : default;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return default;
        }
    }

    public void Dispose()
    {
        _local.Dispose();
        _web.Dispose();
    }

    private static ValorantPresence? Decode(string encoded)
    {
        try
        {
            return encoded.Length == 0 ? null : ValorantPresence.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<string?> LocalAsync(int port, AuthenticationHeaderValue auth, string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}{path}");
        request.Headers.Authorization = auth;
        using var response = await _local.SendAsync(request, token);

        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null;
    }

    private async Task<string?> WebAsync(string url, CancellationToken token)
    {
        using var response = await _web.GetAsync(url, token);

        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null;
    }
}
