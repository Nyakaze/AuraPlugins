#:property TargetFramework=net10.0
#:project ../plugins/Aura.Presence.Netflix/Aura.Presence.Netflix.csproj
#:project ../../AuraPresence/src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj
#:project ../../AuraPresence/services/Aura.Presence.Services.Cinemeta/Aura.Presence.Services.Cinemeta.csproj

using Aura.Presence.Netflix;
using Aura.Presence.Sdk;
using Aura.Presence.Services.Cinemeta;

const string show = """
{"version":"2.1","video":{"type":"show","title":"Dark","id":80100172,"currentEpisode":80117551,
 "boxart":[{"w":426,"h":607,"url":"https://occ-0.nflxso.net/dark.jpg"}],
 "seasons":[
  {"seq":1,"year":2017,"episodes":[{"episodeId":80100173,"id":80100173,"seq":1,"title":"Secrets"}]},
  {"seq":2,"year":2019,"episodes":[{"episodeId":80117550,"id":80117550,"seq":1,"title":"Beginnings and Endings"},
                                   {"episodeId":80117551,"id":80117551,"seq":2,"title":"Dark Matter"}]}]}}
""";
const string movie = """
{"video":{"type":"movie","title":"Glass Onion","id":81458416,"year":2022,"runtime":8340,
 "boxart":[{"w":426,"h":607,"url":"https://occ-0.nflxso.net/onion.jpg"}]}}
""";

var dark = NetflixTitle.Parse(show)!;
Check(dark is { ShowId: 80100172, Title: "Dark", IsMovie: false, Season: 2, Episode: 2, EpisodeTitle: "Dark Matter", Year: 2017 },
    $"show parsed: {dark}");
Check(dark.PosterUrl == "https://occ-0.nflxso.net/dark.jpg", "show poster");

var onion = NetflixTitle.Parse(movie)!;
Check(onion is { Title: "Glass Onion", IsMovie: true, Year: 2022, Season: null, Episode: null }, $"movie parsed: {onion}");

Check(dark.IsPlayable && onion.IsPlayable, "episodes and movies count");
var trailer = NetflixTitle.Parse("""{"video":{"type":"supplemental","title":"Trailer: Guarding Stars","id":83252969}}""")!;
Check(trailer is { Title: "Trailer: Guarding Stars", IsPlayable: false }, $"trailer is ignored: {trailer}");
var unmatched = NetflixTitle.Parse("""{"video":{"type":"show","title":"Dark","id":1,"currentEpisode":9,"seasons":[]}}""")!;
Check(!unmatched.IsPlayable, "a show without the playing episode is ignored");

Check(NetflixTitle.Parse(null) is null, "no body");
Check(NetflixTitle.Parse("<html>Sign in</html>") is null, "html body");
Check(NetflixTitle.Parse("""{"video":{"title":"X","id":"not a number"}}""") is null, "wrong types");

Check(NetflixPlugin.WatchId(new Uri("https://www.netflix.com/watch/81011359?trackId=1&tctx=2")) == "81011359", "watch id");
Check(NetflixPlugin.WatchId(new Uri("https://www.netflix.com/browse")) is null, "browse page");

var minutes = TimeSpan.FromMinutes(1);
var playing = new BrowserVideo(10 * minutes, 50 * minutes, false);
var paused = playing with { Paused = true };
var lookup = new CinemetaItem("tt5753856", "Dark", CinemetaType.Series, null, "2017-2020");

var episode = NetflixPlugin.ToNowPlaying(dark, lookup, playing);
Check(episode is { Kind: MediaKind.Episode, Season: 2, Episode: 2, State: PlaybackState.Playing }, "episode media");
Check(episode.Ids[MediaIdKeys.Imdb] == "tt5753856" && episode.Position == 10 * minutes && episode.Duration == 50 * minutes, "episode ids and time");
Check(NetflixPlugin.StateLine(episode) == "S02E02" && episode.EpisodeTitle == "Dark Matter", "episode state line");
Check(NetflixPlugin.StateLine(NetflixPlugin.ToNowPlaying(dark, null, paused)) == "S02E02 - Paused", "paused episode");
Check(NetflixPlugin.StateLine(NetflixPlugin.ToNowPlaying(onion, null, paused)) == "Movie (2022) - Paused", "paused movie");

var now = DateTimeOffset.UtcNow;
Check(!NetflixPlugin.HidesActivity(false, now - 10 * minutes, now), "hide off keeps a long pause");
Check(!NetflixPlugin.HidesActivity(true, now - 4 * minutes, now), "hide waits 5 minutes");
Check(NetflixPlugin.HidesActivity(true, now - 5 * minutes, now), "hide after 5 minutes");
Check(!NetflixPlugin.HidesActivity(true, null, now), "playing is never hidden");

var unknown = NetflixPlugin.ToNowPlaying(null, null, playing);
Check(unknown is { Title: "Netflix", Kind: MediaKind.Other } && NetflixPlugin.StateLine(unknown) is null, "unknown title");

var longPoster = dark with { PosterUrl = "https://occ-0.nflxso.net/" + new string('a', 300) };
var fallback = new CinemetaItem("tt5753856", "Dark", CinemetaType.Series, "https://images.metahub.space/poster/dark.jpg", null);
Check(NetflixPlugin.ToNowPlaying(longPoster, fallback, playing).PosterUrl == fallback.PosterUrl, "long poster falls back to Cinemeta");

var browse = new BrowserPage("1:1", new Uri("https://www.netflix.com/browse"), "Netflix", true, playing);
var background = new BrowserPage("1:2", new Uri("https://www.netflix.com/watch/1"), "Netflix", false, paused);
var front = new BrowserPage("1:3", new Uri("https://www.netflix.com/watch/2"), "Netflix", false, playing);
Check(NetflixPlugin.Pick([browse, background, front]) == front, "playing watch page wins");
Check(NetflixPlugin.Pick([browse]) is null, "no watch page");

Console.WriteLine("Netflix checks passed.");

static void Check(bool condition, string what)
{
    if (!condition)
        throw new Exception($"Failed: {what}");
}
