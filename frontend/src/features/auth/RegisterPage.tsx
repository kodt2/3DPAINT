import { useState, type FormEvent } from 'react';
import { Link, Navigate, useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, meQueryKey, register, useMe } from '../../api/auth';

/**
 * Registration screen (step 3c.1). POST /api/auth/register; on success the
 * monolith creates the user and sets the "jwt" cookie (auto-login), so the
 * SPA lands directly on the library.
 *
 * Errors: 409 — the login is taken (ProblemDetails.detail), 400 — validation
 * (per-field messages from the errors map).
 */
export function RegisterPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { data: me, isPending } = useMe();

  const [fullName, setFullName] = useState('');
  const [birthDate, setBirthDate] = useState('');
  const [loginValue, setLoginValue] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  if (me) {
    return <Navigate to="/library" replace />;
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    setFieldErrors({});
    try {
      const user = await register({ fullName, birthDate, login: loginValue, password });
      queryClient.setQueryData(meQueryKey, user);
      navigate('/library', { replace: true });
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
        <h1>Регистрация</h1>
        <form onSubmit={handleSubmit} noValidate>
          <label className="field">
            <span>Полное имя</span>
            <input
              name="fullName"
              autoComplete="name"
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
              required
              maxLength={100}
            />
            {fieldErrors.fullName?.map((message) => (
              <small key={message} className="field-error">
                {message}
              </small>
            ))}
          </label>
          <label className="field">
            <span>Дата рождения</span>
            <input
              type="date"
              name="birthDate"
              value={birthDate}
              onChange={(e) => setBirthDate(e.target.value)}
              required
            />
            {fieldErrors.birthDate?.map((message) => (
              <small key={message} className="field-error">
                {message}
              </small>
            ))}
          </label>
          <label className="field">
            <span>Логин</span>
            <input
              name="login"
              autoComplete="username"
              value={loginValue}
              onChange={(e) => setLoginValue(e.target.value)}
              required
              maxLength={50}
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
              autoComplete="new-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              minLength={6}
              maxLength={100}
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
            {submitting ? 'Регистрация…' : 'Зарегистрироваться'}
          </button>
        </form>
        <p className="auth-switch">
          Уже есть аккаунт? <Link to="/login">Войти</Link>
        </p>
      </div>
    </main>
  );
}
