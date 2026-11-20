#:property TargetFramework=net10.0
#:project ../plugins/Aura.Presence.League/Aura.Presence.League.csproj
#:project ../../AuraPresence/src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj

using System.Text;
using System.Text.Json;
using Aura.Presence.League;
using Aura.Presence.Sdk;

const string Me = "11111111-aaaa-bbbb-cccc-222222222222";
const string Friend = "33333333-aaaa-bbbb-cccc-444444444444";
const string Tiles = LeagueClient.GameData + "assets/characters/nautilus/skins/";

static string Quoted(string json) => $"\"{JsonEncodedText.Encode(json)}\"";
static string Private(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Quoted(json)));
static DateTimeOffset At(string time) => DateTimeOffset.Parse($"2026-11-20T{time}Z");

const string inGameJson = """
{"championId":"111","gameMode":"CLASSIC","gameQueueType":"RANKED_SOLO_5x5","gameStatus":"inGame","queueId":"420","skinVariant":"111034",
 "timeStamp":"1795186800000","rankedLeagueTier":"GOLD","rankedLeagueDivision":"II","ptyType":"open",
 "pty":"{\"partyId\":\"p1\",\"queueId\":420,\"summoners\":[1,2]}","puuid":"hidden","spectatorKey":"hidden"}
""";

var ingame = LeaguePresence.Parse(Quoted(inGameJson));
Check(ingame is { Phase: "INGAME", QueueId: 420, ChampionId: 111, SkinId: 111034, RankTier: "GOLD", RankDivision: "II", PartyType: "open" },
    "the double-encoded presence is read");
Check(LeaguePresence.Parse(inGameJson) == ingame, "a plain presence is read too");
Check(LeaguePresence.Parse("""{"gameStatus":"outOfGame","championId":"","queueId":""}""") is { Phase: "CLIENT", ChampionId: 0, QueueId: 0 },
    "empty fields are read as nothing");
Check(LeaguePresence.Parse("""{"level":"1"}""") is null && LeaguePresence.Parse("not json") is null && LeaguePresence.Parse(null) is null,
    "no status, no presence");
Check(new[] { "hosting_RANKED_SOLO_5x5", "inQueue", "championSelect", "spectating", "tutorial" }
          .Select(status => (ingame! with { GameStatus = status }).Phase)
          .SequenceEqual(["LOBBY", "QUEUE", "CHAMPSELECT", "SPECTATING", "CLIENT"]), "game statuses map to phases");

var presences = $$"""
{"presences":[
 {"puuid":"{{Friend}}","product":"league_of_legends","private":"{{Private(inGameJson.Replace("inGame", "championSelect"))}}"},
 {"puuid":"{{Me}}","product":"valorant","private":"{{Private(inGameJson.Replace("inGame", "inQueue"))}}"},
 {"puuid":"{{Me}}","product":"league_of_legends","private":"bm90IGpzb24="},
 {"puuid":"{{Me}}","product":"league_of_legends","private":"{{Private(inGameJson)}}"}]}
""";
Check(LeagueClient.OwnPresence(presences, Me) == ingame, "the own League presence is picked");
Check(LeagueClient.OwnPresence(presences, "nobody") is null && LeagueClient.OwnPresence(null, Me) is null, "no own presence");

Check(LeagueClient.ParseLobby("""{"gameConfig":{"maxLobbySize":2,"queueId":420},"members":[{"isLeader":true},{"isLeader":false}],"partyType":"closed"}""") ==
      new PresenceParty(2, 2), "the lobby gives the party size and its limit");
Check(LeagueClient.ParseLobby("""{"gameConfig":{"maxLobbySize":1},"members":[{},{}]}""") is null &&
      LeagueClient.ParseLobby("""{"gameConfig":{},"members":[]}""") is null &&
      LeagueClient.ParseLobby("""{"errorCode":"RPC_ERROR","httpStatus":404}""") is null && LeagueClient.ParseLobby(null) is null, "no usable lobby");

const string live = """
{"activePlayer":{"riotId":"Me#EUW","level":9},
 "allPlayers":[{"riotId":"Other#EUW","scores":{"kills":9,"deaths":9,"assists":9,"creepScore":9}},
               {"riotId":"Me#EUW","championName":"Nautilus","scores":{"kills":3,"deaths":1,"assists":7,"creepScore":120,"wardScore":4.5}}],
 "gameData":{"gameMode":"CLASSIC","gameTime":754.83}}
""";
Check(LeagueClient.ParseLive(live) == new LiveStats(3, 1, 7, 120, 754.83), "live stats are the active player's");
Check(LeagueClient.ParseLive(live.Replace("\"riotId\":\"Me#EUW\",\"level\"", "\"summonerName\":\"Me#EUW\",\"level\"")) == new LiveStats(3, 1, 7, 120, 754.83),
    "an older client's summoner name works");
Check(LeagueClient.ParseLive("""{"activePlayer":{"error":"Spectator mode"},"allPlayers":[],"gameData":{"gameTime":1}}""") is null &&
      LeagueClient.ParseLive("{}") is null, "no active player, no stats");

var queues = LeagueClient.ParseQueues("""
[{"id":0,"name":"Custom","description":"Custom"},{"id":0,"name":"2v2","description":"2v2"},{"id":420,"name":"Ranked Solo/Duo","description":"Ranked Solo/Duo"},
 {"id":400,"name":"Normal","description":"Draft Pick"},{"id":430,"name":"Normal","description":"Blind Pick"},{"id":1300,"name":"Nexus Blitz","description":"Blind Pick"},
 {"id":840,"name":"Beginner","description":"Beginner","gameSelectCategory":"kVersusAI"},{"id":9,"name":""}]
""");
Check(queues.Count == 6 && queues[420] == "Ranked Solo/Duo" && queues[0] == "Custom" && queues[1300] == "Nexus Blitz", "queues are keyed by id");
Check(queues[400] == "Draft Pick" && queues[430] == "Blind Pick", "a shared name uses the description");
Check(queues[840] == "Co-op vs. AI: Beginner", "bot games say so");
Check(LeagueClient.ParseQueues("{}").Count == 0 && LeagueClient.ParseQueues(null).Count == 0, "no queues");

const string championJson = """
{"id":111,"name":"Nautilus","skins":[
 {"id":111000,"isBase":true,"name":"Nautilus","tilePath":"/lol-game-data/assets/ASSETS/Characters/Nautilus/Skins/Base/Images/nautilus_splash_tile_0.jpg"},
 {"id":111027,"name":"Cosmic Paladin Nautilus","tilePath":"/lol-game-data/assets/ASSETS/Characters/Nautilus/Skins/Skin27/Images/nautilus_splash_tile_27.jpg",
  "chromas":[{"id":111034,"name":"Cosmic Paladin Nautilus (Ruby)","tilePath":"/lol-game-data/assets/ASSETS/Characters/Nautilus/Skins/Skin27/Images/nautilus_splash_tile_27.jpg"}]},
 {"id":111060,"name":"Tier One","tilePath":"/lol-game-data/assets/ASSETS/Characters/Nautilus/Skins/Skin60/Images/nautilus_splash_tile_60.jpg",
  "questSkinInfo":{"tiers":[{"id":111060,"name":"Tier One","tilePath":"/lol-game-data/assets/ASSETS/Characters/Nautilus/Skins/Skin60/Images/nautilus_splash_tile_60.jpg"},
   {"id":111061,"name":"Tier Two","tilePath":"/lol-game-data/assets/ASSETS/Characters/Nautilus/Skins/Skin61/Images/nautilus_splash_tile_61.jpg"}]}},
 {"id":111050,"name":"Broken","tilePath":"/other/path.jpg"}]}
""";
var nautilus = LeagueClient.ParseChampion(championJson);
var paladin = new LeagueSkin("Cosmic Paladin Nautilus", Tiles + "skin27/images/nautilus_splash_tile_27.jpg");
Check(nautilus is { Name: "Nautilus" } && nautilus.Skin(111027) == paladin && nautilus.Skin(111034) == paladin, "a chroma shows its skin");
Check(nautilus!.Skin(111061) == new LeagueSkin("Tier Two", Tiles + "skin61/images/nautilus_splash_tile_61.jpg"), "a skin tier shows its own art");
Check(nautilus.Skin(111050) == nautilus.Skin(0) && nautilus.Skin(0) == new LeagueSkin("Nautilus", Tiles + "base/images/nautilus_splash_tile_0.jpg"),
    "an unknown skin shows the base skin");
Check(LeagueClient.ParseChampion("""{"name":"Nobody","skins":[]}""") is null && LeagueClient.ParseChampion(null) is null, "no skins, no champion");
Check(paladin.Tile.Length <= 256, "skin URLs fit Discord");

var phaseStart = At("10:29:00");
var gameStart = At("09:00:00");
var clock = At("10:05:00");
var lobby = ingame! with { GameStatus = "hosting_RANKED_SOLO_5x5" };
var queue = ingame with { GameStatus = "inQueue" };
Check(new[] { "hosting_NORMAL", "inQueue", "championSelect", "spectating", "outOfGame" }
          .All(status => LeaguePlugin.StartedAt(ingame with { GameStatus = status }, clock, phaseStart) == phaseStart),
    "every phase outside a match counts from its own start");
Check(LeaguePlugin.StartedAt(ingame, clock, phaseStart) == clock, "the match counts from the game clock");
Check(LeaguePlugin.StartedAt(ingame, null, phaseStart) == phaseStart, "no game clock yet");

var all = new CardOptions(true, true, true);
var stats = new LiveStats(3, 1, 7, 120, 754.83);
var logo = new PresenceImage(LeaguePlugin.LogoUrl, "League of Legends");
var gold = new PresenceImage("https://raw.communitydragon.org/latest/plugins/rcp-fe-lol-shared-components/global/default/gold.png", "Gold II");

var duo = new PresenceParty(2, 2);
var matchCard = LeaguePlugin.Card(ingame, "Ranked Solo/Duo", nautilus, stats, duo, clock, all);
Check(matchCard is { Kind: ActivityKind.Playing, Name: "League of Legends", Details: "Ranked Solo/Duo - Nautilus", State: "3/1/7 - 120 CS" } &&
      matchCard.LargeImage == new PresenceImage(paladin.Tile, paladin.Name) && matchCard.SmallImage == gold &&
      matchCard.Party == new PresenceParty(2, 2) && matchCard.StartedAt == clock, "match card");
Check(matchCard == LeaguePlugin.Card(ingame, "Ranked Solo/Duo", nautilus, stats, duo, clock, all), "the same state gives an equal card");
Check(LeaguePlugin.Card(ingame, null, null, null, duo, clock, all) is { Details: "In Game", State: null } bare && bare.LargeImage == logo,
    "a match before anything is known");
Check(LeaguePlugin.Card(ingame, "ARAM", null, stats, duo, clock, all) is { Details: "ARAM", State: "3/1/7 - 120 CS" }, "stats without the champion");
Check(LeaguePlugin.Card(ingame, null, nautilus, null, duo, clock, all) is { Details: "In Game - Nautilus", State: null }, "the champion without a queue");
Check(LeaguePlugin.Card(ingame with { QueueId = 450 }, "ARAM", nautilus, stats, duo, clock, new CardOptions(false, false, false)) is
      { Details: "ARAM - Nautilus", State: null, SmallImage: null, Party: null }, "every toggle off");

var lobbyCard = LeaguePlugin.Card(lobby with { QueueId = 440 }, "Ranked Flex", nautilus, null, new PresenceParty(3, 5), gameStart, all);
Check(lobbyCard is { Details: "Lobby - Ranked Flex", State: "Open Party" } && lobbyCard.LargeImage == logo &&
      lobbyCard.Party == new PresenceParty(3, 5), "lobby card");
Check(LeaguePlugin.Card(lobby with { PartyType = "closed" }, null, null, null, duo, gameStart, all) is { Details: "Lobby", State: "Closed Party" },
    "closed party, unknown queue");
Check(LeaguePlugin.Card(queue, "Ranked Solo/Duo", null, null, duo, gameStart, all) is { Details: "In Queue - Ranked Solo/Duo", State: null },
    "queue card");
Check(LeaguePlugin.Card(ingame with { GameStatus = "championSelect" }, "Ranked Solo/Duo", nautilus, null, duo, gameStart, all) is
      { Details: "Champion Select - Ranked Solo/Duo" } pick && pick.LargeImage == logo, "champion select card");
Check(LeaguePlugin.Card(ingame with { GameStatus = "spectating" }, "ARAM", null, null, duo, gameStart, all) is { Details: "Spectating - ARAM", Party: null },
    "spectating card");
Check(LeaguePlugin.Card(ingame with { GameStatus = "outOfGame" }, "Ranked Solo/Duo", null, null, duo, gameStart, all) is
      { Details: "In the Client", State: null, Party: null }, "client card");

Check(LeaguePlugin.Rank(ingame with { RankTier = "MASTER", RankDivision = "I" })?.Text == "Master", "apex tiers have no division");
Check(LeaguePlugin.Rank(ingame with { RankTier = "EMERALD", RankDivision = "NA" })?.Text == "Emerald", "no division");
Check(LeaguePlugin.Rank(ingame with { RankTier = "" }) is null && LeaguePlugin.Rank(ingame with { RankTier = "NONE" }) is null, "unranked shows no rank");
Check(LeaguePlugin.ModeName(ingame with { GameMode = "PRACTICETOOL" }, "Custom") == "Practice Tool" &&
      LeaguePlugin.ModeName(ingame, "Ranked Solo/Duo") == "Ranked Solo/Duo", "mode names");

Console.WriteLine("All League checks passed.");

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}
