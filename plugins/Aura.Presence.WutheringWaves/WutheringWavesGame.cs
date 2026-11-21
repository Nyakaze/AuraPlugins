using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Aura.Presence.WutheringWaves;

public sealed record UnionLevel(int Level, string Region)
{
    public static UnionLevel? Pick(string? levels, string? quests)
    {
        try
        {
            if (levels is null)
                return null;

            using var levelData = JsonDocument.Parse(levels);
            var rows = levelData.RootElement.GetProperty("Content").EnumerateArray()
                .Select(row => (Id: Id(row[0]), Data: row[1][0]))
                .ToList();
            var owners = new HashSet<string>();

            if (quests is not null)
            {
                using var questData = JsonDocument.Parse(quests);

                foreach (var row in questData.RootElement.GetProperty("Content").EnumerateArray())
                    owners.Add(Id(row[0]));
            }

            var matches = rows.Count == 1 ? rows : rows.Where(row => owners.Contains(row.Id)).ToList();

            if (matches is not [var match])
                return null;

            return match.Data.GetProperty("Level").TryGetInt32(out var level) &&
                   match.Data.GetProperty("Region").GetString() is { Length: > 0 } region
                ? new UnionLevel(level, region)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                              or KeyNotFoundException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string Id(JsonElement id) => id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
}

public sealed record WutheringWavesGame(int ProcessId, string? Root, DateTimeOffset? StartedAt)
{
    private const string ProcessName = "Client-Win64-Shipping";
    private const string LauncherExe = "Wuthering Waves.exe";
    private const int QueryLimitedInformation = 0x1000;

    public static WutheringWavesGame? Find()
    {
        WutheringWavesGame? found = null;

        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                var root = ImagePath(process.Id) is { } exe
                    ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", "..", ".."))
                    : null;

                if (found is null && (root is null || File.Exists(Path.Combine(root, LauncherExe))))
                    found = new WutheringWavesGame(process.Id, root, StartTime(process));
            }
        }

        return found;
    }

    public string? LocalStorage() =>
        Root is not null && new DirectoryInfo(Path.Combine(Root, "Client", "Saved", "LocalStorage")) is { Exists: true } folder
            ? folder.EnumerateFiles("LocalStorage*.db").MaxBy(file => file.LastWriteTimeUtc)?.FullName
            : null;

    public string? Version()
    {
        try
        {
            return Root is not null && new DirectoryInfo(Path.Combine(Root, "Client", "Saved", "Resources")) is { Exists: true } folder
                ? folder.EnumerateDirectories()
                    .Select(directory => global::System.Version.TryParse(directory.Name, out var version) ? version : null)
                    .Max()?.ToString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static UnionLevel? ReadUnionLevel(string database)
    {
        var writtenBefore = File.GetLastWriteTimeUtc(database);
        byte[] bytes;

        using (var stream = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        if (File.GetLastWriteTimeUtc(database) != writtenBefore)
            throw new IOException("The game wrote to its settings file while it was being read.");

        var values = Sqlite.Read(bytes, "select key, value from LocalStorage where key in ('SdkLevelData', 'UserFinishedQuests')");

        return UnionLevel.Pick(values.GetValueOrDefault("SdkLevelData"), values.GetValueOrDefault("UserFinishedQuests"));
    }

    private static DateTimeOffset? StartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? ImagePath(int processId)
    {
        using var handle = OpenProcess(QueryLimitedInformation, false, processId);

        if (handle.IsInvalid)
            return null;

        var buffer = new char[1024];
        var size = buffer.Length;

        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, char[] name, ref int size);
}

public static class Sqlite
{
    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;
    private const int OpenReadWriteCreate = 0x2 | 0x4;
    private const uint DeserializeReadOnly = 4;

    public static Dictionary<string, string> Read(byte[] database, string query)
    {
        var pinned = GCHandle.Alloc(database, GCHandleType.Pinned);
        var connection = IntPtr.Zero;

        try
        {
            Check(sqlite3_open_v2(":memory:", out connection, OpenReadWriteCreate, IntPtr.Zero), connection);
            Check(sqlite3_deserialize(connection, "main", pinned.AddrOfPinnedObject(), database.Length, database.Length, DeserializeReadOnly), connection);
            Check(sqlite3_prepare_v2(connection, query, -1, out var statement, IntPtr.Zero), connection);

            try
            {
                var values = new Dictionary<string, string>();
                int step;

                while ((step = sqlite3_step(statement)) == Row)
                    values[Text(statement, 0)] = Text(statement, 1);

                Check(step == Done ? Ok : step, connection);

                return values;
            }
            finally
            {
                sqlite3_finalize(statement);
            }
        }
        finally
        {
            sqlite3_close_v2(connection);
            pinned.Free();
        }
    }

    private static string Text(IntPtr statement, int column) =>
        Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column)) ?? "";

    private static void Check(int result, IntPtr connection)
    {
        if (result != Ok)
            throw new IOException($"SQLite error {result}: {(connection == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(sqlite3_errmsg(connection)))}");
    }

    [DllImport("winsqlite3")]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr connection, int flags, IntPtr vfs);

    [DllImport("winsqlite3")]
    private static extern int sqlite3_deserialize(IntPtr connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema,
        IntPtr data, long size, long bufferSize, uint flags);

    [DllImport("winsqlite3")]
    private static extern int sqlite3_prepare_v2(IntPtr connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length,
        out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3")]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3")]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3")]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3")]
    private static extern int sqlite3_close_v2(IntPtr connection);

    [DllImport("winsqlite3")]
    private static extern IntPtr sqlite3_errmsg(IntPtr connection);
}
