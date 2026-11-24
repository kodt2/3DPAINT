import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../auth/testUtils';
import { UploadPanel } from './UploadPanel';

type Entry = {
  file: File;
  resolve: () => void;
  reject: (err: unknown) => void;
  abort: () => void;
};

const h = vi.hoisted(() => {
  const started: Entry[] = [];
  class UploadError extends Error {
    kind: 'http' | 'network' | 'aborted';
    status?: number;
    detail?: string;
    constructor(kind: 'http' | 'network' | 'aborted', status?: number, detail?: string) {
      super(detail ?? kind);
      this.name = 'UploadError';
      this.kind = kind;
      this.status = status;
      this.detail = detail;
    }
  }
  return { started, UploadError };
});

vi.mock('./upload-client', () => ({
  UploadError: h.UploadError,
  uploadFile: (file: File): { done: Promise<void>; abort: () => void } => {
    const entry: Entry = {
      file,
      resolve: () => undefined,
      reject: () => undefined,
      abort: () => undefined,
    };
    entry.abort = () => entry.reject(new h.UploadError('aborted'));
    h.started.push(entry);
    return {
      done: new Promise<void>((resolve, reject) => {
        entry.resolve = resolve;
        entry.reject = (err: unknown) => reject(err);
      }),
      abort: () => entry.abort(),
    };
  },
}));

vi.mock('../../api/config', () => ({
  appConfigQueryKey: ['appConfig'],
  getAppConfig: vi.fn(() =>
    Promise.resolve({ maxUploadBytes: 10_000, allowedUploadExtensions: ['.glb'] }),
  ),
}));

function pickFiles(name: string, size = 10) {
  const input = screen.getByLabelText('Файлы для загрузки (GLB)') as HTMLInputElement;
  const file = new File([new Uint8Array(size)], name);
  fireEvent.change(input, { target: { files: [file] } });
  return file;
}

async function openDropzone() {
  await waitFor(() => {
    expect(screen.getByRole('button', { name: 'Выбрать файлы (GLB)' })).toBeTruthy();
  });
}

beforeEach(() => {
  h.started.length = 0;
});

describe('UploadPanel', () => {
  it('uploads a valid GLB and shows the done state', async () => {
    renderWithProviders(<UploadPanel />);
    await openDropzone();

    pickFiles('model.glb');
    await waitFor(() => expect(h.started.length).toBe(1));
    h.started[0].resolve();
    await waitFor(() => expect(screen.getByText('Готово')).toBeTruthy());
    expect(h.started.length).toBe(1);
    expect(h.started[0].file.name).toBe('model.glb');
  });

  it('rejects a non-GLB file locally, without any upload', async () => {
    renderWithProviders(<UploadPanel />);
    await openDropzone();

    pickFiles('model.stl');
    await waitFor(() =>
      expect(screen.getByText('Допускается только формат .glb.')).toBeTruthy(),
    );
    expect(h.started.length).toBe(0);
    // A client-side rejection offers no retry.
    expect(screen.queryByRole('button', { name: 'Повторить' })).toBeNull();
  });

  it('rejects an oversized file locally and names the limit', async () => {
    renderWithProviders(<UploadPanel />);
    await openDropzone();

    pickFiles('big.glb', 20_000);
    await waitFor(() => expect(screen.getByText(/лимит/)).toBeTruthy());
    expect(h.started.length).toBe(0);
  });

  it('lets the user cancel a queued file', async () => {
    renderWithProviders(<UploadPanel />);
    await openDropzone();

    pickFiles('a.glb');
    pickFiles('b.glb');
    pickFiles('c.glb'); // the third waits: max 2 in flight
    await waitFor(() => expect(screen.getByText('В очереди')).toBeTruthy());

    const cancelButtons = screen.getAllByRole('button', { name: 'Отменить' });
    fireEvent.click(cancelButtons[cancelButtons.length - 1]);
    await waitFor(() => expect(screen.getByText('Отменено')).toBeTruthy());
    expect(h.started.length).toBe(2);
  });

  it('offers a retry for a 500 and re-uploads on click', async () => {
    renderWithProviders(<UploadPanel />);
    await openDropzone();

    pickFiles('model.glb');
    await waitFor(() => expect(h.started.length).toBe(1));
    h.started[0].reject(new h.UploadError('http', 500));

    await waitFor(() =>
      expect(screen.getByText('Сервер временно недоступен. Попробуйте ещё раз.')).toBeTruthy(),
    );
    expect(h.started.length).toBe(1);

    fireEvent.click(screen.getByRole('button', { name: 'Повторить' }));
    await waitFor(() => expect(h.started.length).toBe(2));
  });

  it('aborts an in-flight upload on Отменить', async () => {
    renderWithProviders(<UploadPanel />);
    await openDropzone();

    pickFiles('model.glb');
    await waitFor(() => expect(h.started.length).toBe(1));
    expect(screen.getByText(/Загрузка…/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Отменить' }));
    await waitFor(() => expect(screen.getByText('Отменено')).toBeTruthy());
  });
});
