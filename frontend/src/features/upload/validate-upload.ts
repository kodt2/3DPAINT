import type { AppConfig } from '../../api/config';
import { formatSize } from '../../lib/format';

/** Why a file was rejected before it ever reached the network. */
export type RejectionReason = 'empty' | 'extension' | 'size';

export type UploadValidation = { ok: true } | { ok: false; reason: RejectionReason };

/**
 * Client-side pre-flight validation (step 3c.4): a bad file must not pay
 * the round-trip to the server. The server re-checks everything
 * independently — this only saves bandwidth and gives instant feedback.
 *
 * `allowedUploadExtensions` comes from GET /api/config; an empty list means
 * "the config did not say" and the file is left to the server.
 */
export function validateUploadFile(file: File, config: AppConfig): UploadValidation {
  if (file.size <= 0) {
    return { ok: false, reason: 'empty' };
  }
  if (!hasAllowedExtension(file.name, config)) {
    return { ok: false, reason: 'extension' };
  }
  const limit = config.maxUploadBytes;
  if (typeof limit === 'number' && limit > 0 && file.size > limit) {
    return { ok: false, reason: 'size' };
  }
  return { ok: true };
}

export function hasAllowedExtension(fileName: string, config: AppConfig): boolean {
  const dot = fileName.lastIndexOf('.');
  if (dot <= 0 || dot === fileName.length - 1) {
    return false; // no extension at all (or a trailing dot)
  }
  const ext = fileName.slice(dot).toLowerCase();
  const allowed = (config.allowedUploadExtensions ?? []).map((e) => e.toLowerCase());
  if (allowed.length === 0) {
    return true;
  }
  return allowed.includes(ext);
}

/** Human-readable rejection for the upload list (Russian, ru-RU sizes). */
export function messageForRejectedFile(file: File, reason: RejectionReason, config: AppConfig): string {
  switch (reason) {
    case 'empty':
      return 'Файл пустой — загрузите не пустой файл.';
    case 'extension': {
      const allowed = (config.allowedUploadExtensions ?? ['.glb']).join(', ');
      return `Допускается только формат ${allowed}.`;
    }
    case 'size': {
      const limit = config.maxUploadBytes ?? 0;
      return `Файл слишком большой: ${formatSize(file.size)} при лимите ${formatSize(limit)}.`;
    }
  }
}
