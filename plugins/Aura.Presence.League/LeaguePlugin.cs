using System.ComponentModel;
using System.Globalization;
using Aura.Presence.Sdk;

namespace Aura.Presence.League;

public sealed record CardOptions(bool ShowRank, bool ShowParty, bool ShowStats);

public sealed class LeaguePlugin : IPresencePlugin
{
    public const string LogoUrl = "https://raw.githubusercontent.com/Nyakaze/AuraPlugins/main/plugins/Aura.Presence.League/icon.png";

    private const string ClientProcess = "LeagueClientUx";
    private const string GameProcess = "League of Legends";
    private const string RankSetting = "showRank";
    private const string PartySetting = "showParty";
    private const string StatsSetting = "showStats";
    private const string RankIcons = "https://raw.communitydragon.org/latest/plugins/rcp-fe-lol-shared-components/global/default/";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly string[] ApexTiers = ["MASTER", "GRANDMASTER", "CHALLENGER"];

    private readonly LeagueClient _client = new();
    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private CardOptions _options = new(true, true, true);
    private int? _gameId;
    private string _phase = "";
    private DateTimeOffset _phaseStart;
    private DateTimeOffset? _gameClock;
    private PresenceParty? _party;
    private bool _unresponsive;
    private PresenceActivity? _shown;

    public string Id => "league";

    public string DisplayName => "League of Legends";

    public int DefaultPriority => 100;

    public string Description =>
        "Reads your game from the Riot Client and League's live game data on your PC: queue, champion, skin, KDA and party. Nothing to set up.";

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new ToggleSetting(RankSetting, "Show rank", true, "Your rank crest."),
        new ToggleSetting(PartySetting, "Show party", true, "Your party size, and whether your party is open."),
        new ToggleSetting(StatsSetting, "Show KDA and CS", true, "Your kills, deaths, assists and minions killed during a match.")
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _options = new CardOptions(context.Settings.Get(RankSetting, true), context.Settings.Get(PartySetting, true),
            context.Settings.Get(StatsSetting, true));
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

    public static PresenceActivity Card(LeaguePresence presence, string? mode, LeagueChampion? champion, LiveStats? stats,
        PresenceParty? lobby, DateTimeOffset startedAt, CardOptions options)
    {
        var logo = new PresenceImage(LogoUrl, "League of Legends");
        var party = options.ShowParty ? lobby : null;
        var partyLine = options.ShowParty ? presence.PartyType switch { "open" => "Open Party", "closed" => "Closed Party", _ => null } : null;

        string Phase(string label) => mode is null ? label : $"{label} - {mode}";

        var card = new PresenceActivity
        {
            Kind = ActivityKind.Playing,
            Name = "League of Legends",
            LargeImage = logo,
            SmallImage = options.ShowRank ? Rank(presence) : null,
            StartedAt = startedAt
        };

        return presence.Phase switch
        {
            "INGAME" => card with
            {
                Details = champion is null ? mode ?? "In Game" : $"{mode ?? "In Game"} - {champion.Name}",
                State = options.ShowStats && stats is not null ? $"{stats.Kills}/{stats.Deaths}/{stats.Assists} - {stats.CreepScore} CS" : null,
                LargeImage = champion?.Skin(presence.SkinId) is { } skin ? new PresenceImage(skin.Tile, skin.Name) : logo,
                Party = party
            },
            "SPECTATING" => card with { Details = Phase("Spectating") },
            "CHAMPSELECT" => card with { Details = Phase("Champion Select"), Party = party },
            "QUEUE" => card with { Details = Phase("In Queue"), Party = party },
            "LOBBY" => card with { Details = Phase("Lobby"), State = partyLine, Party = party },
            _ => card with { Details = "In the Client" }
        };
    }

    public static string? ModeName(LeaguePresence presence, string? queue) =>
        presence.GameMode.Equals("PRACTICETOOL", StringComparison.OrdinalIgnoreCase) ? "Practice Tool" : queue;

    public static DateTimeOffset StartedAt(LeaguePresence presence, DateTimeOffset? gameClock, DateTimeOffset phaseStart) =>
        presence.Phase == "INGAME" && gameClock is { } clock ? clock : phaseStart;

    public static PresenceImage? Rank(LeaguePresence presence)
    {
        var tier = presence.RankTier.ToUpperInvariant();

        if (tier is "" or "NONE" or "UNRANKED")
            return null;

        var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tier.ToLowerInvariant());
        var text = ApexTiers.Contains(tier) || presence.RankDivision is not ("I" or "II" or "III" or "IV") ? name : $"{name} {presence.RankDivision}";

        return new PresenceImage($"{RankIcons}{tier.ToLowerInvariant()}.png", text);
    }

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
                _context.Log.Warn("Reading League of Legends failed.", exception);
                _context.ReportHealth(PluginHealth.Degraded, "Couldn't read League of Legends; check the log.");
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
        if (Game() is not { } game)
        {
            _context.ReportHealth(PluginHealth.Waiting, "League of Legends isn't open.");
            _gameId = null;
            await ShowAsync(null);
            return;
        }

        if (game.Id != _gameId)
        {
            _gameId = game.Id;
            _phase = "";
            _party = null;
        }

        if (LeagueClient.ReadLockfile(LeagueClient.RiotLockfile) is not { } lockfile)
        {
            _context.ReportHealth(PluginHealth.Waiting, "Waiting for the Riot Client.");
            return;
        }

        LeaguePresence? presence;

        try
        {
            presence = await _client.PresenceAsync(lockfile, token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            if (!_unresponsive)
                _context.Log.Warn("The Riot Client didn't answer.", exception);

            _unresponsive = true;
            _context.ReportHealth(PluginHealth.Degraded, "The Riot Client isn't responding. Restart it and League.");
            return;
        }

        _unresponsive = false;

        if (presence is null)
        {
            _context.ReportHealth(PluginHealth.Waiting, "Waiting for League to sign in.");
            return;
        }

        try
        {
            await _client.LoadContentAsync(presence.ChampionId, token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            _context.Log.Warn("Looking up queues and champions on CommunityDragon failed.", exception);
        }

        if (presence.Phase is "LOBBY" or "QUEUE")
            _party = game.Folder is { } folder && LeagueClient.ReadLockfile(Path.Combine(folder, "lockfile")) is { } leagueLockfile
                ? await _client.LobbyAsync(leagueLockfile, token)
                : null;
        else if (presence.Phase == "CLIENT")
            _party = null;

        var stats = presence.Phase == "INGAME" ? await _client.LiveAsync(token) : null;

        _context.ReportHealth(PluginHealth.Healthy, "Reading League of Legends.");
        await ShowAsync(Build(presence, stats, DateTimeOffset.UtcNow));
    }

    private PresenceActivity Build(LeaguePresence presence, LiveStats? stats, DateTimeOffset now)
    {
        var mode = ModeName(presence, _client.Queue(presence.QueueId));

        if (presence.Phase != _phase)
        {
            _phase = presence.Phase;
            _phaseStart = now;
            _gameClock = null;
            _context.Log.Info($"League is in {_phase} ({mode ?? "unknown queue"}).");
        }

        if (_gameClock is null && stats is { GameTime: > 0 })
            _gameClock = now - TimeSpan.FromSeconds(Math.Floor(stats.GameTime));

        return Card(presence, mode, _client.Champion(presence.ChampionId), stats, _party,
            StartedAt(presence, _gameClock, _phaseStart), _options);
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

    private static (int Id, string? Folder)? Game()
    {
        System.Diagnostics.Process[] processes =
        [
            .. System.Diagnostics.Process.GetProcessesByName(ClientProcess),
            .. System.Diagnostics.Process.GetProcessesByName(GameProcess)
        ];

        try
        {
            if (processes.Length == 0)
                return null;

            var process = processes[0];

            try
            {
                return (process.Id, Path.GetDirectoryName(process.MainModule?.FileName));
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                return (process.Id, null);
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
