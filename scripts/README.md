# Scripts

| Script | What it is for |
| --- | --- |
| `check-product.sh` | CI's check, here or on CI's runner (below) |
| `ci-serve-product.sh` | Serves the product and walks it a short way; used by `check-product.sh` |
| `live.mjs` | The live-lane client for a running host (below) |
| `check-ui.mjs`, `ui-tsc.mjs` | `pnpm run check:ui`: type-checks the DOM companion against the pinned pair |
| `check-terrain-atlas.mjs` | `pnpm run audit:textures`: audits the terrain texture atlas |

## Live proofs and evidence lanes

Two lanes prove different things. The **managed lanes** (`tests/`) run in CI and cover product
rules and SDK values; the focused collision lanes use the Engine's packaged
headless test host. The **live lane** runs the real product against the
installed runtime pack and is the only lane that can prove host-bound mechanisms, rendering and
load paths. A build passing is not evidence about the live lane.

CI is `scripts/check-product.sh`: the verify workflow runs `scripts/check-product.sh --managed` on
den-k8's self-hosted runner, and anyone can run the same command on a den machine. From a clean
checkout of one commit it builds and stages the product, audits the terrain textures, type-checks the
DOM companion, runs every managed lane (with `--managed`), and makes the one automated live check:
it serves the product on llvmpipe alone, checks it keeps updating with a matching generator
fingerprint, and walks the player a short way on input with the client below. Every step of that
walk draws a software-rendered frame, so the check walks 12 steps at a 1 m bar and the longer
default is for a machine with a GPU. The engine-pair workflow runs the same check on its proposed
pin, committed locally, before it pushes that commit; `--propose` tries a pair the same way by hand,
without committing the pin.

The live substrate proof that established the Engine constraints this product builds on left
with the authoring lane; its record is Den `history/live-substrate-proof`.
The product map and the known limitations (Den `product-map` and `known-limitations`)
describe the implementation contracts that a new proof must exercise.

### Operational traps

- **An unwatched stream pauses the entire product.** The product reads Engine
  `Watching` once per update and holds Engine gameplay time when false. Clock,
  survival, creatures, streaming and saving all wait; an attached page resumes
  them. Debug commands and input claims are not watchers. Use `--headless` for
  unattended live checks so its browser page watches. A playtest session already
  supplies a page; do not add another watcher when testing detachment.
- **A product fault stops the world.** An exception that escapes a product callback leaves the
  runtime `state=Faulted` (`craft.runtime`) and prints one line to the host's stderr; start the
  host with `--diagnostics-log <file>` for the full exception and its stack.
- **A stale staged product produces misleading catalog errors.** After changing authored catalog
  code, a previously staged build under `src/CraftSurvive.Game/obj/Rusty.Engine/Product/` can
  still be what runs. If an error does not match the source, remove that directory to force a
  clean restage.
- **A killed run can leave staging half-done.** Killing `rusty dev` mid-flight occasionally makes
  the next start fail to stage its content. It is a race, not a code problem: start again.
- **A stale save is discarded, not fatal.** A save written for another seed or generator version
  is discarded, kept as that key's backup, and the readouts say so (`restore=discarded: ...`).
  `craft.save.manifest` lists every key and whether it and its backup are stored.
- **`rusty dev` restarts a crashed worker twice, then stops.** A product that throws during
  construction appears as `child-exited-unexpectedly` twice and then
  `paused-fault: unexpected-child-exit-budget-exhausted`. Read the first `CSHARP_PRODUCT_CALL`
  line, not the last.
- **Persistence lives in the working tree.** `rusty dev` keeps the product's store under
  `.runtime/persistence`; a live experiment that edits the world changes the saved world. Copy
  that directory aside first when the edits should not stay.

## The live-lane client

`scripts/live.mjs` is the one client for a running product. It needs a host started with
`--live-debug` (as `.den-serve.json` starts it) and takes the host's address from the
environment, never from the script:

```sh
rusty dev --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj --headless --live-debug --bind-host 127.0.0.1 --port <port>
export CRAFT_ORIGIN=http://127.0.0.1:<port>   # or CRAFT_PORT=<port>
```

```sh
node scripts/live.mjs walk --steps 60
```

Holds time (the Engine's manual mode) and walks the player with the forward control over that
many simulation steps (60 a second), then puts time back as it was. It prints the distance, the
steps and product updates it ran, the positions before and after, and how many key events the
product received. Because the steps are counted, not timed, a slow machine walks as far as a fast
one (about 7 m for 60 steps on level ground). It fails unless the player moved at least
`--minimum` metres (default 4) *and* the product received the key events, so the distance is
attributable to input. Other actions: `--action walk-back|strafe-left|strafe-right`.

```sh
node scripts/live.mjs discovery
```

Stands the player at the nearest unvisited standing stones and checks the journal counted a first
visit, stored itself at 32 + 51 bytes per place, and reports what its restore did (`--kind` picks
another kind of place).

```sh
node scripts/live.mjs exec <debug command>
```

Runs any debug command, including the Engine's playtest commands the product registers:
`playtest.observe` (the player's feet, look, vitals and received key events, as JSON),
`playtest.action <id>` (which physical control an action is) and `playtest.look <yaw> <pitch>`.

**The `craft.*` commands are developer tooling, never part of play.** Everything in the core
loop has a player path: moving, climbing and swimming on keys; fighting on the attack key;
crafting, eating, resting, building, blasting, choosing a difficulty and going in and out of
dungeons and the world map from the HUD's actions. A command that does one of these (`craft.player.attack`,
`craft.inventory.craft`/`use`, `craft.dungeon.enter`/`leave`, `craft.build.*`,
`craft.blast.fire`, `craft.world.difficulty`, `craft.world.view`) is a scripted stand-in for that path. The rest
are for the lane:
- reading an owner's state (each family's `readout`, `craft.terrain.*`, `craft.runtime`,
  `craft.save.manifest`, `craft.discovery.near`/`find`/`crossings`, `craft.feedback.readout`,
  `craft.cave.readout`: the caves under the world's cave mouths, how deep the player is and what
  is resident, cave and overworld);
- setting up a live check (`craft.player.teleport`/`strike`, `craft.cave.resume`, `craft.survival.set`,
  `craft.world.hour`, `craft.inventory.grant`, `craft.creatures.restart`,
  `craft.dungeon.visit`, `craft.feedback.cue`, `craft.build.dig`, `craft.cave.goto` then
  `craft.cave.walk <metres>`: outside the nearest cave mouth, then on down its tendril and the
  cave's main route as a walk would go, negative back out);
- comparing generators and measuring costs (`craft.dungeon.approach`/`surface`/`seed`/
  `validate`/`blast`, `craft.creatures.navigation`, `craft.terrain.timing`, `craft.updates`
  `[reset]`: the walking updates' median, p99, worst and how many went over a 60 Hz frame).

A command earns its place by serving one of these; one whose experiment has ended is retired
with it.

**Testing in the caves under the world (#10924).**

A cave under a mouth is generated in the background when the player comes near, then kept by the cave store; the
session's wildlife ledger keeps each cave's home records even after the cave is let go. A study switch such as
`craft.dungeon.veilforest 1` therefore does nothing for a cave already asked for (a restored player position asks
for it on startup), and every build regenerates caves without the switches. Use the aids instead:

```sh
node scripts/live.mjs exec craft.cave.fresh veilforest,glasssteppe   # regenerate the nearest cave now, afresh
node scripts/live.mjs exec craft.cave.readout                        # wait for ready=1; communities=[...]
node scripts/live.mjs exec craft.cave.walk 110                       # into the cave; its animals wake as homes
node scripts/live.mjs exec craft.wildlife.caves                      # the homes it seeded
node scripts/live.mjs exec craft.cave.stand draft-hound 4.5 downwind # stand on its floor, facing the home
node scripts/live.mjs capture --until 'draft-hound.*shows=Search' --out /tmp/search.rscene --keep ward --render /tmp/search.png
```

- `craft.cave.fresh <communities>` stands the player outside the nearest mouth with a cave under it, lets the cave
  go, forgets its animals (hunters, home records, small creatures) and asks for it again with those communities
  grown (`none`, `auto`, or names such as `veilforest,glasssteppe`). It never reads the store or the generator's kept
  copy and is not stored, so it holds across builds and restarts.
- `craft.cave.stand <home key | kind> <metres> <upwind | downwind | degrees | any>` stands the player where a body
  fits on that home's own floor, about that far at that bearing (upwind and downwind of a home resting into a draft),
  facing it, and says how many spots had no floor, were off the home's level or had no room.
- **Grids per body size (#10925).** Each body capsule routes on its own navigation grid in a space (`grid=N` in the
  readouts), published about the player while one of its size routes. Of grids published before, one republishes per
  update. `craft.creatures.routing read` gives each grid's queries, failures and publication cost, and the creatures
  readout's `navigation=[...]` lists them.
- `craft.creatures.why <all | id | homes>` says why creatures cannot route, each on its own grid:
  - outside the published box (32 m about where the player stood);
  - how the creature's column reads at its feet (a support, a capsule overlapping collision and with what, too steep,
    nothing) and the nearest support on its level;
  - for a creature on a grid other than the player's, how the player's footing reads on its grid (a large body may not
    fit where the player stands).
  `homes` flags each held cave home no route could start from as `UNROUTABLE`, with the floor cells about it it could
  move to, and one the box does not reach as `unread`.
- **Cave homes.** `craft.wildlife.caves` shows each home's `route=` as the creatures read it on their own once its
  grid's box covers it: `ok`, `UNROUTABLE (why)` or `pending (why)`. A home on floor its animals cannot route from
  moves once, up to 8 m, to the nearest floor cell that reaches the floor about it (not a pocket), and says
  `moved=<metres>`.
- **Settling and footing.** A creature whose start its grid refuses is settled on the nearest supported cell within
  2 m (at most once in ten seconds), or walks to its footing. A route whose goal the player's footing refuses goes to
  the cell beside them. The creatures readout counts `settled`, `walkedToSupport`, `goalsBeside`, `resitedHomes` and
  `unroutableHomes`.
- `node scripts/live.mjs capture --until <regex> --out <file.rscene>` polls a readout (`--readout`, by default
  `craft.creatures.readout`) until the pattern matches and snapshots the renderer at once, so a short state (a
  search, a display, a bite) is caught. `--keep ward|heal` keeps the player standing meanwhile; `--hold yes` (the
  default) holds time from the match until `--render <file.png>` has drawn the frame with the pair's
  `rusty-scene-render`.
- `craft.creatures.unseen <seconds>` hides the player from every creature, so hunters lose them where they stand and
  search there by the player's own light; `craft.creatures.rouse` wakes every creature to the player.

**Traversal diagnostics: resume at a stall, and why a spot fails (#11226).**

When a walk keeps failing at the same place, do not walk there again from the start: resume at the spot and read it
up close. The planned-system walk and its stall handling are one lane command:

```sh
node scripts/live.mjs cavewalk --from 4 --to 3                       # M4 to M3 of ug:fine:-2:-8:3 on held input
node scripts/live.mjs cavewalk --resume <dir>/stall.json --back 4    # again from 4 m before that stall
```

- `cavewalk` makes the golden-seed world (`--world 19:390000`) if it is not the one restored, holds the planned
  system, lays the walker's way (`craft.cave.plan way:<from>:<to>`) and walks it on held input in real time, with the
  product steering the view (`craft.cave.steer in`). It jumps at a lip and takes hold to climb when stopped, as a
  player would.
- A walk that makes no progress for `--stuck` seconds (default 25) gives up. It writes three files to `--out`
  (default `authoring/assets/traversal-spots/M<from>-M<to>-<time>/`, ignored), then exits with 2:
  - `stall.json`: the distance along the way, the feet and heading, the jumps and climbs tried, the way itself
    (`craft.cave.way`: the steering points, the walker's cells and moves) and the walker's expectation at the stall
    (its cell, move, standing cell, next climb's foot, ledge, axis and pitch);
  - `why.txt`: the report below;
  - `spot.json`: the 3D dump.
- `--resume <stall.json>` asks for the stall's own box first and holds the player at the spot until it is ready.
  It then lays the way again and compares it with the record's: where the walker's cells and the steering points
  match, it places the player `--back` metres before the spot along it; where they differ, it says how and resumes at
  the record's feet. It prints the walker's expectation at the stall then and now, and walks on. On a host with the world
  already restored but no cave asked for, the stall reproduces within a minute; a retry on a warm host takes about
  20 s, most of it re-laying the walker's way.
- The CMS's **Traversal spots** page (`python3 authoring/tools/cms/server.py`) lists the recorded spots with their
  verdicts. A spot's page shows its report and the 17 m cube in 3D:
  - the Engine's rock;
  - the cells the walker counts solid but are not rock (barely open: the walker stands on them, the collision does not);
  - the collision surface as a ray lattice meets it, green on voxels and magenta on static meshes;
  - the static pieces' triangles;
  - the walker's way, coloured by move, and the steering way;
  - the player's capsule and heading, and the expected climb's foot and ledge.

The commands underneath, for a spot anywhere (`here`, `along:<metres>` on the laid way, or `<x>,<y>,<z>,<heading>`:
feet in world metres, degrees with 0 facing -z):

- `craft.cave.resume <spot> | status` holds the player at the spot until it is ready, then places and faces them
  (`ready=True`). The spot is ready once the box holding it is generated, admitted and dressed (its kit's collision
  joined) and the 27 chunks about it are resident. Steering stops; `craft.cave.steer in` walks on.
- `craft.cave.why <spot>` reads the spot as the offline walker (`CaveWalker`) expects it and as the live controller
  answers, and lists where they disagree:
  - the walker's nearest cell and moves about it;
  - the way on, cell by cell: the collision floor under each walker floor and the rise from the cell before,
    against the controller's 1.05 m step and 1.51 m jump;
  - the next climb, its conditions re-read on the cells now;
  - the face column as the walker, the terrain and the Engine hold it;
  - a standing capsule at the expected stand and at the ledge (Engine overlap contacts);
  - looks at the face along the climb's axis;
  - the climb rule the walking world uses (`PlayerClimb.Find` on the Engine's voxels), from the expected stand and
    from the player on all four axes, beside `climbprobe`'s natural read (used only in separate spaces);
  - the static collision about it, and any ids it names twice.

  It ends with a verdict: steering, model (the walker's rules and the controller's disagree) or shape. It takes about
  a second.
- `craft.cave.dump <spot>` is the same spot as JSON, the CMS's `spot.json`.
- `craft.cave.way <spot | ->` is the laid way as JSON with the walker's expectation at a spot: the stall record's way.
- `craft.cave.plan goto:box:<box>` stands the player on the floor of any box of the held planned system, through the
  same hold. `craft.cave.voxel x,y,z` reads a voxel in any ready box (planned or landmark), with the Engine's block and
  residency. `craft.cave.profile <from>-<to>` follows a planned system's laid way too.
- The report reads the collision through rays and capsule overlaps, and the static pieces from the product's own
  composition (`TerrainWorld.LastStaticCollision`). **The voxel collision mesh is not in the dump:** the SDK has no
  readout of the Engine's voxel collision triangles. The upstream capability it needs is a bounded collision-geometry
  readout on `ISpatialService` (a session and a box in, the resident collision triangles in it out, each with its
  source: voxel chunk and its address, or static instance and asset). Until that exists, or Patch accepts the
  stand-in, the dump's voxel surface is the ray lattice's hits, a sample that keeps no triangle connectivity.

**Spawning, inspecting and overriding one creature (#10926).**

```sh
node scripts/live.mjs exec craft.creature.spawn draft-hound 1 ahead:12 rest   # any hunter or herd kind; returns ids
node scripts/live.mjs exec craft.creature.select looked-at                    # or an id, herd:<id>, nearest, nearest:<kind>
node scripts/live.mjs exec craft.creature.inspect                             # state, perception, route, body, animation
node scripts/live.mjs exec craft.creature.watch 60                            # log its changes; then: watch read
node scripts/live.mjs exec craft.creature.force state search                  # or clip/goto/freeze/health; release -
node scripts/live.mjs exec craft.creature.labels all                          # with engine.inspect.overlay show
node scripts/live.mjs frame --out /tmp/labels.png                             # the live frame, labels included
```

- `craft.creature.spawn <kind> <count> <where> <start>` places creatures outside any population: `where` is `here`,
  `ahead:<metres>`, `at:<x>,<y>,<z>` (on the floor under that point, in caves too) or `home:<cave home key>`. A hunter
  starts `default`, `rest`, `idle` or `hunt`. A herd starts `default`, `rest`, `idle` (grazing in place) or `flee`
  (running from the player now), on the floor near the point's height, cave floors included. Herd animals are named
  `herd:<id>`: their ids are the wildlife's own.
- `craft.creature.select` picks the one creature, hunter or herd animal, the other commands act on, and says when it
  is gone. `inspect` reads
  it whole, including the clip it wants and why and what its mesh plays. `watch <seconds>` logs each change of state,
  clip, route, sight, alarm, health or wakefulness with the update step and the facts then, and how it went (killed by
  the player, or released).
- `craft.creature.force <what> <value>` holds the selected creature until `release -`: `state` (a hunter's
  `rest|idle|alert|pursue|search|attack`, a herd animal's `rest|idle|look|warn|flee`), `clip`, `goto <x>,<z>`, `freeze -`
  (no rules, no move, its playback paused and pose held; not woken by a rouse, a pack alarm or its herd's alarm) or
  `health <n>`. Pass `-` where no value is needed: debug commands take no defaults.
- Labels ride the Engine's inspection overlay, which `.rscene` snapshot renders do not carry: save them with
  `live.mjs frame --out` or `capture --frame <file.png>`, which take the live frame with its overlays. Labels of
  creatures behind one another overlap.

**Vegetation reach and forests.**

Ground cover grows on the voxels to 40 m, then on two Engine vegetation tile fields out to the
Ground cover option's reach, thinning with distance; trees are Engine vegetation records with a far
mesh and an impostor level, out to a reach that follows the view distance. `craft.vegetation.readout`
reports both, with each field's tiles, pending tiles, copies and over-budget layers; before judging a
view, wait for `pendingAdmissions=0` and `settled=True` on the tiles. `craft.terrain.groundcover <m>`
and `craft.terrain.fartrees <m>` set the reaches for the session. `engine.renderer` reports the
renderer's per-copy selection (`gpu.scatterSelection`), impostors and pass timings; a stream's frame
rate includes capture and delivery, so it is not a gameplay FPS measurement.

`engine.renderer.camera` takes scene-local coordinates: a long teleport that rebases the world origin
moves every pinned camera, so take fixed-pose captures in a session that has not travelled far.

Forests come from the flora catalog (`content/game/flora/catalog.json`): each species' climate range
sets where the canopy closes, with forest floor under it, stands of mostly one species, sparse
open-country trees. On the ground beside them (`plantTiles` and `floorTiles` in the readout), every plant
species grows on its own, so ferns, woodland flowers, shrubs, saplings and deadwood overlap. These are
Engine vegetation records: shrubs, deadwood and saplings to 160 m, ground plants to 112 m, and shade
grass on the forest floor to 72 m. Fallen logs and stumps are solid near the player:
`craft.vegetation.deadwood <log|stump|fallen-trunk|any> <x> <z>` lists the nearest, and `craft.vegetation.solid off|on` compares
walking the same path over a log without and with them (the player's height rises over the log only
when solid). Grass is blades (#10038): seeded clumps of tapered blades near (to 40 m) and a lighter clump in the mid tiles (to
112 m), with painted cards beyond. `craft.terrain.grass cards|blades` switches it everywhere it grows to compare at a
held pose. A fallen trunk blocks: walk straight at one (`craft.vegetation.deadwood fallen-trunk <x> <z>`)
and the player slides along it, then jump it. `craft.vegetation.sites <x> <z>` finds closed forest near a point with its trees per 128 m
square, and `craft.terrain.trees <species> <x> <z>` the nearest trees of one species. The focused check
is `dotnet run --project tests/TerrainResidency -c Release -- forest`. Compare a forest interior, its
edge, open country and a raised vista; a stand should read as one species with others mixed in.

**Water appearance comparison.**

At a river or lake, `craft.water.visual waves` (the default) draws the sea state's waves (#9902);
`flow` draws the same flowing surface without them, and `baseline` the Engine's water alone, at the
same camera pose and held time. The waves follow the weather's wind; `craft.water.wind <m/s>` holds a
wind (2 calm, 9 a fresh breeze, 20 a gale; a negative speed follows the weather again), and
`craft.water.waves.at <x> <z>` reads a column's fetch, swell share, shore coverage, significant
height in the wind now, and how far the drawn surface stands over the rest level. Compare a calm
pond, a great lake in a gale (whitecaps on the downwind faces), and a river in the same gale (only a
chop). `craft.water.lakes <x> <z> <count>` lists the nearest lakes with the waves a gale raises on each.

Boats and swimmers ride what is drawn: `craft.boat.readout` gives the boat's pitch, roll, heave and the
waves' lift under it. Board in deep water in a gale and paddle: the player stays aboard, rising and
falling with the hull. Shore coverage fades both opacity and displacement into the bed, and motion
fades across the last 24 m of the near-water square to meet the flat distant water. Ice stays still.

The waves are shader displacement on the product's clock, separate from the rest level used for
depth, fords and wading. The installed shader hook returns positions only, so lighting normals come
from the displaced 1 m triangles: look for faceting in highlights and in whitecaps close to the
camera. Culling still uses the undisplaced mesh bounds, so the waves are capped to stay within a metre.

Floods (#9880): `craft.water.floods <x> <z>` gives the area under water now and the flooded floodplain
nodes nearest a point (level, depth over the lattice's ground, reach). Stand on a bank beside one and look
at the water in the river's valley; on the map, a walk across it stops at Floodwater.
`MapLab --floods SEED --size METRES [--from-day D --days N]` draws the worst flood of a stretch of the year
from before the storm to the plain draining.

`craft.water.visual velocity` displays the mesh's current: red increases toward +X, green
toward +Z, and blue means slow flow. Resume time to judge motion as well as the still image.

`craft.water.flow <world-x> <world-z>` starts a 128 m Engine WaterField over the generated bed,
with fixed stages on wet boundary cells. Its sampled current drives the appearance, blending
back to regional current near the edge. This study does not change water levels, swimming,
boats or terrain; it is a visual consumer of the field. `craft.water.flow.at <x> <z>` compares
field depth/current with the regional current, and `craft.water.readout` includes snapshot age
and worker cost. Check movement both inside the field and across its edge.
`craft.water.flow.stop` restores regional flow. The study's bed and boundary stages are captured
when started; restart it after terrain or river-stage changes. Compare in the overworld:
separate landscape studies and distant map water use their own materials.

**How input reaches the product.** `walk` does not use a page. It claims input through the
host's harness lane (`control/claim`, `runtime/input`, `control/release`), which delivers key facts to
the product's ordinary mappings exactly as a page would; an attached page shows the input as held
by `craft-live` meanwhile.

**Keyboard input from a page reaches the product only once the Engine canvas has gameplay
focus.** Measured with a headless page: with the page just loaded (focus on the document body),
or with a product UI button focused, holding W for 1.5 s delivered no key events and moved the
player 0 m; after one click on the canvas the same hold delivered 2 key events and moved the
player about 11 m. A page-driven lane must click the canvas (not the UI panel) before sending
keys. This is why earlier scripted walks recorded no key events.

**A body placed inside the world stops the product.** The character controller refuses to step a
body that starts inside a solid cell, and that refusal faults the product. `craft.player.teleport`
therefore moves the player only where a standing body fits - the point asked for, else the ground
of that column - and building refuses cells the player's body occupies.

## Browser captures through the playtest lane

Screenshots come from the crew-services playtest lane, which serves the product from
`.den-serve.json` and drives a page from `.den-playwright.json`. Do not start a competing host on a
broker-owned session's port merely to look at it.

```sh
playtest start rusty-craftsurvive     # returns SESSION and a first screenshot
playtest observe SESSION              # the current frame
playtest capture SESSION --json '{}'  # a retained capture
playtest stop SESSION
```

A page-driven check must click the world before sending keys (see above). Retained captures
go under `docs/evidence/<task>/` while the task is live; each names the pair and machine it was taken on in Den.
