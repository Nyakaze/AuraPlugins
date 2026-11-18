using System.Text.RegularExpressions;
using Aura.Presence.Sdk;
using Aura.Presence.Services.Cinemeta;

namespace Aura.Presence.Netflix;

public sealed partial class NetflixPlugin : IPresencePlugin
{
    public const string Host = "www.netflix.com";
    public const string IconUrl = "https://raw.githubusercontent.com/Nyakaze/AuraPlugins/main/plugins/Aura.Presence.Netflix/icon.png";

    private const int MissingPollsBeforeClear = 3;
    private const int FetchAttempts = 3;
    private const int MaxImageKey = 256;
    private const string HideWhenPausedSetting = "hideWhenPaused";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RepublishInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AllowedDrift = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PausedHideDelay = TimeSpan.FromMinutes(5);

    private readonly CinemetaClient _cinemeta = new();

    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private string? _watchId;
    private NetflixTitle? _title;
    private int _fetches;
    private string? _lookupKey;
    private CinemetaItem? _lookup;
    private int _missingPolls;
    private bool _hideWhenPaused;
    private DateTimeOffset? _pausedSince;
    private string? _shownKey;
    private DateTimeOffset? _shownStart;
    private DateTimeOffset _publishedAt;

    public string Id => "netflix";

    public string DisplayName => "Netflix";

    public int DefaultPriority => 100;

    public string Description => "Shows what you watch on Netflix in your browser, read through the Aura browser extension.";

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new ToggleSetting(HideWhenPausedSetting, "Hide when paused", false,
            "Hides your Discord activity after 5 minutes paused. It comes back when you press play.")
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _hideWhenPaused = context.Settings.Get(HideWhenPausedSetting, false);
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

        await ClearAsync();
        _cinemeta.Dispose();
        _lifetime?.Dispose();
    }

    public static string? WatchId(Uri url) => WatchPath().Match(url.AbsolutePath) is { Success: true } match ? match.Groups[1].Value : null;

    public static BrowserPage? Pick(IEnumerable<BrowserPage> pages) =>
        pages.Where(page => page.Video is not null && WatchId(page.Url) is not null)
            .OrderBy(page => page.Video!.Paused)
            .ThenByDescending(page => page.Active)
            .FirstOrDefault();

    public static NowPlaying ToNowPlaying(NetflixTitle? title, CinemetaItem? lookup, BrowserVideo video) => new()
    {
        Title = title?.Title ?? "Netflix",
        Kind = title switch
        {
            { IsMovie: true } => MediaKind.Movie,
            { Episode: not null } => MediaKind.Episode,
            _ => MediaKind.Other
        },
        Season = title?.Season,
        Episode = title?.Episode,
        EpisodeTitle = title?.EpisodeTitle,
        Year = title?.Year,
        PosterUrl = title?.PosterUrl is { Length: <= MaxImageKey } poster ? poster : lookup?.PosterUrl,
        Ids = lookup is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [MediaIdKeys.Imdb] = lookup.ImdbId },
        State = video.Paused ? PlaybackState.Paused : PlaybackState.Playing,
        Position = video.Position,
        Duration = video.Duration
    };

    public static string? StateLine(NowPlaying media)
    {
        var label = media.Kind switch
        {
            MediaKind.Episode when media.Season is null => $"Episode {media.Episode}",
            MediaKind.Episode => $"S{media.Season:00}E{media.Episode:00}",
            MediaKind.Movie when media.Year is { } year => $"Movie ({year})",
            MediaKind.Movie => "Movie",
            _ => null
        };

        if (media.State is not PlaybackState.Paused)
            return label;

        return label is null ? "Paused" : $"{label} - Paused";
    }

    public static bool HidesActivity(bool hideWhenPaused, DateTimeOffset? pausedSince, DateTimeOffset now) =>
        hideWhenPaused && now - pausedSince >= PausedHideDelay;

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
                _context.Log.Warn("Reading Netflix failed.", exception);
                _context.ReportHealth(PluginHealth.Degraded, "Couldn't read Netflix; check the log.");
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
        var browser = _context.Browser;

        if (Pick(browser.PagesOn(Host)) is not { Video: { } video } page)
        {
            _context.ReportHealth(PluginHealth.Waiting,
                browser.IsConnected ? "Nothing is playing on Netflix." : "Waiting for the Aura browser extension.");

            if (++_missingPolls >= MissingPollsBeforeClear)
                await ClearAsync();

            return;
        }

        _missingPolls = 0;
        var watchId = WatchId(page.Url)!;

        if (watchId != _watchId)
        {
            _watchId = watchId;
            _title = null;
            _fetches = 0;
        }

        if (_title is null && _fetches < FetchAttempts)
            await ReadTitleAsync(browser, page, watchId, token);

        if (_title is { IsPlayable: false })
        {
            _context.ReportHealth(PluginHealth.Waiting, "Nothing is playing on Netflix.");
            await WithdrawAsync();
            return;
        }

        if (_title is { } title)
            await LookUpAsync(title, token);

        _context.ReportHealth(_title is null ? PluginHealth.Degraded : PluginHealth.Healthy,
            _title is null ? "Couldn't read the title from Netflix; check the log." : "Reading Netflix through the Aura browser extension.");
        await ShowAsync(ToNowPlaying(_title, _lookup, video));
    }

    private async Task ReadTitleAsync(IBrowser browser, BrowserPage page, string watchId, CancellationToken token)
    {
        _fetches++;
        var json = await browser.FetchAsync(page, $"/nq/website/memberapi/release/metadata?movieid={watchId}", token);
        _title = NetflixTitle.Parse(json);

        if (_title is { IsPlayable: false } extra)
            _context.Log.Info($"Ignoring {watchId}: '{extra.Title}' isn't an episode or a movie, such as a trailer.");
        else if (_title is { } title)
            _context.Log.Info($"Now playing {watchId}: '{title.Title}'" +
                              (title.IsMovie ? $" (movie, {title.Year})." : $" S{title.Season}E{title.Episode} '{title.EpisodeTitle}'."));
        else
            _context.Log.Warn($"Netflix's metadata for {watchId} was {(json is null ? "not readable" : "in an unknown format")} (attempt {_fetches}).");
    }

    private async Task LookUpAsync(NetflixTitle title, CancellationToken token)
    {
        var key = $"{title.Title}|{title.IsMovie}";

        if (key == _lookupKey)
            return;

        _lookupKey = key;
        _lookup = null;

        try
        {
            _lookup = await _cinemeta.FindAsync(title.Title, title.IsMovie ? CinemetaType.Movie : CinemetaType.Series, token);
            _context.Log.Info(_lookup is null ? $"No IMDb match for '{title.Title}'." : $"Looked up '{title.Title}' as {_lookup.Name} ({_lookup.ImdbId}).");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            _context.Log.Warn($"Looking up '{title.Title}' on Cinemeta failed.", exception);
        }
    }

    private async Task ShowAsync(NowPlaying media)
    {
        var now = DateTimeOffset.UtcNow;
        var startedAt = now - media.Position;
        var activity = new PresenceActivity
        {
            Kind = ActivityKind.Watching,
            Name = media.Title,
            Details = media.Title,
            State = StateLine(media),
            LargeImage = new PresenceImage(media.PosterUrl ?? IconUrl, media.EpisodeTitle ?? media.Title),
            StartedAt = startedAt,
            EndsAt = startedAt + media.Duration,
            Buttons = _title is { ShowId: > 0 } title
                ? [new PresenceButton("Open on Netflix", new Uri($"https://www.netflix.com/title/{title.ShowId}"))]
                : []
        };
        var key = $"{activity.Name}\n{activity.State}\n{activity.LargeImage?.Key}\n{activity.EndsAt is null}";
        var allowedDrift = media.State is PlaybackState.Paused ? RepublishInterval : AllowedDrift;
        _pausedSince = media.State is PlaybackState.Paused ? _pausedSince ?? now : null;
        var hidden = HidesActivity(_hideWhenPaused, _pausedSince, now);
        var unchanged = hidden
            ? _shownKey is null
            : key == _shownKey && (startedAt - _shownStart)?.Duration() < allowedDrift;

        if (!unchanged && hidden)
        {
            _shownKey = null;
            await _context.Activity.ClearAsync();
            _context.Log.Info("Paused for 5 minutes; hiding the activity until playback resumes.");
        }
        else if (!unchanged)
        {
            await _context.Activity.ReportAsync(activity);
            _shownKey = key;
            _shownStart = startedAt;
        }

        if (!unchanged || now - _publishedAt >= RepublishInterval)
        {
            await _context.Media.PublishAsync(media);
            _publishedAt = now;
        }
    }

    private async Task ClearAsync()
    {
        _watchId = null;
        _title = null;
        _fetches = 0;
        _missingPolls = 0;
        await WithdrawAsync();
    }

    private async Task WithdrawAsync()
    {
        _pausedSince = null;

        if (_shownKey is null && _publishedAt == default)
            return;

        _shownKey = null;
        _shownStart = null;
        _publishedAt = default;
        await _context.Activity.ClearAsync();
        await _context.Media.PublishAsync(null);
    }

    [GeneratedRegex(@"^/watch/(\d+)")]
    private static partial Regex WatchPath();
}
