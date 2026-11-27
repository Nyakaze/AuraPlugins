#:property TargetFramework=net10.0
#:project ../plugins/Aura.Presence.MyAnimeListSync/Aura.Presence.MyAnimeListSync.csproj
#:project ../../AuraPresence/src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj

using System.Text.Json;
using Aura.Presence.MyAnimeListSync;
using Aura.Presence.Sdk;

var minutes = TimeSpan.FromMinutes(1);
var episode = new NowPlaying
{
    Title = "Frieren: Beyond Journey's End",
    Kind = MediaKind.Episode,
    Season = 1,
    Episode = 4,
    Position = 20 * minutes,
    Duration = 24 * minutes
};

var tracker = new WatchTracker(80);
Check(tracker.Claim("stremio", episode with { Position = 18 * minutes }) is null, "below the threshold does not sync");
var key = tracker.Claim("stremio", episode);
Check(key is not null, "at the threshold it syncs");
Check(tracker.Claim("stremio", episode) is null, "the same episode syncs only once");
tracker.Release(key!);
Check(tracker.Claim("stremio", episode) is not null, "a released episode can sync again");
Check(tracker.Claim("stremio", episode with { Season = 0, Episode = 9 }) is null, "specials never sync");

var parsed = MyAnimeListAccount.ToAnime(JsonDocument.Parse("""
    {"id":52991,"title":"Sousou no Frieren","alternative_titles":{"synonyms":[],"en":"Frieren: Beyond Journey’s End","ja":"葬送のフリーレン"},
     "num_episodes":28,"my_list_status":{"status":"completed","score":9,"num_episodes_watched":3,"is_rewatching":true}}
    """).RootElement);
Check(parsed == new MalAnime(52991, "Sousou no Frieren", "Frieren: Beyond Journey’s End", 28, 3, "completed", true), "reads an anime with its list status");
var airing = MyAnimeListAccount.ToAnime(JsonDocument.Parse("""{"id":1,"title":"Show","alternative_titles":{"en":""},"num_episodes":0}""").RootElement);
Check(airing == new MalAnime(1, "Show", null), "no total, no English title and no list entry read as unknown");

var seasonOne = new MalAnime(52991, "Sousou no Frieren", "Frieren: Beyond Journey’s End");
var seasonTwo = new MalAnime(59978, "Sousou no Frieren 2nd Season", "Frieren: Beyond Journey’s End Season 2");
Check(MyAnimeListSyncPlugin.Accepts("Frieren: Beyond Journey's End", 1, seasonOne, strict: true), "strict match ignores punctuation and curly quotes");
Check(MyAnimeListSyncPlugin.Accepts("Sousou no Frieren", 1, seasonOne, strict: true), "the main title matches too");
Check(!MyAnimeListSyncPlugin.Accepts("Frieren", 1, seasonOne, strict: true), "strict match needs the whole title");
Check(MyAnimeListSyncPlugin.Accepts("Frieren: Beyond Journey's End", 2, seasonTwo, strict: true), "season 2 starts with the base title");
Check(!MyAnimeListSyncPlugin.Accepts("Frieren: Beyond Journey's End", 2, seasonOne, strict: true), "season 2 never takes season 1");

Check(MyAnimeListSyncPlugin.StatusFor(4, new MalAnime(1, "Show", null, 12, 3, "watching"), addToList: true) == ("watching", false), "next episode is watching");
Check(MyAnimeListSyncPlugin.StatusFor(4, new MalAnime(1, "Show", null, 12), addToList: true) == ("watching", false), "first watch adds the entry");
Check(MyAnimeListSyncPlugin.StatusFor(4, new MalAnime(1, "Show", null, 12, 0, "plan_to_watch"), addToList: true) == ("watching", false), "planned becomes watching");
Check(MyAnimeListSyncPlugin.StatusFor(12, new MalAnime(1, "Show", null, 12, 11, "watching"), addToList: true) == ("completed", false), "last episode completes");
Check(MyAnimeListSyncPlugin.StatusFor(4, new MalAnime(1, "Show", null, null, 3, "watching"), addToList: true) == ("watching", false), "airing show without a total");
Check(MyAnimeListSyncPlugin.StatusFor(4, new MalAnime(1, "Show", null, 12, 1, "completed", true), addToList: true) == ("completed", true), "a rewatch stays a rewatch");
Check(MyAnimeListSyncPlugin.StatusFor(12, new MalAnime(1, "Show", null, 12, 11, "completed", true), addToList: true) == ("completed", false), "finishing a rewatch ends it");
Check(MyAnimeListSyncPlugin.StatusFor(4, new MalAnime(1, "Show", null, 12, 4, "watching"), addToList: true) is null, "already watched is skipped");
Check(MyAnimeListSyncPlugin.StatusFor(4, new MalAnime(1, "Show", null, 12, 12, "completed"), addToList: true) is null, "completed is skipped");
Check(MyAnimeListSyncPlugin.StatusFor(13, new MalAnime(1, "Show", null, 12, 3, "watching"), addToList: true) is null, "an episode past the total is skipped");

var setup = new Store();
Check(MyAnimeListSyncPlugin.SetupStep(setup) == 1, "setup starts with creating a client");
setup.Set(MyAnimeListSyncPlugin.ClientIdSetting, new string('a', 64));
Check(MyAnimeListSyncPlugin.SetupStep(setup) == 1, "a pasted secret is not a client ID");
Check(MyAnimeListSyncPlugin.SignInUrl(setup).AbsolutePath == "/apiconfig", "without a client ID, sign-in opens the client page");
setup.Set(MyAnimeListSyncPlugin.ClientIdSetting, "0123456789abcdef0123456789abcdef");
setup.Set(MyAnimeListSyncPlugin.VerifierSetting, "verifier");
Check(MyAnimeListSyncPlugin.SetupStep(setup) == 2, "a client ID moves on to sign-in");
Check(MyAnimeListSyncPlugin.SignInUrl(setup).ToString() ==
      "https://myanimelist.net/v1/oauth2/authorize?response_type=code&client_id=0123456789abcdef0123456789abcdef&code_challenge=verifier&code_challenge_method=plain",
    "sign-in uses the user's client and the plain challenge");
setup.Set(MyAnimeListSyncPlugin.TokenSetting, "token");
Check(MyAnimeListSyncPlugin.SetupStep(setup) is null, "a token finishes setup");
setup.Set(MyAnimeListSyncPlugin.TokenSetting, "");
Check(MyAnimeListSyncPlugin.SetupStep(setup) == 2, "signing out goes back to sign-in");

Check(MyAnimeListSyncPlugin.StatusFor(3, new MalAnime(1, "X", null, 12), addToList: true) == ("watching", false), "a show not on the list is added as watching");
Check(MyAnimeListSyncPlugin.StatusFor(3, new MalAnime(1, "X", null, 12, 1, "plan_to_watch"), addToList: true) == ("watching", false), "a planned show moves to watching");
Check(MyAnimeListSyncPlugin.StatusFor(3, new MalAnime(1, "X", null, 12), addToList: false) is null, "adding off leaves a show not on the list alone");
Check(MyAnimeListSyncPlugin.StatusFor(3, new MalAnime(1, "X", null, 12, 1, "plan_to_watch"), addToList: false) is null, "adding off leaves a planned show alone");
Check(MyAnimeListSyncPlugin.StatusFor(3, new MalAnime(1, "X", null, 12, 2, "watching"), addToList: false) == ("watching", false), "adding off still updates a watching show");
Check(MyAnimeListSyncPlugin.StatusFor(3, new MalAnime(1, "X", null, 12, 2, "completed", Rewatching: true), addToList: false) == ("completed", true), "adding off still updates a rewatch");

Console.WriteLine("All MyAnimeList sync checks passed.");

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
