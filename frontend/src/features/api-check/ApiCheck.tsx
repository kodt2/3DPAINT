import { useState } from 'react';
import { api } from '../../api/client';

type CheckState =
  { status: 'idle' } | { status: 'loading' } | { status: 'done'; health: string; me: string };

/**
 * Connection check against the monolith JSON API through the Gateway
 * (same origin, cookie auth). /api/health is public; /api/me answers 401
 * until the user logs in (the login screen is migrated in Phase 3).
 */
export function ApiCheck() {
  const [state, setState] = useState<CheckState>({ status: 'idle' });

  async function runCheck() {
    setState({ status: 'loading' });
    try {
      const healthResult = await api.GET('/api/health');
      const health = healthResult.response.ok ? JSON.stringify(healthResult.data) : 'unreachable';

      const meResult = await api.GET('/api/me');
      const me =
        meResult.response.status === 401 ? '401 (не авторизован)' : JSON.stringify(meResult.data);

      setState({ status: 'done', health, me });
    } catch {
      // Network failure (Gateway down, DNS, ...): report both as unreachable.
      setState({ status: 'done', health: 'unreachable', me: 'unreachable' });
    }
  }

  return (
    <div className="api-check">
      <button type="button" onClick={runCheck} disabled={state.status === 'loading'}>
        Проверить /api
      </button>
      {state.status === 'done' && (
        <dl className="api-check-result">
          <dt>/api/health</dt>
          <dd>{state.health}</dd>
          <dt>/api/me</dt>
          <dd>{state.me}</dd>
        </dl>
      )}
    </div>
  );
}
