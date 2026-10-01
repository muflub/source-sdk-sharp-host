using SourceSharp.Host.Contracts;
using SourceSharp.MapTools.Rooms;
using LevelPlan = SourceSharp.Host.Contracts.LevelPlan;

namespace Descent.MapForge;

/// <summary>The transition keys of a level of a run (the tools' <c>up_map</c> / <c>down_map</c>, or none).</summary>
/// <param name="UpMap">The map above (the town for depth 1); null writes <c>up: none</c>.</param>
/// <param name="DownMap">The map below; null writes <c>down: none</c>.</param>
public sealed record LevelKeys(string? UpMap, string? DownMap);

/// <summary>
/// MapForge's level-file emitter (plan §9.1 Layout): the rules module's <see cref="LevelPlan"/>
/// as the <c>level.yaml</c> the tools' <see cref="LevelYaml"/> reads. Pure: the same plan
/// writes the same bytes.
/// </summary>
/// <remarks>
/// Coordinates are the tools': row 0 is the south row and column 0 the west column; a
/// placement's rotation is in degrees counter-clockwise seen from above (0, 90, 180, 270).
/// The level names its library as <c>&lt;library&gt;.vmf</c>, so the tools key it (and find
/// its namespace in a pack) by the library key.
/// </remarks>
public static class LevelFileWriter
{
    public static string LibraryFile(string library) => library + ".vmf";

    /// <summary>The plan as the tools' grid; throws <see cref="ArgumentException"/> for a plan the format cannot hold.</summary>
    public static LevelGrid ToGrid(LevelPlan plan, string library, string name, LevelKeys? keys)
    {
        if (plan.Rows < 1 || plan.Columns < 1 || (long)plan.Rows * plan.Columns > LevelYaml.MaxCells)
            throw new ArgumentException($"a {plan.Rows}x{plan.Columns} plan is not a level grid (1 to {LevelYaml.MaxCells} cells)");
        var cells = new LevelCell?[plan.Rows * plan.Columns];
        foreach (var p in plan.Placements)
        {
            if (p.Row < 0 || p.Row >= plan.Rows || p.Column < 0 || p.Column >= plan.Columns)
                throw new ArgumentException($"placement of '{p.Room}' at row {p.Row}, column {p.Column} is outside the {plan.Rows}x{plan.Columns} grid");
            if (p.Rotation % 90 != 0)
                throw new ArgumentException($"placement of '{p.Room}' turns {p.Rotation} degrees; a level turns rooms by quarter turns");
            ref var cell = ref cells[p.Row * plan.Columns + p.Column];
            if (cell is not null)
                throw new ArgumentException($"row {p.Row}, column {p.Column} holds '{cell.Room}' and '{p.Room}'");
            cell = new LevelCell(p.Room, ((p.Rotation / 90) % 4 + 4) % 4);
        }
        var grid = new LevelGrid(name, LibraryFile(library), plan.Rows, plan.Columns, cells);
        return keys is null ? grid : grid.WithTransitions(Transitions(keys));
    }

    public static LevelTransitions Transitions(LevelKeys keys) =>
        new() { UpMap = keys.UpMap, DownMap = keys.DownMap, NoUp = keys.UpMap is null, NoDown = keys.DownMap is null };

    /// <summary>The <c>level.yaml</c> text of a plan.</summary>
    public static string Write(LevelPlan plan, string library, string name, LevelKeys? keys, IEnumerable<string>? comments = null) =>
        LevelYaml.Write(ToGrid(plan, library, name, keys), comments);
}
