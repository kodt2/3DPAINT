import { Canvas, useFrame, useThree } from '@react-three/fiber';
import { Grid, OrbitControls } from '@react-three/drei';
import * as THREE from 'three';
import { useEffect, useRef, type MutableRefObject } from 'react';
import type { OrbitControls as OrbitControlsImpl } from 'three-stdlib';
import { disposeSceneObject } from './scene-dispose';

/**
 * The 3D canvas for /spa/models/:id (step 3c.3).
 *
 * Deliberate scope: hemisphere + directional light (no shadows, no
 * Environment/HDR — those pull CDN assets), an infinite grid toggle,
 * OrbitControls with a resettable state, and the camera fit to the model's
 * bounding box on the first frame.
 *
 * Testing hooks (documented for the e2e suite):
 * - the wrapper div gets data-viewer-drawn="true" once a frame is rendered
 *   that actually differs from the background (pixel check, not a guess);
 * - data-viewer-geometries mirrors renderer.info.memory.geometries so the
 *   leak test can assert the count does not grow across visits.
 */

const BACKGROUND = new THREE.Color('#15151d');

interface ModelSceneProps {
  object: THREE.Object3D;
  showGrid: boolean;
  controlsRef: MutableRefObject<OrbitControlsImpl | null>;
  wrapperRef: MutableRefObject<HTMLDivElement | null>;
  onContextLost: () => void;
}

export function ModelScene({
  object,
  showGrid,
  controlsRef,
  wrapperRef,
  onContextLost,
}: ModelSceneProps) {
  return (
    <Canvas
      camera={{ fov: 45, near: 0.01, far: 1000, position: [1.2, 0.8, 1.2] }}
      gl={{ antialias: true, powerPreference: 'high-performance', preserveDrawingBuffer: true }}
      onCreated={({ gl, scene }) => {
        scene.background = BACKGROUND;
        gl.domElement.addEventListener('webglcontextlost', (event) => {
          // We handle the lost context ourselves (overlay + scene reload);
          // do not let the browser tear it down behind our back.
          event.preventDefault();
          onContextLost();
        });
      }}
    >
      <hemisphereLight args={['#bfd4ff', '#3a3222', 0.9]} />
      <directionalLight position={[5, 10, 7]} intensity={1.6} />
      <directionalLight position={[-6, -4, -5]} intensity={0.4} />
      {showGrid && (
        <Grid
          infiniteGrid
          cellSize={0.25}
          sectionSize={1}
          cellColor="#3c3c4e"
          sectionColor="#585870"
          fadeDistance={40}
          fadeStrength={1}
        />
      )}
      <CameraFit object={object} controlsRef={controlsRef} />
      <SceneProbe wrapperRef={wrapperRef} />
      <DisposeOnUnmount object={object} />
      <primitive object={object} />
      <OrbitControls ref={controlsRef} makeDefault enableDamping dampingFactor={0.08} />
    </Canvas>
  );
}

/**
 * Fits the camera and the controls target to the model's bounding box.
 * Runs on the first frame (not in an effect) so the OrbitControls ref is
 * guaranteed to be mounted by then.
 */
function CameraFit({
  object,
  controlsRef,
}: {
  object: THREE.Object3D;
  controlsRef: MutableRefObject<OrbitControlsImpl | null>;
}) {
  const { camera } = useThree();
  const fittedFor = useRef<THREE.Object3D | null>(null);

  /* eslint-disable react-hooks/immutability -- the three.js camera and
   * OrbitControls state are external systems; fitting the camera once per
   * model is imperative by design (useFrame runs outside React render). */
  useFrame(() => {
    if (fittedFor.current === object) {
      return;
    }
    fittedFor.current = object;

    const box = new THREE.Box3().setFromObject(object);
    if (box.isEmpty()) {
      return;
    }
    const center = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3());
    const maxDim = Math.max(size.x, size.y, size.z) || 1;

    const perspective = camera as THREE.PerspectiveCamera;
    const distance = ((maxDim / (2 * Math.tan((perspective.fov * Math.PI) / 360))) as number) * 1.6;
    perspective.near = Math.max(maxDim / 1000, 0.0001);
    perspective.far = maxDim * 100;
    perspective.position
      .copy(center)
      .add(new THREE.Vector3(1, 0.45, 1).normalize().multiplyScalar(distance));
    perspective.updateProjectionMatrix();

    const controls = controlsRef.current;
    if (controls) {
      controls.target.copy(center);
      controls.update();
      controls.saveState();
    }
  });
  /* eslint-enable react-hooks/immutability */

  return null;
}

/**
 * Per-frame probe:
 * - mirrors renderer.info.memory.geometries into the wrapper's
 *   data-viewer-geometries attribute (leak test);
 * - sets data-viewer-drawn once a rendered frame actually differs from the
 *   background color (reads the preserved drawing buffer; the GLB test
 *   files are red, the background is dark).
 */
function SceneProbe({ wrapperRef }: { wrapperRef: MutableRefObject<HTMLDivElement | null> }) {
  const { gl } = useThree();
  const drawn = useRef(false);

  useFrame(() => {
    const wrapper = wrapperRef.current;
    if (!wrapper) {
      return;
    }
    const geometries = String(gl.info.memory.geometries);
    if (wrapper.dataset.viewerGeometries !== geometries) {
      wrapper.dataset.viewerGeometries = geometries;
    }
    if (drawn.current) {
      return;
    }

    const canvas = gl.domElement;
    const width = canvas.width;
    const height = canvas.height;
    if (!width || !height) {
      return;
    }
    const context = gl.getContext();
    const region = 9;
    const pixels = new Uint8Array(region * region * 4);
    context.readPixels(
      Math.floor((width - region) / 2),
      Math.floor((height - region) / 2),
      region,
      region,
      context.RGBA,
      context.UNSIGNED_BYTE,
      pixels,
    );
    for (let i = 0; i < pixels.length; i += 4) {
      if (
        Math.abs(pixels[i] / 255 - BACKGROUND.r) > 0.03 ||
        Math.abs(pixels[i + 1] / 255 - BACKGROUND.g) > 0.03 ||
        Math.abs(pixels[i + 2] / 255 - BACKGROUND.b) > 0.03
      ) {
        drawn.current = true;
        wrapper.setAttribute('data-viewer-drawn', 'true');
        return;
      }
    }
  });

  return null;
}

/** Releases the model's GPU resources when it is replaced or unmounted. */
function DisposeOnUnmount({ object }: { object: THREE.Object3D }) {
  useEffect(() => {
    return () => {
      disposeSceneObject(object);
    };
  }, [object]);
  return null;
}
