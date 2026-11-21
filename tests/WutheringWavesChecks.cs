#:property TargetFramework=net10.0
#:project ../plugins/Aura.Presence.WutheringWaves/Aura.Presence.WutheringWaves.csproj
#:project ../../AuraPresence/src/Aura.Presence.Sdk/Aura.Presence.Sdk.csproj

using System.Runtime.InteropServices;
using Aura.Presence.Sdk;
using Aura.Presence.WutheringWaves;

const string Levels = """{"___MetaType___":"___Map___","Content":[["111111111",[{"Region":"Europe","Level":74}]],["222222222",[{"Region":"America","Level":4}]]]}""";
const string Quests = """{"___MetaType___":"___Map___","Content":[["111111111",{"___MetaType___":"___Map___","Content":[[1,1]]}]]}""";

Check(UnionLevel.Pick(Levels, Quests) == new UnionLevel(74, "Europe"), "the account that owns the quest list is the one playing");
Check(UnionLevel.Pick(Levels, null) is null, "with several accounts and no quest list, nothing is guessed");
Check(UnionLevel.Pick("""{"Content":[[333333333,[{"Region":"Asia","Level":12}]]]}""", null) == new UnionLevel(12, "Asia"),
    "a single account needs no quest list, and number ids work");
Check(UnionLevel.Pick("""{"Content":[["1",[{"Region":"Asia"}]]]}""", null) is null, "a row without a level is ignored");
Check(UnionLevel.Pick("not json", Quests) is null && UnionLevel.Pick("""{"Content":5}""", null) is null, "a changed format reads as nothing");

var start = DateTimeOffset.Parse("2026-11-20T20:00:00Z");
var all = new CardOptions(true, true, true, true);
var card = WutheringWavesPlugin.Card(start, new UnionLevel(74, "Europe"), "3.7.0", all);
Check(card is { Kind: ActivityKind.Playing, Name: "Wuthering Waves", Details: "Union Level 74", State: "Region: Europe" } && card.StartedAt == start &&
      card.LargeImage == new PresenceImage(WutheringWavesPlugin.IconUrl, "Wuthering Waves 3.7.0"), "the full card");
Check(WutheringWavesPlugin.IconUrl.Length <= 256, "the icon URL fits Aura's limit");

var plain = WutheringWavesPlugin.Card(start, null, null, all);
Check(plain is { Details: "Exploring SOL-III", State: null } && plain.LargeImage?.Text == "Wuthering Waves", "without data the card is the plain one");

var regionOnly = WutheringWavesPlugin.Card(start, new UnionLevel(74, "Europe"), "3.7.0", all with { Level = false, Version = false, Timer = false });
Check(regionOnly is { Details: "Exploring SOL-III", State: "Region: Europe", StartedAt: null } && regionOnly.LargeImage?.Text == "Wuthering Waves",
    "each part can be turned off");
Check(WutheringWavesPlugin.Card(start, new UnionLevel(74, "Europe"), null, all with { Region = false }) is { Details: "Union Level 74", State: null },
    "the level without the region");

var folder = Directory.CreateTempSubdirectory("aura-wuwa-checks");
var path = Path.Combine(folder.FullName, "LocalStorage.db");
Check(sqlite3_open_v2(path, out var db, 6, IntPtr.Zero) == 0, "a test settings file opens");
Exec(db, "create table LocalStorage(key text primary key not null, value text not null)");
Exec(db, $"insert into LocalStorage values ('SdkLevelData', '{Levels}'), ('UserFinishedQuests', '{Quests}'), ('SdkLastTimeLoginData', 'x')");
sqlite3_close_v2(db);
Check(WutheringWavesGame.ReadUnionLevel(path) == new UnionLevel(74, "Europe"), "the Union Level is read from the settings file");
foreach (var name in new[] { "3.7.0", "3.8.0", "3.10.0", "Video" })
    Directory.CreateDirectory(Path.Combine(folder.FullName, "Client", "Saved", "Resources", name));
Check(new WutheringWavesGame(0, folder.FullName, null).Version() == "3.10.0", "the newest resource folder is the game version");
Check(new WutheringWavesGame(0, null, null).Version() is null, "no folder, no version");
folder.Delete(true);

Console.WriteLine("All Wuthering Waves checks passed.");

static void Exec(IntPtr db, string sql) => Check(sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0, sql);

static void Check(bool condition, string what)
{
    if (condition)
        return;

    Console.Error.WriteLine($"FAILED: {what}");
    Environment.Exit(1);
}

[DllImport("winsqlite3")]
static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

[DllImport("winsqlite3")]
static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr argument, IntPtr error);

[DllImport("winsqlite3")]
static extern int sqlite3_close_v2(IntPtr db);
