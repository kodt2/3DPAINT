import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router';
import { useMe } from './api/auth';
import { LoginPage } from './features/auth/LoginPage';
import { ProtectedRoute } from './features/auth/ProtectedRoute';
import { RegisterPage } from './features/auth/RegisterPage';
import { LibraryPage } from './features/library/LibraryPage';

/**
 * Lazy chunk (step 3c.3): three.js is only bundled into this route, so the
 * login and library screens do not download it.
 */
const ViewerPage = lazy(() => import('./features/viewer/ViewerPage'));

/**
 * SPA routes (step 3c.1). The SPA is served under /spa/** by the Gateway
 * (React Router basename in main.tsx); the Razor UI keeps the site root.
 */
export default function App() {
  return (
    <Routes>
      <Route path="/" element={<IndexRedirect />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route
        path="/library"
        element={
          <ProtectedRoute>
            <LibraryPage />
          </ProtectedRoute>
        }
      />
      <Route
        path="/models/:id"
        element={
          <ProtectedRoute>
            <Suspense
              fallback={
                <main className="page">
                  <p>Загрузка 3D-вьюера…</p>
                </main>
              }
            >
              <ViewerPage />
            </Suspense>
          </ProtectedRoute>
        }
      />
      {/* Unknown SPA paths go through the index redirect. */}
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

/**
 * Landing: authenticated users go to the library, guests to the login
 * screen (waiting for /api/me, no flash of the wrong destination).
 */
function IndexRedirect() {
  const { data: me, isPending } = useMe();

  if (isPending) {
    return (
      <main className="page">
        <p>Загрузка…</p>
      </main>
    );
  }

  return <Navigate to={me ? '/library' : '/login'} replace />;
}
