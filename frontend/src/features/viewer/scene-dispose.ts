import * as THREE from 'three';

/**
 * Releases the GPU resources of a loaded model scene (step 3c.3).
 *
 * GLTFLoader creates geometry/material/texture objects that three.js does
 * NOT dispose automatically; without this the WebGL context accumulates
 * buffers on every visit to /spa/models/:id (the leak the e2e test
 * watches via renderer.info.memory.geometries).
 */
export function disposeSceneObject(root: THREE.Object3D): void {
  root.traverse((child) => {
    const mesh = child as THREE.Mesh;
    if (!mesh.isMesh) {
      return;
    }
    mesh.geometry?.dispose();
    const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
    for (const material of materials) {
      if (!material) {
        continue;
      }
      disposeMaterialTextures(material);
      material.dispose();
    }
  });
}

function disposeMaterialTextures(material: THREE.Material): void {
  for (const value of Object.values(material)) {
    if (value instanceof THREE.Texture) {
      value.dispose();
    }
  }
}
