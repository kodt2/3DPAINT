import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import { disposeSceneObject } from './scene-dispose';

describe('disposeSceneObject', () => {
  it('disposes geometries, materials and textures of all meshes', () => {
    const group = new THREE.Group();
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(9), 3));
    const texture = new THREE.Texture();
    const material = new THREE.MeshStandardMaterial({ map: texture });
    const mesh = new THREE.Mesh(geometry, material);

    // A second group sharing the same material: dispose must be safe to
    // call twice (three fires the 'dispose' event on every dispose() call).
    const group2 = new THREE.Group();
    const secondGeometry = new THREE.BufferGeometry();
    const mesh2 = new THREE.Mesh(secondGeometry, material);
    group.add(mesh);
    group2.add(mesh2);

    const empty = new THREE.BufferGeometry();
    const materialless = new THREE.Mesh(empty);

    const disposeEvents = { geometry: 0, material: 0, texture: 0 };
    geometry.addEventListener('dispose', () => (disposeEvents.geometry += 1));
    secondGeometry.addEventListener('dispose', () => (disposeEvents.geometry += 1));
    empty.addEventListener('dispose', () => (disposeEvents.geometry += 1));
    material.addEventListener('dispose', () => (disposeEvents.material += 1));
    texture.addEventListener('dispose', () => (disposeEvents.texture += 1));

    disposeSceneObject(group);
    expect(disposeEvents.geometry).toBe(1);
    expect(disposeEvents.material).toBe(1);
    expect(disposeEvents.texture).toBe(1);

    disposeSceneObject(group2);
    disposeSceneObject(materialless);
    expect(disposeEvents.geometry).toBe(3);
    // The shared material (and its map texture) is disposed once per group.
    expect(disposeEvents.material).toBe(2);
    expect(disposeEvents.texture).toBe(2);
  });

  it('ignores non-mesh objects', () => {
    const group = new THREE.Group();
    group.add(new THREE.Group());
    group.add(new THREE.Object3D());
    expect(() => disposeSceneObject(group)).not.toThrow();
  });
});
