using System.Globalization;
using System.Text.Json;

namespace Aura.Presence.Roblox;

public sealed record RpcImage(long? AssetId = null, string? Text = null, bool Hidden = false);

public sealed record RobloxRpc(
    string? Details = null,
    string? State = null,
    long? StartedAt = null,
    long? EndsAt = null,
    RpcImage? LargeImage = null,
    RpcImage? SmallImage = null)
{
    private const string Reset = "<reset>";

    public static readonly RobloxRpc None = new();

    public RobloxRpc Apply(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            if (root.GetProperty("command").GetString() != "SetRichPresence")
                return this;

            var data = root.GetProperty("data");

            return new RobloxRpc(
                Text(data, "details", Details),
                Text(data, "state", State),
                Time(data, "timeStart", StartedAt),
                Time(data, "timeEnd", EndsAt),
                Image(data, "largeImage", LargeImage),
                Image(data, "smallImage", SmallImage));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
                                              or FormatException or OverflowException)
        {
            return this;
        }
    }

    private static string? Text(JsonElement data, string name, string? current) =>
        Value(data, name) is not { } value ? current : value.GetString() is Reset ? null : value.GetString();

    private static long? Time(JsonElement data, string name, long? current) =>
        Value(data, name) is { } value ? (long)value.GetDouble() : current;

    private static RpcImage? Image(JsonElement data, string name, RpcImage? current)
    {
        if (Value(data, name) is not { ValueKind: JsonValueKind.Object } image)
            return current;

        if (Flag(image, "reset"))
            return null;

        var next = current ?? new RpcImage();

        if (Flag(image, "clear"))
            next = next with { Hidden = true };

        if (Value(image, "assetId") is { } asset)
        {
            var id = asset.ValueKind is JsonValueKind.Number
                ? asset.GetInt64()
                : long.Parse(asset.GetString()!.Replace("rbxassetid://", "", StringComparison.OrdinalIgnoreCase).Trim(),
                    CultureInfo.InvariantCulture);
            next = id == 0 ? next with { Hidden = true } : next with { AssetId = id, Hidden = false };
        }

        if (Value(image, "hoverText") is { } text)
            next = next with { Text = text.GetString() is Reset ? null : text.GetString() };

        return next;
    }

    private static bool Flag(JsonElement image, string name) => Value(image, name)?.GetBoolean() == true;

    private static JsonElement? Value(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null ? value : null;
}
