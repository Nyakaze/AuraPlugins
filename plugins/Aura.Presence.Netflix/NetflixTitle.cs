using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aura.Presence.Netflix;

public sealed record NetflixTitle(long ShowId, string Title, bool IsMovie, int? Season, int? Episode, string? EpisodeTitle, int? Year,
    string? PosterUrl)
{
    public bool IsPlayable => IsMovie || Episode is not null;

    public static NetflixTitle? Parse(string? json)
    {
        if (json is null)
            return null;

        try
        {
            return Read(JsonNode.Parse(json)?["video"]);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static NetflixTitle? Read(JsonNode? video)
    {
        if (video is not JsonObject || (string?)video["title"] is not { Length: > 0 } title)
            return null;

        var id = (long?)video["id"] ?? 0;
        var poster = (string?)video["boxart"]?.AsArray().FirstOrDefault()?["url"];

        if ((string?)video["type"] == "movie")
            return new NetflixTitle(id, title, true, null, null, null, (int?)video["year"], poster);

        var seasons = video["seasons"]?.AsArray() ?? [];
        var year = (int?)seasons.FirstOrDefault()?["year"];
        var current = (long?)video["currentEpisode"];

        foreach (var season in seasons)
        foreach (var episode in season?["episodes"]?.AsArray() ?? [])
        {
            if (current is not null && ((long?)episode?["episodeId"] == current || (long?)episode?["id"] == current))
                return new NetflixTitle(id, title, false, (int?)season!["seq"], (int?)episode!["seq"], (string?)episode["title"], year, poster);
        }

        return new NetflixTitle(id, title, false, null, null, null, year, poster);
    }
}
