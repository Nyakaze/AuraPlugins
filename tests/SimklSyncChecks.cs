#:property TargetFramework=net10.0
#:project ../plugins/Aura.Presence.SimklSync/Aura.Presence.SimklSync.csproj
#:project ../../AuraPresence/src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj

using System.Net;
using System.Text.Json;
using Aura.Presence.Sdk;
using Aura.Presence.SimklSync;

var minutes = TimeSpan.FromMinutes(1);
var episode = new NowPlaying
{
    Title = "Breaking Bad",
    Kind = MediaKind.Episode,
    Season = 2,
    Episode = 4,
    Year = 2008,
    Ids = new Dictionary<string, string> { [MediaIdKeys.Imdb] = "tt0903747" },
    Position = 40 * minutes,
    Duration = 47 * minutes
};
var movie = new NowPlaying
{
    Title = "Dune: Part Two",
    Kind = MediaKind.Movie,
    Year = 2024,
    Ids = new Dictionary<string, string> { [MediaIdKeys.Imdb] = "tt15239678" },
    Position = 140 * minutes,
    Duration = 166 * minutes
};

var tracker = new WatchTracker(80);
Check(tracker.Claim("stremio", episode with { Position = 30 * minutes }) is null, "below the threshold does not sync");
var key = tracker.Claim("stremio", episode);
Check(key is not null, "at the threshold it syncs");
Check(tracker.Claim("stremio", episode with { Position = 45 * minutes, State = PlaybackState.Paused }) is null, "the same episode syncs only once");
Check(tracker.Claim("stremio", movie) is not null, "movies sync");
Check(tracker.Claim("stremio", movie with { Position = 150 * minutes }) is null, "the same movie syncs only once");
Check(tracker.Claim("stremio", episode with { Episode = 5 }) is not null, "the next episode syncs");
tracker.Release(key!);
Check(tracker.Claim("stremio", episode) is not null, "a released episode can sync again");
Check(tracker.Claim("stremio", episode with { Season = 0, Episode = 9 }) is null, "specials never sync");
Check(tracker.Claim("stremio", episode with { Episode = null }) is null, "an episode without a number never syncs");
Check(tracker.Claim("stremio", episode with { Kind = MediaKind.Other }) is null, "other media never syncs");
Check(tracker.Claim("stremio", episode with { Episode = 6, Duration = null }) is null, "no duration, no sync");
Check(new WatchTracker(100).Claim("stremio", episode) is null, "the threshold setting is used");

Check(Json(SimklSyncPlugin.HistoryFor(episode)) ==
      """{"shows":[{"title":"Breaking Bad","ids":{"imdb":"tt0903747"},"year":2008,"use_tvdb_anime_seasons":true,"seasons":[{"number":2,"episodes":[{"number":4}]}]}]}""",
    "an imdb episode is sent by season with tvdb numbering");
Check(Json(SimklSyncPlugin.HistoryFor(movie)) ==
      """{"movies":[{"title":"Dune: Part Two","ids":{"imdb":"tt15239678"},"year":2024}]}""",
    "a movie is sent as a movie");
var anime = episode with
{
    Title = "Frieren", Season = null, Episode = 7, Year = null,
    Ids = new Dictionary<string, string> { [MediaIdKeys.Kitsu] = "46474", [MediaIdKeys.Imdb] = "tt22248376" }
};
Check(Json(SimklSyncPlugin.HistoryFor(anime)) ==
      """{"anime":[{"title":"Frieren","ids":{"kitsu":"46474"},"episodes":[{"number":7}]}]}""",
    "anime ids send a flat episode and leave out the series imdb id");
Check(Json(SimklSyncPlugin.HistoryFor(episode with { Season = null })) ==
      """{"shows":[{"title":"Breaking Bad","ids":{"imdb":"tt0903747"},"year":2008,"episodes":[{"number":4}]}]}""",
    "an episode without a season is sent flat");
Check(SimklSyncPlugin.HistoryFor(episode with { Ids = new Dictionary<string, string>() }) is null, "no ids, no sync");

Check(SimklClient.ResultOf(Parse("""{"added":{"movies":0,"shows":0,"episodes":1},"not_found":{"movies":[],"shows":[],"episodes":[]}}""")) ==
      SimklResult.Added, "an added episode is added");
Check(SimklClient.ResultOf(Parse("""{"added":{"movies":0,"shows":0,"episodes":0},"not_found":{"movies":[],"shows":[],"episodes":[]}}""")) ==
      SimklResult.AlreadyWatched, "nothing added means already watched");
Check(SimklClient.ResultOf(Parse("""{"added":{"movies":0,"shows":0,"episodes":0},"not_found":{"movies":[],"shows":[{"ids":{}}],"episodes":[]}}""")) ==
      SimklResult.NotFound, "anything not found is not found");

var setup = new Store();
Check(SimklSyncPlugin.SetupStep(setup) == 1, "setup starts with creating an app");
setup.Set(SimklSyncPlugin.ClientIdSetting, "  ");
Check(SimklSyncPlugin.SetupStep(setup) == 1, "a blank client ID is no client ID");
setup.Set(SimklSyncPlugin.ClientIdSetting, "abc123");
Check(SimklSyncPlugin.SetupStep(setup) == 2, "a client ID moves on to sign-in");
setup.Set(SimklSyncPlugin.TokenSetting, "token");
Check(SimklSyncPlugin.SetupStep(setup) is null, "a token finishes setup");

var simkl = new FakeSimkl();
var saved = new List<SimklTokens>();
var signIn = new SimklClient(new HttpClient(simkl), "abc123", null, saved.Add);
simkl.Answers.Enqueue((HttpStatusCode.OK,
    """{"device_code":"dev","user_code":"BDWP-HQPK","verification_uri":"https://simkl.com/pin","verification_uri_complete":"https://simkl.com/pin?user_code=BDWP-HQPK","expires_in":900,"interval":0}"""));
var code = await signIn.RequestCodeAsync(CancellationToken.None);
Check(code is { UserCode: "BDWP-HQPK", DeviceCode: "dev" } && code.VerificationUrl.Query == "?user_code=BDWP-HQPK", "a sign-in code opens the prefilled pin page");
Check(simkl.Queries[^1].StartsWith("?client_id=abc123&app-name=aura&app-version=", StringComparison.Ordinal) &&
      simkl.Requests[^1].StartsWith("POST /oauth2/device ", StringComparison.Ordinal) && simkl.Requests[^1].Contains("scope=media%3Aread+media%3Awrite"), "the code asks to write history and names the app");
simkl.Answers.Enqueue((HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""));
simkl.Answers.Enqueue((HttpStatusCode.BadRequest, """{"error":"slow_down"}"""));
simkl.Answers.Enqueue((HttpStatusCode.OK, """{"access_token":"at1","refresh_token":"rt","expires_in":604800,"token_type":"Bearer"}"""));
var tokens = await signIn.WaitForApprovalAsync(code with { Interval = TimeSpan.Zero }, CancellationToken.None);
Check(tokens == new SimklTokens("at1", "rt") && saved[^1] == tokens, "approval keeps and saves the tokens");
Check(simkl.Requests[^1].Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code") && simkl.Requests[^1].Contains("device_code=dev"),
    "polling uses the device grant");
simkl.Answers.Enqueue((HttpStatusCode.BadRequest, """{"error":"expired_token"}"""));
Check(await signIn.WaitForApprovalAsync(code with { Interval = TimeSpan.Zero }, CancellationToken.None) is null, "an expired code asks for a new one");

simkl.Answers.Enqueue((HttpStatusCode.Unauthorized, """{"error":"invalid_token","code":401,"message":"Access token has expired"}"""));
simkl.Answers.Enqueue((HttpStatusCode.OK, """{"access_token":"at2","refresh_token":"rt","expires_in":604800,"token_type":"Bearer"}"""));
simkl.Answers.Enqueue((HttpStatusCode.OK, """{"user":{"name":"zoe"},"account":{"id":1}}"""));
Check(await signIn.UserNameAsync(CancellationToken.None) == "zoe", "an expired access token is refreshed and the call retried");
Check(simkl.Requests[^2].Contains("grant_type=refresh_token") && simkl.Requests[^1] == "GET /users/settings Bearer at2" && saved[^1].AccessToken == "at2",
    "the refreshed token is used and saved");

simkl.Answers.Enqueue((HttpStatusCode.Unauthorized, """{"error":"invalid_token","code":401}"""));
simkl.Answers.Enqueue((HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
var rejected = await Throws(() => signIn.UserNameAsync(CancellationToken.None));
Check(rejected is SimklException { Unauthorized: true }, "a rejected refresh means signing in again");

simkl.Answers.Enqueue((HttpStatusCode.BadRequest, """{"error":"RATE_LIMIT"}"""));
var limited = await Throws(() => signIn.AddToHistoryAsync(SimklSyncPlugin.HistoryFor(movie)!, CancellationToken.None));
Check(limited is SimklException { Transient: true, Unauthorized: false }, "the history write lock is retried later");

Console.WriteLine("All Simkl sync checks passed.");

static string Json(System.Text.Json.Nodes.JsonObject? json) => json?.ToJsonString() ?? "null";

static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

static async Task<Exception?> Throws(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}

sealed class Store : IPluginSettings
{
    private readonly Dictionary<string, object?> _values = [];

    public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var value) ? (T)value! : defaultValue;

    public void Set<T>(string key, T value) => _values[key] = value;
}

sealed class FakeSimkl : HttpMessageHandler
{
    public Queue<(HttpStatusCode Status, string Body)> Answers { get; } = new();

    public List<string> Requests { get; } = [];

    public List<string> Queries { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : $" {await request.Content.ReadAsStringAsync(cancellationToken)}";
        var bearer = request.Headers.Authorization is { } auth ? $" Bearer {auth.Parameter}" : "";
        Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}{bearer}{body}");
        Queries.Add(request.RequestUri.Query);
        var (status, json) = Answers.Dequeue();

        return new HttpResponseMessage(status) { Content = new StringContent(json) };
    }
}
