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

const ME = { login: 'newuser', fullName: 'New User', birthDate: '1995-01-15' };

function anonMe() {
  mockApi.GET.mockResolvedValue(errorResult(401, { title: 'Unauthorized' }));
}

async function fillRegisterForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Полное имя'), ME.fullName);
  await user.type(screen.getByLabelText('Дата рождения'), ME.birthDate);
  await user.type(screen.getByLabelText('Логин'), ME.login);
  await user.type(screen.getByLabelText('Пароль'), 'secret-pass-1');
}

describe('RegisterPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('registers and lands on the library', async () => {
    const user = userEvent.setup();
    // Stateful mock mirroring the real cookie semantics: /api/me answers
    // 401 until the register POST succeeds (sets the cookie), then 200.
    let authenticated = false;
    mockApi.GET.mockImplementation(() =>
      Promise.resolve(authenticated ? okResult(ME) : errorResult(401, { title: 'Unauthorized' })),
    );
    mockApi.POST.mockImplementation(() => {
      authenticated = true;
      return Promise.resolve(okResult(ME, 201));
    });

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
      ['/register'],
    );

    await fillRegisterForm(user);
    await user.click(screen.getByRole('button', { name: 'Зарегистрироваться' }));

    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/library'));
    expect(await screen.findByText('Здравствуйте, newuser!')).toBeInTheDocument();
  });

  it('shows the 409 message when the login is taken', async () => {
    const user = userEvent.setup();
    anonMe();
    mockApi.POST.mockResolvedValue(
      errorResult(409, {
        title: 'Conflict',
        status: 409,
        detail: 'Такой логин уже существует',
      }),
    );

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
      ['/register'],
    );

    await fillRegisterForm(user);
    await user.click(screen.getByRole('button', { name: 'Зарегистрироваться' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Такой логин уже существует');
    expect(screen.getByTestId('location')).toHaveTextContent('/register');
  });

  it('shows per-field errors for a 400 validation response', async () => {
    const user = userEvent.setup();
    anonMe();
    mockApi.POST.mockResolvedValue(
      errorResult(400, {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: {
          Password: ['The field Password must be a string with a minimum length of 6.'],
        },
      }),
    );

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
      ['/register'],
    );

    await fillRegisterForm(user);
    await user.click(screen.getByRole('button', { name: 'Зарегистрироваться' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Проверьте правильность заполнения полей.',
    );
    expect(
      screen.getByText('The field Password must be a string with a minimum length of 6.'),
    ).toBeInTheDocument();
  });

  it('redirects an already authenticated user to the library', async () => {
    mockApi.GET.mockResolvedValue(okResult(ME));

    renderWithProviders(
      <>
        <App />
        <LocationProbe />
      </>,
      ['/register'],
    );

    // The location probe exists immediately (pending state); wait until
    // /api/me resolves and the redirect is applied.
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/library'));
  });
});
