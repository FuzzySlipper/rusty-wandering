using System.Collections.Concurrent;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The world's natural caves, from the underground plan alone (#11220, Patch 2026-11-28: "a solid result to become the new
/// standard"): every system the plan sites, on the walked ground its boxes are buried under, on a continent and a regional world
/// alike. It answers the two questions the rest of the world asks of caves: which cave mouth (if any) an anchor cell of the
/// landmark lattice holds, so the mouths count as landmarks where cave mouths always have; and which boxes reach into a region,
/// so the chunks carve their tendrils and the walking session asks for their caves. Systems are built on demand, each once,
/// deterministic whatever is asked first, and safe to share across threads (chunk workers ask too).
/// </summary>
internal sealed class PlannedUnderground
{
    /// <summary>The plan's level every natural cave comes from (#11209 round 3, made the standard).</summary>
    internal const string Level = "fine";

    /// <summary>How far past a system's own reach its mouths' tendrils run outside its boxes, in metres.</summary>
    private const double TendrilMarginMetres = UndergroundPlan.LongestTendrilMetres;

    private readonly UndergroundPlanner planner;
    private readonly double systemReach;
    private readonly ConcurrentDictionary<string, Lazy<Placed?>> systems = new();

    /// <summary>A system and its boxes as placements, in box order.</summary>
    internal sealed record Placed(UndergroundSystem System, IReadOnlyList<CavePlacement> Boxes);

    /// <summary>A mouth as the landmark lattice sees it: its system, its index, and where it opens.</summary>
    internal readonly record struct Mouth(string SystemId, int Index, UndergroundMouth Opening);

    internal PlannedUnderground(WorldMap map, Func<double, double, double> walkedGround)
    {
        planner = new UndergroundPlanner(map, UndergroundPlan.Preset(Level), walkedGround);
        systemReach = UndergroundPlan.ReachMetres(planner.Coarseness) + TendrilMarginMetres;
    }

    /// <summary>The planner the systems come from (debug readouts and the authoring tools).</summary>
    internal UndergroundPlanner Planner => planner;

    /// <summary>A system and its placements by id, or null where the plan has none (or cut it back to nothing).</summary>
    internal Placed? System(string id) => planner.Site(id) is UndergroundSite site ? Build(site) : null;

    /// <summary>Every system whose boxes or tendrils could reach within a reach of a point.</summary>
    internal IEnumerable<Placed> SystemsNear(double x, double z, double reach)
    {
        foreach (UndergroundSite site in planner.SitesNear(x, z, reach + systemReach).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (Build(site) is Placed placed) yield return placed;
        }
    }

    /// <summary>The boxes whose box or tendril reaches into a region of columns.</summary>
    internal IReadOnlyList<CavePlacement> BoxesNear(long xMin, long xMax, long zMin, long zMax)
    {
        double half = Math.Max(xMax - xMin, zMax - zMin) * 0.5;
        List<CavePlacement> found = [];
        foreach (Placed placed in SystemsNear((xMin + xMax) * 0.5, (zMin + zMax) * 0.5, half))
        {
            foreach (CavePlacement box in placed.Boxes)
            {
                if (box.BoxTouches(xMin, xMax, zMin, zMax) || box.TendrilTouches(xMin, xMax, zMin, zMax)) found.Add(box);
            }
        }

        return found;
    }

    /// <summary>
    /// The mouths whose openings lie in an anchor cell of the landmark lattice, ordered by system then mouth: the first is the
    /// cell's own landmark (<see cref="PoiPlacement.SiteAt"/>), each further one a landmark of its own under an extra key
    /// (<see cref="ExtraCellX"/>), so every planned mouth is enumerated, found and exported (#11220 R11220-1).
    /// </summary>
    internal IReadOnlyList<Mouth> MouthsIn(long cellX, long cellZ)
    {
        double centreX = (cellX + 0.5) * PoiConstants.CellSize, centreZ = (cellZ + 0.5) * PoiConstants.CellSize;
        List<Mouth> found = [];
        foreach (Placed placed in SystemsNear(centreX, centreZ, PoiConstants.CellSize))
        {
            for (int i = 0; i < placed.System.Mouths.Count; i++)
            {
                UndergroundMouth m = placed.System.Mouths[i];
                if (GridMath.FloorDivide((long)Math.Floor(m.WorldX), PoiConstants.CellSize) == cellX
                    && GridMath.FloorDivide((long)Math.Floor(m.WorldZ), PoiConstants.CellSize) == cellZ)
                {
                    found.Add(new Mouth(placed.System.Id, i, m));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The mouth a cave-mouth site stands for: its cell's first, or the further one its extra key names; null for none.
    /// </summary>
    internal Mouth? MouthOf(PoiSite site)
    {
        (long cellX, int index) = CellOf(site.CellX);
        IReadOnlyList<Mouth> mouths = MouthsIn(cellX, site.CellZ);
        return index < mouths.Count ? mouths[index] : null;
    }

    /// <summary>
    /// The anchor key a further mouth in a cell goes by: its cell's x moved by a whole <see cref="ExtraKeyStride"/> per index,
    /// far past any cell a world holds, so its identity is stable and its cell recovered from it (<see cref="CellOf"/>).
    /// </summary>
    internal static long ExtraCellX(long cellX, int index) => cellX + (index * ExtraKeyStride);

    /// <summary>A site's anchor cell and its index among its cell's mouths (0 for the cell's own site).</summary>
    internal static (long CellX, int Index) CellOf(long keyX)
    {
        long index = GridMath.FloorDivide(keyX + (ExtraKeyStride / 2), ExtraKeyStride);
        return (keyX - (index * ExtraKeyStride), (int)index);
    }

    /// <summary>The span of anchor cells one mouth index takes in an extra key: far past a world's cells.</summary>
    private const long ExtraKeyStride = 1L << 40;

    /// <summary>The box a mouth opens into, as a placement, or null.</summary>
    internal CavePlacement? BoxOf(Mouth mouth) =>
        System(mouth.SystemId) is Placed placed ? placed.Boxes[mouth.Opening.BoxIndex] : null;

    /// <summary>
    /// Which way a mouth's landmark faces into the hill, as the landmark lattice's aspect (0 +x, 1 -x, 2 +z, 3 -z), or -1 for a
    /// mouth with no face on a slope (a sinkhole opens upward; a cliff mouth is the box's own face).
    /// </summary>
    internal static long AspectOf(UndergroundMouth mouth) =>
        mouth.Kind is UndergroundMouthKind.Sinkhole or UndergroundMouthKind.Cliff ? -1 : mouth.Face switch
        {
            UndergroundFace.PosX => 1,
            UndergroundFace.NegX => 0,
            UndergroundFace.PosZ => 3,
            UndergroundFace.NegZ => 2,
            _ => -1,
        };

    private Placed? Build(UndergroundSite site) =>
        systems.GetOrAdd(site.Id, _ => new Lazy<Placed?>(() =>
            planner.SystemAt(site) is UndergroundSystem system ? new Placed(system, PlannedCaves.Placements(system)) : null)).Value;
}
