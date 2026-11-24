import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({
  api: { GET: vi.fn(), POST: vi.fn() },
}));

import { api } from '../../api/client';
import App from '../../App';
import { LocationProbe } from './LocationProbe';
import { errorResult, okResult, renderWithProviders } from './testUtils';

/**
 * The vi.mock above replaces the generated client with plain vi.fn()s; cast
 * it once so queued results do not have to repeat openapi-fetch's strict
 * FetchResponse shape (the mock never runs the real client).
 */
const mockApi = api as unknown as {
  GET: ReturnType<typeof vi.fn>;
  POST: ReturnType<typeof vi.fn>;
};

const ME = { login: 'user1', fullName: 'User One', birthDate: '1990-05-01' };

function anonMe() {
  // /api/me answers 401 until the login is performed. Sticky: background
  // refetches must not break the test.
  mockApi.GET.mockResolvedValue(errorResult(401, { title: 'Unauthorized' }));
}

async function fillLoginForm(user: ReturnType<typeof userEvent.setup>) {
  // The tests start on "/": wait for the IndexRedirect to land on /login
  // (it awaits /api/me) before the form exists.
  await user.type(await screen.findByLabelText('Логин'), ME.login);
  await user.type(await screen.findByLabelText('Пароль'), 'correct-pass');
}

describe('LoginPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows the 401 ProblemDetails message when credentials are wrong', async () => {
    const user = userEvent.setup();
    anonMe();
    mockApi.POST.mockResolvedValue(
      errorResult(401, {
        title: 'Unauthorized',
        status: 401,
        detail: 'Неверный логин или пароль',
      }),
    );

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
    );

    await fillLoginForm(user);
    await user.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Неверный логин или пароль');
    expect(screen.getByTestId('location')).toHaveTextContent('/login');
  });

  it('logs in and opens the library on success', async () => {
    const user = userEvent.setup();
    // Stateful mock mirroring the real cookie semantics: /api/me answers
    // 401 until the login POST succeeds (sets the cookie), then 200 —
    // the background refetch on the library page must see the session.
    let authenticated = false;
    mockApi.GET.mockImplementation(() =>
      Promise.resolve(authenticated ? okResult(ME) : errorResult(401, { title: 'Unauthorized' })),
    );
    mockApi.POST.mockImplementation(() => {
      authenticated = true;
      return Promise.resolve(okResult(ME));
    });

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
    );

    await fillLoginForm(user);
    await user.click(screen.getByRole('button', { name: 'Войти' }));

    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/library'));
    expect(await screen.findByText('Здравствуйте, user1!')).toBeInTheDocument();
  });

  it('shows per-field errors for a 400 validation response', async () => {
    const user = userEvent.setup();
    anonMe();
    mockApi.POST.mockResolvedValue(
      errorResult(400, {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { Password: ['The Password field is required.'] },
      }),
    );

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
    );

    await fillLoginForm(user);
    await user.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Проверьте правильность заполнения полей.',
    );
    expect(screen.getAllByText('The Password field is required.').length).toBeGreaterThan(0);
  });

  it('redirects an already authenticated user to the library', async () => {
    mockApi.GET.mockResolvedValue(okResult(ME));

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
      ['/login'],
    );

    // The location probe exists immediately (pending state); wait until
    // /api/me resolves and the redirect is applied.
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/library'));
  });

  it('shows a connection error when the request fails', async () => {
    const user = userEvent.setup();
    anonMe();
    mockApi.POST.mockRejectedValue(new TypeError('fetch failed'));

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
    );

    await fillLoginForm(user);
    await user.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Не удалось соединиться с сервером.',
    );
  });
});
