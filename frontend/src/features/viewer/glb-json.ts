import { GlbLoadError } from './glb-errors';

/**
 * GLB container inspection (step 3c.5).
 *
 * Reads ONLY the JSON chunk of the GLB so the viewer can decide which
 * decoders to install (and whether the file is even supported) before
 * handing the buffer to GLTFLoader. The same container rules as the
 * monolith's server-side check (ADR 0015), client-side.
 */

const GLB_MAGIC = 0x46546c67; // 'glTF'
const GLB_VERSION = 2;
const JSON_CHUNK = 0x4e4f534a; // 'JSON'
const GLB_HEADER_BYTES = 12;
const GLB_CHUNK_HEADER_BYTES = 8; // chunkLength + chunkType
const GLB_MIN_BYTES = GLB_HEADER_BYTES + GLB_CHUNK_HEADER_BYTES;

export function readGlbJson(buffer: ArrayBuffer): Record<string, unknown> {
  if (buffer.byteLength < GLB_MIN_BYTES) {
    throw new GlbLoadError('invalid GLB: file is shorter than the container header', 'parse');
  }
  const view = new DataView(buffer);
  if (view.getUint32(0, true) !== GLB_MAGIC) {
    throw new GlbLoadError('invalid GLB: bad magic', 'parse');
  }
  if (view.getUint32(4, true) !== GLB_VERSION) {
    throw new GlbLoadError('invalid GLB: not glTF 2.0', 'parse');
  }
  const declaredLength = view.getUint32(8, true);
  if (declaredLength > buffer.byteLength) {
    throw new GlbLoadError('invalid GLB: declared length exceeds file size', 'parse');
  }
  if (view.getUint32(16, true) !== JSON_CHUNK) {
    throw new GlbLoadError('invalid GLB: first chunk is not JSON', 'parse');
  }
  const jsonLength = view.getUint32(12, true);
  if (GLB_HEADER_BYTES + GLB_CHUNK_HEADER_BYTES + jsonLength > buffer.byteLength) {
    throw new GlbLoadError('invalid GLB: JSON chunk overruns the file', 'parse');
  }
  const jsonOffset = GLB_HEADER_BYTES + GLB_CHUNK_HEADER_BYTES;
  const text = new TextDecoder().decode(new Uint8Array(buffer, jsonOffset, jsonLength));
  let json: unknown;
  try {
    json = JSON.parse(text);
  } catch {
    throw new GlbLoadError('invalid GLB: JSON chunk is not valid JSON', 'parse');
  }
  if (typeof json !== 'object' || json === null || Array.isArray(json)) {
    throw new GlbLoadError('invalid GLB: JSON chunk is not a glTF object', 'parse');
  }
  return json as Record<string, unknown>;
}
