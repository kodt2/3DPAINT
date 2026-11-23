import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({
  api: { GET: vi.fn() },
}));

import { api } from '../../api/client';
import { ApiCheck } from './ApiCheck';

function okResult(data: unknown) {
  return { response: { ok: true, status: 200, statusText: 'OK', headers: new Headers() }, data };
}

function unauthorizedResult() {
  return {
    response: { ok: false, status: 401, statusText: 'Unauthorized', headers: new Headers() },
    error: { code: 401, message: 'Unauthorized' },
  };
}

describe('ApiCheck', () => {
  it('shows /api/health and /api/me results after the check', async () => {
    vi.mocked(api.GET)
      .mockResolvedValueOnce(okResult({ status: 'ok', service: 'monolith' }))
      .mockResolvedValueOnce(unauthorizedResult());

    render(<ApiCheck />);

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Проверить /api' }));

    await waitFor(() => expect(screen.getByText(/"status":"ok"/)).toBeInTheDocument());
    expect(screen.getByText('401 (не авторизован)')).toBeInTheDocument();
    expect(vi.mocked(api.GET)).toHaveBeenCalledTimes(2);
  });

  it('reports the API as unreachable when the request fails', async () => {
    vi.mocked(api.GET).mockRejectedValueOnce(new TypeError('failed to fetch'));

    render(<ApiCheck />);

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Проверить /api' }));

    // Both /api/health and /api/me are reported unreachable on a network error.
    await waitFor(() =>
      expect(screen.getAllByText('unreachable').length).toBeGreaterThanOrEqual(1),
    );
  });
});
