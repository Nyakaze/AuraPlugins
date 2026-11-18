using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aura.Presence.Roblox;

public sealed record RobloxPlace(long PlaceId, long? UniverseId, DateTimeOffset JoinedAt, DateTimeOffset PlayingSince);

public sealed partial class RobloxLog(string path)
{
    private const string JoinMarker = "! Joining game '";
    private const string LoadTimeMarker = "Report game_join_loadtime:";
    private const string LeaveMarker = "[FLog::SingleSurfaceApp] leaveUGCGameInternal";
    private const string RpcMarker = "[FLog::Output] [BloxstrapRPC] ";

    private static readonly TimeSpan RejoinWindow = TimeSpan.FromMinutes(1);

    private long _position;
    private byte[] _rest = [];
    private long? _lastUniverse;
    private DateTimeOffset _lastPlayingSince;

    public string Path { get; } = path;

    public DateTimeOffset? OpenedAt { get; private set; }

    public RobloxPlace? Place { get; private set; }

    public DateTimeOffset? LeftAt { get; private set; }

    public long? UserId { get; private set; }

    public RobloxRpc Rpc { get; private set; } = RobloxRpc.None;

    public static string? Newest(string folder, DateTime createdSince) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles("*_Player_*_last.log")
                .Where(file => !file.Name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) &&
                               file.CreationTimeUtc >= createdSince)
                .MaxBy(file => file.CreationTimeUtc)?.FullName
            : null;

    public void Update()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length <= _position)
            return;

        stream.Position = _position;
        var fresh = new byte[stream.Length - _position];
        stream.ReadExactly(fresh);
        _position += fresh.Length;

        var bytes = _rest.Length == 0 ? fresh : [.. _rest, .. fresh];
        var end = Array.LastIndexOf(bytes, (byte)'\n');

        if (end < 0)
        {
            _rest = bytes;
            return;
        }

        _rest = bytes[(end + 1)..];

        foreach (var line in Encoding.UTF8.GetString(bytes, 0, end).Split('\n'))
            Read(line.TrimEnd('\r'));
    }

    public void Read(string line)
    {
        if (Timestamp(line) is not { } time)
            return;

        OpenedAt ??= time;

        if (UserId is null && User().Match(line) is { Success: true } user)
            UserId = Number(user.Groups["user"]);

        if (line.Contains(JoinMarker, StringComparison.Ordinal) && Joining().Match(line) is { Success: true } join)
        {
            Place = new RobloxPlace(Number(join.Groups["place"]), null, time, time);
            Rpc = RobloxRpc.None;
        }
        else if (Place is { UniverseId: null } place && line.Contains(LoadTimeMarker, StringComparison.Ordinal) &&
                 LoadTime().Match(line) is { Success: true } load && Number(load.Groups["place"]) == place.PlaceId)
        {
            var universe = Number(load.Groups["universe"]);
            var continues = universe == _lastUniverse && (LeftAt is null || place.JoinedAt - LeftAt <= RejoinWindow);
            Place = place with { UniverseId = universe, PlayingSince = continues ? _lastPlayingSince : place.JoinedAt };
            _lastUniverse = universe;
            _lastPlayingSince = Place.PlayingSince;
            LeftAt = null;
        }
        else if (line.Contains(LeaveMarker, StringComparison.Ordinal))
        {
            Place = null;
            LeftAt = time;
            Rpc = RobloxRpc.None;
        }
        else if (Place is not null && line.IndexOf(RpcMarker, StringComparison.Ordinal) is var rpc and >= 0)
        {
            Rpc = Rpc.Apply(line[(rpc + RpcMarker.Length)..]);
        }
    }

    private static DateTimeOffset? Timestamp(string line)
    {
        var comma = line.IndexOf(',');

        return comma > 0 && DateTimeOffset.TryParse(line.AsSpan(0, comma), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;
    }

    private static long Number(Group group) => long.Parse(group.Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"! Joining game '[^']*' place (?<place>\d+) ")]
    private static partial Regex Joining();

    [GeneratedRegex(@"placeid:(?<place>\d+),.*?universeid:(?<universe>\d+)")]
    private static partial Regex LoadTime();

    [GeneratedRegex(@"(?:rbxuid=|game_join_loadtime:.*?userid:)(?<user>\d+)")]
    private static partial Regex User();
}
