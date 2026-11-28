using System.Globalization;
using System.Numerics;
using System.Text;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// Location diagnostics for traversal (#11226; Patch, 2026-11-28: "if a walk keeps hitting a problem area ... teleport back
/// into that position"): resume a walk at a spot (the boxes and chunks there made resident first), and one report on why a
/// spot fails - the offline walker's expectation there against what the live controller and the collision answer - with a
/// local dump of the spot for the CMS's 3D view. Reads only: nothing here changes a rule.
/// </summary>
internal sealed partial class DungeonModule
{
    /// <summary>The walker's way laid by craft.cave.plan way (<see cref="GravityWay"/>): its cells on a grid at this world corner.</summary>
    private sealed record WalkerWay(long X0, long Y0, long Z0, CaveWalker.Way Way, PlannedHeld Held);

    private WalkerWay? walkerWay;

    /// <summary>How far about a point the report and the dump read, in cells (a 17 m cube).</summary>
    private const int SpotRadiusCells = 8;

    /// <summary>How many walker cells either side of the nearest one the report lists, and how far on it looks for a climb.</summary>
    private const int SpotMovesBefore = 3, SpotMovesAfter = 8, SpotClimbAhead = 12;

    /// <summary>The collision floor is sought this far over a cell's foot, and this far under it.</summary>
    private const float FloorProbeAbove = 1.5f, FloorProbeBelow = 3f;

    /// <summary>A collision floor further than this from its voxel floor is called out.</summary>
    private const float FloorOffsetFlag = 0.35f;

    /// <summary>The face's looks: every this many metres up from the feet, to this far over the ledge.</summary>
    private const float FaceLookSpacing = 0.5f, FaceLookOver = 1f, FaceLookReach = 2f;

    /// <summary>How far off the expected stand, and how far off its heading, the player may be before steering is suspected.</summary>
    private const float SteerOffMetres = 0.75f, SteerOffDegrees = 30f;

    /// <summary>A contact deeper than this into the capsule counts as overlapping.</summary>
    private const double OverlapDepth = 0.01d;

    /// <summary>The dump's ray lattice spacing, and the most hits kept along one line.</summary>
    private const float DumpRaySpacing = 0.5f;
    private const int DumpHitsPerLine = 12;

    /// <summary>How far a ray steps on past a hit, or out of a start inside a collider.</summary>
    private const float RayNudge = 0.02f, RayStartSolidStep = 0.25f;

    private static SpatialQueryFilter AllCollision => new(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll);

    // --- Resume at a spot ---------------------------------------------------------------------------------------------

    /// <summary>A resume asked for: where the feet go, the heading faced, and whether the spot is ready.</summary>
    private sealed record ResumeTarget(Vector3 Feet, float HeadingDegrees, string Asked)
    {
        internal bool Placed { get; set; }
        internal string Waiting { get; set; } = "not checked";
    }

    private ResumeTarget? resume;

    /// <summary>
    /// Assisted, for traversal diagnostics (#11226): along:&lt;m&gt; resumes on the laid way that far along (a planned system's,
    /// after craft.cave.plan way; or a landmark cave's), at:x,y,z,heading at a feet point facing a heading (degrees, 0 faces -z);
    /// status reads the last. Each update until the spot is ready the player is held there: the box holding it admitted and
    /// dressed, the chunks about it resident; then placed again and faced. The walk goes on with craft.cave.steer in.
    /// </summary>
    internal string Resume(string spec)
    {
        if (spec == "status")
        {
            return resume is null ? "no resume asked for" : ResumeReadout();
        }

        if (SpotOf(spec) is not (Vector3 feet, float heading))
        {
            return "craft.cave.resume along:<metres> | at:<x>,<y>,<z>,<heading> | status (along: after craft.cave.plan way or craft.cave.goto)";
        }

        steer = 0;
        resume = new ResumeTarget(feet, heading, spec);
        AskAt(feet);
        HoldResume();
        return ResumeReadout();
    }

    private string ResumeReadout() => string.Create(CultureInfo.InvariantCulture,
        $"resume {resume!.Asked}: feet {resume.Feet.X:F2},{resume.Feet.Y:F2},{resume.Feet.Z:F2} heading {resume.HeadingDegrees:F0}; ready={resume.Placed} {(resume.Placed ? "" : "waiting: " + resume.Waiting)}; {SteerReadout()}");

    /// <summary>
    /// Assisted (#11226): stands the player in any box of a held planned system, on the floor where its first route starts,
    /// through the resume's hold (the box asked for, admitted and dressed first); craft.cave.resume status follows it.
    /// </summary>
    private string PlanGoToBox(string id, int index)
    {
        if (!plannedHeld.TryGetValue(id, out PlannedHeld? held) || held.System is null)
        {
            return $"{id} is not held yet: craft.cave.plan goto:<mouth> first";
        }

        if (index < 0 || index >= held.Boxes.Count)
        {
            return $"{id} has boxes 0..{held.Boxes.Count - 1}";
        }

        CavePlacement box = held.Boxes[index];
        if (!underCaves.TryGetValue(box.Key, out UnderCave? cave))
        {
            Ask(box);
            return $"box {index} asked for; goto:box:{index} again once it is ready. {PlannedReadout()}";
        }

        if (cave.Ready is not UndergroundCave ready)
        {
            return $"box {index} is generating; goto:box:{index} again once it is ready";
        }

        IReadOnlyList<Vector3> path = ready.Layout.PortRoutes.Values.Select(r => r.Path).FirstOrDefault(p => p.Count > 1) ?? ready.Layout.MainRoute;
        if (path.Count < 2)
        {
            return $"box {index} has no route to stand on";
        }

        Vector3 feet = box.ToWorld(path[0]), next = box.ToWorld(path[1]);
        walkCave = box.Key;
        steer = 0;
        resume = new ResumeTarget(feet, (float)(Math.Atan2(next.X - feet.X, -(next.Z - feet.Z)) * 180d / Math.PI), $"box:{index}");
        HoldResume();
        return ResumeReadout();
    }

    /// <summary>Asks for the box holding a point (it is admitted when ready, as any asked-for cave is).</summary>
    private void AskAt(Vector3 world)
    {
        foreach (CavePlacement box in BoxesNear(world))
        {
            if (box.InBox((long)MathF.Floor(world.X), (long)MathF.Floor(world.Y), (long)MathF.Floor(world.Z), out _, out _, out _) && !underCaves.ContainsKey(box.Key))
            {
                Ask(box);
            }
        }
    }

    /// <summary>The planned boxes held and the landmark caves near a column.</summary>
    private IEnumerable<CavePlacement> BoxesNear(Vector3 world)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        return plannedHeld.Values.SelectMany(h => h.Boxes)
            .Concat(terrain.Recipe.Caves.NearChunkColumn(GridMath.FloorDivide((long)MathF.Floor(world.X), edge), GridMath.FloorDivide((long)MathF.Floor(world.Z), edge)))
            .DistinctBy(b => b.Key);
    }

    /// <summary>Holds the player at a resume's spot until it is ready (each update), then places and faces them there.</summary>
    private void HoldResume()
    {
        if (resume is not ResumeTarget target || target.Placed)
        {
            return;
        }

        string? waiting = SpotWaiting(target.Feet);
        target.Waiting = waiting ?? "";
        PlaceFeet(target.Feet);
        if (waiting is not null)
        {
            return;
        }

        if (PlaceFeet(target.Feet) is null)
        {
            target.Waiting = "no standing room at the spot";
            return;
        }

        float r = target.HeadingDegrees * MathF.PI / 180f;
        Face(new Vector3(MathF.Sin(r), 0f, -MathF.Cos(r)), 0d);
        target.Placed = true;
    }

    /// <summary>What the spot still waits for (null: ready): its box admitted and dressed, the chunks about it resident.</summary>
    private string? SpotWaiting(Vector3 feet)
    {
        long x = (long)MathF.Floor(feet.X), y = (long)MathF.Floor(feet.Y), z = (long)MathF.Floor(feet.Z);
        CavePlacement? box = BoxesNear(feet).FirstOrDefault(b => b.InBox(x, y, z, out _, out _, out _));
        if (box is not null)
        {
            if (!underCaves.TryGetValue(box.Key, out UnderCave? under) || under.Ready is null)
            {
                return $"box {CaveName(box)} generating";
            }

            if (!terrain.Caves.IsAdmitted(box))
            {
                return $"box {CaveName(box)} admitting";
            }

            if (!dressings.TryGetValue(box.Key, out DressedCave? dressed) || !dressed.Dressing.Built)
            {
                return $"box {CaveName(box)} dressing (its kit's collision)";
            }
        }

        int missing = 0;
        for (long dx = -1; dx <= 1; dx++)
        for (long dy = -1; dy <= 1; dy++)
        for (long dz = -1; dz <= 1; dz++)
        {
            long edge = TerrainConstants.ChunkEdgeLength;
            if (!terrain.IsResident(new VoxelAddress(x + (dx * edge), y + (dy * edge), z + (dz * edge)))) missing++;
        }

        return missing == 0 ? null : $"{missing} of the 27 chunks about it not resident";
    }

    /// <summary>Stands the player with their feet at a point, or a little over or under it (the way runs a body's height over the floor).</summary>
    private PlayerRuntimeComponent? PlaceFeet(Vector3 feet)
    {
        float centre = PlayerConstants.StandingHeight / 2f;
        foreach (float dy in (ReadOnlySpan<float>)[0.05f, 0.3f, 0.6f, -0.3f, 1f])
        {
            if (player.TeleportExact(feet.X, feet.Y + centre + dy, feet.Z) is PlayerRuntimeComponent placed) return placed;
        }

        return null;
    }

    /// <summary>A spot from a command's words: here, along:&lt;m&gt; on the laid way, or x,y,z,heading (feet, degrees).</summary>
    private (Vector3 Feet, float Heading)? SpotOf(string spec)
    {
        if (spec == "here")
        {
            return (player.WorldFeetPosition, (float)(player.ClimbState.YawRadians * 180d / Math.PI));
        }

        if (spec.StartsWith("along:", StringComparison.Ordinal))
        {
            if (!float.TryParse(spec["along:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out float along)) return null;
            if (walkCave is (long, long) key && underCaves.TryGetValue(key, out UnderCave? cave)) EnsureWay(cave);
            if (walkWay is null) return null;
            (Vector3 at, Vector3 heading) = Along(walkWay, Math.Clamp(along, 0f, Length(walkWay)));
            return (at - (Vector3.UnitY * WalkAboveFloor), (float)(Math.Atan2(heading.X, -heading.Z) * 180d / Math.PI));
        }

        string body = spec.StartsWith("at:", StringComparison.Ordinal) ? spec[3..] : spec;
        float[] v = [.. body.Split(',').Select(t => float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) ? n : float.NaN)];
        return v.Length == 4 && !v.Any(float.IsNaN) ? (new Vector3(v[0], v[1], v[2]), v[3]) : null;
    }

    // --- The report ---------------------------------------------------------------------------------------------------

    /// <summary>The cells about a spot: the walker's grid on them as the walker samples them, and the voxels as the game holds them.</summary>
    private sealed class SpotCells
    {
        internal required long X0 { get; init; }
        internal required long Y0 { get; init; }
        internal required long Z0 { get; init; }
        internal required int Size { get; init; }
        internal required float[] Openness { get; init; }
        internal required ushort[] Terrain { get; init; }
        internal required ushort[] Engine { get; init; }
        internal required CaveWalker Walker { get; init; }
        internal required bool Sampled { get; init; }

        internal int Index(int i, int j, int k) => (((k * Size) + j) * Size) + i;

        internal (int, int, int) Cell(long x, long y, long z) => ((int)(x - X0), (int)(y - Y0), (int)(z - Z0));
    }

    /// <summary>Reads the cells about a spot (the walker's measure from the ready boxes and tendrils, the terrain's and the Engine's voxels).</summary>
    private SpotCells ReadCells(Vector3 feet)
    {
        int size = (SpotRadiusCells * 2) + 1;
        long x0 = (long)MathF.Floor(feet.X) - SpotRadiusCells, y0 = (long)MathF.Floor(feet.Y) - SpotRadiusCells, z0 = (long)MathF.Floor(feet.Z) - SpotRadiusCells;
        float[] openness = new float[size * size * size];
        ushort[] terrainCells = new ushort[openness.Length], engineCells = new ushort[openness.Length];
        bool[] solid = new bool[openness.Length], rock = new bool[openness.Length];
        PlannedHeld? held = plannedHeld.Values.FirstOrDefault(h => h.Boxes.Any(b => b.InBox((long)MathF.Floor(feet.X), (long)MathF.Floor(feet.Y), (long)MathF.Floor(feet.Z), out _, out _, out _)));
        (CavePlacement Box, CaveInterior Interior)[] boxes = held is null ? [] : [.. held.Boxes
            .Select(b => underCaves.TryGetValue(b.Key, out UnderCave? c) && c.Ready is UndergroundCave r ? (b, r.Interior) : (b, null!))
            .Where(p => p.Interior is not null)];
        for (int k = 0; k < size; k++)
        for (int j = 0; j < size; j++)
        for (int i = 0; i < size; i++)
        {
            int at = (((k * size) + j) * size) + i;
            long x = x0 + i, y = y0 + j, z = z0 + k;
            terrainCells[at] = terrain.MaterialAt(new VoxelAddress(x, y, z));
            engineCells[at] = (ushort)player.ClimbBlockAt(x, y, z);
            // Where no planned system holds the spot, the walker's measure is the terrain's own: open or rock.
            openness[at] = held is not null ? WalkerOpenness(held, boxes, x, y, z) : Collides(terrainCells[at]) ? -1f : 1f;
            solid[at] = openness[at] < CaveWalker.OpenAtLeast;
            rock[at] = openness[at] <= 0f;
        }

        return new SpotCells
        {
            X0 = x0, Y0 = y0, Z0 = z0, Size = size, Openness = openness, Terrain = terrainCells, Engine = engineCells,
            Walker = new CaveWalker(size, size, size, solid, rock: rock), Sampled = held is not null,
        };
    }

    private static bool Collides(ushort material) => BlockRegistry.TryGetBySlot(material, out BlockDefinition block) && block.Collidable;

    /// <summary>
    /// Assisted, for traversal diagnostics (#11226): why a spot fails - here, along:&lt;m&gt; on the laid way, or x,y,z,heading
    /// (feet, degrees) - the walker's expectation there (its move, standing cell, pitch, ledge) against the live controller's
    /// answer (the climb rule from the expected stand and from the player, climbprobe along the heading and its neighbours,
    /// a standing capsule, the collision floor and face), and where they disagree.
    /// </summary>
    internal string Why(string spec)
    {
        if (SpotOf(spec) is not (Vector3 feet, float heading))
        {
            return "craft.cave.why here | along:<metres> | <x>,<y>,<z>,<heading>";
        }

        return WhyReport(feet, heading).Text;
    }

    /// <summary>The climb the walker expects about a spot: its foot and ledge (world cells), its axis and its pitch.</summary>
    private sealed record ExpectedClimb((long X, long Y, long Z) Foot, (long X, long Y, long Z) Ledge, int Dx, int Dz, int Pitch, int Index);

    private sealed record SpotReport(string Text, SpotCells Cells, ExpectedClimb? Climb, int? Nearest);

    private SpotReport WhyReport(Vector3 feet, float heading)
    {
        StringBuilder text = new();
        List<string> disagree = [];
        string? verdict = null;
        void Line(string line) => text.Append(line).Append('\n');
        long fx = (long)MathF.Floor(feet.X), fy = (long)MathF.Floor(feet.Y), fz = (long)MathF.Floor(feet.Z);
        Vector3 playerFeet = player.WorldFeetPosition;
        (bool held, ClimbRail? lastRail, double yaw) = player.ClimbState;
        float playerHeading = (float)(yaw * 180d / Math.PI);
        Line(string.Create(CultureInfo.InvariantCulture,
            $"spot feet {feet.X:F2},{feet.Y:F2},{feet.Z:F2} heading {heading:F0}; player feet {playerFeet.X:F2},{playerFeet.Y:F2},{playerFeet.Z:F2} heading {playerHeading:F0} ({Vector3.Distance(feet, playerFeet):F2} m off the spot); climb rule {(player.ClimbsNaturalRock ? "natural rock (rays)" : "blocks (PlayerClimb.Find on the Engine's voxels)")}; holding={held} lastRail={(lastRail?.ToString() ?? "none")}; stamina={player.Stamina.Current:F1}/{player.Stamina.Maximum}"));

        // Where the spot lies: which box, and whether it is ready, admitted, dressed and resident.
        CavePlacement? box = BoxesNear(feet).FirstOrDefault(b => b.InBox(fx, fy, fz, out _, out _, out _));
        Line(box is null ? "box: none holds the spot" : string.Create(CultureInfo.InvariantCulture,
            $"box: {CaveName(box)} ready={underCaves.TryGetValue(box.Key, out UnderCave? u) && u.Ready is not null} admitted={terrain.Caves.IsAdmitted(box)} dressed={dressings.ContainsKey(box.Key)} built={dressings.TryGetValue(box.Key, out DressedCave? dc) && dc.Dressing.Built} kit={(dressings.TryGetValue(box.Key, out DressedCave? dk) ? dk.Dressing.KitCount : 0)}; waiting: {SpotWaiting(feet) ?? "nothing"}"));
        if (walkWay is not null)
        {
            (float along, float off) = Progress(walkWay, feet + (Vector3.UnitY * WalkAboveFloor));
            Line(string.Create(CultureInfo.InvariantCulture, $"way: {along:F1}/{Length(walkWay):F0} m along, {off:F2} m off it"));
        }

        SpotCells cells = ReadCells(feet);
        Line($"cells: {cells.Size}^3 from {cells.X0},{cells.Y0},{cells.Z0}; walker measure {(cells.Sampled ? "from the planned boxes' fields (as GravityWay)" : "from the terrain's materials (no planned box here)")}");

        // The walker's expectation: the nearest cell of its way, the moves about it, and the next climb.
        ExpectedClimb? climb = null;
        int? nearest = null;
        if (walkerWay is WalkerWay ww && plannedWay)
        {
            IReadOnlyList<(int X, int Y, int Z)> wc = ww.Way.Cells;
            Vector3 FeetOf(int i) => new(wc[i].X + ww.X0 + 0.5f, wc[i].Y + ww.Y0, wc[i].Z + ww.Z0 + 0.5f);
            int best = 0;
            for (int i = 1; i < wc.Count; i++)
            {
                if (Vector3.DistanceSquared(FeetOf(i), feet) < Vector3.DistanceSquared(FeetOf(best), feet)) best = i;
            }

            nearest = best;
            StringBuilder moves = new();
            for (int i = Math.Max(0, best - SpotMovesBefore); i <= Math.Min(wc.Count - 1, best + SpotMovesAfter); i++)
            {
                CaveWalker.Move m = ww.Way.Moves[i];
                int rise = i > 0 ? wc[i].Y - wc[i - 1].Y : 0;
                moves.Append(i == best ? "[" : "").Append(m switch { CaveWalker.Move.Climb => $"C{rise}", CaveWalker.Move.Drop => $"D{-rise}", _ => rise > 0 ? "W+" : rise < 0 ? "W-" : "W" }).Append(i == best ? "]" : "").Append(' ');
            }

            Vector3 nf = FeetOf(best);
            Line(string.Create(CultureInfo.InvariantCulture,
                $"walker: cell {best}/{wc.Count} at feet {nf.X:F1},{nf.Y:F1},{nf.Z:F1} ({Vector3.Distance(nf, feet):F2} m from the spot); moves {moves}({ww.Way.Drops} drops, {ww.Way.Climbs} climbs on the whole way)"));

            // The way on, cell by cell, as the collision holds it: where its floor is against the walker's, and the rise.
            float jumpRise = PlayerConstants.JumpSpeed * PlayerConstants.JumpSpeed / (2f * PlayerConstants.Gravity);
            StringBuilder ahead = new(string.Create(CultureInfo.InvariantCulture,
                $"walker way vs collision (cell: move, walker floor, collision floor offset, rise from the cell before; the controller steps {PlayerConstants.MaximumStepHeight:F2} m, jumps {jumpRise:F2} m): "));
            float? previous = null;
            for (int i = Math.Max(0, best - SpotMovesBefore); i <= Math.Min(wc.Count - 1, best + SpotMovesAfter); i++)
            {
                (long X, long Y, long Z) cell = (wc[i].X + ww.X0, wc[i].Y + ww.Y0, wc[i].Z + ww.Z0);
                float? floor = CollisionFloor(cell);
                CaveWalker.Move m = ww.Way.Moves[i];
                string rise = floor is float fl && previous is float pf ? string.Create(CultureInfo.InvariantCulture, $"{fl - pf:+0.00;-0.00}") : "?";
                ahead.Append(CultureInfo.InvariantCulture, $"{i}:{m} {cell.Y} {(floor is float f ? (f - cell.Y).ToString("+0.00;-0.00", CultureInfo.InvariantCulture) : "none")} {rise}; ");
                if (floor is float now && previous is float before && m == CaveWalker.Move.Walk && i > best - 1)
                {
                    float up = now - before;
                    if (up > PlayerConstants.MaximumStepHeight)
                    {
                        disagree.Add(string.Create(CultureInfo.InvariantCulture,
                            $"the walker walks from cell {i - 1} to {i} (a step of {wc[i].Y - wc[i - 1].Y} m); the collision floor rises {up:F2} m there, past the controller's {PlayerConstants.MaximumStepHeight:F2} m step{(up > jumpRise ? $" and its {jumpRise:F2} m jump" : "")}"));
                        verdict ??= "model: the walker's step and the controller's step disagree here (the collision floor rises more than the walker's cell does)";
                    }
                }

                if (floor is float under && under - cell.Y < -FloorOffsetFlag && i >= best - 1 && i <= best + 2)
                {
                    disagree.Add(string.Create(CultureInfo.InvariantCulture,
                        $"at walker cell {i} the collision floor lies {cell.Y - under:F2} m under the walker's floor: the walker stands on cells it counts solid (open less than {CaveWalker.OpenAtLeast} m) that the collision leaves open"));
                }

                previous = floor ?? previous;
            }

            Line(ahead.ToString());

            // The player off the walker's floor: under it, in space the walker counts as rock.
            float below = nf.Y - playerFeet.Y;
            if (below > FloorOffsetFlag && Vector2.Distance(new Vector2(nf.X, nf.Z), new Vector2(playerFeet.X, playerFeet.Z)) < 2f)
            {
                disagree.Add(string.Create(CultureInfo.InvariantCulture,
                    $"the player's feet are {below:F2} m under the walker's floor at its nearest cell: the controller stands where the walker sees no standing room"));
                verdict ??= "model: the controller walks under the walker's floor, in cells the walker counts solid that the collision leaves open";
            }
            for (int i = Math.Max(1, best - 1); i < Math.Min(wc.Count, best + SpotClimbAhead); i++)
            {
                if (ww.Way.Moves[i] != CaveWalker.Move.Climb) continue;
                (long X, long Y, long Z) foot = (wc[i - 1].X + ww.X0, wc[i - 1].Y + ww.Y0, wc[i - 1].Z + ww.Z0);
                (long X, long Y, long Z) ledge = (wc[i].X + ww.X0, wc[i].Y + ww.Y0, wc[i].Z + ww.Z0);
                climb = new ExpectedClimb(foot, ledge, (int)(ledge.X - foot.X), (int)(ledge.Z - foot.Z), (int)(ledge.Y - foot.Y), i);
                break;
            }
        }
        else
        {
            Line("walker: no walker's way laid (craft.cave.plan way first); the walker's rule is read about the spot only");
        }

        if (climb is null)
        {
            // No climb on the way here: the spot itself, as a stand.
            (int ci, int cj, int ck) = cells.Cell(fx, fy, fz);
            Line($"walker stand at the spot's cell: {cells.Walker.ExplainStanding(ci, cj, ck)}");
            FloorFacts(cells, (fx, fy, fz), "spot", Line, disagree);
            CapsuleFacts(feet, "spot", Line, disagree);
            ClimbFacts(feet, heading, "spot", Line, disagree, out _);
        }
        else
        {
            ExpectedClimb c = climb;
            float axisHeading = (float)(Math.Atan2(c.Dx, -c.Dz) * 180d / Math.PI);
            Line(string.Create(CultureInfo.InvariantCulture,
                $"walker climb (way cell {c.Index}): from foot cell {c.Foot} up {c.Pitch} m onto ledge cell {c.Ledge}, along ({c.Dx},{c.Dz}) = heading {axisHeading:F0}; the face is column ({c.Foot.X + c.Dx},{c.Foot.Z + c.Dz})"));
            (int ai, int aj, int ak) = cells.Cell(c.Foot.X, c.Foot.Y, c.Foot.Z);
            bool inside = c.Ledge.Y - cells.Y0 + 2 < cells.Size && ai >= 1 && ak >= 1 && ai < cells.Size - 1 && ak < cells.Size - 1;
            string? walkerFails = inside ? cells.Walker.ExplainClimb((ai, aj, ak), c.Dx, c.Dz, c.Pitch) : "outside the cells read";
            Line($"walker rule re-read here: {walkerFails ?? "the climb holds (every condition)"}; foot {cells.Walker.ExplainStanding(ai, aj, ak)}");
            if (walkerFails is not null)
            {
                disagree.Add($"the walker's way climbs here, but its own rule on the cells now says: {walkerFails}");
                verdict ??= "shape: the cells the walker climbs here differ from the ones it found its way on";
            }

            // The face column as the walker, the terrain and the Engine hold it.
            StringBuilder column = new("face column (walker/terrain/engine, from the foot up): ");
            for (int h = 0; h <= c.Pitch + 1; h++)
            {
                (int i, int j, int k) = cells.Cell(c.Foot.X + c.Dx, c.Foot.Y + h, c.Foot.Z + c.Dz);
                if (j >= cells.Size) break;
                int at = cells.Index(i, j, k);
                float o = cells.Openness[at];
                string w = o <= 0f ? "rock" : o < CaveWalker.OpenAtLeast ? "barely" : "open";
                column.Append(CultureInfo.InvariantCulture, $"{h}:{w}/{(BlockId)cells.Terrain[at]}/{(BlockId)cells.Engine[at]} ");
                if (Collides(cells.Terrain[at]) != Collides(cells.Engine[at]))
                {
                    disagree.Add($"the terrain and the Engine disagree on the face {h} m up ({(BlockId)cells.Terrain[at]} vs {(BlockId)cells.Engine[at]}): a stale chunk");
                }
                else if (h >= 1 && h < c.Pitch && o <= 0f && !BlockRegistry.Get((BlockId)cells.Engine[at]).Climbable)
                {
                    disagree.Add($"the walker holds rock {h} m up the face, but the Engine's block there ({(BlockId)cells.Engine[at]}) is not climbable");
                }
            }

            Line(column.ToString());
            FloorFacts(cells, c.Foot, "foot", Line, disagree);
            FloorFacts(cells, c.Ledge, "ledge", Line, disagree);
            float? footFloor = CollisionFloor(c.Foot);
            float? ledgeFloor = CollisionFloor(c.Ledge);
            if (footFloor is float ff && ledgeFloor is float lf)
            {
                Line(string.Create(CultureInfo.InvariantCulture, $"rise: collision floor foot {ff:F2} to ledge {lf:F2} = {lf - ff:F2} m (the controller steps {PlayerConstants.MaximumStepHeight:F2} m; the walker's pitch {c.Pitch} m)"));
            }

            // The expected stand: in the foot cell, pressed to the face as the steering leans it.
            float press = 0.5f - PlayerConstants.CapsuleRadius - (float)PlayerClimb.StandoffMetres - 0.02f;
            Vector3 stand = new(c.Foot.X + 0.5f + (c.Dx * press), footFloor ?? c.Foot.Y, c.Foot.Z + 0.5f + (c.Dz * press));
            CapsuleFacts(stand, "expected stand", Line, disagree);
            CapsuleFacts(new Vector3(c.Ledge.X + 0.5f, ledgeFloor ?? c.Ledge.Y, c.Ledge.Z + 0.5f), "ledge", Line, disagree);
            FaceFacts(stand, c, Line, disagree);
            ClimbFacts(stand, axisHeading, "expected stand", Line, disagree, out ClimbRail? expectedRail);
            bool expectedTops = expectedRail is ClimbRail er && er.TopFeetY >= c.Ledge.Y - 0.05;
            if (expectedRail is null)
            {
                disagree.Add("the walker climbs here; the controller's climb rule finds no rail from the expected stand along the walker's heading");
                verdict ??= "model: the offline climbing model and the controller's climb rule disagree at this face";
            }
            else if (!expectedTops)
            {
                disagree.Add(string.Create(CultureInfo.InvariantCulture, $"the controller's rail from the expected stand tops out at feet {expectedRail.Value.TopFeetY:F2}, short of the ledge at {c.Ledge.Y}"));
                verdict ??= "model: the controller's rail stops short of the walker's ledge";
            }

            // The player as they stand: their own climb rule answer, and how far they are from the expected stand and heading.
            ClimbFacts(playerFeet, playerHeading, "player", Line, disagree, out ClimbRail? playerRail);
            float offStand = Vector2.Distance(new Vector2(playerFeet.X, playerFeet.Z), new Vector2(stand.X, stand.Z));
            float offHeading = MathF.Abs(((playerHeading - axisHeading + 540f) % 360f) - 180f);
            Line(string.Create(CultureInfo.InvariantCulture, $"player against the expected stand: {offStand:F2} m off, heading {offHeading:F0} degrees off, feet {playerFeet.Y - stand.Y:F2} m over it"));
            if (playerRail is null && (offStand > SteerOffMetres || offHeading > SteerOffDegrees))
            {
                disagree.Add(string.Create(CultureInfo.InvariantCulture, $"the player stands {offStand:F2} m and {offHeading:F0} degrees off the expected stand, where the controller {(expectedTops ? "would climb" : "would not climb either")}"));
                if (expectedTops) verdict ??= "steering: the walk does not bring the player to the face the walker climbs";
            }
            else if (playerRail is ClimbRail pr && pr.TopFeetY < c.Ledge.Y - 0.05)
            {
                disagree.Add(string.Create(CultureInfo.InvariantCulture, $"the player's own rail tops out at feet {pr.TopFeetY:F2}, short of the ledge at {c.Ledge.Y}"));
            }
        }

        StaticFacts(feet, Line, disagree, ref verdict);
        Line(disagree.Count == 0 ? "disagreements: none found" : "disagreements:\n  - " + string.Join("\n  - ", disagree));
        Line($"verdict: {verdict ?? (disagree.Count == 0 ? "the walker and the controller agree here; look at the walk's timing (a jump, a held key) or the next spot" : "see the disagreements")}");
        return new SpotReport(text.ToString(), cells, climb, nearest);
    }

    /// <summary>A collision floor: the first surface a ray down meets over a cell's foot (world height), or null.</summary>
    private float? CollisionFloor((long X, long Y, long Z) cell) =>
        RayWorld(new Vector3(cell.X + 0.5f, cell.Y + FloorProbeAbove, cell.Z + 0.5f), -Vector3.UnitY, FloorProbeAbove + FloorProbeBelow) is (Vector3 p, _, _) ? p.Y : null;

    private void FloorFacts(SpotCells cells, (long X, long Y, long Z) cell, string name, Action<string> line, List<string> disagree)
    {
        StringBuilder floors = new($"{name} collision floors (cell foot {cell.Y}; offset of the surface a ray down meets, centre then +x -x +z -z): ");
        foreach ((int dx, int dz) in (ReadOnlySpan<(int, int)>)[(0, 0), (1, 0), (-1, 0), (0, 1), (0, -1)])
        {
            (long X, long Y, long Z) c = (cell.X + dx, cell.Y, cell.Z + dz);
            if (RayWorld(new Vector3(c.X + 0.5f, c.Y + FloorProbeAbove, c.Z + 0.5f), -Vector3.UnitY, FloorProbeAbove + FloorProbeBelow) is (Vector3 p, SpatialHitKind kind, _))
            {
                float offset = p.Y - c.Y;
                floors.Append(CultureInfo.InvariantCulture, $"{offset:+0.00;-0.00}{(kind == SpatialHitKind.Voxel ? "" : "(" + kind + ")")} ");
                if (dx == 0 && dz == 0 && MathF.Abs(offset) > FloorOffsetFlag)
                {
                    disagree.Add(string.Create(CultureInfo.InvariantCulture, $"the {name}'s collision floor sits {offset:+0.00;-0.00} m off its voxel floor ({kind})"));
                }
            }
            else
            {
                floors.Append("none ");
            }
        }

        line(floors.ToString());
    }

    /// <summary>Whether a standing capsule fits with its feet at a point: what the Engine's collision touches it with.</summary>
    private void CapsuleFacts(Vector3 feet, string name, Action<string> line, List<string> disagree)
    {
        float half = (PlayerConstants.StandingHeight / 2f) - PlayerConstants.CapsuleRadius;
        Vector3 centre = feet + (Vector3.UnitY * ((PlayerConstants.StandingHeight / 2f) + PlayerConstants.ContactSkin + 0.02f));
        SpatialContactResult result = engine.Spatial.OverlapContacts(SpatialContactQueryRequest.Overlap(terrain.Session,
            SpatialShape.Capsule(new Vector3(0f, -half, 0f), new Vector3(0f, half, 0f), PlayerConstants.CapsuleRadius),
            new SpatialPose(terrain.Frame.ToLocal(centre), Quaternion.Identity), ReadOnlyMemory<SpatialEntityCollider>.Empty,
            ReadOnlyMemory<SpatialEntityPart>.Empty, ReadOnlyMemory<ulong>.Empty, AllCollision, false, 16));
        SpatialContact[] deep = [.. result.Contacts.ToArray().Where(c => c.PenetrationDepth > OverlapDepth)];
        line(deep.Length == 0
            ? $"{name} capsule: fits (feet {feet.Y:F2})"
            : $"{name} capsule: overlaps " + string.Join(", ", deep.Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{c.Kind}{(c.Kind == SpatialHitKind.StaticMesh ? $" instance 0x{c.Instance:X} asset 0x{c.Asset:X}" : "")} {c.PenetrationDepth:F2} m at {terrain.Frame.ToWorld(c.Point).Y - feet.Y:F2} m over the feet"))));
        if (deep.Length > 0)
        {
            disagree.Add(string.Create(CultureInfo.InvariantCulture, $"a standing capsule does not fit at the {name}: {string.Join(", ", deep.Select(c => $"{c.Kind} {c.PenetrationDepth:F2} m"))}"));
        }
    }

    /// <summary>Looks along the climb's axis from the stand every half metre up: where the collision's face is, against the voxel face.</summary>
    private void FaceFacts(Vector3 stand, ExpectedClimb c, Action<string> line, List<string> disagree)
    {
        Vector3 axis = new(c.Dx, 0f, c.Dz);
        float voxelFace = (c.Dx != 0 ? (c.Dx > 0 ? c.Foot.X + 1f - stand.X : stand.X - c.Foot.X) : (c.Dz > 0 ? c.Foot.Z + 1f - stand.Z : stand.Z - c.Foot.Z));
        StringBuilder looks = new(string.Create(CultureInfo.InvariantCulture, $"face looks along ({c.Dx},{c.Dz}) from the expected stand (voxel face {voxelFace:F2} m off; reach {PlayerConstants.CapsuleRadius + PlayerClimb.HoldReachMetres:F2}): "));
        int staticHits = 0;
        for (float h = FaceLookSpacing / 2f; h <= c.Pitch + FaceLookOver; h += FaceLookSpacing)
        {
            if (RayWorld(stand + (Vector3.UnitY * h), axis, FaceLookReach) is (Vector3 p, SpatialHitKind kind, Vector3 normal))
            {
                float d = Vector3.Dot(p - stand, axis);
                looks.Append(CultureInfo.InvariantCulture, $"{h:F2}:{d:F2}{(kind == SpatialHitKind.Voxel ? "" : "(" + kind + ")")}/n{normal.Y:F1} ");
                if (kind == SpatialHitKind.StaticMesh) staticHits++;
            }
            else
            {
                looks.Append(CultureInfo.InvariantCulture, $"{h:F2}:- ");
            }
        }

        line(looks.ToString());
        if (staticHits > 0)
        {
            disagree.Add($"a static mesh (a dressing piece's collider) stands in front of the face the walker climbs ({staticHits} of the looks meet it)");
        }
    }

    /// <summary>
    /// The controller's answer about climbing from a stand facing a heading: the climb rule the walking world uses
    /// (<see cref="PlayerClimb.Find"/> on the Engine's voxels, taking hold, pressing forward) and its inputs, along the heading
    /// and the other three axes, and climbprobe's natural read along the heading and 20 degrees either side.
    /// </summary>
    private void ClimbFacts(Vector3 feet, float heading, string name, Action<string> line, List<string> disagree, out ClimbRail? rail)
    {
        rail = null;
        StringBuilder rule = new($"{name} climb rule: ");
        foreach (float offset in (ReadOnlySpan<float>)[0f, 90f, 180f, 270f])
        {
            float r = (heading + offset) * MathF.PI / 180f;
            Vector2 facing = new(MathF.Sin(r), -MathF.Cos(r));
            ClimbRail? found = PlayerClimb.Find(feet.X, feet.Y, feet.Z, facing, 1f, ClimbGrip.Taking, (x, y, z) => player.ClimbBlockAt(x, y, z));
            if (offset == 0f) rail = found;
            bool alongX = MathF.Abs(facing.X) >= MathF.Abs(facing.Y);
            int sign = alongX ? Math.Sign(facing.X) : Math.Sign(facing.Y);
            long cx = (long)Math.Floor(feet.X), cz = (long)Math.Floor(feet.Z), f = (long)Math.Floor(feet.Y + 1e-3);
            bool own = BlockRegistry.Get(player.ClimbBlockAt(cx, f + 1, cz)).Climbable;
            long wx = own ? cx : alongX ? cx + sign : cx, wz = own ? cz : alongX ? cz : cz + sign;
            double face = own ? (alongX ? cx : cz) + (sign > 0 ? 0d : 1d) : (alongX ? cx : cz) + (sign > 0 ? 1d : 0d);
            double distance = Math.Abs(face - (alongX ? feet.X : feet.Z));
            string wall = string.Join("", Enumerable.Range(-1, 4).Select(d => BlockRegistry.Get(player.ClimbBlockAt(wx, f + d, wz)).Climbable ? 'c' : BlockRegistry.Get(player.ClimbBlockAt(wx, f + d, wz)).Collidable ? '#' : '.'));
            rule.Append(CultureInfo.InvariantCulture,
                $"{(offset == 0f ? "ahead" : $"+{offset:F0}")} {(alongX ? (sign > 0 ? "+x" : "-x") : (sign > 0 ? "+z" : "-z"))}: {(found is ClimbRail fr ? $"rail feet {fr.BottomFeetY:F2}..{fr.TopFeetY:F2}" : "none")} (feet cell {f}, wall ({wx},{wz}){(own ? " own cell" : "")} {distance:F2} m off, wall cells {f - 1}..{f + 2} [{wall}]); ");
        }

        line(rule.ToString());
        Vector3 centre = terrain.Frame.ToLocal(feet + (Vector3.UnitY * (PlayerConstants.StandingHeight / 2f)));
        foreach (float offset in (ReadOnlySpan<float>)[0f, -20f, 20f])
        {
            float r = (heading + offset) * MathF.PI / 180f;
            line($"{name} climbprobe {heading + offset:F0}: {player.DescribeClimbAt(centre, new Vector3(MathF.Sin(r), 0f, -MathF.Cos(r)))}");
        }
    }

    /// <summary>The static collision about a spot: its pieces near, and whether the composition gives two pieces one id.</summary>
    private void StaticFacts(Vector3 feet, Action<string> line, List<string> disagree, ref string? verdict)
    {
        if (terrain.LastStaticCollision is not TerrainWorld.StaticCollision s)
        {
            line("static collision: none composed");
            return;
        }

        int duplicateAssets = s.Assets.GroupBy(a => a.Id).Count(g => g.Count() > 1);
        int duplicateInstances = s.Instances.GroupBy(i => i.Id).Count(g => g.Count() > 1);
        List<string> near = [];
        foreach (StaticMeshInstance instance in s.Instances)
        {
            Vector3 at = terrain.Frame.ToWorld(instance.Transform.Translation);
            if (Vector3.Distance(at, feet) <= SpotRadiusCells) near.Add(string.Create(CultureInfo.InvariantCulture, $"0x{instance.Id:X}/0x{instance.Asset:X} at {Vector3.Distance(at, feet):F1} m"));
        }

        line($"static collision: {s.Assets.Length} assets ({duplicateAssets} ids given twice or more), {s.Instances.Length} instances ({duplicateInstances} ids given twice or more); within {SpotRadiusCells} m: {(near.Count == 0 ? "none" : string.Join(", ", near.Take(12)))}");
        if (duplicateAssets > 0 || duplicateInstances > 0)
        {
            disagree.Add($"the walking session's static collision names {duplicateAssets} asset ids and {duplicateInstances} instance ids more than once (each dressed cave's kit numbers its own from the same base): which geometry the Engine keeps for a shared id is not the product's choice");
            if (near.Count > 0) verdict ??= "shape: dressing colliders stand here, and their ids clash between dressed caves";
        }
    }

    /// <summary>A ray in world metres against the walking session's collision: the hit's world point, kind and normal, or null.</summary>
    private (Vector3 Point, SpatialHitKind Kind, Vector3 Normal)? RayWorld(Vector3 origin, Vector3 direction, float reach)
    {
        SpatialHit hit = CastWorld(origin, direction, reach);
        return hit.Present ? (terrain.Frame.ToWorld(hit.Point), hit.Kind, hit.Normal) : null;
    }

    private SpatialHit CastWorld(Vector3 origin, Vector3 direction, float reach) =>
        engine.Spatial.CastRay(new SpatialRaycastRequest(terrain.Session, terrain.Frame.ToLocal(origin), Vector3.Normalize(direction), reach, AllCollision,
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));

    // --- The dump -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Assisted, for traversal diagnostics (#11226): the spot's 3D dump as JSON for the CMS's traversal view - the voxels about
    /// it (the walker's measure, the terrain's and the Engine's materials), the collision as rays meet it on a half-metre
    /// lattice (voxel surface or static mesh), the static pieces' triangles, the walker's way and the steering way, the
    /// player, the expected climb, and the report.
    /// </summary>
    internal string Dump(string spec)
    {
        if (SpotOf(spec) is not (Vector3 feet, float heading))
        {
            return "craft.cave.dump here | along:<metres> | <x>,<y>,<z>,<heading>";
        }

        SpotReport report = WhyReport(feet, heading);
        SpotCells cells = report.Cells;
        StringBuilder json = new();
        CultureInfo inv = CultureInfo.InvariantCulture;
        string F(float v) => v.ToString("0.###", inv);
        string V(Vector3 v) => $"[{F(v.X)},{F(v.Y)},{F(v.Z)}]";
        json.Append("{\"kind\":\"traversal-spot\",\"version\":1");
        json.Append(inv, $",\"feet\":{V(feet)},\"heading\":{F(heading)}");
        json.Append(inv, $",\"origin\":[{cells.X0},{cells.Y0},{cells.Z0}],\"size\":{cells.Size},\"openAtLeast\":{F(CaveWalker.OpenAtLeast)}");
        json.Append(",\"openness\":[").AppendJoin(',', cells.Openness.Select(o => F(MathF.Round(o, 2)))).Append(']');
        json.Append(",\"terrain\":[").AppendJoin(',', cells.Terrain).Append(']');
        json.Append(",\"engine\":[").AppendJoin(',', cells.Engine).Append(']');
        json.Append(",\"materials\":{").AppendJoin(',', cells.Terrain.Concat(cells.Engine).Distinct().Select(m => $"\"{m}\":\"{(BlockId)m}\"")).Append('}');

        // The collision as rays meet it: along each axis on a half-metre lattice, from the start of each open run of Engine voxels,
        // every surface met to the run's end (on past a static mesh; out of a start inside a collider).
        List<string> hits = [];
        float lo = 0.25f, hi = cells.Size - 0.25f;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 dir = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            for (float a = lo; a <= hi; a += DumpRaySpacing)
            for (float b = lo; b <= hi; b += DumpRaySpacing)
            {
                Vector3 Point(float t) => new Vector3(cells.X0, cells.Y0, cells.Z0) + (axis == 0 ? new Vector3(t, a, b) : axis == 1 ? new Vector3(a, t, b) : new Vector3(a, b, t));
                bool Open(float t)
                {
                    Vector3 p = Point(t) - new Vector3(cells.X0, cells.Y0, cells.Z0);
                    int i = (int)MathF.Floor(p.X), j = (int)MathF.Floor(p.Y), k = (int)MathF.Floor(p.Z);
                    return i >= 0 && j >= 0 && k >= 0 && i < cells.Size && j < cells.Size && k < cells.Size && !Collides(cells.Engine[cells.Index(i, j, k)]);
                }

                for (int cell = 0; cell < cells.Size; cell++)
                {
                    float t = cell + 0.5f;
                    if (!Open(t) || (cell > 0 && Open(t - 1f))) continue;
                    int end = cell;
                    while (end + 1 < cells.Size && Open(end + 1.5f)) end++;
                    float stop = end + 1.5f, at = t;
                    for (int n = 0; n < DumpHitsPerLine && at < stop; n++)
                    {
                        SpatialHit hit = CastWorld(Point(at), dir, stop - at);
                        if (!hit.Present) break;
                        if (hit.StartSolid || hit.Distance < 1e-4)
                        {
                            at += RayStartSolidStep;
                            continue;
                        }

                        Vector3 p = terrain.Frame.ToWorld(hit.Point);
                        hits.Add($"[{F(p.X)},{F(p.Y)},{F(p.Z)},{F(hit.Normal.X)},{F(hit.Normal.Y)},{F(hit.Normal.Z)},{(int)hit.Kind}]");
                        at += (float)hit.Distance + RayNudge;
                    }

                    // And back from the run's end, for the near wall's far side.
                    SpatialHit back = CastWorld(Point(end + 0.5f), -dir, end - cell + 1f);
                    if (back.Present && !back.StartSolid && back.Distance > 1e-4)
                    {
                        Vector3 p = terrain.Frame.ToWorld(back.Point);
                        hits.Add($"[{F(p.X)},{F(p.Y)},{F(p.Z)},{F(back.Normal.X)},{F(back.Normal.Y)},{F(back.Normal.Z)},{(int)back.Kind}]");
                    }

                    cell = end;
                }
            }
        }

        json.Append(",\"hitKinds\":{\"1\":\"Voxel\",\"2\":\"StaticMesh\",\"3\":\"Entity\"},\"hits\":[").AppendJoin(',', hits).Append(']');

        // The static pieces whose triangles reach into the cube, as the product composed them, in world metres.
        json.Append(",\"meshes\":[");
        bool firstMesh = true;
        Vector3 min = new(cells.X0, cells.Y0, cells.Z0), max = min + new Vector3(cells.Size);
        if (terrain.LastStaticCollision is TerrainWorld.StaticCollision s)
        {
            Dictionary<ulong, StaticMeshAsset[]> assets = s.Assets.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (StaticMeshInstance instance in s.Instances)
            {
                if (!assets.TryGetValue(instance.Asset, out StaticMeshAsset[]? named)) continue;
                for (int variant = 0; variant < named.Length; variant++)
                {
                    StaticMeshAsset asset = named[variant];
                    List<string> triangles = [];
                    for (uint t = 0; t < asset.TriangleCount; t++)
                    {
                        Triangle tri = s.Triangles[asset.FirstTriangle + t];
                        Vector3[] corners = [.. new[] { tri.A, tri.B, tri.C }.Select(v => terrain.Frame.ToWorld(instance.Transform.Translation
                            + Vector3.Transform(s.Vertices[asset.FirstVertex + v] * instance.Transform.Scale, instance.Transform.Rotation)))];
                        if (corners.All(p => p.X < min.X || p.Y < min.Y || p.Z < min.Z || p.X > max.X || p.Y > max.Y || p.Z > max.Z)) continue;
                        triangles.Add(string.Join(',', corners.Select(V)));
                    }

                    if (triangles.Count == 0) continue;
                    json.Append(firstMesh ? "" : ",").Append(inv, $"{{\"instance\":{instance.Id},\"asset\":{instance.Asset},\"variant\":{variant},\"variants\":{named.Length},\"triangles\":[{string.Join(',', triangles.Select(t => "[" + t + "]"))}]}}");
                    firstMesh = false;
                }
            }
        }

        json.Append(']');

        // The walker's way (feet at each cell's foot, how it was reached) and the steering way, within a few metres of the cube.
        json.Append(",\"walker\":[");
        if (walkerWay is WalkerWay ww && plannedWay)
        {
            json.AppendJoin(',', ww.Way.Cells.Select((c, i) => (c, i))
                .Where(p => InCube(new Vector3(p.c.X + ww.X0, p.c.Y + ww.Y0, p.c.Z + ww.Z0)))
                .Select(p => $"[{F(p.c.X + ww.X0 + 0.5f)},{F(p.c.Y + ww.Y0)},{F(p.c.Z + ww.Z0 + 0.5f)},{(int)ww.Way.Moves[p.i]},{p.i}]"));
        }

        json.Append("],\"way\":[");
        if (walkWay is not null) json.AppendJoin(',', walkWay.Where(InCube).Select(p => V(p - (Vector3.UnitY * WalkAboveFloor))));
        json.Append(']');
        (_, _, double yaw) = player.ClimbState;
        json.Append(inv, $",\"player\":{{\"feet\":{V(player.WorldFeetPosition)},\"heading\":{F((float)(yaw * 180d / Math.PI))},\"radius\":{F(PlayerConstants.CapsuleRadius)},\"height\":{F(PlayerConstants.StandingHeight)}}}");
        if (report.Climb is ExpectedClimb c)
        {
            json.Append(inv, $",\"climb\":{{\"foot\":[{c.Foot.X},{c.Foot.Y},{c.Foot.Z}],\"ledge\":[{c.Ledge.X},{c.Ledge.Y},{c.Ledge.Z}],\"axis\":[{c.Dx},{c.Dz}],\"pitch\":{c.Pitch}}}");
        }

        json.Append(",\"report\":\"").Append(System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(report.Text)).Append("\"}");
        return json.ToString();

        bool InCube(Vector3 p) => p.X >= min.X - 2f && p.Y >= min.Y - 2f && p.Z >= min.Z - 2f && p.X <= max.X + 2f && p.Y <= max.Y + 2f && p.Z <= max.Z + 2f;
    }
}
