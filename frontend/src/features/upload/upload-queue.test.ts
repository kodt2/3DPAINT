import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AppConfig } from '../../api/config';

/**
 * The upload client is replaced by a controllable fake: each started
 * upload is a promise the test settles (resolves, rejects with a concrete
 * UploadError) or aborts.
 */
const h = vi.hoisted(() => {
  class UploadError extends Error {
    kind: 'http' | 'network' | 'aborted';
    status?: number;
    detail?: string;
    constructor(kind: 'http' | 'network' | 'aborted', status?: number, detail?: string) {
      super(detail ?? kind);
      this.name = 'UploadError';
      this.kind = kind;
      this.status = status;
      this.detail = detail;
    }
  }
  const started: {
    file: File;
    resolve: () => void;
    reject: (err: unknown) => void;
    onProgress: ((p: { loaded: number; total: number }) => void) | null;
    abort: () => void;
  }[] = [];
  return { UploadError, started };
});

vi.mock('./upload-client', () => ({
  UploadError: h.UploadError,
  uploadFile: (file: File, onProgress?: (p: { loaded: number; total: number }) => void) => {
    const entry: {
      file: File;
      resolve: () => void;
      reject: (err: unknown) => void;
      onProgress: ((p: { loaded: number; total: number }) => void) | null;
      abort: () => void;
    } = {
      file,
      resolve: () => undefined,
      reject: () => undefined,
      onProgress: onProgress ?? null,
      abort: () => undefined,
    };
    entry.abort = () => entry.reject(new h.UploadError('aborted'));
    h.started.push(entry);
    return {
      done: new Promise<void>((resolve, reject) => {
        entry.resolve = resolve;
        entry.reject = (err: unknown) => reject(err);
      }),
      abort: () => entry.abort(),
    };
  },
}));

import { UploadQueue, type UploadItem, type UploadStatus } from './upload-queue';

/** Narrows an item's status to the error variant (tests must read `message`). */
function errorStatus(item: UploadItem): Extract<UploadStatus, { state: 'error' }> {
  if (item.status.state !== 'error') {
    throw new Error(`expected an error status, got ${item.status.state}`);
  }
  return item.status;
}

const CONFIG: AppConfig = { maxUploadBytes: 10_000, allowedUploadExtensions: ['.glb'] };

function file(name = 'a.glb', size = 10): File {
  return new File([new Uint8Array(size)], name);
}

function makeQueue(options: { concurrency?: number; onSettled?: (ok: boolean) => void; onSessionExpired?: () => void } = {}) {
  return new UploadQueue({
    config: CONFIG,
    concurrency: options.concurrency ?? 2,
    onSettled: options.onSettled,
    onSessionExpired: options.onSessionExpired,
  });
}

function items(queue: UploadQueue) {
  return queue.getItems();
}

async function flush() {
  await Promise.resolve();
  await Promise.resolve();
}

beforeEach(() => {
  h.started.length = 0;
});

describe('UploadQueue', () => {
  it('uploads at most `concurrency` files in parallel', async () => {
    const queue = makeQueue({ concurrency: 2 });
    queue.addFiles([file('a.glb'), file('b.glb'), file('c.glb'), file('d.glb')]);
    await flush();

    expect(h.started.length).toBe(2);
    expect(items(queue)[2].status.state).toBe('queued');
    expect(items(queue)[3].status.state).toBe('queued');

    h.started[0].resolve();
    await flush();
    expect(h.started.length).toBe(3); // the third file started

    h.started[1].resolve();
    h.started[2].resolve();
    await flush();
    expect(h.started.length).toBe(4); // the fourth file started

    h.started[3].resolve();
    await flush();
    expect(items(queue).every((i) => i.status.state === 'done')).toBe(true);
  });

  it('reports progress as a 0..1 fraction', async () => {
    const queue = makeQueue();
    queue.addFiles([file()]);
    await flush();

    h.started[0].onProgress?.({ loaded: 25, total: 100 });
    await flush();
    expect(items(queue)[0].status).toEqual({ state: 'uploading', progress: 0.25 });
  });

  it('calls onSettled(ok) only for real outcomes', async () => {
    const settled: boolean[] = [];
    const queue = makeQueue({ onSettled: (ok) => settled.push(ok) });
    queue.addFiles([file('a.glb'), file('b.glb')]);
    await flush();

    h.started[0].resolve();
    h.started[1].reject(new h.UploadError('http', 500));
    await flush();

    expect(settled).toEqual([true, false]);
  });

  it('aborts an in-flight upload and marks it canceled (not an error)', async () => {
    const queue = makeQueue();
    queue.addFiles([file()]);
    await flush();

    queue.cancel(items(queue)[0].id);
    await flush();

    expect(items(queue)[0].status).toEqual({ state: 'canceled' });
  });

  it('cancels a queued file without starting it', async () => {
    const queue = makeQueue({ concurrency: 1 });
    queue.addFiles([file('a.glb'), file('b.glb')]);
    await flush();
    expect(h.started.length).toBe(1);

    queue.cancel(items(queue)[1].id);
    await flush();
    expect(items(queue)[1].status).toEqual({ state: 'canceled' });

    h.started[0].resolve();
    await flush();
    expect(h.started.length).toBe(1); // b was never started
  });

  it('never uploads a file rejected by client validation', async () => {
    const queue = makeQueue();
    queue.addFiles([file('big.glb', 20_000)]); // over the 10 000 limit
    await flush();

    expect(h.started.length).toBe(0);
    const item = items(queue)[0];
    expect(errorStatus(item).retryable).toBe(false);
    expect(errorStatus(item).message).toContain('лимит');

    queue.retry(item.id);
    await flush();
    expect(h.started.length).toBe(0); // a non-retryable error stays
  });

  it('rejects a wrong extension before the network', async () => {
    const queue = makeQueue();
    queue.addFiles([file('model.stl')]);
    await flush();

    expect(h.started.length).toBe(0);
    const extItem = items(queue)[0];
    expect(extItem.status).toMatchObject({ state: 'error', retryable: false });
    expect(errorStatus(extItem).message).toContain('.glb');
  });

  it('retries a 500 error once the user asks', async () => {
    const queue = makeQueue();
    queue.addFiles([file()]);
    await flush();

    h.started[0].reject(new h.UploadError('http', 500));
    await flush();
    const fiveHundred = items(queue)[0];
    expect(fiveHundred.status).toMatchObject({ state: 'error', retryable: true });
    expect(errorStatus(fiveHundred).message).toContain('Сервер временно недоступен');

    queue.retry(items(queue)[0].id);
    await flush();
    expect(h.started.length).toBe(2);
  });

  it('does not retry a 400 content error', async () => {
    const queue = makeQueue();
    queue.addFiles([file()]);
    await flush();

    h.started[0].reject(new h.UploadError('http', 400, 'Файл не является GLB'));
    await flush();
    const item = items(queue)[0];
    expect(item.status).toMatchObject({ state: 'error', retryable: false });
    expect(errorStatus(item).message).toBe('Файл не является GLB');

    queue.retry(item.id);
    await flush();
    expect(h.started.length).toBe(1);
  });

  it('treats a network failure as retryable', async () => {
    const queue = makeQueue();
    queue.addFiles([file()]);
    await flush();

    h.started[0].reject(new h.UploadError('network'));
    await flush();
    expect(items(queue)[0].status).toMatchObject({ state: 'error', retryable: true });
  });

  it('shows the ProblemDetails detail for a 413 (bare body, no detail)', async () => {
    const queue = makeQueue();
    queue.addFiles([file()]);
    await flush();

    h.started[0].reject(new h.UploadError('http', 413));
    await flush();
    const item = items(queue)[0];
    expect(item.status).toMatchObject({ state: 'error', retryable: false });
    expect(errorStatus(item).message).toContain('лимит загрузки');
  });

  it('fires onSessionExpired on 401 and does not offer a retry', async () => {
    let expired = false;
    const queue = makeQueue({ onSessionExpired: () => (expired = true) });
    queue.addFiles([file()]);
    await flush();

    h.started[0].reject(new h.UploadError('http', 401));
    await flush();

    expect(expired).toBe(true);
    expect(items(queue)[0].status).toMatchObject({ state: 'error', retryable: false });
  });

  it('emits a new snapshot on every change (useSyncExternalStore-friendly)', () => {
    const queue = makeQueue();
    const before = queue.getItems();
    queue.addFiles([file()]);
    expect(queue.getItems()).not.toBe(before);
  });
});
