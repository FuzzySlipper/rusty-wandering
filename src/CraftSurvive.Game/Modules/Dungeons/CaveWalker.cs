namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The way a body with weight takes through a cave (#10908, Patch 2026-11-28: one-way drops and minimal rest ledges), on its
/// 1 m voxels: it walks, steps a cell up or down, drops off an edge (one way), and climbs a face in pitches of at most
/// <see cref="ClimbPitch"/> (about a stamina bar, which climbs some 9 m) onto a ledge to rest on. With edits allowed it may also
/// dig rock out of its way and bridge a gap with a floor, at a cost, so a way always exists and the fewest edits make it: the
/// karst generator's walker carve. Without edits it is the gravity-aware reach check.
/// </summary>
internal sealed class CaveWalker
{
    /// <summary>The highest pitch climbed in one go, in cells (metres): a rest ledge at least this often.</summary>
    internal const int ClimbPitch = 8;

    /// <summary>
    /// How open a cell's centre is at least (metres to the nearest rock, the shape's own measure) for a body to stand or pass in
    /// it. Smooth ground puts its surface where the shape crosses zero, so a cell barely open at its centre is mostly rock: its
    /// floor stands up to half a metre above the cell's foot, past what a climb's top clears (#10908). Such a cell is neither
    /// rock nor open: a floor to stand on, not room for a body nor a face to climb.
    /// </summary>
    internal const float OpenAtLeast = 0.4f;

    /// <summary>A standing body's height in cells.</summary>
    internal const int BodyCells = 2;

    // What each move costs: walking a cell, a step up and down, a drop (and per metre fallen), a climb per metre, and with edits
    // a cell dug or a floor cell laid (dearer than going round, so edits stay few).
    private const float WalkCost = 1f, StepUpCost = 1.6f, StepDownCost = 1.2f, DropCost = 4f, DropPerMetre = 0.05f, ClimbPerMetre = 3f;
    private const float DigCost = 7f, BridgeCost = 9f, FillCost = 2f;

    /// <summary>How far down a drop is followed before it is given up (the box's height bounds it anyway).</summary>
    private const int MostDrop = 256;

    /// <summary>How many cells back along a way a move's edits are checked against the way's own (see <see cref="Find"/>).</summary>
    private const int ClashLookBack = 8;

    /// <summary>The deepest drop beside a walked cell (open cells under its side) that a guard fills up rather than floors over.</summary>
    private const int GuardDropCells = 4;

    /// <summary>The search gives up past this many cells expanded.</summary>
    private const int MostExpanded = 3_000_000;

    /// <summary>How a cell was reached: walked or stepped, dropped to, climbed to.</summary>
    internal enum Move : byte
    {
        Walk = 0,
        Drop = 1,
        Climb = 2,
    }

    private readonly int sizeX, sizeY, sizeZ;
    private readonly bool[] solid;
    private readonly bool[]? rock;
    private readonly int editMargin;

    /// <param name="solid">Per cell, x fastest then y then z, whether a body cannot be in it (rock, or not <see cref="OpenAtLeast"/> open); edits change it in place.</param>
    /// <param name="editMargin">How near the grid's faces an edit may be (the box's rind stays whole), in cells.</param>
    /// <param name="rock">Per cell, whether it is rock (the shape below zero): a face is climbed only on rock. Null where every solid cell is rock.</param>
    internal CaveWalker(int sizeX, int sizeY, int sizeZ, bool[] solid, int editMargin = 2, bool[]? rock = null)
    {
        if (solid.Length != sizeX * sizeY * sizeZ || (rock is not null && rock.Length != solid.Length))
        {
            throw new ArgumentException("One solid (and rock) flag per cell.", nameof(solid));
        }

        this.rock = rock;

        this.sizeX = sizeX;
        this.sizeY = sizeY;
        this.sizeZ = sizeZ;
        this.solid = solid;
        this.editMargin = editMargin;
    }

    internal int SizeX => sizeX;
    internal int SizeY => sizeY;
    internal int SizeZ => sizeZ;

    private int Index(int x, int y, int z) => (((z * sizeY) + y) * sizeX) + x;

    private bool Inside(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < sizeX && y < sizeY && z < sizeZ;

    /// <summary>
    /// Whether a cell is rock. Outside the grid is open, never stood on or climbed: past a box's faces lies its neighbour or a
    /// mouth's tendril where its own rind is open, and its own rind is rock inside the grid everywhere else.
    /// </summary>
    internal bool Solid(int x, int y, int z) => Inside(x, y, z) && solid[Index(x, y, z)];

    /// <summary>Whether a cell is rock a climb holds (outside the grid is none).</summary>
    internal bool Rock(int x, int y, int z) => Inside(x, y, z) && (rock?[Index(x, y, z)] ?? solid[Index(x, y, z)]);

    /// <summary>
    /// The solid and rock flags of a box's 1 m cells from a shape (open where positive), sampled at each cell's centre as the
    /// game samples it: solid where not <see cref="OpenAtLeast"/> open, rock where below zero.
    /// </summary>
    internal static (bool[] Solid, bool[] Rock) Cells(int sizeX, int sizeY, int sizeZ, Func<System.Numerics.Vector3, float> shape)
    {
        bool[] solid = new bool[sizeX * sizeY * sizeZ], rock = new bool[solid.Length];
        Parallel.For(0, sizeZ, z =>
        {
            for (int y = 0; y < sizeY; y++)
            for (int x = 0; x < sizeX; x++)
            {
                float v = shape(new System.Numerics.Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
                int i = (((z * sizeY) + y) * sizeX) + x;
                solid[i] = v < OpenAtLeast;
                rock[i] = v < 0f;
            }
        });

        return (solid, rock);
    }

    private bool Editable(int x, int y, int z) =>
        x >= editMargin && z >= editMargin && y >= editMargin && x < sizeX - editMargin && z < sizeZ - editMargin && y < sizeY - editMargin;

    /// <summary>Whether a body stands at a cell: its two cells open, rock under its feet.</summary>
    internal bool Standing(int x, int y, int z) => !Solid(x, y, z) && !Solid(x, y + 1, z) && Footing(x, y - 1, z) && Broad(x, y - 1, z) && Pinch(x, y + 1, z) is null;

    /// <summary>
    /// Whether a cell holds a body up (#11226's first case): rock, or a barely open cell lying on rock, whose surface is within
    /// it. A stack of barely open cells is no floor: smooth ground's surface lies at the rock under the stack, metres lower.
    /// </summary>
    internal bool Footing(int x, int y, int z) => Rock(x, y, z) || (Solid(x, y, z) && Rock(x, y - 1, z));

    /// <summary>
    /// Where a body's head is pinched (#10908): rock either side of it along one axis, a crack one cell wide at head height,
    /// which smooth ground narrows past a body's width. The first of the two cells, or null where it is not pinched.
    /// </summary>
    private (int X, int Y, int Z)? Pinch(int x, int y, int z) =>
        Solid(x + 1, y, z) && Solid(x - 1, y, z) ? (x + 1, y, z) : Solid(x, y, z + 1) && Solid(x, y, z - 1) ? (x, y, z + 1) : null;

    /// <summary>
    /// Whether a floor cell is more than a post: rock beside it at its own level, so the floor is two cells across one way at
    /// least. Smooth ground rounds a lone cell's top into a knob a body slides off (#10908).
    /// </summary>
    private bool Broad(int x, int y, int z) => Solid(x + 1, y, z) || Solid(x - 1, y, z) || Solid(x, y, z + 1) || Solid(x, y, z - 1);

    /// <summary>The standing cell nearest a point (cells), searching down a few cells and out a few; null where there is none.</summary>
    internal (int X, int Y, int Z)? Settle(int x, int y, int z, int across = 3, int down = 10, int up = 2)
    {
        for (int r = 0; r <= across; r++)
        for (int dy = up; dy >= -down; dy--)
        for (int dx = -r; dx <= r; dx++)
        for (int dz = -r; dz <= r; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
            if (Standing(x + dx, y + dy, z + dz)) return (x + dx, y + dy, z + dz);
        }

        return null;
    }

    /// <summary>A way found: its cells, how each was reached, and the cells it needs dug and floored.</summary>
    internal sealed record Way(IReadOnlyList<(int X, int Y, int Z)> Cells, IReadOnlyList<Move> Moves, IReadOnlyList<(int X, int Y, int Z)> Dug, IReadOnlyList<(int X, int Y, int Z)> Floored, int Expanded)
    {
        internal int Drops => Moves.Count(m => m == Move.Drop);
        internal int Climbs => Moves.Count(m => m == Move.Climb);
    }

    /// <summary>
    /// The cheapest way from one cell to another (within a cell of it), or null. With <paramref name="edits"/> the cells it needs
    /// dug and floored are part of the way (the caller applies them, <see cref="Apply"/>); the start and the goal need not stand.
    /// </summary>
    internal Way? Find((int X, int Y, int Z) start, (int X, int Y, int Z) goal, bool edits)
    {
        // Each node: its cost, where it came from, how, and the edits its last move needed.
        Dictionary<(int, int, int), (float Cost, (int, int, int)? From, Move How, (int, int, int)[] Dig, (int, int, int)[] Floor, (int, int, int)[] Keep)> best = new()
        {
            [start] = (0f, null, Move.Walk, [], [], []),
        };
        PriorityQueue<(int X, int Y, int Z), float> open = new();
        float Guess((int X, int Y, int Z) c) => Math.Abs(c.X - goal.X) + Math.Abs(c.Y - goal.Y) + Math.Abs(c.Z - goal.Z);
        open.Enqueue(start, Guess(start));
        int expanded = 0;
        (int X, int Y, int Z)? reached = null;
        // The edits the move being tried needs, and the open cells it needs kept open (a climb's landing).
        List<(int, int, int)> dig = [], floor = [], keep = [], faceFill = [];

        // What standing at a cell would take: its two cells open, rock under it (with edits, their cost and the cells).
        bool Make((int X, int Y, int Z) c, out float cost)
        {
            cost = 0f;
            dig.Clear();
            floor.Clear();
            keep.Clear();
            for (int h = 0; h < BodyCells; h++)
            {
                if (Solid(c.X, c.Y + h, c.Z))
                {
                    if (!edits || !Editable(c.X, c.Y + h, c.Z)) return false;
                    dig.Add((c.X, c.Y + h, c.Z));
                    cost += DigCost;
                }
            }

            if (!Footing(c.X, c.Y - 1, c.Z))
            {
                if (!edits || !LayFloor(c.X, c.Y - 1, c.Z, ref cost)) return false;
            }

            // A head pinched between rock either side, along either axis, is given room by digging one side (with edits).
            int head = c.Y + 1;
            foreach (((int ax, int az), (int bx, int bz)) in (ReadOnlySpan<((int, int), (int, int))>)[((1, 0), (-1, 0)), ((0, 1), (0, -1))])
            {
                if (!Solid(c.X + ax, head, c.Z + az) || !Solid(c.X + bx, head, c.Z + bz)) continue;
                if (!edits) return false;
                (int, int, int)? side = Editable(c.X + ax, head, c.Z + az) ? (c.X + ax, head, c.Z + az)
                    : Editable(c.X + bx, head, c.Z + bz) ? (c.X + bx, head, c.Z + bz) : null;
                if (side is not (int, int, int) opened) return false;
                dig.Add(opened);
                cost += DigCost;
            }

            // A floor a post alone is widened by a cell beside it (with edits).
            if (!Broad(c.X, c.Y - 1, c.Z))
            {
                if (!edits) return false;
                (int X, int Y, int Z)? beside = null;
                foreach ((int dx, int dz) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
                {
                    if (Editable(c.X + dx, c.Y - 1, c.Z + dz)) { beside = (c.X + dx, c.Y - 1, c.Z + dz); break; }
                }

                if (beside is not (int, int, int) b || !LayFloor(b.X, b.Y, b.Z, ref cost)) return false;
            }

            return true;
        }

        // A floor laid at a cell, filled down to the rock under it where that is near (a step, not a knob in the air that smooth
        // ground rounds into something a body slides off or hits its head on); over a deep gap, a bridge at the cell alone.
        bool LayFloor(int x, int y, int z, ref float cost)
        {
            if (Footing(x, y, z)) return true;
            // Down through open and barely open cells alike, to the rock under them.
            int depth = 0;
            while (depth < GuardDropCells && !Rock(x, y - 1 - depth, z) && y - 1 - depth > 0) depth++;
            int fill = Rock(x, y - 1 - depth, z) ? depth + 1 : 1;
            for (int f = 0; f < fill; f++)
            {
                if (!Editable(x, y - f, z)) return false;
            }

            for (int f = 0; f < fill; f++)
            {
                if (!floor.Contains((x, y - f, z))) floor.Add((x, y - f, z));
            }

            cost += BridgeCost + ((fill - 1) * FillCost);
            return true;
        }

        // A climb's ledge has room to step onto from the top of the face (dug, with edits): the cells either side of it open at
        // its feet, and the cell beyond it open for the whole body. Smooth ground's rounding pinches a one-cell slot, or a ledge
        // one cell deep, past what a body's width steps onto.
        bool Landing(int x, int y, int z, int dx, int dz, ref float cost)
        {
            foreach ((int lx, int ly, int lz) in LandingCells(x, y, z, dx, dz))
            {
                keep.Add((lx, ly, lz));
                if (!Solid(lx, ly, lz)) continue;
                if (!edits || !Editable(lx, ly, lz)) return false;
                dig.Add((lx, ly, lz));
                cost += DigCost;
            }

            return true;
        }

        // With edits, a move whose edits undo the way just behind it, or that the edits behind it undo (a stair dug up through
        // rock digs out the floor of the step above it; a ledge's landing dug where the way on floors its walk), is not taken.
        bool Clashes((int X, int Y, int Z) at, (int X, int Y, int Z) next)
        {
            (int, int, int)? c = at;
            for (int k = 0; k < ClashLookBack && c is (int, int, int) cell; k++)
            {
                var node = best[cell];
                (int X, int Y, int Z) w = cell;
                foreach (var d in dig) if (d == (w.X, w.Y - 1, w.Z)) return true;
                foreach (var f in floor) if (f == w || f == (w.X, w.Y + 1, w.Z) || node.Keep.Contains(f)) return true;
                foreach (var d in node.Dig) if (d == (next.X, next.Y - 1, next.Z) || floor.Contains(d)) return true;
                foreach (var f in node.Floor) if (f == next || f == (next.X, next.Y + 1, next.Z) || dig.Contains(f) || keep.Contains(f)) return true;
                c = node.From;
            }

            return false;
        }

        void Try((int X, int Y, int Z) at, (int X, int Y, int Z) next, float add, Move how)
        {
            if (edits && Clashes(at, next)) return;
            float through = best[at].Cost + add;
            if (!best.TryGetValue(next, out var known) || through < known.Cost)
            {
                best[next] = (through, at, how, [.. dig], [.. floor], [.. keep]);
                open.Enqueue(next, through + Guess(next));
            }
        }

        (int, int)[] steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (open.TryDequeue(out (int X, int Y, int Z) at, out float priority) && expanded++ < MostExpanded)
        {
            if (priority - Guess(at) > best[at].Cost + 1e-3f) continue;
            if (Math.Abs(at.X - goal.X) <= 1 && Math.Abs(at.Z - goal.Z) <= 1 && Math.Abs(at.Y - goal.Y) <= 1)
            {
                reached = at;
                break;
            }

            foreach ((int dx, int dz) in steps)
            {
                int nx = at.X + dx, nz = at.Z + dz;
                // Walk on, a cell up (headroom over the body first) or a cell down.
                if (Make((nx, at.Y, nz), out float flat)) Try(at, (nx, at.Y, nz), WalkCost + flat, Move.Walk);
                if (!Solid(at.X, at.Y + BodyCells, at.Z) && Make((nx, at.Y + 1, nz), out float up)) Try(at, (nx, at.Y + 1, nz), StepUpCost + up, Move.Walk);
                if (Make((nx, at.Y - 1, nz), out float down)) Try(at, (nx, at.Y - 1, nz), StepDownCost + down, Move.Walk);

                // Off an edge: falls to the first floor under it (one way; no edits).
                if (!Solid(nx, at.Y, nz) && !Solid(nx, at.Y + 1, nz) && !Solid(nx, at.Y - 1, nz))
                {
                    dig.Clear();
                    floor.Clear();
                    keep.Clear();
                    for (int y = at.Y - 2; y > at.Y - MostDrop && y > 0; y--)
                    {
                        if (Solid(nx, y, nz)) break;
                        if (Standing(nx, y, nz))
                        {
                            Try(at, (nx, y, nz), DropCost + ((at.Y - y) * DropPerMetre), Move.Drop);
                            break;
                        }
                    }
                }

                // A face ahead: climbed in a pitch onto a ledge at its top or one dug into it, the body's own column clear. The
                // face is rock from a cell over the feet to its top (barely open cells filled, with edits).
                if (Solid(nx, at.Y, nz))
                {
                    faceFill.Clear();
                    for (int h = 2; h <= ClimbPitch; h++)
                    {
                        if (!Solid(nx, at.Y + h - 1, nz)) break;
                        if (!Rock(nx, at.Y + h - 1, nz))
                        {
                            if (!edits || !Editable(nx, at.Y + h - 1, nz)) break;
                            faceFill.Add((nx, at.Y + h - 1, nz));
                        }

                        // The climber's own column, up to its head at the top of the pitch.
                        bool clear = true;
                        for (int c = at.Y + BodyCells; c <= at.Y + h + 1 && clear; c++) clear = !Solid(at.X, c, at.Z);
                        if (!clear) break;
                        if (Make((nx, at.Y + h, nz), out float ledge) && dig.Count + floor.Count <= BodyCells && Landing(nx, at.Y + h, nz, dx, dz, ref ledge))
                        {
                            floor.AddRange(faceFill);
                            ledge += faceFill.Count * BridgeCost;
                            Try(at, (nx, at.Y + h, nz), (h * ClimbPerMetre) + ledge, Move.Climb);
                        }
                    }
                }
            }
        }

        if (reached is not (int, int, int) end)
        {
            return null;
        }

        List<(int, int, int)> cells = [];
        List<Move> moves = [];
        HashSet<(int, int, int)> dug = [], floored = [];
        for ((int, int, int)? at = end; at is (int, int, int) cell; at = best[cell].From)
        {
            cells.Insert(0, cell);
            moves.Insert(0, best[cell].How);
            foreach (var d in best[cell].Dig) dug.Add(d);
            foreach (var f in best[cell].Floor) floored.Add(f);
        }

        return new Way(cells, moves, [.. dug], [.. floored], expanded);
    }

    /// <summary>
    /// The cells a climb's ledge needs open to be stepped onto from the top of its face, climbing along (dx, dz): either side of
    /// it at its feet, and beyond it for the whole body.
    /// </summary>
    private static (int X, int Y, int Z)[] LandingCells(int x, int y, int z, int dx, int dz) =>
        [(x + dz, y, z + dx), (x - dz, y, z - dx), (x + dx, y, z + dz), (x + dx, y + 1, z + dz)];

    /// <summary>Whether a climb's ledge has room to step onto (see <see cref="Find"/>'s landing), with no edits.</summary>
    private bool Roomy(int x, int y, int z, int dx, int dz)
    {
        foreach ((int lx, int ly, int lz) in LandingCells(x, y, z, dx, dz))
        {
            if (Solid(lx, ly, lz)) return false;
        }

        return true;
    }

    /// <summary>
    /// Diagnostics (#11226): why a body stands at a cell or not, condition by condition (<see cref="Standing"/>): its two
    /// cells open, rock under its feet that is more than a post, its head not pinched.
    /// </summary>
    internal string ExplainStanding(int x, int y, int z)
    {
        List<string> fails = [];
        if (Solid(x, y, z)) fails.Add("feet cell solid");
        if (Solid(x, y + 1, z)) fails.Add("head cell solid");
        if (!Solid(x, y - 1, z)) fails.Add("no floor under");
        else if (!Broad(x, y - 1, z)) fails.Add("floor a lone post");
        if (Pinch(x, y + 1, z) is (int, int, int) p) fails.Add($"head pinched at {p}");
        return fails.Count == 0 ? "stands" : string.Join(", ", fails);
    }

    /// <summary>
    /// Diagnostics (#11226): each condition of a climb from a standing cell along (dx, dz) onto the ledge h cells up, as
    /// <see cref="Reach"/> takes it with no edits: rock at the face's foot, rock a climb holds from a cell over the feet to
    /// under the ledge, the climber's own column clear to its head at the top, a body standing on the ledge with room to step on.
    /// Null where every condition holds, else what fails.
    /// </summary>
    internal string? ExplainClimb((int X, int Y, int Z) at, int dx, int dz, int h)
    {
        int nx = at.X + dx, nz = at.Z + dz;
        List<string> fails = [];
        if (!Solid(nx, at.Y, nz)) fails.Add("no face at the feet");
        for (int k = 1; k < h; k++)
        {
            if (!Solid(nx, at.Y + k, nz)) fails.Add($"face open {k} m up");
            else if (!Rock(nx, at.Y + k, nz)) fails.Add($"face barely open (not rock) {k} m up");
        }

        for (int c = at.Y + BodyCells; c <= at.Y + h + 1; c++)
        {
            if (Solid(at.X, c, at.Z)) fails.Add($"climber's column blocked {c - at.Y} m up");
        }

        string ledge = ExplainStanding(nx, at.Y + h, nz);
        if (ledge != "stands") fails.Add("ledge: " + ledge);
        foreach ((int lx, int ly, int lz) in LandingCells(nx, at.Y + h, nz, dx, dz))
        {
            if (Solid(lx, ly, lz)) fails.Add($"landing cell ({lx},{ly},{lz}) solid");
        }

        return fails.Count == 0 ? null : string.Join(", ", fails);
    }

    /// <summary>Every standing cell a body reaches from one, walking, stepping, dropping and climbing, with no edits.</summary>
    internal HashSet<(int X, int Y, int Z)> Reach((int X, int Y, int Z) start, int most = MostExpanded)
    {
        HashSet<(int X, int Y, int Z)> reached = [start];
        Queue<(int X, int Y, int Z)> frontier = new([start]);
        (int, int)[] steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        void Add((int, int, int) c)
        {
            if (reached.Add(c)) frontier.Enqueue(c);
        }

        while (frontier.TryDequeue(out (int X, int Y, int Z) at) && reached.Count < most)
        {
            foreach ((int dx, int dz) in steps)
            {
                int nx = at.X + dx, nz = at.Z + dz;
                if (Standing(nx, at.Y, nz)) Add((nx, at.Y, nz));
                if (!Solid(at.X, at.Y + BodyCells, at.Z) && Standing(nx, at.Y + 1, nz)) Add((nx, at.Y + 1, nz));
                if (Standing(nx, at.Y - 1, nz)) Add((nx, at.Y - 1, nz));
                if (!Solid(nx, at.Y, nz) && !Solid(nx, at.Y + 1, nz) && !Solid(nx, at.Y - 1, nz))
                {
                    for (int y = at.Y - 2; y > at.Y - MostDrop && y > 0; y--)
                    {
                        if (Solid(nx, y, nz)) break;
                        if (Standing(nx, y, nz)) { Add((nx, y, nz)); break; }
                    }
                }

                if (Solid(nx, at.Y, nz))
                {
                    for (int h = 2; h <= ClimbPitch; h++)
                    {
                        if (!Solid(nx, at.Y + h - 1, nz) || !Rock(nx, at.Y + h - 1, nz)) break;
                        bool clear = true;
                        for (int c = at.Y + BodyCells; c <= at.Y + h + 1 && clear; c++) clear = !Solid(at.X, c, at.Z);
                        if (!clear) break;
                        if (Standing(nx, at.Y + h, nz) && Roomy(nx, at.Y + h, nz, dx, dz)) Add((nx, at.Y + h, nz));
                    }
                }
            }
        }

        return reached;
    }

    /// <summary>
    /// The floors that make a way safe to walk with a body that wanders off its line (#10908): beside each cell it walks, a drop
    /// off the side (open at the walk's height and two cells under it at least) is filled up to the walk's level where shallow,
    /// floored over at it where deep, so a way over a pit is three cells wide. The caller applies them as it applies the way's.
    /// </summary>
    internal List<(int X, int Y, int Z)> Guards(Way way, Func<int, int, bool>? keepOpen = null)
    {
        HashSet<(int, int, int)> guards = [];
        const int Near = 4;
        for (int i = 0; i < way.Cells.Count; i++)
        {
            // Only along a walked stretch: not where the way drops or climbs next, nor just after.
            bool walked = true;
            for (int j = Math.Max(0, i - 1); j <= Math.Min(way.Cells.Count - 1, i + 2) && walked; j++) walked = way.Moves[j] == Move.Walk;
            if (!walked) continue;
            (int x, int y, int z) = way.Cells[i];
            // The columns the way itself passes through about here stay as they are.
            HashSet<(int, int)> own = [];
            for (int j = Math.Max(0, i - Near); j <= Math.Min(way.Cells.Count - 1, i + Near); j++) own.Add((way.Cells[j].X, way.Cells[j].Z));
            foreach ((int dx, int dz) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)])
            {
                int nx = x + dx, nz = z + dz;
                if (own.Contains((nx, nz)) || (keepOpen?.Invoke(nx, nz) ?? false)) continue;
                // A drop off the side, open at the walked cell's height and two cells under it at least: a shallow one (its floor
                // at most GuardDropCells down) is filled up to the walk's level, so nothing hangs over a passage under it at head
                // height; over a deep one, a floor at the walk's level.
                if (Solid(nx, y, nz) || Solid(nx, y + 1, nz)) continue;
                int depth = 0;
                while (depth <= GuardDropCells && !Solid(nx, y - 1 - depth, nz) && y - 1 - depth > 0) depth++;
                if (depth < 2) continue;
                int fill = depth <= GuardDropCells ? depth : 1;
                bool editable = true;
                for (int f = 1; f <= fill && editable; f++) editable = Editable(nx, y - f, nz);
                if (!editable) continue;
                for (int f = 1; f <= fill; f++) guards.Add((nx, y - f, nz));
            }
        }

        return [.. guards];
    }

    /// <summary>Lays floors (from <see cref="Guards"/>) in the grid.</summary>
    internal void Floor(IEnumerable<(int X, int Y, int Z)> cells)
    {
        foreach ((int x, int y, int z) in cells) SetRock(x, y, z, true);
    }

    /// <summary>Whether an edit may be made at a cell (not within the edit margin of the grid's faces).</summary>
    internal bool CanEdit(int x, int y, int z) => Editable(x, y, z);

    /// <summary>Opens cells in the grid (the caller carries them into its own field).</summary>
    internal void Dig(IEnumerable<(int X, int Y, int Z)> cells)
    {
        foreach ((int x, int y, int z) in cells) SetRock(x, y, z, false);
    }

    private void SetRock(int x, int y, int z, bool isRock)
    {
        solid[Index(x, y, z)] = isRock;
        if (rock is not null) rock[Index(x, y, z)] = isRock;
    }

    /// <summary>Applies a way's edits to the grid (the caller carries them into its own field).</summary>
    internal void Apply(Way way)
    {
        foreach ((int x, int y, int z) in way.Dug) SetRock(x, y, z, false);
        foreach ((int x, int y, int z) in way.Floored) SetRock(x, y, z, true);
    }
}
