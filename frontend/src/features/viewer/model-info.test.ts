import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import { buildCubeGlb, CUBE_TRIANGLE_COUNT, CUBE_VERTEX_COUNT } from '../../test/glbfactory';
import { parseGlb } from './glb-loader';
import { computeModelInfo } from './model-info';

/**
 * A unit cube built directly (no GLB involved): 6 faces x 4 vertices =
 * 24 vertices, 12 triangles, uv attribute when withUv.
 */
function cubeGeometry(withUv = true): THREE.BufferGeometry {
  // Unit cube, one quad per face (24 vertices, 12 triangles).
  const pos = new Float32Array([
    -0.5,
    -0.5,
    0.5,
    0.5,
    -0.5,
    0.5,
    0.5,
    0.5,
    0.5,
    -0.5,
    0.5,
    0.5, // front
    0.5,
    -0.5,
    -0.5,
    -0.5,
    -0.5,
    -0.5,
    -0.5,
    0.5,
    -0.5,
    0.5,
    0.5,
    -0.5, // back
    0.5,
    -0.5,
    0.5,
    0.5,
    -0.5,
    -0.5,
    0.5,
    0.5,
    -0.5,
    0.5,
    0.5,
    0.5, // right
    -0.5,
    -0.5,
    -0.5,
    -0.5,
    -0.5,
    0.5,
    -0.5,
    0.5,
    0.5,
    -0.5,
    0.5,
    -0.5, // left
    0.5,
    0.5,
    0.5,
    0.5,
    0.5,
    -0.5,
    -0.5,
    0.5,
    -0.5,
    -0.5,
    0.5,
    0.5, // top
    -0.5,
    -0.5,
    -0.5,
    0.5,
    -0.5,
    -0.5,
    0.5,
    -0.5,
    0.5,
    -0.5,
    -0.5,
    0.5, // bottom
  ]);

  const indices = new Uint16Array(36);
  for (let face = 0; face < 6; face += 1) {
    const base = face * 4;
    indices.set([base, base + 1, base + 2, base, base + 2, base + 3], face * 6);
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  if (withUv) {
    const uvs = new Float32Array(24 * 2);
    for (let i = 0; i < 24; i += 1) {
      uvs[i * 2 + 0] = [0, 1, 1, 0][i % 4];
      uvs[i * 2 + 1] = [0, 0, 1, 1][i % 4];
    }
    geometry.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
  }
  geometry.setIndex(new THREE.BufferAttribute(indices, 1));
  return geometry;
}

describe('computeModelInfo', () => {
  it('counts vertices, triangles and materials of a cube', () => {
    const scene = new THREE.Scene();
    const mesh = new THREE.Mesh(cubeGeometry(), new THREE.MeshStandardMaterial());
    scene.add(mesh);

    const info = computeModelInfo(scene);

    expect(info.vertexCount).toBe(CUBE_VERTEX_COUNT);
    expect(info.triangleCount).toBe(CUBE_TRIANGLE_COUNT);
    expect(info.meshCount).toBe(1);
    expect(info.materialCount).toBe(1);
    expect(info.textureCount).toBe(0);
    expect(info.hasUv).toBe(true);
  });

  it('reports hasUv=false when a mesh has no uv attribute', () => {
    const scene = new THREE.Scene();
    scene.add(new THREE.Mesh(cubeGeometry(false), new THREE.MeshStandardMaterial()));

    expect(computeModelInfo(scene).hasUv).toBe(false);
  });

  it('measures the world-space bounding box', () => {
    const scene = new THREE.Scene();
    const mesh = new THREE.Mesh(cubeGeometry(), new THREE.MeshStandardMaterial());
    mesh.scale.setScalar(2); // cube 1x1x1 -> 2x2x2
    scene.add(mesh);

    const info = computeModelInfo(scene);

    expect(info.size.x).toBeCloseTo(2);
    expect(info.size.y).toBeCloseTo(2);
    expect(info.size.z).toBeCloseTo(2);
    expect(info.center.x).toBeCloseTo(0);
    expect(info.center.y).toBeCloseTo(0);
    expect(info.center.z).toBeCloseTo(0);
  });

  it('counts shared materials once and textures across material slots', () => {
    const scene = new THREE.Scene();
    const sharedMaterial = new THREE.MeshStandardMaterial();
    const texturedMaterial = new THREE.MeshStandardMaterial({ map: new THREE.Texture() });
    scene.add(new THREE.Mesh(cubeGeometry(), sharedMaterial));
    scene.add(new THREE.Mesh(cubeGeometry(), sharedMaterial));
    scene.add(new THREE.Mesh(cubeGeometry(), texturedMaterial));

    const info = computeModelInfo(scene);

    expect(info.meshCount).toBe(3);
    expect(info.materialCount).toBe(2);
    expect(info.textureCount).toBe(1);
  });

  it('returns zeros for an empty scene', () => {
    const info = computeModelInfo(new THREE.Group());

    expect(info.vertexCount).toBe(0);
    expect(info.triangleCount).toBe(0);
    expect(info.meshCount).toBe(0);
    expect(info.materialCount).toBe(0);
    expect(info.size.x).toBe(0);
    expect(info.hasUv).toBe(true);
  });

  it('works on a scene parsed from a real GLB buffer', async () => {
    const { scene } = await parseGlb(buildCubeGlb({ withMaterial: true }));

    const info = computeModelInfo(scene);

    expect(info.meshCount).toBe(1);
    expect(info.vertexCount).toBe(CUBE_VERTEX_COUNT);
    expect(info.triangleCount).toBe(CUBE_TRIANGLE_COUNT);
    expect(info.materialCount).toBe(1);
    expect(info.hasUv).toBe(true);
  });
});
