#:property TargetFramework=net10.0
#:project ../plugins/Aura.Presence.Valorant/Aura.Presence.Valorant.csproj
#:project ../../AuraPresence/src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj

using System.Text;
using Aura.Presence.Sdk;
using Aura.Presence.Valorant;

const string Me = "11111111-aaaa-bbbb-cccc-222222222222";
const string Friend = "33333333-aaaa-bbbb-cccc-444444444444";
const string MatchA = "6b42ad51-0d3c-4519-bdd0-409da3c1a597";
const string MatchB = "134b2726-1111-2222-3333-444444444444";
const string Clove = "1dbf2edd-4729-0984-3115-daa5eed44993";
const string Jett = "add6443a-41bd-e414-f6ad-e58d267f4e95";

static string Private(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
static string Line(string time, string text) => $"[2026.11.16-{time}][497]LogPlatformCommon: Platform HTTP Query End. {text}";
static string Url(string time, string path) => Line(time, $"QueryName: [Q], URL [GET https://glz-eu-1.eu.a.pvp.net/{path}], @trace");
static DateTimeOffset At(string time) => DateTimeOffset.Parse($"2026-11-16T{time}Z");

const string lobbyJson = """
{"isValid":true,"sessionLoopState":"MENUS","partyOwnerSessionLoopState":"MENUS",
 "matchPresenceData":{"sessionLoopState":"MENUS","provisioningFlow":"Invalid","matchMap":"","queueId":"competitive"},
 "partyPresenceData":{"partyId":"p1","partyState":"DEFAULT","partyAccessibility":"OPEN","partySize":2,"maxPartySize":5,
   "queueEntryTime":"0001.01.01-00.00.00","partyOwnerMatchScoreAllyTeam":0,"partyOwnerMatchScoreEnemyTeam":0},
 "playerPresenceData":{"competitiveTier":13,"isIdle":false}}
""";

var lobby = ValorantPresence.Parse(lobbyJson);
Check(lobby is { Phase: "LOBBY", QueueId: "competitive", PartySize: 2, MaxPartySize: 5, CompetitiveTier: 13, IsIdle: false, QueueEntryTime: null },
    "the nested presence is read");
Check(ValorantPresence.Parse("""{"sessionLoopState":"INGAME","matchMap":"/Game/Maps/Ascent/Ascent","queueId":"unrated","partyOwnerMatchScoreAllyTeam":"7","isIdle":"true"}""")
    is { Phase: "INGAME", MapCode: "Ascent", ScoreAlly: 7, PartySize: 1, IsIdle: true }, "the flat presence is read");
Check(ValorantPresence.Parse("""{"isValid":true}""") is null && ValorantPresence.Parse("not json") is null, "no state, no presence");

var friendPrivate = Private(lobbyJson.Replace("OPEN", "CLOSED"));
var presences = $$"""
{"presences":[
 {"puuid":"{{Friend}}","product":"valorant","private":"{{friendPrivate}}"},
 {"puuid":"{{Me}}","product":"league_of_legends","private":"bm90IGpzb24="},
 {"puuid":"{{Me}}","product":"valorant","private":"{{Private(lobbyJson)}}"}]}
""";
Check(ValorantClient.OwnPresence(presences, Me) == lobby, "the own Valorant presence is picked");
Check(ValorantClient.OwnPresence(presences, "nobody") is null && ValorantClient.OwnPresence(null, Me) is null, "no own presence");

const string maps = """
{"data":[{"displayName":"Ascent","mapUrl":"/Game/Maps/Ascent/Ascent","splash":"https://media.valorant-api.com/maps/a/splash.png"},
{"displayName":"The Range","mapUrl":"/Game/Maps/Poveglia/Range","splash":null},{"displayName":"","mapUrl":"/Game/Maps/X/X"}]}
""";
var parsedMaps = ValorantClient.ParseMaps(maps);
Check(parsedMaps.Count == 2 && parsedMaps["ascent"] == new ValorantMap("Ascent", "https://media.valorant-api.com/maps/a/splash.png") &&
      parsedMaps["Range"].Splash is null, "maps are keyed by their code");
Check(ValorantClient.ParseAgents($$"""{"data":[{"uuid":"{{Clove}}","displayName":"Clove","displayIcon":"https://media.valorant-api.com/agents/c/displayicon.png"}]}""")
    [Clove.ToUpperInvariant()] == new ValorantAgent("Clove", "https://media.valorant-api.com/agents/c/displayicon.png"), "agents are keyed by uuid");
Check(ValorantClient.ParseMaps("{}").Count == 0 && ValorantClient.ParseAgents(null).Count == 0, "no content");

var log = new ValorantLog("unused");
log.Read("[2026.11.16-15.10.20:000][  1]LogTemp: nothing here");
log.Read(Url("15.10.23:730", $"pregame/v1/matches/{MatchA}"));
log.Read(Url("15.10.37:083", $"pregame/v1/matches/{MatchA}/select/{Jett}"));
log.Read(Url("15.10.48:466", $"pregame/v1/matches/{MatchA}/lock/{Clove}"));
Check(log.Latest == new LogMatch(MatchA, At("15:10:23.730"), null, Clove), "the locked agent and the pregame start are read");
log.Read(Url("15.11.50:347", $"core-game/v1/matches/{MatchA}"));
log.Read(Url("15.20.00:000", $"core-game/v1/matches/{MatchA}/loadouts"));
Check(log.Latest?.InGameAt == At("15:11:50.347"), "the match starts at its first in-match line");
log.Read(Url("16.42.08:548", $"pregame/v1/matches/{MatchB}"));
log.Read(Url("16.42.16:892", $"core-game/v1/matches/{MatchA}"));
Check(log.Latest == new LogMatch(MatchB, At("16:42:08.548"), null, null), "a late line for the old match keeps the new one");
log.Read(Url("16.42.20:000", $"match-details/v1/matches/{MatchA}"));
Check(log.Latest?.Id == MatchB, "other match URLs are ignored");

var folder = Directory.CreateTempSubdirectory("aura-valorant-checks");
var file = Path.Combine(folder.FullName, "ShooterGame.log");
var pregame = Url("15.10.23:730", $"pregame/v1/matches/{MatchA}");
File.WriteAllText(file, "﻿Log file open\r\n" + pregame[..30]);
var tail = new ValorantLog(file);
tail.Update();
Check(tail.Latest is null, "half a line waits");
File.AppendAllText(file, pregame[30..] + "\r\n");
tail.Update();
Check(tail.Latest?.Id == MatchA, "the line is read once it is complete");
File.WriteAllText(file, Url("18.30.00:000", $"pregame/v1/matches/{MatchB}") + "\n");
tail.Update();
Check(tail.Latest == new LogMatch(MatchB, At("18:30:00.000"), null, null), "a new, shorter log starts over");
folder.Delete(recursive: true);

var oldMatch = new LogMatch(MatchA, At("15:10:23.730"), At("15:11:50.347"), Clove);
Check(ValorantPlugin.CurrentMatch("INGAME", oldMatch, null) == oldMatch, "a match found after starting mid-game counts");
Check(ValorantPlugin.CurrentMatch("PREGAME", oldMatch, At("15:10:40.000")) == oldMatch, "a match a little older than the menus counts");
Check(ValorantPlugin.CurrentMatch("PREGAME", oldMatch, At("15:30:00.000")) is null, "the last match doesn't count for a new one");
Check(ValorantPlugin.CurrentMatch("LOBBY", oldMatch, null) is null, "no match in the lobby");

var now = At("15:20:00.000");
var phaseStart = At("15:19:00.000");
var gameStart = At("15:00:00.000");
var ingame = lobby! with { SessionState = "INGAME", MatchMap = "/Game/Maps/Ascent/Ascent", ScoreAlly = 7, ScoreEnemy = 5 };
var pick = ingame with { SessionState = "PREGAME" };
var queue = lobby with { PartyState = "MATCHMAKING", QueueEntryTime = At("15:18:30.000") };
var range = ingame with { MatchMap = "/Game/Maps/Poveglia/Range", QueueId = "" };
Check(ValorantPlugin.StartedAt(lobby, null, phaseStart, now) == phaseStart, "the lobby counts from entering it");
Check(ValorantPlugin.StartedAt(lobby with { IsIdle = true }, null, phaseStart, now) == phaseStart, "idle keeps the lobby timer");
Check(ValorantPlugin.StartedAt(queue, null, phaseStart, now) == At("15:18:30.000"), "the queue counts from entering it");
Check(ValorantPlugin.StartedAt(queue with { QueueEntryTime = At("13:00:00.000") }, null, phaseStart, now) == phaseStart,
    "an old queue time is ignored");
Check(ValorantPlugin.StartedAt(pick, oldMatch, phaseStart, now) == At("15:10:23.730"), "agent select counts from the log");
Check(ValorantPlugin.StartedAt(ingame, oldMatch, phaseStart, now) == At("15:11:50.347"), "the match counts from the log");
Check(ValorantPlugin.StartedAt(ingame, oldMatch with { InGameAt = null }, phaseStart, now) == phaseStart, "no log time yet");
Check(ValorantPlugin.StartedAt(range, oldMatch, phaseStart, now) == phaseStart, "the Range never uses the log");

var all = new CardOptions(true, true, true);
var ascent = parsedMaps["Ascent"];
var clove = new ValorantAgent("Clove", "https://media.valorant-api.com/agents/c/displayicon.png");
var gold = new PresenceImage("https://media.valorant-api.com/competitivetiers/03621f52-342b-cf4e-4f86-9350a49c6d04/13/smallicon.png", "Gold 2");
var logo = new PresenceImage(ValorantPlugin.LogoUrl, "VALORANT");

var lobbyCard = ValorantPlugin.Card(lobby, null, null, gameStart, all);
Check(lobbyCard is { Kind: ActivityKind.Playing, Name: "VALORANT", Details: "Lobby - Competitive", State: "Open Party" } &&
      lobbyCard.LargeImage == logo && lobbyCard.SmallImage == gold && lobbyCard.Party == new PresenceParty(2, 5) &&
      lobbyCard.StartedAt == gameStart, "lobby card");
Check(lobbyCard == ValorantPlugin.Card(lobby, null, null, gameStart, all), "the same state gives an equal card");
Check(ValorantPlugin.Card(lobby with { PartyAccessibility = "CLOSED" }, ascent, null, gameStart, all).State == "Closed Party", "closed party");
var idle = ValorantPlugin.Card(lobby with { IsIdle = true }, ascent, null, gameStart, all);
Check(idle is { Details: "Idle in Lobby" } && idle.LargeImage == new PresenceImage(ValorantPlugin.IdleUrl, "VALORANT - Idle"), "idle card");
Check(ValorantPlugin.Card(queue, ascent, null, gameStart, all) is { Details: "In Queue - Competitive", State: null } queueCard &&
      queueCard.LargeImage == logo && queueCard.SmallImage == gold, "queue card");

var noMapYet = ValorantPlugin.Card(pick with { MatchMap = "" }, null, null, gameStart, all);
Check(noMapYet is { Details: "Agent Select - Competitive", State: null } && noMapYet.LargeImage == logo, "agent select before the map is known");
Check(ValorantPlugin.Card(ingame with { MatchMap = "" }, null, null, gameStart, all) is { Details: "Competitive", State: "7 – 5 - Round 13" },
    "a match before the map is known");

Check(ValorantPlugin.StartedFromLog(pick, oldMatch).Phase == "INGAME", "the log starts the match before the presence does");
Check(ValorantPlugin.StartedFromLog(pick, oldMatch with { InGameAt = null }).Phase == "PREGAME", "agent select stays until the match starts");
Check(ValorantPlugin.StartedFromLog(lobby, oldMatch).Phase == "LOBBY", "the menus never come from the log");
Check(ValorantPlugin.StartedFromLog(pick, null).Phase == "PREGAME", "no match, no change");

var pickCard = ValorantPlugin.Card(pick, ascent, clove, gameStart, all);
Check(pickCard is { Details: "Agent Select - Competitive", State: "Ascent" } && pickCard.LargeImage == new PresenceImage(ascent.Splash!, "Ascent") &&
      pickCard.SmallImage == new PresenceImage(clove.Icon, "Clove"), "agent select card");
Check(ValorantPlugin.Card(pick, ascent, null, gameStart, all).SmallImage == gold, "no agent yet shows the rank");

var matchCard = ValorantPlugin.Card(ingame, ascent, clove, gameStart, all);
Check(matchCard is { Details: "Competitive - Ascent", State: "7 – 5 - Round 13" } && matchCard.SmallImage?.Text == "Clove" &&
      matchCard.Party == new PresenceParty(2, 5), "match card");
Check(ValorantPlugin.Card(ingame with { QueueId = "deathmatch" }, ascent, clove, gameStart, all).State is null, "no score in Deathmatch");
Check(ValorantPlugin.Card(ingame with { QueueId = "hurm", ScoreAlly = 64, ScoreEnemy = 58 }, ascent, clove, gameStart, all).State == "64 – 58",
    "Team Deathmatch has kills, not rounds");
Check(ValorantPlugin.Card(ingame with { QueueId = "", ProvisioningFlow = "CustomGame", ScoreAlly = 0, ScoreEnemy = 0 }, ascent, clove, gameStart, all)
    .State == "0 – 0 - Round 1", "a custom game starts at round 1");
var bare = ValorantPlugin.Card(ingame, ascent, null, gameStart, new CardOptions(false, false, false));
Check(bare is { State: null, SmallImage: null, Party: null }, "every toggle off");
Check(ValorantPlugin.Card(ingame, new ValorantMap("Bonsai", null), null, gameStart, all).LargeImage == new PresenceImage(ValorantPlugin.LogoUrl, "Bonsai"),
    "an unknown map keeps the logo");
Check(ValorantPlugin.Card(lobby with { CompetitiveTier = 0 }, ascent, null, gameStart, all).SmallImage is null, "unranked shows no rank");

var rangeCard = ValorantPlugin.Card(range, parsedMaps["Range"], clove, gameStart, all);
Check(rangeCard is { Details: "In the Range", State: null, Party: null } && rangeCard.SmallImage == gold &&
      rangeCard.LargeImage == new PresenceImage(ValorantPlugin.LogoUrl, "The Range"), "range card");

Check(ValorantPlugin.ModeName(lobby with { QueueId = "ggteam" }) == "Escalation", "known mode");
Check(ValorantPlugin.ModeName(lobby with { QueueId = "" }) == "Standard", "no queue");
Check(ValorantPlugin.ModeName(lobby with { QueueId = "arcade" }) == "Arcade", "unknown mode");
Check(ValorantPlugin.ModeName(lobby with { PartyState = "CUSTOM_GAME_SETUP" }) == "Custom Game", "custom game");

Console.WriteLine("All Valorant checks passed.");

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}
