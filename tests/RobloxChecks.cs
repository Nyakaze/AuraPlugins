#:property TargetFramework=net10.0
#:project ../plugins/Aura.Presence.Roblox/Aura.Presence.Roblox.csproj
#:project ../../AuraPresence/src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj

using System.Text;
using Aura.Presence.Roblox;
using Aura.Presence.Sdk;

static string Line(string time, string text) => $"2026-11-14T{time}Z,1.0,4bd8,6 {text}";
static string Join(string time, long place) =>
    Line(time, $"[FLog::Output] ! Joining game '11111111-2222-3333-4444-555555555555' place {place} at 10.0.0.1");
static string Load(string time, long place, long universe) =>
    Line(time, $"[FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:{place}, join_time:0.4, universeid:{universe}, referral_page:, sid:x, userid:1, ");
static string Leave(string time) => Line(time, "[FLog::SingleSurfaceApp] leaveUGCGameInternal");
static DateTimeOffset At(string time) => DateTimeOffset.Parse($"2026-11-14T{time}Z");
static string Rpc(string time, string json) => Line(time, $"[FLog::Output] [BloxstrapRPC] {json}");
static string? NoAssets(long id) => null;

var log = new RobloxLog("unused");
log.Read("[FLog::Output] All use of Roblox services must comply with Roblox's Terms of Use.");
log.Read(Line("20:05:47.315", "Warning [FLog::RobloxStarter] Starting module: Logging"));
Check(log.OpenedAt == At("20:05:47.315") && log.Place is null, "the first timestamp is when Roblox opened");
Check(log.UserId is null, "no user before Roblox names one");
log.Read(Line("20:05:47.400", "Info [FLog::BTId] LogWin32BTId, App, cookie Native => Engine set RBXEventTrackerV2 as CreateDate=09/30/2026 07:58:17&rbxid=5&rbxuid=4242"));
Check(log.UserId == 4242, "the user id comes from Roblox's first lines");

log.Read(Join("20:06:55.957", 100));
Check(log.Place is { PlaceId: 100, UniverseId: null }, "a join sets the place");
log.Read(Load("20:06:55.957", 100, 900));
Check(log.Place is { UniverseId: 900 } place && place.PlayingSince == At("20:06:55.957"), "the load time line gives the universe");

log.Read(Line("20:31:22.709", "Info [FLog::UgcExperienceController] UgcExperienceController: doTeleport: url "));
log.Read(Line("20:31:23.122", "[FLog::SessionTransitionFSM] Teleported."));
log.Read(Join("20:31:23.135", 101));
log.Read(Load("20:31:23.135", 101, 900));
Check(log.Place is { PlaceId: 101, UniverseId: 900 } teleported && teleported.PlayingSince == At("20:06:55.957"),
    "a teleport inside the game keeps the timer");

log.Read(Leave("20:40:00.000"));
Check(log.Place is null && log.LeftAt == At("20:40:00.000"), "leaving goes back to the home screen");
log.Read(Join("20:40:30.000", 100));
log.Read(Load("20:40:30.000", 100, 900));
Check(log.Place?.PlayingSince == At("20:06:55.957") && log.LeftAt is null, "rejoining the same game soon keeps the timer");

log.Read(Leave("20:50:00.000"));
log.Read(Join("20:55:00.000", 100));
log.Read(Load("20:55:00.000", 100, 900));
Check(log.Place?.PlayingSince == At("20:55:00.000"), "rejoining later restarts the timer");

log.Read(Join("21:00:00.000", 200));
log.Read(Load("21:00:00.000", 999, 800));
Check(log.Place is { PlaceId: 200, UniverseId: null }, "a load time line for another place is ignored");
log.Read(Load("21:00:00.000", 200, 800));
Check(log.Place?.PlayingSince == At("21:00:00.000"), "another game restarts the timer");

var folder = Directory.CreateTempSubdirectory("aura-roblox-checks");
var file = Path.Combine(folder.FullName, "0.745.0_20261114T200547Z_Player_8988B_last.log");
File.WriteAllText(Path.Combine(folder.FullName, "0.745.0_20261114T200547Z_Player_CF971_CrashHandler_last.log"), "");
File.WriteAllText(file, Join("20:06:55.957", 100) + "\n" + Load("20:06:55.957", 100, 900)[..40]);
Check(RobloxLog.Newest(folder.FullName, DateTime.MinValue) == file, "the newest player log is picked, not the crash handler's");
Check(RobloxLog.Newest(folder.FullName, DateTime.UtcNow.AddMinutes(1)) is null, "a log from before Roblox started is skipped");
Check(RobloxLog.Newest(Path.Combine(folder.FullName, "missing"), DateTime.MinValue) is null, "no log folder");

var tail = new RobloxLog(file);
tail.Update();
Check(tail.Place is { PlaceId: 100, UniverseId: null }, "complete lines are read");
File.AppendAllText(file, Load("20:06:55.957", 100, 900)[40..] + "\n");
File.AppendAllBytes(file, [0xF0, 0x9F]);
tail.Update();
Check(tail.Place?.UniverseId == 900, "a line written in two parts is read once it is complete");
File.AppendAllBytes(file, [0x8E, 0x83, .. Encoding.UTF8.GetBytes(" Dress\n" + Leave("20:08:01.712") + "\n")]);
tail.Update();
Check(tail.Place is null, "lines after a split character are still read");
folder.Delete(recursive: true);

const string games = """
{"data":[{"id":900,"rootPlaceId":100,"name":"[🎃] Dress To Impress","creator":{"id":7,"name":"Dress To Impress Group","type":"Group"},"maxPlayers":16}]}
""";
const string icons = """{"data":[{"targetId":900,"state":"Completed","imageUrl":"https://tr.rbxcdn.com/180DAY-abc/512/512/Image/Png/noFilter","version":"TN3"}]}""";
Check(RobloxApi.ParseGame(games) == new RobloxGame("[🎃] Dress To Impress", "Dress To Impress Group", 100, null), "game parsed");
Check(RobloxApi.ParseThumbnail(icons) == "https://tr.rbxcdn.com/180DAY-abc/512/512/Image/Png/noFilter", "icon parsed");
Check(RobloxApi.ParseThumbnail("""{"data":[{"targetId":900,"state":"Pending","imageUrl":""}]}""") is null, "a pending icon is skipped");
Check(RobloxApi.ParseUniverse("""{"universeId":900}""") == 900, "universe parsed");
Check(RobloxApi.ParsePlaceName("""{"AssetId":101,"Name":"DTI: Casual Server","AssetTypeId":9}""") == "DTI: Casual Server", "place name parsed");
Check(RobloxApi.ParseGame("""{"data":[]}""") is null && RobloxApi.ParseGame("<html>") is null && RobloxApi.ParseGame(null) is null,
    "unknown games");
Check(RobloxApi.ParseUniverse("""{"errors":[{"code":9002}]}""") is null, "an error has no universe");

Check(RobloxPlugin.GameName("[🎃] Dress To Impress") == "Dress To Impress", "leading tags are dropped");
Check(RobloxPlugin.GameName("[UPD 5] [x2] Blox Fruits") == "Blox Fruits", "several tags are dropped");
Check(RobloxPlugin.GameName("Grow a Garden 🍓") == "Grow a Garden 🍓", "other names stay");
Check(RobloxPlugin.GameName("[Testing]") == "[Testing]", "a name of only tags stays");

var game = new RobloxGame("[🎃] Dress To Impress", "Dress To Impress Group", 100, "https://tr.rbxcdn.com/icon");
var lobby = new RobloxPlace(100, 900, At("20:06:55"), At("20:06:55"));
var card = RobloxPlugin.GameCard(game, null, lobby, RobloxRpc.None, null, NoAssets, openButton: true);
Check(card is { Kind: ActivityKind.Playing, Name: "Roblox", Details: "Dress To Impress", State: null }, "game card lines");
Check(card.LargeImage == new PresenceImage("https://tr.rbxcdn.com/icon", "Dress To Impress, by Dress To Impress Group") && card.StartedAt == At("20:06:55"),
    "game card image and timer");
Check(card.Buttons.Single() == new PresenceButton("Open game", new Uri("https://www.roblox.com/games/100")), "open game button");

var server = RobloxPlugin.GameCard(game, "DTI: Casual Server", lobby with { PlaceId = 101 }, RobloxRpc.None, null, NoAssets, openButton: false);
Check(server.State == "DTI: Casual Server" && server.Buttons.Count == 0, "a sub-place shows its name, and the button can be off");
Check(RobloxPlugin.GameCard(game, "Dress To Impress", lobby with { PlaceId = 101 }, RobloxRpc.None, null, NoAssets, true).State is null, "a place named like its game shows no state");

var unknown = RobloxPlugin.GameCard(null, null, lobby with { PlaceId = 555 }, RobloxRpc.None, null, NoAssets, true);
Check(unknown is { Name: "Roblox", Details: "In a game" } && unknown.LargeImage?.Key == RobloxPlugin.LogoUrl, "a game that couldn't be looked up");
Check(unknown.Buttons.Single().Url.AbsoluteUri == "https://www.roblox.com/games/555", "the button opens the place when the game is unknown");

var avatar = new PresenceImage("https://tr.rbxcdn.com/avatar", "Nova");
var home = RobloxPlugin.HomeCard(At("20:05:47"), avatar);
Check(home is { Name: "Roblox", Details: "Browsing games" } && home.StartedAt == At("20:05:47") && home.SmallImage == avatar, "home card");

var rpcLog = new RobloxLog("unused");
rpcLog.Read(Rpc("20:00:00.000", """{"command":"SetRichPresence","data":{"details":"ignored"}}"""));
Check(rpcLog.Rpc == RobloxRpc.None, "messages outside a game are ignored");
rpcLog.Read(Join("20:00:01.000", 100));
rpcLog.Read(Load("20:00:01.000", 100, 900));
Check(rpcLog.UserId == 1, "the user id also comes from a join");

rpcLog.Read(Rpc("20:00:02.000", """
{"command":"SetRichPresence","data":{"details":"Round 3","state":"Score 120","timeStart":1794686815,"largeImage":{"assetId":"rbxassetid://777","hoverText":"Map: Docks"}}}
""".Trim()));
Check(rpcLog.Rpc is { Details: "Round 3", State: "Score 120", StartedAt: 1794686815, LargeImage: { AssetId: 777, Text: "Map: Docks", Hidden: false }, SmallImage: null },
    "a game's message is read");
rpcLog.Read(Rpc("20:00:03.000", """{"command":"SetRichPresence","data":{"state":""}}"""));
Check(rpcLog.Rpc is { Details: "Round 3", State: "" }, "omitted fields stay and an empty one is cleared");
rpcLog.Read(Rpc("20:00:04.000", """{"command":"SetRichPresence","data":{"details":"<reset>","largeImage":{"reset":true},"smallImage":{"clear":true},"timeStart":0}}"""));
Check(rpcLog.Rpc is { Details: null, LargeImage: null, SmallImage: { Hidden: true }, StartedAt: 0 }, "reset, clear and zero");

var kept = rpcLog.Rpc;
rpcLog.Read(Rpc("20:00:05.000", "not json"));
rpcLog.Read(Rpc("20:00:05.000", """{"command":"SetLaunchData","data":"x"}"""));
rpcLog.Read(Rpc("20:00:05.000", """{"command":"SetRichPresence","data":{"details":5}}"""));
rpcLog.Read(Rpc("20:00:05.000", """{"command":"SetRichPresence","data":{"largeImage":{"assetId":"99999999999999999999"}}}"""));
Check(rpcLog.Rpc == kept, "broken and other messages change nothing");
rpcLog.Read(Join("20:10:00.000", 101));
Check(rpcLog.Rpc == RobloxRpc.None, "a new server starts without the game's details");

var custom = new RobloxRpc("Round 3", "", 1794686815, null, new RpcImage(777, "Map: Docks"), new RpcImage(Hidden: true));
var rpcCard = RobloxPlugin.GameCard(game, "DTI: Casual Server", lobby with { PlaceId = 101 }, custom, avatar,
    id => id == 777 ? "https://tr.rbxcdn.com/map" : null, false);
Check(rpcCard is { Name: "Roblox", Details: "Round 3", State: "", SmallImage: null } &&
      rpcCard.LargeImage == new PresenceImage("https://tr.rbxcdn.com/map", "Map: Docks") &&
      rpcCard.StartedAt == DateTimeOffset.FromUnixTimeSeconds(1794686815), "a game's details replace the card's");
var textOnly = RobloxPlugin.GameCard(game, null, lobby, new RobloxRpc(StartedAt: 0, LargeImage: new RpcImage(Text: "Lobby")), avatar, NoAssets, false);
Check(textOnly.LargeImage == new PresenceImage("https://tr.rbxcdn.com/icon", "Lobby") && textOnly.StartedAt is null && textOnly.SmallImage == avatar,
    "hover text alone keeps the icon, and a zero start hides the timer");
Check(RobloxPlugin.GameCard(game, null, lobby, new RobloxRpc(LargeImage: new RpcImage(999)), null, NoAssets, false).LargeImage?.Key == "https://tr.rbxcdn.com/icon",
    "an image that isn't looked up yet keeps the icon");
Check(RobloxPlugin.GameCard(game, null, lobby, new RobloxRpc(EndsAt: 99999999999), null, NoAssets, false).EndsAt is null, "an end far in the future is dropped");
Check(RobloxPlugin.GameCard(game, null, lobby, RobloxRpc.None, avatar, NoAssets, false).SmallImage == avatar, "the avatar is the small picture");

Check(RobloxApi.ParseDisplayName("""{"id":1,"name":"nova_dev","displayName":"Nova"}""") == "Nova", "display name parsed");

Console.WriteLine("All Roblox checks passed.");

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}
