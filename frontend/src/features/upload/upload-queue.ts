import type { AppConfig } from '../../api/config';
import { uploadFile, UploadError, type UploadHandle, type UploadProgress } from './upload-client';
import { messageForRejectedFile, validateUploadFile } from './validate-upload';

/**
 * Upload queue (step 3c.4): at most `concurrency` files in flight (default
 * 2), per-file status, cancel and retry. Framework-agnostic — the React
 * panel subscribes through useSyncExternalStore.
 */

export type UploadStatus =
  | { state: 'queued' }
  | { state: 'uploading'; progress: number } // 0..1
  | { state: 'done' }
  | { state: 'canceled' }
  | { state: 'error'; message: string; retryable: boolean };

export interface UploadItem {
  id: string;
  fileName: string;
  sizeBytes: number;
  status: UploadStatus;
}

export interface UploadQueueOptions {
  config: AppConfig;
  /** Max files uploading in parallel. Default 2. */
  concurrency?: number;
  /** Called after each settled item; `ok` = the upload succeeded. */
  onSettled?: (ok: boolean) => void;
  /** The server answered 401: the session is gone, go to /spa/login. */
  onSessionExpired?: () => void;
}

export type UploadQueueListener = () => void;

export class UploadQueue {
  private items: UploadItem[] = [];
  private readonly fileById = new Map<string, File>();
  private readonly handles = new Map<string, UploadHandle>();
  private readonly listeners = new Set<UploadQueueListener>();
  private readonly config: AppConfig;
  private readonly concurrency: number;
  private readonly onSettled?: (ok: boolean) => void;
  private readonly onSessionExpired?: () => void;
  private active = 0;
  private nextId = 1;

  constructor(options: UploadQueueOptions) {
    this.config = options.config;
    this.concurrency = options.concurrency ?? 2;
    this.onSettled = options.onSettled;
    this.onSessionExpired = options.onSessionExpired;
  }

  /** A new array on every change (useSyncExternalStore snapshot). */
  getItems(): readonly UploadItem[] {
    return this.items;
  }

  subscribe(listener: UploadQueueListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /**
   * Adds files to the queue. Each file is pre-validated against the config
   * first; a rejected file gets an immediate (non-retryable) error row and
   * is never sent.
   */
  addFiles(files: File[]): void {
    const additions: UploadItem[] = files.map((file) => {
      const id = `upload-${this.nextId++}`;
      this.fileById.set(id, file);
      const validation = validateUploadFile(file, this.config);
      return {
        id,
        fileName: file.name,
        sizeBytes: file.size,
        status: validation.ok
          ? { state: 'queued' }
          : {
              state: 'error',
              message: messageForRejectedFile(file, validation.reason, this.config),
              retryable: false,
            },
      };
    });
    if (additions.length > 0) {
      this.items = [...this.items, ...additions];
      this.emit();
      this.pump();
    }
  }

  /** Cancels a queued or in-flight item (an in-flight upload is aborted). */
  cancel(id: string): void {
    const item = this.items.find((i) => i.id === id);
    if (!item) {
      return;
    }
    if (item.status.state === 'queued') {
      this.setStatus(id, { state: 'canceled' });
      this.emit();
    } else if (item.status.state === 'uploading') {
      this.handles.get(id)?.abort(); // settled asynchronously as 'canceled'
    }
  }

  /** Re-queues a retryable error item. */
  retry(id: string): void {
    const item = this.items.find((i) => i.id === id);
    if (!item || item.status.state !== 'error' || !item.status.retryable) {
      return;
    }
    this.setStatus(id, { state: 'queued' });
    this.emit();
    this.pump();
  }

  private pump(): void {
    while (this.active < this.concurrency) {
      const next = this.items.find((i) => i.status.state === 'queued');
      if (!next) {
        break;
      }
      this.start(next.id);
    }
  }

  private start(id: string): void {
    const file = this.fileById.get(id);
    if (!file) {
      return;
    }
    this.active += 1;
    this.setStatus(id, { state: 'uploading', progress: 0 });
    const handle = uploadFile(file, (progress: UploadProgress) => {
      const item = this.items.find((i) => i.id === id);
      if (item && item.status.state === 'uploading') {
        this.setStatus(id, {
          state: 'uploading',
          progress: progress.total > 0 ? progress.loaded / progress.total : 0,
        });
      }
    });
    this.handles.set(id, handle);
    void handle.done.then(
      () => this.settle(id, true),
      (err: unknown) => this.settle(id, false, err),
    );
  }

  private settle(id: string, ok: boolean, err?: unknown): void {
    this.active = Math.max(0, this.active - 1);
    this.handles.delete(id);

    if (ok) {
      this.setStatus(id, { state: 'done' });
      this.emit();
      this.onSettled?.(true);
    } else if (err instanceof UploadError && err.kind === 'aborted') {
      this.setStatus(id, { state: 'canceled' });
      this.emit();
    } else {
      const message = toErrorMessage(err);
      this.setStatus(id, { state: 'error', message, retryable: isRetryable(err) });
      this.emit();
      this.onSettled?.(false);
      if (err instanceof UploadError && err.status === 401) {
        this.onSessionExpired?.();
      }
    }
    this.pump();
  }

  private setStatus(id: string, status: UploadStatus): void {
    this.items = this.items.map((i) => (i.id === id ? { ...i, status } : i));
  }

  private emit(): void {
    this.listeners.forEach((listener) => listener());
  }
}

/** Maps a failed upload to the user-facing message (Russian). */
function toErrorMessage(err: unknown): string {
  if (err instanceof UploadError) {
    if (err.kind === 'aborted') {
      return 'Загрузка отменена.';
    }
    if (err.status === 400) {
      return err.detail ?? 'Файл отклонён сервером.';
    }
    if (err.status === 401) {
      return 'Сессия истекла — войдите снова.';
    }
    if (err.status === 413) {
      return 'Файл слишком большой: превышен лимит загрузки.';
    }
    if (err.status === 429) {
      return 'Слишком много запросов — подождите немного и повторите.';
    }
    if (err.status !== undefined && err.status >= 500) {
      return 'Сервер временно недоступен. Попробуйте ещё раз.';
    }
    if (err.status !== undefined) {
      return `Не удалось загрузить (код ${err.status}).`;
    }
  }
  return 'Нет соединения с сервером. Проверьте сеть и попробуйте ещё раз.';
}

/**
 * Retry policy (step 3c.4): 429 / 5xx / network — retry; content errors
 * (400), size (413) and session (401) — no, the file or the session is the
 * problem, not the transport.
 */
function isRetryable(err: unknown): boolean {
  if (!(err instanceof UploadError)) {
    return true;
  }
  if (err.kind === 'aborted' || err.kind === 'network') {
    return err.kind === 'network';
  }
  return err.status === 429 || (err.status !== undefined && err.status >= 500);
}
