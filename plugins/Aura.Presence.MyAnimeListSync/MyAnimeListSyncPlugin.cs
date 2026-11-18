using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aura.Presence.Sdk;

namespace Aura.Presence.MyAnimeListSync;

public sealed class MyAnimeListSyncPlugin : IPresencePlugin
{
    public const string ClientIdSetting = "clientId";
    public const string TokenSetting = "accessToken";
    public const string RefreshTokenSetting = "refreshToken";
    public const string VerifierSetting = "signInVerifier";
    public const string ThresholdSetting = "watchedPercent";
    public const int DefaultThreshold = 80;
    public const string Redirect = "http://localhost:47301/";

    private static readonly Uri ClientsPage = new("https://myanimelist.net/apiconfig");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private IPluginContext _context = null!;
    private CancellationTokenSource? _lifetime;
    private MyAnimeListAccount _account = null!;
    private WatchTracker _tracker = null!;
    private IDisposable? _subscription;
    private int _threshold;
    private volatile string? _accountName;
    private string? _watching;

    public string Id => "myanimelist-sync";

    public string DisplayName => "MyAnimeList Sync";

    public string Description => "Marks the anime episodes you watch as watched on your MyAnimeList.";

    public bool ShowsActivity => false;

    public IReadOnlyList<PluginSetting> Settings =>
    [
        new NumberSetting(ThresholdSetting, "Mark as watched at", DefaultThreshold, 50, 100, "%",
                "How much of an episode you watch before it counts.")
            { ShowWhen = settings => SetupStep(settings) is null, Card = "sync" },
        new TextSetting(TokenSetting, _accountName is { } name ? $"Signed in as {name}" : "MyAnimeList account", "",
                "Your access token. Click it to see it, or clear it to sign out.", Monospace: true, Secret: true)
            { ShowWhen = settings => SetupStep(settings) is null, Card = "account" },
        new TextSetting(ClientIdSetting, "Client ID", "",
                "The MyAnimeList client Aura signs in with. After changing it, clear the token and sign in again.", Monospace: true)
            { ShowWhen = settings => SetupStep(settings) is null, Card = "account" },
        new LinkSetting("Step 1 of 2 · Create a MyAnimeList client", _ => ClientsPage, "Open MyAnimeList",
                $"Create an ID named Aura with the app type other and the redirect URL {Redirect}. Aura doesn't need a secret.")
            { ShowWhen = settings => SetupStep(settings) is not null },
        new TextSetting(ClientIdSetting, "Client ID", "",
                "The Client ID MyAnimeList shows for your new client.", Monospace: true)
            { ShowWhen = settings => SetupStep(settings) is not null },
        new LinkSetting("Step 2 of 2 · Sign in to MyAnimeList", SignInUrl, "Sign in",
                "Allow Aura on MyAnimeList. Aura picks up the sign-in by itself.")
            { ShowWhen = settings => SetupStep(settings) == 2 }
    ];

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lifetime = _lifetime.Token;

        if (SetupStep(context.Settings) is null)
        {
            Begin(lifetime);
        }
        else if (SetupStep(context.Settings) == 2)
        {
            if (context.Settings.Get(VerifierSetting, "").Length == 0)
                context.Settings.Set(VerifierSetting, RandomNumberGenerator.GetHexString(64, lowercase: true));

            context.ReportHealth(PluginHealth.Waiting, "Step 2 of 2: sign in to MyAnimeList.");
            _ = Task.Run(() => AwaitSignInAsync(lifetime));
        }
        else
        {
            context.ReportHealth(PluginHealth.Waiting, context.Settings.Get(ClientIdSetting, "").Length > 0
                ? "That client ID doesn't look right. Paste the Client ID, not the secret."
                : "Step 1 of 2: create a MyAnimeList client and paste its ID.");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
            await _lifetime.CancelAsync();

        _subscription?.Dispose();
        _http.Dispose();
        _lifetime?.Dispose();
    }

    public static int? SetupStep(IPluginSettings settings) =>
        settings.Get(TokenSetting, "").Length > 0 ? null : ClientIdOf(settings) is null ? 1 : 2;

    public static Uri SignInUrl(IPluginSettings settings) =>
        ClientIdOf(settings) is { } clientId
            ? MyAnimeListAccount.SignInUrl(clientId, settings.Get(VerifierSetting, ""))
            : ClientsPage;

    private static string? ClientIdOf(IPluginSettings settings) =>
        settings.Get(ClientIdSetting, "") is { Length: 32 } clientId && clientId.All(char.IsAsciiHexDigit) ? clientId : null;

    public static bool Accepts(string title, int? season, MalAnime candidate, bool strict) =>
        TitleMatches(title, season, candidate.English, strict) || TitleMatches(title, season, candidate.Title, strict);

    public static (string Status, bool Rewatching)? StatusFor(int episode, MalAnime anime)
    {
        if (anime.Watched >= episode || anime.Episodes < episode)
            return null;

        return anime.Episodes <= episode ? ("completed", false) : anime.Rewatching ? ("completed", true) : ("watching", false);
    }

    private static bool TitleMatches(string title, int? season, string? candidate, bool strict)
    {
        var wanted = Normalize(title);
        var found = Normalize(candidate);

        if (wanted.Length == 0 || found.Length == 0)
            return false;

        if (season is > 1)
            return found != wanted && (strict ? found.StartsWith(wanted, StringComparison.Ordinal) : found.Contains(wanted, StringComparison.Ordinal));

        return strict
            ? found == wanted
            : found.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(found, StringComparison.Ordinal);
    }

    private static string Normalize(string? title) =>
        string.Concat((title ?? "").Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private void Begin(CancellationToken lifetime)
    {
        var settings = _context.Settings;
        _threshold = Math.Clamp(settings.Get(ThresholdSetting, DefaultThreshold), 50, 100);
        _account = new MyAnimeListAccount(_http, settings.Get(ClientIdSetting, ""),
            new MalTokens(settings.Get(TokenSetting, ""), settings.Get(RefreshTokenSetting, "")), Save);
        _tracker = new WatchTracker(_threshold);
        _context.ReportHealth(PluginHealth.Healthy, $"Marks episodes as watched at {_threshold} %.");
        _subscription = _context.Media.Subscribe(update => OnMediaAsync(update, lifetime));

        if (lifetime.IsCancellationRequested)
            _subscription.Dispose();

        _ = Task.Run(() => CheckSignInAsync(lifetime));
    }

    private void Save(MalTokens tokens)
    {
        _context.Settings.Set(TokenSetting, tokens.Access);
        _context.Settings.Set(RefreshTokenSetting, tokens.Refresh);
    }

    private async Task AwaitSignInAsync(CancellationToken token)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(Redirect);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException exception)
        {
            _context.Log.Warn($"Couldn't listen on {Redirect} for the MyAnimeList sign-in.", exception);
            _context.ReportHealth(PluginHealth.Failed, $"Couldn't listen on {Redirect} for the sign-in. Close what uses it, then turn the plugin off and on.");
            return;
        }

        using var stop = token.Register(listener.Stop);

        while (true)
        {
            HttpListenerContext request;

            try
            {
                request = await listener.GetContextAsync();
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }

            var query = request.Request.QueryString;

            if (query["error"] is { } error)
            {
                Reply(request, "Aura isn't signed in, because MyAnimeList didn't allow it. Go back to Aura to try again.");
                _context.Log.Info($"MyAnimeList sign-in ended with '{error}'.");
                _context.ReportHealth(PluginHealth.Waiting, "The MyAnimeList sign-in wasn't allowed. Sign in again.");
                continue;
            }

            if (query["code"] is not { } code)
            {
                request.Response.StatusCode = 404;
                request.Response.Close();
                continue;
            }

            try
            {
                Save(await MyAnimeListAccount.SignInAsync(_http, ClientIdOf(_context.Settings) ?? "", code,
                    _context.Settings.Get(VerifierSetting, ""), token));
                _context.Settings.Set(VerifierSetting, "");
                _context.Log.Info("Signed in to MyAnimeList.");
                Begin(token);
                Reply(request, "Aura is signed in to MyAnimeList. You can close this tab.");
                return;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or MalException)
            {
                Reply(request, "Aura couldn't finish signing in to MyAnimeList. Go back to Aura to try again.");
                _context.Log.Warn("Finishing the MyAnimeList sign-in failed.", exception);
                _context.ReportHealth(PluginHealth.Waiting, "Couldn't finish the MyAnimeList sign-in. Sign in again.");
            }
        }
    }

    private static void Reply(HttpListenerContext request, string text)
    {
        try
        {
            request.Response.ContentType = "text/plain; charset=utf-8";
            request.Response.Close(Encoding.UTF8.GetBytes(text), willBlock: true);
        }
        catch (HttpListenerException)
        {
        }
    }

    private ValueTask OnMediaAsync(MediaUpdate update, CancellationToken token)
    {
        if (update.Media is { Kind: MediaKind.Episode } seen && $"{update.SourceId}|{seen.Title}|{seen.Season}|{seen.Episode}" is var watching &&
            watching != _watching)
        {
            _watching = watching;
            _context.Log.Info($"Following '{seen.Title}' season {seen.Season?.ToString() ?? "-"} episode {seen.Episode} from {update.SourceId}, " +
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
            var name = await _account.NameAsync(token);
            _accountName = name;
            _context.Log.Info($"Signed in to MyAnimeList as {name}.");
            _context.ReportHealth(PluginHealth.Healthy, $"Signed in as {name}. Marks episodes as watched at {_threshold} %.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (MalException exception) when (exception.Unauthorized)
        {
            SignInRejected(exception);
        }
        catch (Exception exception)
        {
            _context.Log.Warn("Couldn't check the MyAnimeList sign-in.", exception);
        }
    }

    private async Task SyncAsync(string key, NowPlaying media, CancellationToken token)
    {
        var episode = media.Episode!.Value;
        var label = $"'{media.Title}'{(media.Season is { } season ? $" season {season}" : "")} episode {episode}";

        try
        {
            if (await ResolveAsync(media, token) is not { } id)
            {
                _context.Log.Info($"No MyAnimeList match for {label}; not syncing it.");
                return;
            }

            if (await _account.AnimeAsync(id, token) is not { } anime)
            {
                _context.Log.Info($"MyAnimeList has no anime {id} for {label}; not syncing it.");
                return;
            }

            if (StatusFor(episode, anime) is not { } next)
            {
                _context.Log.Info($"Skipped {label} on {anime.Title} ({id}): watched {anime.Watched?.ToString() ?? "none"} of {anime.Episodes?.ToString() ?? "?"}.");
                return;
            }

            await _account.SaveAsync(id, episode, next.Status, next.Rewatching, token);
            _context.Log.Info($"Marked {anime.Title} ({id}) episode {episode} as watched, status {next.Status}{(next.Rewatching ? ", rewatching" : "")}, from {label}.");
            _context.ReportHealth(PluginHealth.Healthy, $"Marked {anime.Title} episode {episode} as watched.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (MalException exception) when (exception.Unauthorized)
        {
            SignInRejected(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or MalException { Transient: true })
        {
            _tracker.Release(key);
            _context.Log.Warn($"Couldn't reach MyAnimeList for {label}; trying again on the next update.", exception);
            _context.ReportHealth(PluginHealth.Degraded, "Couldn't reach MyAnimeList; trying again.");
        }
        catch (Exception exception)
        {
            _context.Log.Error($"Syncing {label} failed.", exception);
            _context.ReportHealth(PluginHealth.Degraded, "Couldn't sync the last episode; check the log.");
        }
    }

    private async Task<int?> ResolveAsync(NowPlaying media, CancellationToken token)
    {
        if (media.Ids.TryGetValue(MediaIdKeys.MyAnimeList, out var mal) && int.TryParse(mal, out var malId))
            return malId;

        var search = media.Season is > 1 ? $"{media.Title} Season {media.Season}" : media.Title;
        var strict = !media.IsAnime;

        return (await _account.SearchAsync(search, token)).FirstOrDefault(candidate => Accepts(media.Title, media.Season, candidate, strict))?.Id;
    }

    private void SignInRejected(Exception exception)
    {
        _subscription?.Dispose();
        _context.Log.Warn("MyAnimeList rejected the sign-in.", exception);
        _context.ReportHealth(PluginHealth.Failed, "MyAnimeList rejected the sign-in. Clear the token and sign in again.");
    }
}

public sealed class WatchTracker(int percent)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

    public string? Claim(string sourceId, NowPlaying media)
    {
        if (media is not { Kind: MediaKind.Episode, Episode: { } episode, Position: { } position, Duration: { } duration } ||
            media.Season is 0 || duration <= TimeSpan.Zero || position / duration * 100 < percent)
            return null;

        var key = $"{sourceId}\n{media.Title}\n{media.Season}\n{episode}\n" +
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
