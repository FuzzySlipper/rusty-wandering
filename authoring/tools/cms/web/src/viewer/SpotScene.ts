import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import type { TraversalSpot } from '../types';

/** The traversal view's layers (#11226). */
export const SPOT_LAYERS = ['rock', 'barely', 'hits', 'meshes', 'walker', 'way', 'player'] as const;
export type SpotLayer = (typeof SPOT_LAYERS)[number];

export interface SpotSceneHandle {
  setLayers: (visible: Record<SpotLayer, boolean>) => void;
  /** Cuts the scene away above a height (metres over the cube's floor), to look inside. */
  setCut: (ceiling: number) => void;
  dispose: () => void;
}

const BACKGROUND = 0x101412;
const FIELD_OF_VIEW = 45;
const CAMERA_BACK = 1.4;
const OVERLAY = { depthTest: false, transparent: true } as const;
const COLOURS = {
  rock: 0x6f7a72,
  barely: 0xe39a3b,
  voxelHit: 0x6fd08c,
  meshHit: 0xe05ad8,
  entityHit: 0x5ab4e0,
  mesh: 0xe05ad8,
  walk: 0xffffff,
  drop: 0x4ea8ff,
  climb: 0xffd166,
  way: 0x4de0c9,
  player: 0xff6b5c,
  foot: 0xffd166,
};
const HIT_SIZE = 0.07;
const WALKER_MARK = 0.12;

/**
 * A spot's 3D dump (craft.cave.dump): the Engine's rock as blocks (its shell), the cells the walker counts solid that are
 * not rock (barely open: the walker stands on them, the collision does not), the collision surface as the ray lattice met
 * it (green on voxels, magenta on static meshes), the static pieces' triangles, the walker's way (white walks, blue drops,
 * yellow climbs), the steering way, the player and the expected climb. Positions are metres from the cube's corner.
 */
export function mountSpot(container: HTMLElement, spot: TraversalSpot): SpotSceneHandle {
  const width = container.clientWidth || 800;
  const height = container.clientHeight || 560;
  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  renderer.setSize(width, height);
  renderer.localClippingEnabled = true;
  container.replaceChildren(renderer.domElement);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(BACKGROUND);
  scene.add(new THREE.HemisphereLight(0xf2eee5, 0x2b332c, 1.5));
  const sun = new THREE.DirectionalLight(0xffffff, 1.4);
  sun.position.set(-20, 40, 30);
  scene.add(sun);

  const n = spot.size;
  const [ox, oy, oz] = spot.origin;
  const local = (p: number[]) => new THREE.Vector3(p[0] - ox, p[1] - oy, p[2] - oz);
  const index = (i: number, j: number, k: number) => (k * n + j) * n + i;
  const inside = (i: number, j: number, k: number) => i >= 0 && j >= 0 && k >= 0 && i < n && j < n && k < n;
  const collides = (m: number) => m !== 0;
  const rock = (i: number, j: number, k: number) => inside(i, j, k) && collides(spot.engine[index(i, j, k)]);
  const ceiling = new THREE.Plane(new THREE.Vector3(0, -1, 0), n);
  const planes = [ceiling];
  const groups = Object.fromEntries(SPOT_LAYERS.map(name => [name, new THREE.Group()])) as Record<SpotLayer, THREE.Group>;
  for (const group of Object.values(groups)) scene.add(group);

  // Blocks: the rock's shell (rock beside open space), and the barely open cells the walker counts solid.
  const block = new THREE.BoxGeometry(1, 1, 1);
  const shell: Array<[number, number, number]> = [];
  const barely: Array<[number, number, number]> = [];
  for (let k = 0; k < n; k++)
    for (let j = 0; j < n; j++)
      for (let i = 0; i < n; i++) {
        const at = index(i, j, k);
        if (rock(i, j, k)) {
          if (!rock(i + 1, j, k) || !rock(i - 1, j, k) || !rock(i, j + 1, k) || !rock(i, j - 1, k) || !rock(i, j, k + 1) || !rock(i, j, k - 1)) shell.push([i, j, k]);
        } else if (spot.openness[at] < spot.openAtLeast) {
          barely.push([i, j, k]);
        }
      }
  const matrix = new THREE.Matrix4();
  const instanced = (cells: Array<[number, number, number]>, material: THREE.Material, scale: number) => {
    const mesh = new THREE.InstancedMesh(block, material, Math.max(1, cells.length));
    mesh.count = cells.length;
    cells.forEach(([i, j, k], c) => mesh.setMatrixAt(c, matrix.makeScale(scale, scale, scale).setPosition(i + 0.5, j + 0.5, k + 0.5)));
    return mesh;
  };
  groups.rock.add(instanced(shell, new THREE.MeshLambertMaterial({ color: COLOURS.rock, transparent: true, opacity: 0.35, depthWrite: false, clippingPlanes: planes }), 0.98));
  groups.barely.add(instanced(barely, new THREE.MeshLambertMaterial({ color: COLOURS.barely, transparent: true, opacity: 0.45, depthWrite: false, clippingPlanes: planes }), 0.9));

  // The collision surface as the rays met it.
  const hitColours = [0, COLOURS.voxelHit, COLOURS.meshHit, COLOURS.entityHit];
  const hitPositions: number[] = [];
  const hitColourList: number[] = [];
  const colour = new THREE.Color();
  for (const hit of spot.hits) {
    const p = local(hit);
    hitPositions.push(p.x, p.y, p.z);
    colour.setHex(hitColours[hit[6]] ?? 0xffffff);
    hitColourList.push(colour.r, colour.g, colour.b);
  }
  const hitGeometry = new THREE.BufferGeometry();
  hitGeometry.setAttribute('position', new THREE.Float32BufferAttribute(hitPositions, 3));
  hitGeometry.setAttribute('color', new THREE.Float32BufferAttribute(hitColourList, 3));
  groups.hits.add(new THREE.Points(hitGeometry, new THREE.PointsMaterial({ size: HIT_SIZE, vertexColors: true, clippingPlanes: planes })));

  // The static pieces, as the product composed them.
  for (const piece of spot.meshes) {
    const positions: number[] = [];
    for (const t of piece.triangles) for (let c = 0; c < 3; c++) positions.push(t[c][0] - ox, t[c][1] - oy, t[c][2] - oz);
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
    groups.meshes.add(new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ color: COLOURS.mesh, wireframe: true, clippingPlanes: planes })));
  }

  // The walker's way (its feet at each cell), coloured by how each cell is reached, and the steering way.
  const moveColours = [COLOURS.walk, COLOURS.drop, COLOURS.climb];
  const mark = new THREE.SphereGeometry(WALKER_MARK, 8, 6);
  for (let w = 0; w < spot.walker.length; w++) {
    const cell = spot.walker[w];
    const material = new THREE.MeshBasicMaterial({ color: moveColours[cell[3]] ?? COLOURS.walk, ...OVERLAY });
    const m = new THREE.Mesh(mark, material);
    m.position.copy(local(cell));
    groups.walker.add(m);
    if (w > 0 && spot.walker[w - 1][4] === cell[4] - 1) {
      const line = new THREE.BufferGeometry().setFromPoints([local(spot.walker[w - 1]), local(cell)]);
      groups.walker.add(new THREE.Line(line, new THREE.LineBasicMaterial({ color: moveColours[cell[3]] ?? COLOURS.walk, ...OVERLAY })));
    }
  }
  if (spot.way.length > 1) {
    const geometry = new THREE.BufferGeometry().setFromPoints(spot.way.map(local));
    groups.way.add(new THREE.Line(geometry, new THREE.LineDashedMaterial({ color: COLOURS.way, dashSize: 0.4, gapSize: 0.25, ...OVERLAY })).computeLineDistances());
  }

  // The player: a capsule at their feet, an arrow along their heading; the walker's expected climb: its foot and ledge.
  const p = spot.player;
  const body = new THREE.Mesh(new THREE.CapsuleGeometry(p.radius, p.height - 2 * p.radius, 4, 12),
    new THREE.MeshLambertMaterial({ color: COLOURS.player, transparent: true, opacity: 0.7 }));
  body.position.copy(local(p.feet)).add(new THREE.Vector3(0, p.height / 2, 0));
  groups.player.add(body);
  const r = (p.heading * Math.PI) / 180;
  groups.player.add(new THREE.ArrowHelper(new THREE.Vector3(Math.sin(r), 0, -Math.cos(r)), local(p.feet).add(new THREE.Vector3(0, 1.4, 0)), 1.2, COLOURS.player));
  if (spot.climb) {
    for (const cell of [spot.climb.foot, spot.climb.ledge]) {
      const box = new THREE.LineSegments(new THREE.EdgesGeometry(new THREE.BoxGeometry(1, 2, 1)), new THREE.LineBasicMaterial({ color: COLOURS.foot, ...OVERLAY }));
      box.position.set(cell[0] - ox + 0.5, cell[1] - oy + 1, cell[2] - oz + 0.5);
      groups.player.add(box);
    }
  }

  const camera = new THREE.PerspectiveCamera(FIELD_OF_VIEW, width / height, 0.05, 500);
  const centre = local(p.feet).add(new THREE.Vector3(0, 1, 0));
  camera.position.copy(centre).add(new THREE.Vector3(0.7, 0.6, 1).normalize().multiplyScalar(n * CAMERA_BACK * 0.6));
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.target.copy(centre);
  controls.update();
  let frame = 0;
  const render = () => {
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      renderer.render(scene, camera);
    });
  };
  controls.addEventListener('change', render);
  render();
  const resize = new ResizeObserver(() => {
    const w = container.clientWidth;
    const h = container.clientHeight;
    if (!w || !h) return;
    renderer.setSize(w, h);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    render();
  });
  resize.observe(container);

  return {
    setLayers(visible) {
      for (const name of SPOT_LAYERS) groups[name].visible = visible[name] !== false;
      render();
    },
    setCut(top) {
      ceiling.constant = top;
      render();
    },
    dispose() {
      cancelAnimationFrame(frame);
      controls.removeEventListener('change', render);
      resize.disconnect();
      controls.dispose();
      scene.traverse(object => {
        if (object instanceof THREE.Mesh || object instanceof THREE.Line || object instanceof THREE.Points) {
          object.geometry.dispose();
          (Array.isArray(object.material) ? object.material : [object.material]).forEach(material => material.dispose());
        }
      });
      renderer.dispose();
    },
  };
}
