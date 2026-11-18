using System.Text.RegularExpressions;
using Aura.Presence.Sdk;

namespace Aura.Presence.Roblox;

public sealed partial class RobloxPlugin : IPresencePlugin
{
    public const string LogoUrl = "https://cdn.discordapp.com/app-icons/363445589247131668/f2b60e350a2097289b3b0b877495e55f.png?size=512";

    private const string ProcessName = "RobloxPlayerBeta";
    private const string ShowHomeSetting = "showHome";
    private const string OpenButtonSetting = "openButton";
    private const string AvatarSetting = "showAvatar";
    private const string GameMessagesSetting = "gameMessages";
    private const long LatestUnixSeconds = 32503680000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HomeDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LookupRetry = TimeSpan.FromMinutes(1);
    private static readonly string LogFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

    private readonly RobloxApi _api = new();
    private readonly Dictionary<long, (long? Value, DateTimeOffset At)> _universes = [];
    private readonly Dictionary<long, (RobloxGame? Value, DateTimeOffset At)> _games = [];
    private readonly Dictionary<long, (string? Value, DateTimeOffset At)> _placeNames = [];
    private readonly Dictionary<long, (PresenceImage? Value, DateTimeOffset At)> _avatars = [];
    private readonly Dictionary<long, (string? Value, DateTimeOffset At)> _assets = [];

    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private bool _showHome;
    private bool _openButton;
    private bool _showAvatar;
    private bool _gameMessages;
    private RobloxLog? _log;
    private DateTimeOffset? _loggedJoin;
    private DateTimeOffset? _loggedMessages;
    private PresenceActivity? _shown;
    private string? _shownKey;

    public string Id => "roblox";

    public string DisplayName => "Roblox";

    public int DefaultPriority => 100;

    public string Description => "Reads the game you play from Roblox's log, including what games send for Bloxstrap's rich presence. Nothing to set up.";

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new ToggleSetting(ShowHomeSetting, "Show while browsing", true,
            "Shows \"Browsing games\" while Roblox is open but you aren't in a game."),
        new ToggleSetting(OpenButtonSetting, "Open game button", false,
            "Adds a button to your activity that opens the game's Roblox page."),
        new ToggleSetting(AvatarSetting, "Show my avatar", true,
            "Shows your Roblox avatar as the small picture on your activity."),
        new ToggleSetting(GameMessagesSetting, "Let games add details", true,
            "Some games send their own details for your activity, made for Bloxstrap (BloxstrapRPC), such as the map or your score.")
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _showHome = context.Settings.Get(ShowHomeSetting, true);
        _openButton = context.Settings.Get(OpenButtonSetting, false);
        _showAvatar = context.Settings.Get(AvatarSetting, true);
        _gameMessages = context.Settings.Get(GameMessagesSetting, true);
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

        _api.Dispose();
        _lifetime?.Dispose();
    }

    public static string GameName(string name) =>
        LeadingTags().Replace(name, "").Trim() is { Length: > 0 } clean ? clean : name.Trim();

    public static PresenceActivity HomeCard(DateTimeOffset openedAt, PresenceImage? avatar) => new()
    {
        Kind = ActivityKind.Playing,
        Name = "Roblox",
        Details = "Browsing games",
        LargeImage = new PresenceImage(LogoUrl, "Roblox"),
        SmallImage = avatar,
        StartedAt = openedAt
    };

    public static PresenceActivity GameCard(RobloxGame? game, string? placeName, RobloxPlace place, RobloxRpc rpc,
        PresenceImage? avatar, Func<long, string?> assetUrl, bool openButton)
    {
        var name = game is null ? "In a game" : GameName(game.Name);
        var icon = new PresenceImage(game?.IconUrl ?? LogoUrl, game?.Creator is { } creator ? $"{name}, by {creator}" : name);

        return new PresenceActivity
        {
            Kind = ActivityKind.Playing,
            Name = "Roblox",
            Details = rpc.Details ?? name,
            State = rpc.State ?? (placeName is not null && GameName(placeName) != name ? placeName : null),
            LargeImage = Pick(rpc.LargeImage, icon, assetUrl),
            SmallImage = Pick(rpc.SmallImage, avatar, assetUrl),
            StartedAt = rpc.StartedAt is { } start ? Unix(start) : place.PlayingSince,
            EndsAt = rpc.EndsAt is { } end ? Unix(end) : null,
            Buttons = openButton
                ? [new PresenceButton("Open game", new Uri($"https://www.roblox.com/games/{game?.RootPlaceId ?? place.PlaceId}"))]
                : []
        };
    }

    public static PresenceImage? Pick(RpcImage? custom, PresenceImage? fallback, Func<long, string?> assetUrl) => custom switch
    {
        null => fallback,
        { Hidden: true } => null,
        _ when ((custom.AssetId is { } id ? assetUrl(id) : null) ?? fallback?.Key) is { } key =>
            new PresenceImage(key, custom.Text ?? fallback?.Text),
        _ => null
    };

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
                _context.Log.Warn("Reading Roblox failed.", exception);
                _context.ReportHealth(PluginHealth.Degraded, "Couldn't read Roblox's log; check the log.");
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
        if (RobloxStartedAt() is not { } startedAt)
        {
            _context.ReportHealth(PluginHealth.Waiting, "Roblox isn't open.");
            _log = null;
            await ShowAsync(null);
            return;
        }

        if (RobloxLog.Newest(LogFolder, startedAt) is not { } path)
        {
            _context.ReportHealth(PluginHealth.Waiting, "Waiting for Roblox's log.");
            return;
        }

        if (_log?.Path != path)
        {
            _log = new RobloxLog(path);
            _context.Log.Info($"Reading Roblox's log {Path.GetFileName(path)}.");
        }

        _log.Update();
        var (card, found) = await CardAsync(_log, token);
        _context.ReportHealth(found ? PluginHealth.Healthy : PluginHealth.Degraded,
            found ? "Reading Roblox's log." : "Couldn't look up the game on Roblox; check the log.");
        await ShowAsync(card);
    }

    private async Task<(PresenceActivity? Card, bool Found)> CardAsync(RobloxLog log, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;

        if (log.Place is not { } place)
        {
            if (log.LeftAt is { } left && now - left < HomeDelay)
                return (_shown, true);

            return (_showHome ? HomeCard(log.OpenedAt ?? now, await AvatarAsync(log, token)) : null, true);
        }

        var universe = place.UniverseId ?? await CachedAsync(_universes, place.PlaceId, _api.UniverseAsync, token);
        var game = universe is { } id ? await CachedAsync(_games, id, _api.GameAsync, token) : null;
        var placeName = game is not null && game.RootPlaceId != place.PlaceId
            ? await CachedAsync(_placeNames, place.PlaceId, _api.PlaceNameAsync, token)
            : null;

        if (_loggedJoin != place.JoinedAt)
        {
            _loggedJoin = place.JoinedAt;
            _context.Log.Info(game is null
                ? $"Joined place {place.PlaceId}, but couldn't look up its game."
                : $"Joined place {place.PlaceId} in '{game.Name}'{(placeName is null ? "" : $" ('{placeName}')")}.");
        }

        var rpc = _gameMessages ? log.Rpc : RobloxRpc.None;

        if (rpc != RobloxRpc.None && _loggedMessages != place.JoinedAt)
        {
            _loggedMessages = place.JoinedAt;
            _context.Log.Info($"Place {place.PlaceId} sends its own details for the activity.");
        }

        foreach (var asset in new[] { rpc.LargeImage?.AssetId, rpc.SmallImage?.AssetId }.OfType<long>())
            await CachedAsync(_assets, asset, _api.AssetImageAsync, token);

        return (GameCard(game, placeName, place, rpc, await AvatarAsync(log, token),
            asset => _assets.GetValueOrDefault(asset).Value, _openButton), game is not null);
    }

    private async Task<PresenceImage?> AvatarAsync(RobloxLog log, CancellationToken token) =>
        _showAvatar && log.UserId is { } user ? await CachedAsync(_avatars, user, _api.AvatarAsync, token) : null;

    private async Task<T?> CachedAsync<T>(Dictionary<long, (T? Value, DateTimeOffset At)> cache, long key,
        Func<long, CancellationToken, Task<T?>> fetch, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;

        if (cache.TryGetValue(key, out var hit) && (hit.Value is not null || now - hit.At < LookupRetry))
            return hit.Value;

        T? value = default;

        try
        {
            value = await fetch(key, token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            _context.Log.Warn($"Looking up {key} on Roblox failed.", exception);
        }

        cache[key] = (value, now);

        return value;
    }

    private async Task ShowAsync(PresenceActivity? activity)
    {
        var key = activity is null
            ? null
            : $"{activity.Name}\n{activity.Details}\n{activity.State}\n{activity.LargeImage}\n{activity.SmallImage}\n" +
              $"{activity.StartedAt}\n{activity.EndsAt}\n{activity.Buttons.Count}";

        if (key == _shownKey)
            return;

        _shownKey = key;
        _shown = activity;

        if (activity is null)
            await _context.Activity.ClearAsync();
        else
            await _context.Activity.ReportAsync(activity);
    }

    private static DateTimeOffset? Unix(long seconds) =>
        seconds is > 0 and < LatestUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static DateTime? RobloxStartedAt()
    {
        DateTime? startedAt = null;

        foreach (var process in System.Diagnostics.Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    var started = process.StartTime.ToUniversalTime();
                    startedAt = startedAt < started ? startedAt : started;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    startedAt = DateTime.MinValue;
                }
            }
        }

        return startedAt;
    }

    [GeneratedRegex(@"^(\s*\[[^\]]*\])+")]
    private static partial Regex LeadingTags();
}
