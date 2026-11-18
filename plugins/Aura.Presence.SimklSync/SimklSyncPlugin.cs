using System.Text.Json.Nodes;
using Aura.Presence.Sdk;

namespace Aura.Presence.SimklSync;

public sealed class SimklSyncPlugin : IPresencePlugin
{
    public const string ClientIdSetting = "clientId";
    public const string TokenSetting = "accessToken";
    public const string RefreshTokenSetting = "refreshToken";
    public const string ThresholdSetting = "watchedPercent";
    public const int DefaultThreshold = 80;

    private static readonly Uri DeveloperPage = new("https://simkl.com/settings/developer/");
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly string[] AnimeIds = [MediaIdKeys.Kitsu, MediaIdKeys.MyAnimeList, MediaIdKeys.AniList, MediaIdKeys.AniDb];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private SimklClient _simkl = null!;
    private WatchTracker _tracker = null!;
    private IDisposable? _subscription;
    private int _threshold;
    private volatile string? _accountName;
    private volatile SimklCode? _code;
    private string? _watching;

    public string Id => "simkl-sync";

    public string DisplayName => "Simkl Sync";

    public string Description => "Marks the movies, episodes and anime you watch as watched on your Simkl.";

    public bool ShowsActivity => false;

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new NumberSetting(ThresholdSetting, "Mark as watched at", DefaultThreshold, 50, 100, "%",
                "How much of a movie or episode you watch before it counts.")
            { ShowWhen = settings => SetupStep(settings) is null, Card = "sync" },
        new TextSetting(TokenSetting, _accountName is { } name ? $"Signed in as {name}" : "Simkl account", "",
                "Your access token. Click it to see it, or clear it to sign out.", Monospace: true, Secret: true)
            { ShowWhen = settings => SetupStep(settings) is null, Card = "account" },
        new TextSetting(ClientIdSetting, "Client ID", "",
                "The Simkl app Aura signs in with. After changing it, clear the token and sign in again.", Monospace: true)
            { ShowWhen = settings => SetupStep(settings) is null, Card = "account" },
        new LinkSetting("Step 1 of 2 · Create a Simkl app", _ => DeveloperPage, "Open Simkl",
                "Create an app named Aura of the type \"TV, devices & command line\". Aura doesn't need a secret or a redirect URL.")
            { ShowWhen = settings => SetupStep(settings) is not null },
        new TextSetting(ClientIdSetting, "Client ID", "",
                "The client ID Simkl shows for your new app.", Monospace: true)
            { ShowWhen = settings => SetupStep(settings) is not null },
        new LinkSetting("Step 2 of 2 · Sign in to Simkl", _ => _code?.VerificationUrl ?? SimklClient.PinPage, "Sign in",
                _code is { } code
                    ? $"Allow Aura on Simkl with the code {code.UserCode}. Aura finishes signing in by itself."
                    : "Aura is getting a sign-in code from Simkl.")
            { ShowWhen = settings => SetupStep(settings) == 2 }
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        var clientId = context.Settings.Get(ClientIdSetting, "").Trim();

        if (clientId.Length == 0)
        {
            context.ReportHealth(PluginHealth.Waiting, "Step 1 of 2: create a Simkl app and paste its client ID.");
            return Task.CompletedTask;
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lifetime = _lifetime.Token;
        var token = context.Settings.Get(TokenSetting, "");
        _simkl = new SimklClient(_http, clientId,
            token.Length > 0 ? new SimklTokens(token, context.Settings.Get(RefreshTokenSetting, "")) : null, SaveTokens);

        if (token.Length == 0)
        {
            context.ReportHealth(PluginHealth.Waiting, "Step 2 of 2: getting a sign-in code from Simkl.");
            _ = Task.Run(() => SignInAsync(lifetime));
            return Task.CompletedTask;
        }

        StartSyncing(lifetime);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();

        if (_lifetime is not null)
            await _lifetime.CancelAsync();

        _http.Dispose();
        _lifetime?.Dispose();
    }

    public static int? SetupStep(IPluginSettings settings) =>
        settings.Get(TokenSetting, "").Length > 0 ? null : settings.Get(ClientIdSetting, "").Trim().Length == 0 ? 1 : 2;

    public static JsonObject? HistoryFor(NowPlaying media)
    {
        var anime = media.Kind is MediaKind.Episode && AnimeIds.Any(media.Ids.ContainsKey);
        var ids = new JsonObject();

        foreach (var (key, value) in media.Ids.Where(id => !anime || AnimeIds.Contains(id.Key)))
            ids[key] = value;

        if (ids.Count == 0)
            return null;

        var item = new JsonObject { ["title"] = media.Title, ["ids"] = ids };

        if (media.Year is { } year)
            item["year"] = year;

        if (media.Kind is MediaKind.Movie)
            return new JsonObject { ["movies"] = new JsonArray(item) };

        var episodes = new JsonArray(new JsonObject { ["number"] = media.Episode });

        if (anime || media.Season is null)
        {
            item["episodes"] = episodes;
        }
        else
        {
            item["use_tvdb_anime_seasons"] = true;
            item["seasons"] = new JsonArray(new JsonObject { ["number"] = media.Season, ["episodes"] = episodes });
        }

        return new JsonObject { [anime ? "anime" : "shows"] = new JsonArray(item) };
    }

    private void StartSyncing(CancellationToken lifetime)
    {
        _threshold = Math.Clamp(_context.Settings.Get(ThresholdSetting, DefaultThreshold), 50, 100);
        _tracker = new WatchTracker(_threshold);
        _context.ReportHealth(PluginHealth.Healthy, $"Marks what you watch as watched at {_threshold} %.");
        _subscription = _context.Media.Subscribe(update => OnMediaAsync(update, lifetime));
        _ = Task.Run(() => CheckSignInAsync(lifetime));
    }

    private void SaveTokens(SimklTokens tokens)
    {
        _context.Settings.Set(RefreshTokenSetting, tokens.RefreshToken);
        _context.Settings.Set(TokenSetting, tokens.AccessToken);
    }

    private async Task SignInAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var code = await _simkl.RequestCodeAsync(token);
                _code = code;
                _context.Log.Info($"Waiting for the Simkl sign-in with code {code.UserCode}.");
                _context.ReportHealth(PluginHealth.Waiting, $"Step 2 of 2: sign in to Simkl and allow Aura with the code {code.UserCode}.");

                if (await _simkl.WaitForApprovalAsync(code, token) is null)
                    continue;

                _code = null;
                _context.Log.Info("Signed in to Simkl.");
                StartSyncing(token);
                return;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
            }
            catch (SimklException exception) when (exception.Unauthorized)
            {
                _context.Log.Warn("Simkl rejected the client ID.", exception);
                _context.ReportHealth(PluginHealth.Failed,
                    "Simkl doesn't accept that client ID. Paste the ID of an app of the type \"TV, devices & command line\".");
                return;
            }
            catch (Exception exception)
            {
                _code = null;
                _context.Log.Warn("Couldn't get a sign-in code from Simkl; trying again.", exception);
                _context.ReportHealth(PluginHealth.Degraded, "Couldn't reach Simkl to sign in; trying again.");
                await Task.Delay(RetryDelay, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private ValueTask OnMediaAsync(MediaUpdate update, CancellationToken token)
    {
        if (update.Media is { Kind: MediaKind.Episode or MediaKind.Movie } seen &&
            $"{update.SourceId}|{seen.Kind}|{seen.Title}|{seen.Season}|{seen.Episode}" is var watching && watching != _watching)
        {
            _watching = watching;
            _context.Log.Info($"Following {Label(seen)} from {update.SourceId}, " +
                              $"at {seen.Position:h\\:mm\\:ss} of {seen.Duration:h\\:mm\\:ss}; marks it at {_threshold} %.");
        }

        if (update.Media is { } media && _tracker.Claim(update.SourceId, media) is { } key)
            _ = Task.Run(() => SyncAsync(key, media, token));

        return ValueTask.CompletedTask;
    }

    private async Task CheckSignInAsync(CancellationToken token)
    {
        try
        {
            var name = await _simkl.UserNameAsync(token);
            _accountName = name;
            _context.Log.Info($"Signed in to Simkl as {name}.");
            _context.ReportHealth(PluginHealth.Healthy, $"Signed in as {name}. Marks what you watch as watched at {_threshold} %.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (SimklException exception) when (exception.Unauthorized)
        {
            SignInRejected(exception);
        }
        catch (Exception exception)
        {
            _context.Log.Warn("Couldn't check the Simkl sign-in.", exception);
        }
    }

    private async Task SyncAsync(string key, NowPlaying media, CancellationToken token)
    {
        var label = Label(media);

        try
        {
            if (HistoryFor(media) is not { } history)
            {
                _context.Log.Info($"No ids for {label}; not syncing it.");
                return;
            }

            var result = await _simkl.AddToHistoryAsync(history, token);
            _context.Log.Info($"Simkl answered {result} for {label} ({history.ToJsonString()}).");

            if (result is SimklResult.Added)
                _context.ReportHealth(PluginHealth.Healthy, $"Marked {label} as watched.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (SimklException exception) when (exception.Unauthorized)
        {
            SignInRejected(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or SimklException { Transient: true })
        {
            _tracker.Release(key);
            _context.Log.Warn($"Couldn't reach Simkl for {label}; trying again on the next update.", exception);
            _context.ReportHealth(PluginHealth.Degraded, "Couldn't reach Simkl; trying again.");
        }
        catch (Exception exception)
        {
            _context.Log.Error($"Syncing {label} failed.", exception);
            _context.ReportHealth(PluginHealth.Degraded, "Couldn't sync the last one; check the log.");
        }
    }

    private static string Label(NowPlaying media) =>
        media.Kind is MediaKind.Movie
            ? $"'{media.Title}'{(media.Year is { } year ? $" ({year})" : "")}"
            : $"'{media.Title}'{(media.Season is { } season ? $" season {season}" : "")} episode {media.Episode}";

    private void SignInRejected(Exception exception)
    {
        _subscription?.Dispose();
        _context.Log.Warn("Simkl rejected the sign-in.", exception);
        _context.ReportHealth(PluginHealth.Failed, "Simkl rejected the sign-in; clear the token and sign in again.");
    }
}

public sealed class WatchTracker(int percent)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

    public string? Claim(string sourceId, NowPlaying media)
    {
        if (media is not ({ Kind: MediaKind.Movie } or { Kind: MediaKind.Episode, Episode: not null }) ||
            media is not { Position: { } position, Duration: { } duration } ||
            media.Season is 0 || duration <= TimeSpan.Zero || position / duration * 100 < percent)
            return null;

        var key = $"{sourceId}\n{media.Kind}\n{media.Title}\n{media.Season}\n{media.Episode}\n" +
                  string.Join(',', media.Ids.OrderBy(id => id.Key, StringComparer.Ordinal).Select(id => $"{id.Key}={id.Value}"));

        lock (_gate)
            return _claimed.Add(key) ? key : null;
    }

    public void Release(string key)
    {
        lock (_gate)
            _claimed.Remove(key);
    }
}
