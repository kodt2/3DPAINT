import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useMe } from '../../api/auth';

/**
 * Gate for authenticated screens (step 3c.1). Waits for /api/me:
 * - pending  → a short "loading" state (no flash of the login form);
 * - 401/null → redirect to /login, remembering the intended destination;
 * - user     → renders the child screen.
 */
export function ProtectedRoute({ children }: { children: ReactNode }) {
  const location = useLocation();
  const { data: me, isPending } = useMe();

  if (isPending) {
    return (
      <main className="page">
        <p>Загрузка…</p>
      </main>
    );
  }

  if (me === null) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  return <>{children}</>;
}
