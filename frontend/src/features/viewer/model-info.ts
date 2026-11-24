import * as THREE from 'three';

/**
 * Pure (no React) description of a loaded model (step 3c.3).
 *
 * Feeds the info panel on /spa/models/:id and the Vitest tests on synthetic
 * scenes: vertices, triangles, meshes, materials, textures, world-space
 * bounding box and the Paint precondition (does every mesh carry UVs?).
 */
export interface ModelInfo {
  vertexCount: number;
  triangleCount: number;
  meshCount: number;
  materialCount: number;
  textureCount: number;
  /** World-space extent of the model's bounding box. */
  size: { x: number; y: number; z: number };
  /** World-space center of the bounding box. */
  center: { x: number; y: number; z: number };
  /** True when every mesh has a uv attribute (vacuously true for no meshes). */
  hasUv: boolean;
}

export function computeModelInfo(root: THREE.Object3D): ModelInfo {
  let vertexCount = 0;
  let triangleCount = 0;
  let meshCount = 0;
  let hasUv = true;
  const materials = new Set<THREE.Material>();

  root.updateWorldMatrix(true, true);
  root.traverse((child) => {
    const mesh = child as THREE.Mesh;
    if (!mesh.isMesh) {
      return;
    }
    meshCount += 1;
    const geometry = mesh.geometry as THREE.BufferGeometry;
    const position = geometry?.getAttribute('position');
    if (position) {
      vertexCount += position.count;
      const index = geometry.getIndex();
      triangleCount += Math.floor((index ? index.count : position.count) / 3);
      if (!geometry.getAttribute('uv')) {
        hasUv = false;
      }
    }
    const material = mesh.material;
    if (Array.isArray(material)) {
      for (const m of material) {
        if (m) {
          materials.add(m);
        }
      }
    } else if (material) {
      materials.add(material);
    }
  });

  const box = new THREE.Box3().setFromObject(root);
  const size = new THREE.Vector3();
  const center = new THREE.Vector3();
  if (!box.isEmpty()) {
    box.getSize(size);
    box.getCenter(center);
  }

  return {
    vertexCount,
    triangleCount,
    meshCount,
    materialCount: materials.size,
    textureCount: countTextures(materials),
    size: { x: size.x, y: size.y, z: size.z },
    center: { x: center.x, y: center.y, z: center.z },
    hasUv,
  };
}

/** Unique textures referenced by any material slot (map, normalMap, ...). */
function countTextures(materials: Set<THREE.Material>): number {
  const textures = new Set<THREE.Texture>();
  for (const material of materials) {
    for (const value of Object.values(material)) {
      if (value instanceof THREE.Texture) {
        textures.add(value);
      }
    }
  }
  return textures.size;
}
