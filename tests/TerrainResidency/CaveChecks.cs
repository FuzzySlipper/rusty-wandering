using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The caves under the world (#10889; from the underground plan since #11220), checked on the default world without an
/// Engine: a cave mouth's box and tendril are a pure function of the plan; the tendril opens at the mouth and runs into its
/// box, which lies under the ground; generating a chunk a tendril or a cave reaches costs about what any chunk does; and the
/// residency keeps a cave's chunks round a player deep in it and lets the overworld go.
/// </summary>
internal static class CaveChecks
{
    internal static CavePlacement? FirstCave(TerrainRecipe recipe, out PoiSite mouth)
    {
        mouth = default;
        List<PoiSite> sites = [];
        recipe.Placement.CollectSitesNear(0, 0, 4000, sites);
        foreach (PoiSite site in sites.Where(s => s.Kind == PoiKind.CaveMouth).OrderBy(s => Math.Abs(s.X) + Math.Abs(s.Z)))
        {
            // A mouth with a tendril (a cliff mouth opens straight into its box's face).
            if (recipe.Caves.For(site) is CavePlacement cave && cave.Tendrils.Count > 0)
            {
                mouth = site;
                return cave;
            }
        }

        return null;
    }

    internal static void Run()
    {
        TerrainConfiguration configuration = TerrainConfiguration.Default;
        TerrainRecipe recipe = configuration.CreateRecipe(new TestDraws(configuration.Seed));
        CavePlacement? found = FirstCave(recipe, out PoiSite mouth);
        Check.That(found is not null, "the default world must have a cave mouth with a cave under it within 4 km of the origin");
        if (found is not CavePlacement cave)
        {
            return;
        }

        // The same mouth gives the same way underground from a fresh recipe (a pure function of its site).
        TerrainRecipe again = configuration.CreateRecipe(new TestDraws(configuration.Seed));
        CavePlacement? twice = again.Caves.For(mouth);
        Check.That(twice is not null && twice.Corner == cave.Corner && twice.Path.SequenceEqual(cave.Path),
            "a cave mouth's tendril and box must be the same from every recipe");

        // The tendril opens at the mouth and runs into its box, which lies under the ground.
        Check.That(cave.TendrilOpen(new Vector3(mouth.X + 0.5f, mouth.Ground + 1.5f, mouth.Z + 0.5f)) > 0f, "the tendril must open at the mouth");
        Check.That(cave.Tendrils.Any(t => cave.InBox((long)Math.Floor(t.Path[^1].X), (long)Math.Floor(t.Path[^1].Y), (long)Math.Floor(t.Path[^1].Z), out _, out _, out _)),
            "a mouth's tendril must end inside its cave's box");
        long boxCentreX = (cave.BoxMinimum.X + cave.BoxMaximum.X) / 2, boxCentreZ = (cave.BoxMinimum.Z + cave.BoxMaximum.Z) / 2;
        Check.That(cave.BoxMaximum.Y < recipe.ContinuousHeightAt(boxCentreX, boxCentreZ), "the cave's box must lie under the ground");
        Vector3 start = cave.Path[0], end = cave.Path[^1];

        // A chunk the tendril reaches: its air is the tendril's, generated smooth (densities below the half-voxel face).
        TerrainChunkGenerator generator = new(recipe);
        TerrainOverlaySnapshot overlay = new(configuration.Seed, []);
        Vector3 middle = cave.Path[cave.Path.Count / 2];
        VoxelAddress inside = new((long)MathF.Floor(middle.X), (long)MathF.Floor(middle.Y), (long)MathF.Floor(middle.Z));
        TerrainChunk tendrilChunk = generator.Generate(inside.Chunk, overlay);
        int index = (int)(((inside.Z - inside.Chunk.Origin.Z) * TerrainConstants.ChunkEdgeLength * TerrainConstants.ChunkEdgeLength)
            + ((inside.Y - inside.Chunk.Origin.Y) * TerrainConstants.ChunkEdgeLength) + (inside.X - inside.Chunk.Origin.X));
        Check.That(tendrilChunk.Materials.Span[index] == TerrainConstants.EmptyMaterial, "the tendril's centre must be air");
        Check.That(!generator.Cacheable(inside.Chunk), "a chunk a tendril reaches must never be cached");
        int smooth = 0, solid = 0;
        for (int i = 0; i < TerrainConstants.ChunkVolume; i++)
        {
            if (tendrilChunk.Materials.Span[i] == TerrainConstants.EmptyMaterial) continue;
            solid++;
            float magnitude = -tendrilChunk.Densities.Span[i];
            if (magnitude > 0f && magnitude < 0.49f) smooth++;
        }

        Check.That(smooth > 0, "a tendril's wall must take smooth densities, not faces");

        // Generation cost: chunks round the tendril against chunks of plain ground far from any cave.
        double Time(IEnumerable<TerrainChunkAddress> chunks)
        {
            TerrainChunkGenerator fresh = new(recipe);
            Stopwatch clock = Stopwatch.StartNew();
            int n = 0;
            foreach (TerrainChunkAddress chunk in chunks)
            {
                fresh.Generate(chunk, overlay);
                n++;
            }

            return clock.Elapsed.TotalMilliseconds / Math.Max(1, n);
        }

        TerrainChunkAddress[] round = [.. Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => new TerrainChunkAddress(inside.Chunk.X + dx, inside.Chunk.Y + dy, inside.Chunk.Z)))];
        long plainX = cave.BoxMaximum.X + 900, plainZ = cave.BoxMaximum.Z + 900;
        VoxelAddress plain = new(plainX, recipe.SurfaceAt(plainX, plainZ), plainZ);
        TerrainChunkAddress[] ground = [.. Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => new TerrainChunkAddress(plain.Chunk.X + dx, plain.Chunk.Y + dy, plain.Chunk.Z)))];
        Time(round);
        Time(ground);
        double caveMs = Time(round), groundMs = Time(ground);
        Console.WriteLine(System.FormattableString.Invariant(
            $"cave under the mouth at {mouth.X},{mouth.Z}: tendril {start}..{end}, box {cave.BoxMinimum}..{cave.BoxMaximum}; tendril wall {smooth}/{solid} smooth; a chunk round the tendril {caveMs:F2} ms, of plain ground {groundMs:F2} ms"));
        Check.That(caveMs < Math.Max(4d, groundMs * 3d), $"a chunk a tendril reaches must cost about what any chunk does ({caveMs:F2} ms against {groundMs:F2} ms)");

        // Residency deep in a cave: the overworld's surface is let go, the cave's chunks kept.
        TerrainResidencyPolicy policy = new(recipe, new TerrainChunkGenerator(recipe));
        TerrainOverlayState state = new(configuration.Seed);
        HashSet<TerrainChunkAddress> shell = [new TerrainChunkAddress(inside.Chunk.X, inside.Chunk.Y, inside.Chunk.Z), new TerrainChunkAddress(inside.Chunk.X + 1, inside.Chunk.Y, inside.Chunk.Z)];
        policy.SetUnderground(new TerrainResidencyPolicy.UndergroundResidency(1, -1, false, shell));
        TerrainResidencyPlan deep = policy.PlanFor(inside.Chunk, state);
        Check.That(deep.Retained.All(shell.Contains) && deep.Retained.Count == shell.Count, "deep in a cave only its chunks must be kept, none of the overworld");
        policy.SetUnderground(new TerrainResidencyPolicy.UndergroundResidency(2, int.MaxValue, true, shell));
        TerrainResidencyPlan above = policy.PlanFor(inside.Chunk, state);
        Check.That(above.Retained.Count > shell.Count * 10 && shell.All(above.Retained.Contains), "near the surface the overworld must be kept again, and the cave's chunks with it");

        // --- lowland cave depth vs. bedrock floor and un-admitted cave chunk caching (#11187) ---
        {
            // The lowest planned box near the origin (the plan keeps every box floor above the bedrock floor, #11220) sits
            // wholly above bedrock, with bedrock intact under it.
            CavePlacement? lowland = recipe.Underground.SystemsNear(0, 0, 4000).SelectMany(p => p.Boxes).MinBy(b => b.BoxMinimum.Y);
            Check.That(lowland is not null, "the default world must have a planned box within 4 km of the origin");
            if (lowland is not null)
            {
                long floorTop = recipe.MinimumMaterialY + GenerationConstants.WorldFloorThickness;
                Check.That(lowland.BoxMinimum.Y >= floorTop && lowland.TendrilMinimum.Y >= floorTop,
                    $"a lowland cave box ({lowland.BoxMinimum.Y}) and tendril ({lowland.TendrilMinimum.Y}) must sit above the bedrock floor ({floorTop})");
                Check.That(recipe.MaterialAt(new VoxelAddress((long)lowland.Corner.X, recipe.MinimumMaterialY, (long)lowland.Corner.Z)) == (ushort)BlockId.Bedrock,
                    "the bedrock floor directly under a lowland cave box must remain intact bedrock");
                ushort between = recipe.MaterialAt(new VoxelAddress((long)lowland.Corner.X, floorTop, (long)lowland.Corner.Z));
                Check.That(between != TerrainConstants.EmptyMaterial && between != (ushort)BlockId.Bedrock,
                    $"solid rock must sit between the bedrock floor and the lowest cave box (found material {between})");
            }

            // CaveUnderground.At must never rewrite or carve bedrock.
            Check.That(recipe.Caves.At(inside, recipe.SurfaceAt(inside.X, inside.Z), recipe.ContinuousHeightAt(inside.X, inside.Z), (ushort)BlockId.Bedrock) is null,
                "CaveUnderground.At must preserve bedrock rather than rewriting it to stone or air");

            // Un-admitted cave box chunks (outside the tendril) stay cacheable; admitting invalidates cacheability and swaps to cave interior; releasing restores both.
            long edge = TerrainConstants.ChunkEdgeLength;
            TerrainChunkAddress? boxOnly = null;
            TerrainChunkAddress? surfaceOverBox = null;
            for (long cx = GridMath.FloorDivide(cave.BoxMinimum.X, edge) + 1; cx < GridMath.FloorDivide(cave.BoxMaximum.X, edge) && (boxOnly is null || surfaceOverBox is null); cx++)
            for (long cz = GridMath.FloorDivide(cave.BoxMinimum.Z, edge) + 1; cz < GridMath.FloorDivide(cave.BoxMaximum.Z, edge) && (boxOnly is null || surfaceOverBox is null); cz++)
            {
                long minCy = GridMath.FloorDivide(cave.BoxMinimum.Y, edge);
                long maxCy = GridMath.FloorDivide(cave.BoxMaximum.Y, edge);
                TerrainChunkAddress deepCandidate = new(cx, minCy + 1, cz);
                if (boxOnly is null && cave.ChunkTouchesBox(deepCandidate) && !cave.ChunkTouchesTendril(deepCandidate))
                {
                    boxOnly = deepCandidate;
                }

                TerrainChunkAddress surfaceCandidate = new(cx, maxCy + 2, cz);
                if (surfaceOverBox is null && !cave.ChunkTouchesBox(surfaceCandidate) && !cave.ChunkTouchesTendril(surfaceCandidate))
                {
                    surfaceOverBox = surfaceCandidate;
                }
            }

            Check.That(boxOnly is not null && surfaceOverBox is not null, "a cave must have both a box-only chunk and a surface chunk over its box outside the tendril");
            if (boxOnly is TerrainChunkAddress boxChunk && surfaceOverBox is TerrainChunkAddress overChunk)
            {
                Check.That(recipe.Caves.Touches(boxChunk), "Touches must still report a box chunk for worker staleness checks");
                Check.That(!recipe.Caves.Touches(overChunk), "Touches must not falsely flag a 3D-disjoint surface chunk above the box");
                Check.That(generator.Cacheable(boxChunk), "an un-admitted cave box chunk outside the tendril must stay cacheable");
                Check.That(generator.Cacheable(overChunk), "a surface chunk over a cave box outside the tendril must stay cacheable");

                TerrainChunk beforeAdmit = generator.Generate(boxChunk, overlay);
                int volume = CavePlacement.SizeX * CavePlacement.SizeY * CavePlacement.SizeZ;
                ushort[] mats = new ushort[volume];
                float[] dens = new float[volume];
                Array.Fill(mats, (ushort)BlockId.CaveLimestone);
                Array.Fill(dens, 1.5f);
                // Carve an air chamber strictly inside boxChunk's canonical voxels.
                int carvedInBox = 0;
                VoxelAddress boxOrigin = boxChunk.Origin;
                for (int z = 2; z < 14; z++)
                for (int y = 2; y < 14; y++)
                for (int x = 2; x < 14; x++)
                {
                    if (cave.InBox(boxOrigin.X + x, boxOrigin.Y + y, boxOrigin.Z + z, out int ci, out int cj, out int ck))
                    {
                        int idx = (((ck * CavePlacement.SizeY) + cj) * CavePlacement.SizeX) + ci;
                        mats[idx] = TerrainConstants.EmptyMaterial;
                        dens[idx] = 0.25f;
                        carvedInBox++;
                    }
                }

                Check.That(carvedInBox > 0, "synthetic cave interior must overlap boxChunk");
                CaveInterior interior = new(CavePlacement.SizeX, CavePlacement.SizeY, CavePlacement.SizeZ, mats, dens);
                recipe.Caves.Admit(cave, interior);
                Check.That(!generator.Cacheable(boxChunk), "admitting a cave must immediately make its box chunks uncacheable");
                Check.That(generator.Cacheable(overChunk), "admitting a cave must leave disjoint surface chunks above it cacheable");

                TerrainChunk whileAdmitted = generator.Generate(boxChunk, overlay);
                int emptyAfterAdmit = 0;
                foreach (ushort m in whileAdmitted.Materials.Span)
                {
                    if (m == TerrainConstants.EmptyMaterial) emptyAfterAdmit++;
                }

                Check.That(emptyAfterAdmit > 0, "admitting a cave must replace box rock with the cave interior");
                recipe.Caves.Release(cave);
                Check.That(generator.Cacheable(boxChunk), "releasing a cave must restore cacheability of its box chunks");
                TerrainChunk afterRelease = generator.Generate(boxChunk, overlay);
                Check.That(afterRelease.Materials.Span.SequenceEqual(beforeAdmit.Materials.Span),
                    "releasing a cave must restore the exact pre-admission overworld chunk");
            }
        }
    }
}
