import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { logout, meQueryKey, useMe } from '../../api/auth';
import { ApiCheck } from '../api-check/ApiCheck';

/**
 * Library screen placeholder (step 3c.1). The model list with previews,
 * pagination and delete confirmation arrives in step 3c.2 — this page only
 * proves the authenticated route and the logout round trip.
 */
export function LibraryPage() {
  const { data: me } = useMe();

  return (
    <main className="page">
      <h1>Библиотека</h1>
      {me ? <p className="library-greeting">Здравствуйте, {me.login}!</p> : <p>Загрузка…</p>}
      <p>Список моделей появится в шаге 3c.2.</p>
      <LogoutButton />
      <ApiCheck />
    </main>
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
