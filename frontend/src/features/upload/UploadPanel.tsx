import { useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { appConfigQueryKey, getAppConfig } from '../../api/config';
import { modelsListKey } from '../../api/models';
import { formatSize } from '../../lib/format';
import { UploadQueue, type UploadItem } from './upload-queue';

const NO_ITEMS: readonly UploadItem[] = [];
const noopSubscribe = () => () => undefined;

/**
 * Temporary upload panel (step 3c.4, ADR 0015): button + drag & drop, a
 * queue of at most 2 in-flight files, per-file status/progress, cancel and
 * retry. Replaced by the real upload screen in step 3c.5.
 *
 * Accessibility: the file input is visually hidden but focusable (the
 * button activates it), every status change lands in an aria-live list,
 * and no HTML is injected from file contents (names are rendered as text).
 */
export function UploadPanel() {
  const queryClient = useQueryClient();
  const configQuery = useQuery({ queryKey: appConfigQueryKey, queryFn: getAppConfig });
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragOver, setDragOver] = useState(false);

  const queue = useMemo(() => {
    if (!configQuery.data) {
      return null;
    }
    return new UploadQueue({
      config: configQuery.data,
      concurrency: 2,
      onSettled: (ok) => {
        if (ok) {
          // A new model appeared: refresh the library list.
          void queryClient.invalidateQueries({ queryKey: modelsListKey });
        }
      },
      onSessionExpired: () => {
        // The cookie is gone: a full reload to the login screen is the
        // honest way out (no router needed here).
        window.location.assign('/spa/login');
      },
    });
  }, [configQuery.data, queryClient]);

  const items = useSyncExternalStore(
    queue ? (cb) => queue.subscribe(cb) : noopSubscribe,
    () => (queue ? queue.getItems() : NO_ITEMS),
  );

  function addFiles(files: FileList | File[]) {
    const list = Array.from(files);
    if (list.length > 0) {
      queue?.addFiles(list);
    }
  }

  return (
    <section className="upload-panel" aria-label="Загрузка моделей">
      <h2>Загрузка моделей</h2>

      {configQuery.isPending && <p>Подготовка загрузки…</p>}
      {configQuery.isError && (
        <p className="form-error">Не удалось получить настройки загрузки.</p>
      )}

      {queue && configQuery.data && (
        <>
          <div
            className={`upload-dropzone${dragOver ? ' upload-dropzone-active' : ''}`}
            onDragOver={(e) => {
              e.preventDefault();
              setDragOver(true);
            }}
            onDragLeave={() => setDragOver(false)}
            onDrop={(e) => {
              e.preventDefault();
              setDragOver(false);
              addFiles(e.dataTransfer.files);
            }}
          >
            <button
              type="button"
              className="upload-browse"
              onClick={() => inputRef.current?.click()}
            >
              Выбрать файлы (GLB)
            </button>
            <span className="upload-dropzone-hint">
              или перетащите файлы сюда · очередь: до 2 файлов одновременно
            </span>
            <input
              ref={inputRef}
              className="upload-input"
              type="file"
              // Picker hint from the same config the validation uses; an empty
              // list means "no hint" (validation still enforces the list).
              accept={
                (configQuery.data.allowedUploadExtensions ?? []).join(',') || undefined
              }
              multiple
              aria-label="Файлы для загрузки (GLB)"
              onChange={(e) => {
                addFiles(e.target.files ?? []);
                // Allow re-selecting the same file afterwards.
                e.target.value = '';
              }}
            />
          </div>

          {items.length > 0 && (
            <ul className="upload-list" aria-live="polite">
              {items.map((item) => (
                <li key={item.id} className={`upload-item upload-item-${item.status.state}`}>
                  <span className="upload-item-name" title={item.fileName}>
                    {item.fileName}
                  </span>
                  <span className="upload-item-size">{formatSize(item.sizeBytes)}</span>
                  <UploadItemControl item={item} queue={queue} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}

function UploadItemControl({ item, queue }: { item: UploadItem; queue: UploadQueue }) {
  const { status } = item;

  return (
    <span className="upload-item-control">
      {status.state === 'queued' && (
        <>
          <span className="upload-item-status">В очереди</span>
          <button type="button" onClick={() => queue.cancel(item.id)}>
            Отменить
          </button>
        </>
      )}
      {status.state === 'uploading' && (
        <>
          <span className="upload-item-status">
            Загрузка… {Math.round(status.progress * 100)}%
          </span>
          <span
            className="upload-progress"
            role="progressbar"
            aria-valuenow={Math.round(status.progress * 100)}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-label={`Загрузка ${item.fileName}`}
          >
            <span className="upload-progress-fill" style={{ width: `${status.progress * 100}%` }} />
          </span>
          <button type="button" onClick={() => queue.cancel(item.id)}>
            Отменить
          </button>
        </>
      )}
      {status.state === 'done' && <span className="upload-item-status">Готово</span>}
      {status.state === 'canceled' && <span className="upload-item-status">Отменено</span>}
      {status.state === 'error' && (
        <>
          <span className="upload-item-status upload-item-error-text">{status.message}</span>
          {status.retryable && (
            <button type="button" onClick={() => queue.retry(item.id)}>
              Повторить
            </button>
          )}
        </>
      )}
    </span>
  );
}
