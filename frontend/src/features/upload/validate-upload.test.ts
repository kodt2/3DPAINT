import { describe, expect, it } from 'vitest';
import type { AppConfig } from '../../api/config';
import {
  hasAllowedExtension,
  messageForRejectedFile,
  validateUploadFile,
} from './validate-upload';

const CONFIG: AppConfig = {
  maxUploadBytes: 1024,
  allowedUploadExtensions: ['.glb'],
};

function file(name: string, size: number): File {
  return new File([new Uint8Array(size)], name, { type: 'model/gltf-binary' });
}

describe('validateUploadFile', () => {
  it('accepts a non-empty .glb within the limit', () => {
    expect(validateUploadFile(file('a.glb', 100), CONFIG)).toEqual({ ok: true });
  });

  it('accepts a file of exactly the limit size', () => {
    expect(validateUploadFile(file('a.glb', 1024), CONFIG)).toEqual({ ok: true });
  });

  it('rejects an empty file', () => {
    expect(validateUploadFile(file('a.glb', 0), CONFIG)).toEqual({ ok: false, reason: 'empty' });
  });

  it('rejects a non-glb extension (case-insensitively)', () => {
    expect(validateUploadFile(file('a.STL', 10), CONFIG)).toEqual({ ok: false, reason: 'extension' });
    expect(validateUploadFile(file('a.obj', 10), CONFIG)).toEqual({ ok: false, reason: 'extension' });
  });

  it('rejects a name without an extension', () => {
    expect(validateUploadFile(file('model', 10), CONFIG)).toEqual({ ok: false, reason: 'extension' });
    expect(validateUploadFile(file('model.', 10), CONFIG)).toEqual({ ok: false, reason: 'extension' });
  });

  it('rejects a file above the limit', () => {
    expect(validateUploadFile(file('a.glb', 1025), CONFIG)).toEqual({ ok: false, reason: 'size' });
  });

  it('skips the size check when the config has no limit', () => {
    expect(validateUploadFile(file('a.glb', 10_000), { allowedUploadExtensions: ['.glb'] })).toEqual({
      ok: true,
    });
  });

  it('leaves the file to the server when the config has no extension list', () => {
    expect(validateUploadFile(file('a.stl', 10), { maxUploadBytes: 100 })).toEqual({ ok: true });
  });
});

describe('hasAllowedExtension', () => {
  it('matches the configured extensions case-insensitively', () => {
    expect(hasAllowedExtension('Model.GLB', CONFIG)).toBe(true);
    expect(hasAllowedExtension('model.glb', CONFIG)).toBe(true);
    expect(hasAllowedExtension('model.step', CONFIG)).toBe(false);
  });
});

describe('messageForRejectedFile', () => {
  it('describes an empty file', () => {
    expect(messageForRejectedFile(file('a.glb', 0), 'empty', CONFIG)).toContain('пустой');
  });

  it('names the allowed extensions for a wrong format', () => {
    expect(messageForRejectedFile(file('a.stl', 10), 'extension', CONFIG)).toContain('.glb');
  });

  it('shows both the actual size and the limit in ru-RU for an oversized file', () => {
    const msg = messageForRejectedFile(file('a.glb', 2048), 'size', CONFIG);
    // ru-RU formatting: comma decimals (see format.ts).
    expect(msg).toContain('2 КБ');
    expect(msg).toContain('1 КБ');
    expect(msg).toContain('лимит');
  });
});
