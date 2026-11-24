import { beforeEach, describe, expect, it, vi } from 'vitest';
import { uploadFile, UploadError, type UploadProgress } from './upload-client';

/**
 * Minimal fake XHR: the test drives the lifecycle (progress, load, error,
 * abort) instead of a real network.
 */
class FakeXHR {
  static last: FakeXHR | null = null;

  upload: { onprogress: ((e: { lengthComputable: boolean; loaded: number; total: number }) => void) | null } =
    { onprogress: null };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  ontimeout: (() => void) | null = null;
  onabort: (() => void) | null = null;
  status = 0;
  responseText = '';
  responseType = '';
  timeout = 0;
  url = '';
  method = '';

  aborted = false;

  constructor() {
    FakeXHR.last = this;
  }

  open(method: string, url: string): void {
    this.method = method;
    this.url = url;
  }

  send(): void {
    // The test fires events manually after uploadFile returns.
  }

  abort(): void {
    this.aborted = true;
    this.onabort?.();
  }

  // Test helpers -----------------------------------------------------------

  fireProgress(loaded: number, total: number): void {
    this.upload.onprogress?.({ lengthComputable: true, loaded, total });
  }

  respond(status: number, body: string): void {
    this.status = status;
    this.responseText = body;
    this.onload?.();
  }

  fail(): void {
    this.onerror?.();
  }
}

beforeEach(() => {
  FakeXHR.last = null;
  vi.stubGlobal('XMLHttpRequest', FakeXHR);
});

function file(): File {
  return new File([new Uint8Array(8)], 'a.glb');
}

describe('uploadFile', () => {
  it('posts the file to /api/files/upload as multipart', async () => {
    const { done } = uploadFile(file());
    const xhr = FakeXHR.last!;
    expect(xhr.method).toBe('POST');
    expect(xhr.url).toBe('/api/files/upload');
    xhr.respond(200, '');
    await expect(done).resolves.toBeUndefined();
  });

  it('forwards upload progress events', async () => {
    const progress: UploadProgress[] = [];
    const { done } = uploadFile(file(), (p) => progress.push(p));
    const xhr = FakeXHR.last!;
    xhr.fireProgress(5, 10);
    xhr.fireProgress(10, 10);
    xhr.respond(200, '');
    await done;
    expect(progress).toEqual([
      { loaded: 5, total: 10 },
      { loaded: 10, total: 10 },
    ]);
  });

  it('resolves on 201 too', async () => {
    const { done } = uploadFile(file());
    FakeXHR.last!.respond(201, '');
    await expect(done).resolves.toBeUndefined();
  });

  it('rejects a 400 with the ProblemDetails detail message', async () => {
    const { done } = uploadFile(file());
    FakeXHR.last!.respond(400, JSON.stringify({ title: 'Invalid GLB file', detail: 'Файл не является GLB' }));
    await expect(done).rejects.toMatchObject({
      kind: 'http',
      status: 400,
      detail: 'Файл не является GLB',
    });
  });

  it('falls back to the legacy message field', async () => {
    const { done } = uploadFile(file());
    FakeXHR.last!.respond(500, JSON.stringify({ message: 'legacy text' }));
    await expect(done).rejects.toMatchObject({ status: 500, detail: 'legacy text' });
  });

  it('keeps detail undefined for a non-JSON body (a bare Gateway 413)', async () => {
    const { done } = uploadFile(file());
    FakeXHR.last!.respond(413, '');
    const err = await done.then(
      () => {
        throw new Error('expected rejection');
      },
      (e: unknown) => e,
    );
    expect(err).toBeInstanceOf(UploadError);
    expect((err as UploadError).detail).toBeUndefined();
    expect((err as UploadError).status).toBe(413);
  });

  it('rejects with kind network when there is no answer', async () => {
    const { done } = uploadFile(file());
    FakeXHR.last!.fail();
    await expect(done).rejects.toMatchObject({ kind: 'network' });
  });

  it('rejects with kind network on timeout', async () => {
    const { done } = uploadFile(file());
    FakeXHR.last!.ontimeout?.();
    await expect(done).rejects.toMatchObject({ kind: 'network' });
  });

  it('rejects with kind aborted when cancelled, exactly once', async () => {
    const { done, abort } = uploadFile(file());
    const settled = done.then(
      () => 'resolved',
      (e: unknown) => (e as UploadError).kind,
    );
    abort();
    // A late response must not change the outcome.
    FakeXHR.last!.respond(200, '');
    await expect(settled).resolves.toBe('aborted');
  });

  it('ignores abort after the upload already settled', async () => {
    const { done, abort } = uploadFile(file());
    FakeXHR.last!.respond(200, '');
    await done;
    expect(() => abort()).not.toThrow();
    expect(FakeXHR.last!.aborted).toBe(false);
  });
});
