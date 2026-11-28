import { useEffect, useRef, useState } from 'preact/hooks';
import { apiRequest, isAbortError } from '../api';
import { traversalUrl } from '../router';
import type { TraversalSpot, TraversalSpotRecord, TraversalSpotRow } from '../types';
import { Breadcrumb, Empty, FactList, JsonView, PageHeading } from '../components/ui';
import type { SpotLayer, SpotSceneHandle } from '../viewer/SpotScene';

const LAYERS: Array<[SpotLayer, string, string]> = [
  ['rock', 'Rock (the Engine’s voxels)', '#6f7a72'],
  ['barely', 'Barely open: the walker’s floor, open to the collision', '#e39a3b'],
  ['hits', 'Collision as rays meet it (green voxel, magenta mesh)', '#6fd08c'],
  ['meshes', 'Static pieces', '#e05ad8'],
  ['walker', 'Walker’s way (white walk, blue drop, yellow climb)', '#ffffff'],
  ['way', 'Steering way', '#4de0c9'],
  ['player', 'Player and expected climb', '#ff6b5c'],
];

const CUT_OVER_HEAD = 3;

/** The stalls the live walk lane recorded (#11226): each spot's report and its 3D dump. */
export function TraversalPage() {
  const [rows, setRows] = useState<{ root: string; spots: TraversalSpotRow[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    apiRequest<{ root: string; spots: TraversalSpotRow[] }>('/api/traversal').then(setRows).catch(err => setError(String(err)));
  }, []);
  if (error) return <Empty>{error}</Empty>;
  if (!rows) return <p class="muted">Loading traversal spots…</p>;
  return (
    <>
      <PageHeading
        title="Traversal spots"
        intro={
          <>
            Where a live walk stalled, as <code>node scripts/live.mjs cavewalk</code> recorded it in <code>{rows.root}</code>: the report from{' '}
            <code>craft.cave.why</code> and the spot in three dimensions from <code>craft.cave.dump</code>.
          </>
        }
      />
      {rows.spots.length === 0 ? (
        <Empty>No spots recorded yet.</Empty>
      ) : (
        <section class="panel">
          <table>
            <thead>
              <tr>
                <th>Spot</th>
                <th>Way</th>
                <th>Along</th>
                <th>Verdict</th>
              </tr>
            </thead>
            <tbody>
              {rows.spots.map(row => (
                <tr key={row.name}>
                  <td>
                    <a href={traversalUrl(row.name)}>{row.name}</a>
                  </td>
                  <td>{row.from !== undefined ? `M${row.from} → M${row.to}` : '-'}</td>
                  <td>{row.along !== undefined ? `${row.along} / ${row.wayLength} m` : '-'}</td>
                  <td>{row.verdict ?? '-'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </>
  );
}

function SpotViewer3D({ spot, name }: { spot: TraversalSpot; name: string }) {
  const host = useRef<HTMLDivElement>(null);
  const handle = useRef<SpotSceneHandle | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [layers, setLayers] = useState<Record<SpotLayer, boolean>>({ rock: true, barely: true, hits: false, meshes: true, walker: true, way: true, player: true });
  // The cut starts a little over the player's head, so the spot opens to the view.
  const [ceiling, setCeiling] = useState(Math.min(spot.size, Math.round(spot.player.feet[1] - spot.origin[1] + CUT_OVER_HEAD)));
  useEffect(() => {
    let cancelled = false;
    void import('../viewer/SpotScene')
      .then(module => (host.current ? module.mountSpot(host.current, spot) : null))
      .then(scene => {
        if (!scene) return;
        if (cancelled) return scene.dispose();
        handle.current = scene;
        scene.setLayers(layers);
        scene.setCut(ceiling);
      })
      .catch(err => setError(err instanceof Error ? err.message : String(err)));
    return () => {
      cancelled = true;
      handle.current?.dispose();
      handle.current = null;
    };
  }, [name]);
  useEffect(() => handle.current?.setLayers(layers), [layers]);
  useEffect(() => handle.current?.setCut(ceiling), [ceiling]);
  if (error) return <Empty>{error}</Empty>;
  return (
    <div class="cave-3d">
      <div class="cave-3d-controls">
        {LAYERS.map(([key, label, colour]) => (
          <label class="toggle" key={key}>
            <input type="checkbox" checked={layers[key]} onChange={event => setLayers({ ...layers, [key]: (event.currentTarget as HTMLInputElement).checked })} />
            <i style={{ background: colour }} /> {label}
          </label>
        ))}
        <label>
          Cut away above {(spot.origin[1] + ceiling).toFixed(0)} m
          <input type="range" min={1} max={spot.size} step={0.5} value={ceiling} onInput={event => setCeiling(Number((event.currentTarget as HTMLInputElement).value))} />
        </label>
      </div>
      <div class="cave-3d-canvas" ref={host} />
    </div>
  );
}

/** One recorded spot: what the walk gave up on, the report, and the spot in three dimensions. */
export function TraversalSpotPage({ name }: { name: string }) {
  const [record, setRecord] = useState<TraversalSpotRecord | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setRecord(null);
    apiRequest<TraversalSpotRecord>(`/api/traversal/${encodeURIComponent(name)}`, { signal: controller.signal })
      .then(setRecord)
      .catch(err => {
        if (!isAbortError(err)) setError(err instanceof Error ? err.message : String(err));
      });
    return () => controller.abort();
  }, [name]);
  if (error) return <Empty>{error}</Empty>;
  if (!record) return <p class="muted">Loading {name}…</p>;
  const s = record.spot;
  const verdict = (record.why || s.report).split('\n').find(line => line.startsWith('verdict: '))?.slice('verdict: '.length);
  return (
    <>
      <Breadcrumb href={traversalUrl()} label="Traversal spots" />
      <PageHeading title={name} intro={verdict ? `Verdict: ${verdict}` : undefined} />
      <section class="panel">
        <FactList
          facts={{
            Feet: s.feet.map(v => v.toFixed(2)).join(', '),
            Heading: `${s.heading.toFixed(0)}°`,
            Cube: `${s.size} m from ${s.origin.join(', ')}`,
            'Collision hits': s.hits.length,
            'Static pieces': s.meshes.length,
            'Walker cells': s.walker.length,
          }}
        />
      </section>
      <section class="panel">
        <h2>In three dimensions</h2>
        <SpotViewer3D spot={s} name={name} />
      </section>
      <section class="panel">
        <h2>Why here</h2>
        <pre class="report">{record.why || s.report}</pre>
      </section>
      {record.stall ? (
        <section class="panel">
          <details>
            <summary>The stall record</summary>
            <JsonView value={{ ...record.stall, why: undefined }} />
          </details>
        </section>
      ) : null}
    </>
  );
}
