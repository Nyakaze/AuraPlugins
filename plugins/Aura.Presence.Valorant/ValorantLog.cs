using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aura.Presence.Valorant;

public sealed record LogMatch(string Id, DateTimeOffset FirstSeen, DateTimeOffset? InGameAt, string? Agent);

public sealed partial class ValorantLog(string path)
{
    public static readonly string GamePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");

    private readonly Dictionary<string, LogMatch> _matches = [];
    private long _position;
    private byte[] _rest = [];
    private string? _newest;

    public LogMatch? Latest => _newest is null ? null : _matches[_newest];

    public void Update()
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < _position)
        {
            _position = 0;
            _rest = [];
            _matches.Clear();
            _newest = null;
        }

        if (stream.Length == _position)
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
            Read(line);
    }

    public void Read(string line)
    {
        if (!line.Contains("/v1/matches/", StringComparison.Ordinal) || MatchUrl().Match(line) is not { Success: true } url ||
            Time(line) is not { } at)
            return;

        var id = url.Groups["id"].Value;

        if (!_matches.TryGetValue(id, out var match))
        {
            match = new LogMatch(id, at, null, null);
            _newest = id;
        }

        if (url.Groups["stage"].Value == "core-game")
            match = match with { InGameAt = match.InGameAt ?? at };

        if (url.Groups["agent"].Success)
            match = match with { Agent = url.Groups["agent"].Value };

        _matches[id] = match;
    }

    private static DateTimeOffset? Time(string line) =>
        line.Length > 24 && line[0] == '[' &&
        DateTimeOffset.TryParseExact(line.AsSpan(1, 23), "yyyy.MM.dd-HH.mm.ss:fff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    [GeneratedRegex(@"(?<stage>pregame|core-game)/v1/matches/(?<id>[0-9a-f-]{36})(?:/(?:select|lock)/(?<agent>[0-9a-f-]{36}))?")]
    private static partial Regex MatchUrl();
}
