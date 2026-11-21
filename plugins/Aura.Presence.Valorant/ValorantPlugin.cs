using Aura.Presence.Sdk;

namespace Aura.Presence.Valorant;

public sealed record CardOptions(bool ShowRank, bool ShowParty, bool ShowScore);

public sealed class ValorantPlugin : IPresencePlugin
{
    public const string LogoUrl = "https://raw.githubusercontent.com/Nyakaze/AuraPlugins/main/plugins/Aura.Presence.Valorant/icon.png";
    public const string IdleUrl = "https://raw.githubusercontent.com/Nyakaze/AuraPlugins/main/plugins/Aura.Presence.Valorant/idle.png";

    private const string ProcessName = "VALORANT-Win64-Shipping";
    private const string RankSetting = "showRank";
    private const string PartySetting = "showParty";
    private const string ScoreSetting = "showScore";
    private const string RankIcons = "https://media.valorant-api.com/competitivetiers/03621f52-342b-cf4e-4f86-9350a49c6d04";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxQueueAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan LogLag = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, string> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["competitive"] = "Competitive",
        ["unrated"] = "Unrated",
        ["premier"] = "Premier",
        ["swiftplay"] = "Swiftplay",
        ["spikerush"] = "Spike Rush",
        ["deathmatch"] = "Deathmatch",
        ["hurm"] = "Team Deathmatch",
        ["ggteam"] = "Escalation",
        ["onefa"] = "Replication",
        ["snowball"] = "Snowball Fight",
        ["newmap"] = "New Map",
        ["skirmish2v2"] = "Skirmish 2v2"
    };

    private static readonly HashSet<string> RoundModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "competitive", "unrated", "premier", "swiftplay", "spikerush", "onefa", "newmap"
    };

    private static readonly string[] Ranks =
    [
        "Unranked", "Unused", "Unused",
        "Iron 1", "Iron 2", "Iron 3",
        "Bronze 1", "Bronze 2", "Bronze 3",
        "Silver 1", "Silver 2", "Silver 3",
        "Gold 1", "Gold 2", "Gold 3",
        "Platinum 1", "Platinum 2", "Platinum 3",
        "Diamond 1", "Diamond 2", "Diamond 3",
        "Ascendant 1", "Ascendant 2", "Ascendant 3",
        "Immortal 1", "Immortal 2", "Immortal 3",
        "Radiant"
    ];

    private readonly ValorantClient _client = new();
    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private CardOptions _options = new(true, true, true);
    private int? _gameId;
    private ValorantLog _log = new(ValorantLog.GamePath);
    private string _phase = "";
    private DateTimeOffset _phaseStart;
    private DateTimeOffset? _menusSeenAt;
    private bool _unresponsive;
    private PresenceActivity? _shown;

    public string Id => "valorant";

    public string DisplayName => "Valorant";

    public int DefaultPriority => 100;

    public string Description => "Reads your game from the Riot Client and VALORANT's log on your PC: mode, map, agent, score and party. Nothing to set up.";

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new ToggleSetting(RankSetting, "Show rank", true, "Your rank icon while no agent is shown, such as in the lobby or queue."),
        new ToggleSetting(PartySetting, "Show party", true, "Your party size, and whether your party is open."),
        new ToggleSetting(ScoreSetting, "Show score", true, "The live score and round during a match.")
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _options = new CardOptions(context.Settings.Get(RankSetting, true), context.Settings.Get(PartySetting, true),
            context.Settings.Get(ScoreSetting, true));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
            await _lifetime.CancelAsync();

        if (_loop is not null)
            await _loop.WaitAsync(cancellationToken);

        _client.Dispose();
        _lifetime?.Dispose();
    }

    public static PresenceActivity Card(ValorantPresence presence, ValorantMap? map, ValorantAgent? agent, DateTimeOffset startedAt,
        CardOptions options)
    {
        var mode = ModeName(presence);
        var logo = new PresenceImage(LogoUrl, "VALORANT");
        var mapImage = map is null ? logo : new PresenceImage(map.Splash ?? LogoUrl, map.Name);
        var rank = options.ShowRank ? Rank(presence.CompetitiveTier) : null;
        var pick = agent is null ? rank : new PresenceImage(agent.Icon, agent.Name);
        var partyLine = options.ShowParty ? presence.PartyAccessibility == "OPEN" ? "Open Party" : "Closed Party" : null;

        var card = new PresenceActivity
        {
            Kind = ActivityKind.Playing,
            Name = "VALORANT",
            LargeImage = logo,
            SmallImage = rank,
            StartedAt = startedAt,
            Party = options.ShowParty ? new PresenceParty(presence.PartySize, Math.Max(presence.PartySize, presence.MaxPartySize)) : null
        };

        return presence.Phase switch
        {
            "INGAME" when presence.IsRange => card with { Details = "In the Range", LargeImage = mapImage, Party = null },
            "INGAME" => card with
            {
                Details = map is null ? mode : $"{mode} - {map.Name}",
                State = options.ShowScore ? ScoreLine(presence) : null,
                LargeImage = mapImage,
                SmallImage = pick
            },
            "PREGAME" => card with { Details = $"Agent Select - {mode}", State = map?.Name, LargeImage = mapImage, SmallImage = pick },
            "QUEUE" => card with { Details = $"In Queue - {mode}" },
            _ when presence.IsIdle => card with
            {
                Details = "Idle in Lobby",
                State = partyLine,
                LargeImage = new PresenceImage(IdleUrl, "VALORANT - Idle")
            },
            _ => card with { Details = $"Lobby - {mode}", State = partyLine }
        };
    }

    public static LogMatch? CurrentMatch(string phase, LogMatch? latest, DateTimeOffset? menusSeenAt) =>
        phase is "PREGAME" or "INGAME" && latest is not null && (menusSeenAt is not { } menus || latest.FirstSeen >= menus - LogLag)
            ? latest
            : null;

    public static ValorantPresence StartedFromLog(ValorantPresence presence, LogMatch? match) =>
        presence.SessionState == "PREGAME" && match?.InGameAt is not null ? presence with { SessionState = "INGAME" } : presence;

    public static DateTimeOffset StartedAt(ValorantPresence presence, LogMatch? match, DateTimeOffset phaseStart, DateTimeOffset now) =>
        presence.Phase switch
        {
            "QUEUE" => presence.QueueEntryTime is { } entry && now - entry >= TimeSpan.Zero && now - entry < MaxQueueAge ? entry : phaseStart,
            "PREGAME" => match?.FirstSeen ?? phaseStart,
            "INGAME" => (presence.IsRange ? null : match?.InGameAt) ?? phaseStart,
            _ => phaseStart
        };

    private static string? ScoreLine(ValorantPresence presence) =>
        presence.QueueId.Equals("deathmatch", StringComparison.OrdinalIgnoreCase) ? null
        : RoundModes.Contains(presence.QueueId) || presence.IsCustomGame
            ? $"{presence.ScoreAlly} – {presence.ScoreEnemy} - Round {presence.ScoreAlly + presence.ScoreEnemy + 1}"
            : $"{presence.ScoreAlly} – {presence.ScoreEnemy}";

    public static string ModeName(ValorantPresence presence) =>
        presence.IsCustomGame ? "Custom Game"
        : Modes.TryGetValue(presence.QueueId, out var name) ? name
        : presence.QueueId == "" ? "Standard"
        : char.ToUpperInvariant(presence.QueueId[0]) + presence.QueueId[1..];

    private static PresenceImage? Rank(int tier) =>
        tier >= 3 && tier < Ranks.Length ? new PresenceImage($"{RankIcons}/{tier}/smallicon.png", Ranks[tier]) : null;

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _context.Log.Warn("Reading VALORANT failed.", exception);
                _context.ReportHealth(PluginHealth.Degraded, "Couldn't read VALORANT; check the log.");
            }

            try
            {
                await Task.Delay(PollInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        if (GameId() is not { } gameId)
        {
            _context.ReportHealth(PluginHealth.Waiting, "VALORANT isn't open.");
            _gameId = null;
            await ShowAsync(null);
            return;
        }

        if (gameId != _gameId)
        {
            _gameId = gameId;
            _log = new ValorantLog(ValorantLog.GamePath);
            _phase = "";
            _menusSeenAt = null;
        }

        if (ValorantClient.ReadLockfile() is not { } lockfile)
        {
            _context.ReportHealth(PluginHealth.Waiting, "Waiting for the Riot Client.");
            return;
        }

        ValorantPresence? presence;

        try
        {
            presence = await _client.PresenceAsync(lockfile, token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            if (!_unresponsive)
                _context.Log.Warn("The Riot Client didn't answer.", exception);

            _unresponsive = true;
            _context.ReportHealth(PluginHealth.Degraded, "The Riot Client isn't responding. Restart it and VALORANT.");
            return;
        }

        _unresponsive = false;

        if (presence is null)
        {
            _context.ReportHealth(PluginHealth.Waiting, "Waiting for VALORANT to sign in.");
            return;
        }

        try
        {
            _log.Update();
        }
        catch (IOException)
        {
        }

        try
        {
            await _client.LoadContentAsync(token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            _context.Log.Warn("Looking up maps and agents on valorant-api.com failed.", exception);
        }

        _context.ReportHealth(PluginHealth.Healthy, "Reading VALORANT.");
        await ShowAsync(Build(presence, DateTimeOffset.UtcNow));
    }

    private PresenceActivity Build(ValorantPresence presence, DateTimeOffset now)
    {
        if (presence.SessionState == "MENUS")
            _menusSeenAt = now;

        var match = CurrentMatch(presence.Phase, _log.Latest, _menusSeenAt);
        presence = StartedFromLog(presence, match);

        if (presence.Phase != _phase)
        {
            _phase = presence.Phase;
            _phaseStart = now;
            _context.Log.Info($"VALORANT is in {_phase} ({ModeName(presence)}).");
        }

        return Card(presence, _client.Map(presence.MapCode), _client.Agent(match?.Agent),
            StartedAt(presence, match, _phaseStart, now), _options);
    }

    private async Task ShowAsync(PresenceActivity? activity)
    {
        if (activity == _shown)
            return;

        _shown = activity;

        if (activity is null)
            await _context.Activity.ClearAsync();
        else
            await _context.Activity.ReportAsync(activity);
    }

    private static int? GameId()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(ProcessName);

        try
        {
            return processes.Length == 0 ? null : processes[0].Id;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
