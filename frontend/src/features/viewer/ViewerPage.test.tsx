import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import * as THREE from 'three';
import { ApiError } from '../../api/auth';
import { getModel } from '../../api/models';
import { GlbLoadError, loadGlb } from './glb-loader';
import type { ParsedGlb } from './glb-loader';
import ViewerPage from './ViewerPage';

vi.mock('./ModelScene', () => ({
  ModelScene: (props: { object: object }) => (
    <div data-testid="model-scene" data-object={String(Boolean(props.object))} />
  ),
}));

vi.mock('./webgl', () => ({ isWebGLAvailable: () => true }));

// Keep the real GlbLoadError (the page checks instanceof); stub only loadGlb.
vi.mock('./glb-loader', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./glb-loader')>();
  return { ...actual, loadGlb: vi.fn() };
});

vi.mock('../../api/models', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/models')>();
  return { ...actual, getModel: vi.fn() };
});

const getModelMock = vi.mocked(getModel);
const loadGlbMock = vi.mocked(loadGlb);

function detail(name: string, downloadUrl: string | null) {
  return {
    id: 'id-1',
    name,
    extension: name.split('.').at(-1),
    sizeBytes: 123,
    createdAt: '',
    thumbnailUrl: null,
    downloadUrl,
  } as never;
}

function renderPage(id = 'id-1') {
  return render(
    <MemoryRouter initialEntries={[`/models/${id}`]}>
      <Routes>
        <Route path="/models/:id" element={<ViewerPage />} />
        <Route path="/library" element={<div>library</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

function emptyScene(): ParsedGlb {
  return { scene: new THREE.Group() };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('ViewerPage', () => {
  it('shows the not-found page for a 404 (a model that is not ours)', async () => {
    getModelMock.mockRejectedValue(new ApiError(404, { title: 'Model not found' }));

    renderPage('someone-elses-id');

    expect(await screen.findByRole('heading', { name: 'Модель не найдена' })).toBeTruthy();
    expect(
      within(screen.getByRole('main')).getByRole('link', { name: /В библиотеку/ }),
    ).toBeTruthy();
    expect(loadGlbMock).not.toHaveBeenCalled();
  });

  it('shows a notice for non-GLB extensions and never downloads', async () => {
    getModelMock.mockResolvedValue(detail('triangle.stl', 'http://s3/dl?sig=a'));

    renderPage();

    expect(await screen.findByText(/Просмотр 3D недоступен для формата \.stl/)).toBeTruthy();
    expect(loadGlbMock).not.toHaveBeenCalled();
    expect(screen.queryByTestId('model-scene')).toBeNull();
  });

  it('shows a friendly error for a corrupt GLB', async () => {
    getModelMock.mockResolvedValue(detail('cube.glb', 'http://s3/dl?sig=a'));
    loadGlbMock.mockRejectedValue(new GlbLoadError('JSON parse failed', 'parse'));

    renderPage();

    expect(await screen.findByText('Файл повреждён или не является корректным GLB.')).toBeTruthy();
    expect(loadGlbMock).toHaveBeenCalledTimes(1);
  });

  it('shows a friendly error for a compressed-file decode failure', async () => {
    getModelMock.mockResolvedValue(detail('cube.glb', 'http://s3/dl?sig=a'));
    loadGlbMock.mockRejectedValue(new GlbLoadError('DRACO decode failed', 'compression'));

    renderPage();

    expect(
      await screen.findByText(
        'Не удалось декодировать сжатые данные файла (Draco/Meshopt/KTX2). Файл повреждён.',
      ),
    ).toBeTruthy();
  });

  it('names the unsupported glTF extension in the error', async () => {
    getModelMock.mockResolvedValue(detail('cube.glb', 'http://s3/dl?sig=a'));
    loadGlbMock.mockRejectedValue(
      new GlbLoadError('file requires a glTF extension', 'unsupported-extension', 'FOO_baz'),
    );

    renderPage();

    expect(
      await screen.findByText(
        'Файл использует glTF-расширение, которое вьюер не поддерживает: FOO_baz.',
      ),
    ).toBeTruthy();
  });

  it('shows a friendly error when the decode does not finish in time', async () => {
    getModelMock.mockResolvedValue(detail('cube.glb', 'http://s3/dl?sig=a'));
    loadGlbMock.mockRejectedValue(new GlbLoadError('decode did not finish', 'decode-timeout'));

    renderPage();

    expect(
      await screen.findByText(
        'Декодирование файла не завершилось за 60 с. Попробуйте ещё раз или проверьте файл.',
      ),
    ).toBeTruthy();
  });

  it('refetches the DTO once when the presigned URL fails, then loads', async () => {
    const firstUrl = 'http://s3/dl?sig=expired';
    const secondUrl = 'http://s3/dl?sig=fresh';
    getModelMock
      .mockResolvedValueOnce(detail('cube.glb', firstUrl))
      .mockResolvedValueOnce(detail('cube.glb', secondUrl));
    loadGlbMock
      .mockRejectedValueOnce(new GlbLoadError('http 403', 'http'))
      .mockResolvedValueOnce(emptyScene());

    renderPage();

    expect(await screen.findByTestId('model-scene')).toBeTruthy();
    expect(getModelMock).toHaveBeenCalledTimes(2);
    expect(loadGlbMock).toHaveBeenCalledTimes(2);
    expect(loadGlbMock.mock.calls[0]?.[0]).toBe(firstUrl);
    expect(loadGlbMock.mock.calls[1]?.[0]).toBe(secondUrl);
  });

  it('gives up after one presigned retry (no retry loop)', async () => {
    getModelMock
      .mockResolvedValueOnce(detail('cube.glb', 'http://s3/dl?sig=a'))
      .mockResolvedValueOnce(detail('cube.glb', 'http://s3/dl?sig=b'));
    loadGlbMock.mockRejectedValue(new GlbLoadError('http 403', 'http'));

    renderPage();

    expect(
      await screen.findByText('Не удалось скачать файл (ссылка истекла или недоступна).'),
    ).toBeTruthy();
    expect(loadGlbMock).toHaveBeenCalledTimes(2);
  });

  it('shows a load error instead of hanging when downloadUrl is missing', async () => {
    getModelMock.mockResolvedValue(detail('cube.glb', null));

    renderPage();

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByText('Не удалось загрузить модель.')).toBeTruthy();
    expect(loadGlbMock).not.toHaveBeenCalled();
  });

  it('aborts the in-flight download when the page unmounts', async () => {
    getModelMock.mockResolvedValue(detail('cube.glb', 'http://s3/dl?sig=a'));
    let signal: AbortSignal | undefined;
    loadGlbMock.mockImplementation((_url, options) => {
      signal = options.signal;
      return new Promise<ParsedGlb>(() => {});
    });

    const { unmount } = renderPage();
    await vi.waitFor(() => {
      expect(loadGlbMock).toHaveBeenCalledTimes(1);
    });
    unmount();

    expect(signal?.aborted).toBe(true);
  });

  it('shows the info panel with vertices, triangles and UV state once the scene is ready', async () => {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute(
      'position',
      new THREE.BufferAttribute(new Float32Array([0, 0, 0, 1, 0, 0, 0, 1, 0]), 3),
    );
    geometry.setAttribute('uv', new THREE.BufferAttribute(new Float32Array([0, 0, 1, 0, 0, 1]), 2));
    const scene = new THREE.Group();
    scene.add(new THREE.Mesh(geometry, new THREE.MeshStandardMaterial()));

    getModelMock.mockResolvedValue(detail('cube.glb', 'http://s3/dl?sig=a'));
    loadGlbMock.mockResolvedValue({ scene });

    renderPage();

    expect(await screen.findByTestId('model-scene')).toBeTruthy();
    const panel = screen.getByRole('complementary', { name: /Информация о модели/ });
    const row = (label: string): HTMLElement => within(panel).getByText(label).closest('div')!;
    expect(within(row('Вершины')).getByText('3')).toBeTruthy();
    expect(within(row('Треугольники')).getByText('1')).toBeTruthy();
    expect(within(row('Мешей')).getByText('1')).toBeTruthy();
    expect(within(row('Материалов')).getByText('1')).toBeTruthy();
    expect(within(row('Текстур')).getByText('0')).toBeTruthy();
    expect(within(row('UV')).getByText('да')).toBeTruthy();
    expect(row('Размеры (X × Y × Z)').textContent).toMatch(/×/);
  });
});
