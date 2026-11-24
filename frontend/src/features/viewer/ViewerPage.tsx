import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import * as THREE from 'three';
import type { OrbitControls as OrbitControlsImpl } from 'three-stdlib';
import { ApiError } from '../../api/auth';
import { getModel, type ModelDetail } from '../../api/models';
import { formatNumber, formatSize } from '../../lib/format';
import { GlbLoadError, loadGlb, type GlbProgress } from './glb-loader';
import { computeModelInfo, type ModelInfo } from './model-info';
import { ModelScene } from './ModelScene';
import { isWebGLAvailable } from './webgl';

/**
 * 3D viewer screen (step 3c.3): /spa/models/:id, a lazy chunk (three.js is
 * NOT bundled into login/library).
 *
 * States:
 * - the model detail comes from GET /api/models/{id} (owner-scoped, ADR
 *   0013): 404/403 renders "Модель не найдена";
 * - non-.glb extensions: a notice (the viewer is GLB-only) + download link;
 * - no WebGL: a friendly notice;
 * - GLB loading: progress bar, cancel via AbortController on unmount, one
 *   automatic refetch of the DTO when the presigned URL fails (expiry),
 *   then a friendly error (corrupt file, blocked external resource,
 *   unsupported compression, network/CORS);
 * - WebGL context loss: overlay with a scene-reload button (a fresh canvas
 *   gets a fresh context; the geometry is re-uploaded from the CPU data).
 */
export default function ViewerPage() {
  const { id } = useParams<{ id: string }>();
  if (!id) {
    return <NotFoundView />;
  }
  // key={id}: navigating between models remounts the component, so every
  // piece of state (detail, GLB status, progress) starts clean without a
  // reset inside an effect.
  return <ViewerModel key={id} id={id} />;
}

type DetailStatus = 'loading' | 'ready' | 'not-found' | 'error';
type GlbStatus = 'loading' | 'ready' | 'error';

function ViewerModel({ id }: { id: string }) {
  const [model, setModel] = useState<ModelDetail | null>(null);
  const [detailStatus, setDetailStatus] = useState<DetailStatus>('loading');
  const [object, setObject] = useState<THREE.Object3D | null>(null);
  const [glbStatus, setGlbStatus] = useState<GlbStatus>('loading');
  const [glbError, setGlbError] = useState('');
  const [progress, setProgress] = useState<GlbProgress | null>(null);
  const [showGrid, setShowGrid] = useState(true);
  const [contextLost, setContextLost] = useState(false);
  const [sceneKey, setSceneKey] = useState(0);
  const [glbAttempt, setGlbAttempt] = useState(0);
  const presignedRetried = useRef(false);
  const controlsRef = useRef<OrbitControlsImpl | null>(null);
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const webglOk = useMemo(() => isWebGLAvailable(), []);

  function failLoad(error: unknown) {
    setGlbStatus('error');
    setGlbError(friendlyGlbError(error));
  }

  // 1) Model detail (fresh presigned downloadUrl on every fetch).
  useEffect(() => {
    let cancelled = false;
    getModel(id)
      .then((detail) => {
        if (cancelled) {
          return;
        }
        setModel(detail);
        setDetailStatus('ready');
      })
      .catch((error: unknown) => {
        if (cancelled) {
          return;
        }
        setDetailStatus(
          error instanceof ApiError && (error.status === 404 || error.status === 403)
            ? 'not-found'
            : 'error',
        );
      });
    return () => {
      cancelled = true;
    };
  }, [id]);

  // 2) GLB download + parse. One retry with a fresh DTO when the presigned
  // URL fails (it lives 30 minutes); aborts on unmount/navigation.
  useEffect(() => {
    if (!model) {
      return;
    }
    if (modelExtension(model.name) !== '.glb' || !webglOk) {
      return;
    }
    if (model.downloadUrl === null || model.downloadUrl === undefined) {
      // Defensive: the backend always sets downloadUrl, but a missing URL
      // must not leave the viewer stuck on "loading" forever.
      // eslint-disable-next-line react-hooks/set-state-in-effect -- defensive error state, one-shot on a new model DTO
      failLoad(new Error('missing downloadUrl'));
      return;
    }
    const controller = new AbortController();
    let cancelled = false;
    // The GLB state machine must be reset when a fresh DTO arrives (the
    // presigned-URL retry path) — an effect-level reset is the only place
    // where "model changed" is observable.
    setGlbStatus('loading');
    setGlbError('');
    setProgress(null);

    loadGlb(model.downloadUrl, {
      signal: controller.signal,
      onProgress: (p) => {
        if (!cancelled) {
          setProgress(p);
        }
      },
    })
      .then((parsed) => {
        if (cancelled) {
          return;
        }
        setObject(parsed.scene);
        setGlbStatus('ready');
        setContextLost(false);
      })
      .catch(async (error: unknown) => {
        if (cancelled) {
          return;
        }
        if (error instanceof GlbLoadError && error.kind === 'aborted') {
          return;
        }
        if (
          error instanceof GlbLoadError &&
          (error.kind === 'http' || error.kind === 'network') &&
          !presignedRetried.current
        ) {
          presignedRetried.current = true;
          try {
            const fresh = await getModel(id);
            if (!cancelled) {
              setModel(fresh);
              setGlbAttempt((a) => a + 1);
            }
          } catch {
            if (!cancelled) {
              failLoad(error);
            }
          }
          return;
        }
        failLoad(error);
      });

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [model, glbAttempt, webglOk, id]);

  const info = useMemo(() => (object ? computeModelInfo(object) : null), [object]);

  function handleReloadScene() {
    wrapperRef.current?.removeAttribute('data-viewer-drawn');
    setContextLost(false);
    setSceneKey((k) => k + 1);
  }

  if (detailStatus === 'not-found') {
    return <NotFoundView />;
  }
  if (detailStatus === 'error') {
    return (
      <main className="page viewer-page">
        <p role="alert">Не удалось получить модель.</p>
        <p>
          <Link to="/library">← В библиотеку</Link>
        </p>
      </main>
    );
  }
  if (!model) {
    return (
      <main className="page viewer-page">
        <p>Загрузка модели…</p>
      </main>
    );
  }

  const extension = modelExtension(model.name);

  return (
    <main className="page viewer-page">
      <header className="viewer-header">
        <div className="viewer-title">
          <h1 title={model.name ?? undefined}>{model.name}</h1>
          <p className="viewer-meta">{formatSize(model.sizeBytes ?? 0)}</p>
        </div>
        <div className="viewer-actions">
          <button
            type="button"
            onClick={() => controlsRef.current?.reset()}
            disabled={glbStatus !== 'ready'}
          >
            Сбросить камеру
          </button>
          <label className="viewer-grid-toggle">
            <input
              type="checkbox"
              checked={showGrid}
              onChange={(event) => setShowGrid(event.target.checked)}
              disabled={glbStatus !== 'ready'}
            />
            Сетка
          </label>
          <a className="viewer-download" href={model.downloadUrl ?? undefined} download>
            Скачать
          </a>
          <Link to="/library">← В библиотеку</Link>
        </div>
      </header>

      {extension !== '.glb' ? (
        <div className="viewer-notice" role="status">
          <p>Просмотр 3D недоступен для {extension ? `формата ${extension}` : 'этого файла'}.</p>
          <p>Файл можно скачать по кнопке выше.</p>
        </div>
      ) : !webglOk ? (
        <div className="viewer-notice" role="alert">
          <p>WebGL недоступен в вашем браузере — 3D-просмотр работать не будет.</p>
          <p>Обновите браузер или включите аппаратное ускорение.</p>
        </div>
      ) : (
        <div className="viewer-body">
          <div ref={wrapperRef} className="viewer-canvas-wrap">
            {glbStatus === 'ready' && object && !contextLost && (
              <ModelScene
                key={sceneKey}
                object={object}
                showGrid={showGrid}
                controlsRef={controlsRef}
                wrapperRef={wrapperRef}
                onContextLost={() => setContextLost(true)}
              />
            )}
            {glbStatus === 'loading' && (
              <div className="viewer-loading" role="status">
                {progress?.total
                  ? `Загрузка… ${Math.min(100, Math.round((progress.loaded / progress.total) * 100))}%`
                  : 'Загрузка…'}
              </div>
            )}
            {glbStatus === 'error' && (
              <div className="viewer-error" role="alert">
                <p>{glbError}</p>
              </div>
            )}
            {contextLost && (
              <div className="viewer-error" role="alert">
                <p>Контекст WebGL потерян.</p>
                <button type="button" onClick={handleReloadScene}>
                  Перезагрузить сцену
                </button>
              </div>
            )}
          </div>
          {glbStatus === 'ready' && info && <ModelInfoPanel info={info} />}
        </div>
      )}
    </main>
  );
}

function ModelInfoPanel({ info }: { info: ModelInfo }) {
  return (
    <aside className="viewer-info" role="complementary" aria-label="Информация о модели">
      <h2>Информация о модели</h2>
      <dl className="viewer-info-list">
        <div>
          <dt>Вершины</dt>
          <dd>{info.vertexCount.toLocaleString('ru-RU')}</dd>
        </div>
        <div>
          <dt>Треугольники</dt>
          <dd>{info.triangleCount.toLocaleString('ru-RU')}</dd>
        </div>
        <div>
          <dt>Мешей</dt>
          <dd>{info.meshCount}</dd>
        </div>
        <div>
          <dt>Материалов</dt>
          <dd>{info.materialCount}</dd>
        </div>
        <div>
          <dt>Текстур</dt>
          <dd>{info.textureCount}</dd>
        </div>
        <div>
          <dt>Размеры (X × Y × Z)</dt>
          <dd>
            {formatNumber(info.size.x)} × {formatNumber(info.size.y)} × {formatNumber(info.size.z)}
          </dd>
        </div>
        <div>
          <dt>UV</dt>
          <dd>{info.hasUv ? 'да' : 'нет'}</dd>
        </div>
      </dl>
    </aside>
  );
}

function NotFoundView() {
  return (
    <main className="page viewer-page">
      <h1>Модель не найдена</h1>
      <p>Возможно, модель была удалена или принадлежит другому пользователю.</p>
      <p>
        <Link to="/library">← В библиотеку</Link>
      </p>
    </main>
  );
}

function friendlyGlbError(error: unknown): string {
  if (error instanceof GlbLoadError) {
    switch (error.kind) {
      case 'compression':
        return 'Файл использует сжатую геометрию (Draco/Meshopt). Этот формат пока не поддерживается.';
      case 'http':
        return 'Не удалось скачать файл (ссылка истекла или недоступна).';
      case 'network':
        return 'Не удалось скачать файл (ошибка сети или CORS).';
      case 'parse':
        return 'Файл повреждён или не является корректным GLB.';
      default:
        return 'Не удалось загрузить модель.';
    }
  }
  return 'Не удалось загрузить модель.';
}

function modelExtension(name: string | null | undefined): string {
  if (!name) {
    return '';
  }
  const dot = name.lastIndexOf('.');
  return dot >= 0 ? name.slice(dot).toLowerCase() : '';
}
