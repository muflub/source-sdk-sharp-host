using System.Text.Json;
using System.Text.Json.Serialization;
using SourceSharp.Host.Contracts;

namespace Descent.MapForge;

/// <summary>
/// <c>library.json</c> (plan §9.1, TF2 plan 6a): what the game knows about a library's rooms
/// that the map tools do not. One entry per room of the VMF, by the room's name.
/// </summary>
/// <example>
/// { "mod": "descent", "library": "crypt",
///   "rooms": { "entry": { "tags": ["start"], "weight": 1, "depths": [1, 15], "markers": ["spawn"] } } }
/// </example>
public sealed class LibraryManifest
{
    /// <summary>The game's room tags (§9.1). Anything else is refused by lint.</summary>
    public static readonly IReadOnlyList<string> KnownTags = ["start", "stairs", "boss_arena", "treasure", "corridor", "junction", "shrine"];

    /// <summary>Depths that end in a boss (TF2 plan D13): each needs a <c>boss_arena</c> room.</summary>
    public static readonly IReadOnlyList<int> BossDepths = [5, 10, 15];

    [JsonPropertyName("mod")] public string Mod { get; set; } = "";
    [JsonPropertyName("library")] public string Library { get; set; } = "";
    [JsonPropertyName("rooms")] public Dictionary<string, RoomEntry> Rooms { get; set; } = [];

    public sealed class RoomEntry
    {
        [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
        [JsonPropertyName("weight")] public int Weight { get; set; } = 1;
        /// <summary>[min, max], inclusive.</summary>
        [JsonPropertyName("depths")] public int[] Depths { get; set; } = [1, 99];
        /// <summary>R19: the room needs a jump to cross, or has a low ceiling.</summary>
        [JsonPropertyName("jump")] public bool Jump { get; set; }
        [JsonPropertyName("low")] public bool Low { get; set; }
        /// <summary>Spawn and furniture markers the game reads: an entity's classname, targetname or <c>info_poi</c> type in the room.</summary>
        [JsonPropertyName("markers")] public List<string> Markers { get; set; } = [];

        [JsonIgnore] public int MinDepth => Depths.Length > 0 ? Depths[0] : 1;
        [JsonIgnore] public int MaxDepth => Depths.Length > 1 ? Depths[1] : MinDepth;
    }

    static readonly JsonSerializerOptions Options = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Parses the text; throws <see cref="InvalidDataException"/> with the parser's position.</summary>
    public static LibraryManifest Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LibraryManifest>(json, Options) ?? throw new InvalidDataException("library.json is empty");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"library.json: {e.Message}", e);
        }
    }

    /// <summary>The rooms as the rules module's layout wants them (<see cref="LayoutKey.Rooms"/>), in name order.</summary>
    public IReadOnlyList<RoomInfo> RoomInfos() =>
        Rooms.OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new RoomInfo(r.Key, [.. r.Value.Tags], r.Value.Weight, r.Value.MinDepth, r.Value.MaxDepth))
            .ToList();

    /// <summary>The rooms tagged <paramref name="tag"/>.</summary>
    public IEnumerable<string> Tagged(string tag) => Rooms.Where(r => r.Value.Tags.Contains(tag)).Select(r => r.Key);
}
