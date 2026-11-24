import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../../api/client';
import { renderWithProviders, okResult, errorResult } from '../auth/testUtils';
import { LibraryPage } from './LibraryPage';

vi.mock('../../api/client', () => ({
  api: { GET: vi.fn(), POST: vi.fn(), DELETE: vi.fn() },
}));

const mockApi = api as unknown as {
  GET: ReturnType<typeof vi.fn>;
  POST: ReturnType<typeof vi.fn>;
  DELETE: ReturnType<typeof vi.fn>;
};

const ME = { login: 'owner', fullName: 'Owner', birthDate: '1990-01-01' };
const THUMB_1 = 'http://localhost:9000/userdata/1/thumbnails/a.svg?sig=1';
const THUMB_2 = 'http://localhost:9000/userdata/1/thumbnails/a.svg?sig=2';
const MODEL_A = {
  id: 'a1',
  name: 'first.glb',
  sizeBytes: 1234,
  createdAt: '2026-11-24T10:00:00Z',
  thumbnailUrl: THUMB_1,
};
const MODEL_B = {
  id: 'b1',
  name: 'second.glb',
  sizeBytes: 2048,
  createdAt: '2026-11-24T11:00:00Z',
  thumbnailUrl: null,
};

function pageDto(items: unknown[], page = 1, totalPages = 1) {
  return { items, page, pageSize: 20, totalCount: items.length, totalPages };
}

/**
 * Stateful GET mock: /api/me always resolves; /api/models serves
 * `listResponses` in call order (the last one is repeated forever).
 */
function mockApiWithLists(listResponses: unknown[]) {
  let listCalls = 0;
  mockApi.GET.mockImplementation((path: string) => {
    if (path === '/api/me') {
      return Promise.resolve(okResult(ME));
    }
    const body = listResponses[Math.min(listCalls++, listResponses.length - 1)];
    return Promise.resolve(okResult(body));
  });
  return () => listCalls;
}

function cardOf(name: string): HTMLElement {
  const card = screen.getByText(name).closest('.library-card');
  if (card === null) {
    throw new Error(`card for ${name} not found`);
  }
  return card as HTMLElement;
}

beforeEach(() => {
  vi.clearAllMocks();
  mockApi.POST.mockResolvedValue(okResult(undefined));
  mockApi.DELETE.mockResolvedValue(okResult(undefined, 204));
});

describe('LibraryPage (step 3c.2)', () => {
  it('renders the user models with presigned previews and size', async () => {
    mockApiWithLists([pageDto([MODEL_A, MODEL_B])]);
    renderWithProviders(<LibraryPage />, ['/library']);

    await screen.findByText('first.glb');
    expect(screen.getByText('second.glb')).toBeDefined();
    expect(screen.getByText('Здравствуйте, owner!')).toBeDefined();

    const img = screen.getByAltText('first.glb') as HTMLImageElement;
    expect(img).toHaveAttribute('src', THUMB_1);
    // 1234 bytes -> "1,2 КБ" (ru-RU decimal separator)
    expect(screen.getByText(/1[,.]2 КБ/)).toBeDefined();
    // A model without a thumbnail renders the fallback, not a broken img.
    expect(within(cardOf('second.glb')).queryByRole('img')).toBeNull();
    // One page only: no pagination controls.
    expect(screen.queryByRole('navigation', { name: 'Страницы моделей' })).toBeNull();
  });

  it('shows the empty state when the user has no models', async () => {
    mockApiWithLists([pageDto([])]);
    renderWithProviders(<LibraryPage />, ['/library']);

    await screen.findByText('У вас пока нет моделей.');
  });

  it('requests the next page with page/pageSize params', async () => {
    mockApiWithLists([pageDto([MODEL_A], 1, 2), pageDto([MODEL_B], 2, 2)]);
    renderWithProviders(<LibraryPage />, ['/library']);

    await screen.findByText('Стр. 1 из 2');
    expect(screen.getByRole('button', { name: '← Предыдущая' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Следующая →' }));
    await waitFor(() => {
      expect(mockApi.GET).toHaveBeenCalledWith('/api/models', {
        params: { query: { page: 2, pageSize: 20 } },
      });
    });
    expect(await screen.findByText('Стр. 2 из 2')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Следующая →' })).toBeDisabled();
  });

  it('deletes a model after the inline confirmation and refetches the list', async () => {
    mockApiWithLists([pageDto([MODEL_A, MODEL_B]), pageDto([MODEL_B])]);
    renderWithProviders(<LibraryPage />, ['/library']);

    await screen.findByText('first.glb');
    fireEvent.click(within(cardOf('first.glb')).getByRole('button', { name: 'Удалить' }));

    // The confirmation appears; the DELETE request is not sent yet.
    await screen.findByText('Удалить модель?');
    expect(mockApi.DELETE).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Да, удалить' }));
    await waitFor(() => {
      expect(mockApi.DELETE).toHaveBeenCalledWith('/api/models/{id}', {
        params: { path: { id: 'a1' } },
      });
    });

    // The refetched list no longer contains the deleted model.
    expect(await screen.findByText('second.glb')).toBeDefined();
    await waitFor(() => {
      expect(screen.queryByText('first.glb')).toBeNull();
    });
  });

  it('does not call DELETE when the confirmation is cancelled', async () => {
    mockApiWithLists([pageDto([MODEL_A])]);
    renderWithProviders(<LibraryPage />, ['/library']);

    await screen.findByText('first.glb');
    fireEvent.click(within(cardOf('first.glb')).getByRole('button', { name: 'Удалить' }));
    await screen.findByText('Удалить модель?');

    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    await waitFor(() => {
      expect(screen.queryByText('Удалить модель?')).toBeNull();
    });
    expect(mockApi.DELETE).not.toHaveBeenCalled();
  });

  it('keeps the model and shows the ProblemDetails detail on delete failure', async () => {
    mockApiWithLists([pageDto([MODEL_A])]);
    mockApi.DELETE.mockResolvedValue(
      errorResult(500, { detail: 'Не удалось удалить файлы в хранилище, попробуйте ещё раз.' }),
    );
    renderWithProviders(<LibraryPage />, ['/library']);

    await screen.findByText('first.glb');
    fireEvent.click(within(cardOf('first.glb')).getByRole('button', { name: 'Удалить' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Да, удалить' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Не удалось удалить файлы в хранилище, попробуйте ещё раз.',
    );
    expect(screen.getByText('first.glb')).toBeDefined();
  });

  it('refetches the list exactly once when a preview URL has expired', async () => {
    const A_STALE = { ...MODEL_A, thumbnailUrl: THUMB_1 };
    const A_FRESH = { ...MODEL_A, thumbnailUrl: THUMB_2 };
    const listCalls = mockApiWithLists([pageDto([A_STALE]), pageDto([A_FRESH])]);
    const { container } = renderWithProviders(<LibraryPage />, ['/library']);

    const img = (await screen.findByAltText('first.glb')) as HTMLImageElement;
    expect(img).toHaveAttribute('src', THUMB_1);

    // The 30-minute-old URL 403s: one list refetch brings fresh signatures.
    fireEvent.error(img);
    await waitFor(() => {
      expect(container.querySelector(`img[src="${THUMB_2}"]`)).not.toBeNull();
    });
    const freshImg = container.querySelector(`img[src="${THUMB_2}"]`) as HTMLImageElement;
    expect(listCalls()).toBe(2);

    // A second failure must NOT trigger another refetch: the fallback
    // appears and the request count stays put.
    fireEvent.error(freshImg);
    await waitFor(() => {
      expect(container.querySelector('.library-preview-fallback')).not.toBeNull();
    });
    expect(listCalls()).toBe(2);
  });
});
