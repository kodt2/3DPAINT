/**
 * glTF extension diagnostics (step 3c.5). Pure functions over the glTF JSON
 * (no three imports — unit-testable without a GL context).
 *
 * The viewer installs three decoders (all local, no CDN — ADR 0014):
 *   - Draco  (KHR_draco_mesh_compression) — DRACOLoader, local WASM;
 *   - Meshopt (EXT_meshopt_compression)   — MeshoptDecoder, wasm inlined;
 *   - KTX2/BasisU (KHR_texture_basisu)    — KTX2Loader, local WASM
 *     (optional: most user models do not carry BasisU textures).
 */

export const DRACO_EXTENSION = 'KHR_draco_mesh_compression';
export const MESHOPT_EXTENSION = 'EXT_meshopt_compression';
export const KTX2_EXTENSION = 'KHR_texture_basisu';

/**
 * glTF extensions the viewer can handle: the ones above plus everything
 * three r187's GLTFLoader understands on its own (quantized accessors,
 * texture transforms, material extensions, ...). A name in
 * `extensionsRequired` outside this set makes the file UNSUPPORTED — the
 * viewer rejects it up front and names the extension in the error (this is
 * the "specific unsupported extension" diagnostic from step 3c.5).
 *
 * Kept in sync with the extension list in
 * node_modules/three/examples/jsm/loaders/GLTFLoader.js (three 0.187.0);
 * bump the list together with the three version (and the decoders
 * directory, which is versioned).
 */
export const SUPPORTED_GLTF_EXTENSIONS: ReadonlySet<string> = new Set<string>([
  DRACO_EXTENSION,
  MESHOPT_EXTENSION,
  KTX2_EXTENSION,
  'EXT_materials_bump',
  'EXT_mesh_gpu_instancing',
  'EXT_texture_avif',
  'EXT_texture_webp',
  'KHR_binary_glTF',
  'KHR_lights_punctual',
  'KHR_materials_anisotropy',
  'KHR_materials_clearcoat',
  'KHR_materials_diffuse_roughness',
  'KHR_materials_dispersion',
  'KHR_materials_emissive_strength',
  'KHR_materials_ior',
  'KHR_materials_iridescence',
  'KHR_materials_sheen',
  'KHR_materials_specular',
  'KHR_materials_transmission',
  'KHR_materials_unlit',
  'KHR_materials_volume',
  // Legacy name, still accepted by three's GLTFLoader:
  'KHR_meshopt_compression',
  'KHR_mesh_quantization',
  'KHR_texture_transform',
]);

export interface DecoderRequirements {
  draco: boolean;
  meshopt: boolean;
  ktx2: boolean;
}

function stringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];
}

function extensionNames(json: object): { used: string[]; required: string[] } {
  const record = json as Record<string, unknown>;
  return {
    used: stringArray(record.extensionsUsed),
    required: stringArray(record.extensionsRequired),
  };
}

/**
 * Which decoders the file needs. `extensionsUsed` alone is enough: a file
 * may list an extension as used (not required) and still carry compressed
 * primitives — the decoder must be present to read them.
 */
export function decoderRequirements(json: object): DecoderRequirements {
  const { used, required } = extensionNames(json);
  const present = (extension: string) =>
    used.includes(extension) || required.includes(extension);
  return {
    draco: present(DRACO_EXTENSION),
    meshopt: present(MESHOPT_EXTENSION),
    ktx2: present(KTX2_EXTENSION),
  };
}

export function hasDecoderRequirements(requirements: DecoderRequirements): boolean {
  return requirements.draco || requirements.meshopt || requirements.ktx2;
}

/**
 * The first extension in `extensionsRequired` the viewer cannot handle.
 * `null` when the file is supported (possibly with decoders).
 *
 * Only `extensionsRequired` rejects: an OPTIONAL extension we do not
 * understand may be skipped by the loader, so `extensionsUsed` alone never
 * makes a file unsupported.
 */
export function unsupportedRequiredExtension(json: object): string | null {
  const { required } = extensionNames(json);
  for (const extension of required) {
    if (!SUPPORTED_GLTF_EXTENSIONS.has(extension)) {
      return extension;
    }
  }
  return null;
}
