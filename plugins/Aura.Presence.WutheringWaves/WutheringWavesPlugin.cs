using Aura.Presence.Sdk;

namespace Aura.Presence.WutheringWaves;

public sealed record CardOptions(bool Level, bool Region, bool Version, bool Timer);

public sealed class WutheringWavesPlugin : IPresencePlugin
{
    public const string IconUrl = "https://cdn.discordapp.com/app-icons/1247227126416146462/1e7d7e9ca69ea0951467994c581f70f5.png?size=512";

    private const string LevelSetting = "showLevel";
    private const string RegionSetting = "showRegion";
    private const string VersionSetting = "showVersion";
    private const string TimerSetting = "showTimer";
    private const string ShownCard = "On your activity";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WriteSettle = TimeSpan.FromSeconds(5);

    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private CardOptions _show = new(true, true, true, true);
    private (int ProcessId, DateTimeOffset At) _seen;
    private string? _readKey;
    private UnionLevel? _player;
    private string? _version;
    private PresenceActivity? _shown;

    public string Id => "wuthering-waves";

    public string DisplayName => "Wuthering Waves";

    public int DefaultPriority => 100;

    public string Description => "Shows Wuthering Waves on your profile with your Union Level and region. Nothing to set up.";

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new ToggleSetting(LevelSetting, "Union Level", true,
            "Read from the game's settings file on your PC. With Union Level and Region both off, your activity says \"Exploring SOL-III\" and Aura doesn't read that file.")
            { Card = ShownCard },
        new ToggleSetting(RegionSetting, "Region", true, "Your server, such as Europe or America.") { Card = ShownCard },
        new ToggleSetting(VersionSetting, "Game version", true, "Shows when someone hovers over the picture.") { Card = ShownCard },
        new ToggleSetting(TimerSetting, "Time played", true, "How long the game has been open.") { Card = ShownCard }
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _show = new CardOptions(
            context.Settings.Get(LevelSetting, true),
            context.Settings.Get(RegionSetting, true),
            context.Settings.Get(VersionSetting, true),
            context.Settings.Get(TimerSetting, true));
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

        _lifetime?.Dispose();
    }

    public static PresenceActivity Card(DateTimeOffset startedAt, UnionLevel? player, string? version, CardOptions show)
    {
        var level = show.Level && player is not null ? $"Union Level {player.Level}" : null;
        var region = show.Region && player is not null ? $"Region: {player.Region}" : null;

        return new PresenceActivity
        {
            Kind = ActivityKind.Playing,
            Name = "Wuthering Waves",
            Details = level ?? "Exploring SOL-III",
            State = region,
            LargeImage = new PresenceImage(IconUrl, show.Version && version is not null ? $"Wuthering Waves {version}" : "Wuthering Waves"),
            StartedAt = show.Timer ? startedAt : null
        };
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollAsync();
            }
            catch (Exception exception)
            {
                _context.Log.Warn("Reading Wuthering Waves failed.", exception);
                _context.ReportHealth(PluginHealth.Degraded, "Couldn't read Wuthering Waves; check the log.");
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

    private async Task PollAsync()
    {
        if (WutheringWavesGame.Find() is not { } game)
        {
            _context.ReportHealth(PluginHealth.Waiting, "Wuthering Waves isn't running.");
            await ShowAsync(null);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (_seen.ProcessId != game.ProcessId)
        {
            _seen = (game.ProcessId, game.StartedAt ?? now);
            _version = game.Version();
            _context.Log.Info($"Wuthering Waves {_version ?? "(unknown version)"} is running{(game.Root is null ? ", but its folder couldn't be found" : "")}.");
        }

        var reads = _show.Level || _show.Region;

        if (reads)
            ReadPlayer(game, now);

        var missing = reads && (game.Root is null || (_readKey is not null && _player is null));
        _context.ReportHealth(missing ? PluginHealth.Degraded : PluginHealth.Healthy,
            missing ? "Couldn't find your Union Level in the game's files; check the log." : "Showing Wuthering Waves.");
        await ShowAsync(Card(_seen.At, _player, _version, _show));
    }

    private void ReadPlayer(WutheringWavesGame game, DateTimeOffset now)
    {
        if (game.LocalStorage() is not { } file)
            return;

        var written = File.GetLastWriteTimeUtc(file);
        var key = $"{file}\n{written.Ticks}";

        if (key == _readKey || now - _seen.At < StartupGrace || now.UtcDateTime - written < WriteSettle)
            return;

        _readKey = key;

        try
        {
            var player = WutheringWavesGame.ReadUnionLevel(file);

            if (player is null)
                _context.Log.Warn("Couldn't find the Union Level in the game's settings file.");
            else if (player != _player)
                _context.Log.Info($"Union Level {player.Level}, region {player.Region}.");

            _player = player;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _context.Log.Warn("Couldn't read the game's settings file; trying again when it changes.", exception);
        }
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
}
