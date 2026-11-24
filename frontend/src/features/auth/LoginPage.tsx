import { useState, type FormEvent } from 'react';
import { Link, Navigate, useLocation, useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, login, meQueryKey, useMe } from '../../api/auth';

/**
 * Login screen (step 3c.1). Talks to POST /api/auth/login; on success the
 * monolith sets the "jwt" cookie and we switch the /api/me cache so the
 * protected routes open immediately.
 *
 * Errors come from the monolith as ProblemDetails (RFC 7807): 401 — wrong
 * credentials, 400 — validation. The message is shown verbatim from `detail`.
 */
export function LoginPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const { data: me, isPending } = useMe();

  const [loginValue, setLoginValue] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  // Already authenticated (e.g. back/forward navigation): go to the app.
  if (me) {
    return <Navigate to="/library" replace />;
  }

  // ProtectedRoute passes the original destination in the location state.
  const from = (location.state as { from?: string } | null)?.from ?? '/library';

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    setFieldErrors({});
    try {
      const user = await login(loginValue, password);
      queryClient.setQueryData(meQueryKey, user);
      navigate(from, { replace: true });
    } catch (err) {
      if (err instanceof ApiError) {
        if (err.status === 400) {
          setFieldErrors(err.fieldErrors);
          setError('Проверьте правильность заполнения полей.');
        } else {
          setError(err.message);
        }
      } else {
        setError('Не удалось соединиться с сервером.');
      }
      setSubmitting(false);
    }
  }

  return (
    <main className="page">
      <div className="auth-card">
        <h1>Вход</h1>
        <form onSubmit={handleSubmit} noValidate>
          <label className="field">
            <span>Логин</span>
            <input
              name="login"
              autoComplete="username"
              value={loginValue}
              onChange={(e) => setLoginValue(e.target.value)}
              required
            />
            {fieldErrors.login?.map((message) => (
              <small key={message} className="field-error">
                {message}
              </small>
            ))}
          </label>
          <label className="field">
            <span>Пароль</span>
            <input
              type="password"
              name="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
            />
            {fieldErrors.password?.map((message) => (
              <small key={message} className="field-error">
                {message}
              </small>
            ))}
          </label>
          {error && (
            <p role="alert" className="form-error">
              {error}
            </p>
          )}
          <button type="submit" disabled={submitting || isPending}>
            {submitting ? 'Вход…' : 'Войти'}
          </button>
        </form>
        <p className="auth-switch">
          Нет аккаунта? <Link to="/register">Зарегистрироваться</Link>
        </p>
      </div>
    </main>
  );
}
