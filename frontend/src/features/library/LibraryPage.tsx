import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { logout, meQueryKey, useMe } from '../../api/auth';
import { deleteModel, modelsListKey, useModels, type Model } from '../../api/models';
import { formatDate, formatSize } from '../../lib/format';
import { ApiCheck } from '../api-check/ApiCheck';
import { UploadPanel } from '../upload/UploadPanel';

/**
 * Library screen (step 3c.2): the current user's models with presigned
 * previews, pagination and delete-with-confirmation. Data comes from
 * GET /api/models (owner-scoped in the monolith, ADR 0013).
 */
export function LibraryPage() {
  const { data: me } = useMe();
  const [page, setPage] = useState(1);
  const models = useModels(page);
  const queryClient = useQueryClient();

  // Presigned URLs live for 30 minutes. An expired URL is healed by ONE
  // list refetch per page (fresh signatures); a second failure on the same
  // page renders the fallback and stays quiet. The flag is reset on page
  // change only — not on every refetch — otherwise the "one retry" rule
  // would never hold.
  const previewRetried = useRef(false);
  useEffect(() => {
    previewRetried.current = false;
  }, [page]);

  // After a delete shrinks the list the current page may no longer exist
  // (the last model of the last page was removed); clamp back to the last
  // existing page so the user is not stranded on an empty one.
  useEffect(() => {
    const totalPages = models.data?.totalPages;
    if (totalPages !== undefined && page > totalPages) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- the new totalPages are only known after the post-delete refetch; clamping here is the minimal fix (a render-time clamp would require a second query round-trip)
      setPage(Math.max(1, totalPages));
    }
  }, [models.data, page]);

  function handlePreviewError() {
    if (previewRetried.current) {
      return;
    }
    previewRetried.current = true;
    void queryClient.invalidateQueries({ queryKey: modelsListKey });
  }

  function handleDeleted() {
    void queryClient.invalidateQueries({ queryKey: modelsListKey });
  }

  return (
    <main className="page">
      <h1>Библиотека</h1>
      {me ? <p className="library-greeting">Здравствуйте, {me.login}!</p> : <p>Загрузка…</p>}

      <UploadPanel />

      {models.isPending && <p>Загрузка списка моделей…</p>}
      {models.isError && (
        <p role="alert" className="form-error">
          Не удалось загрузить список моделей.
        </p>
      )}

      {models.data && models.data.items?.length === 0 && <p>У вас пока нет моделей.</p>}

      {models.data && (models.data.items?.length ?? 0) > 0 && (
        <ul className="library-grid">
          {models.data.items!.map((model) => (
            <ModelCard
              key={model.id}
              model={model}
              onPreviewError={handlePreviewError}
              onDeleted={handleDeleted}
            />
          ))}
        </ul>
      )}

      {models.data && (models.data.totalPages ?? 0) > 1 && (
        <nav className="library-pagination" aria-label="Страницы моделей">
          <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
            ← Предыдущая
          </button>
          <span>
            Стр. {models.data.page} из {models.data.totalPages}
          </span>
          <button
            type="button"
            disabled={page >= (models.data.totalPages ?? 1)}
            onClick={() => setPage((p) => p + 1)}
          >
            Следующая →
          </button>
        </nav>
      )}

      <LogoutButton />
      <ApiCheck />
    </main>
  );
}

interface ModelCardProps {
  model: Model;
  onPreviewError: () => void;
  onDeleted: () => void;
}

function ModelCard({ model, onPreviewError, onDeleted }: ModelCardProps) {
  const [confirming, setConfirming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  async function handleDelete() {
    setDeleting(true);
    setDeleteError(null);
    try {
      await deleteModel(model.id ?? '');
      setConfirming(false);
      onDeleted();
    } catch (err) {
      setDeleteError(err instanceof Error ? err.message : 'Не удалось удалить модель.');
    } finally {
      setDeleting(false);
    }
  }

  return (
    <li className="library-card">
      <ModelPreview
        url={model.thumbnailUrl}
        alt={model.name ?? model.id ?? 'model'}
        onError={onPreviewError}
      />
      <h2 className="library-card-name" title={model.name ?? undefined}>
        {model.name}
      </h2>
      <p className="library-card-meta">
        {formatSize(model.sizeBytes ?? 0)} · {formatDate(model.createdAt ?? '')}
      </p>
      <div className="library-card-actions">
        <Link className="library-open-3d" to={`/models/${model.id}`}>
          Открыть 3D
        </Link>
        {confirming ? (
          <>
            <span className="library-confirm-question">Удалить модель?</span>
            <button type="button" className="danger" onClick={handleDelete} disabled={deleting}>
              {deleting ? 'Удаление…' : 'Да, удалить'}
            </button>
            <button type="button" onClick={() => setConfirming(false)} disabled={deleting}>
              Отмена
            </button>
          </>
        ) : (
          <button
            type="button"
            onClick={() => {
              setConfirming(true);
              setDeleteError(null);
            }}
          >
            Удалить
          </button>
        )}
        {deleteError && (
          <p role="alert" className="form-error">
            {deleteError}
          </p>
        )}
      </div>
    </li>
  );
}

/**
 * Presigned preview image. Remembers which URL failed: a fresh URL from a
 * refetch (after an expiry retry) is attempted again, a repeated failure of
 * the same URL renders the fallback instead of a broken image.
 */
function ModelPreview({
  url,
  alt,
  onError,
}: {
  url: string | null | undefined;
  alt: string;
  onError: () => void;
}) {
  const [failedFor, setFailedFor] = useState<string | null>(null);

  if (url === null || url === undefined || failedFor === url) {
    return (
      <div className="library-preview-fallback" aria-hidden="true">
        3D
      </div>
    );
  }

  return (
    <img
      className="library-preview"
      src={url}
      alt={alt}
      loading="lazy"
      onError={() => {
        setFailedFor(url);
        onError();
      }}
    />
  );
}

function LogoutButton() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  async function handleLogout() {
    setBusy(true);
    setFailed(false);
    try {
      await logout();
      queryClient.setQueryData(meQueryKey, null);
      navigate('/login', { replace: true });
    } catch {
      setFailed(true);
      setBusy(false);
    }
  }

  return (
    <div className="library-logout">
      <button type="button" onClick={handleLogout} disabled={busy}>
        {busy ? 'Выход…' : 'Выйти'}
      </button>
      {failed && (
        <p role="alert" className="form-error">
          Не удалось завершить сеанс, попробуйте ещё раз.
        </p>
      )}
    </div>
  );
}
