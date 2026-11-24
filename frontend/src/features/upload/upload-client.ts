/**
 * XHR-based upload client (step 3c.4). XMLHttpRequest instead of fetch:
 * it is the only standard way to observe byte-level upload progress
 * (xhr.upload.onprogress) and to abort the request from the UI. Same
 * origin — the Gateway (or the Vite dev proxy) serves /api/**, so cookies
 * flow without any extra headers.
 */

export interface UploadProgress {
  loaded: number;
  total: number;
}

export interface UploadHandle {
  /** Resolves on 2xx; rejects with UploadError on http/network/abort. */
  readonly done: Promise<void>;
  /** Aborts the in-flight request; `done` settles with kind 'aborted'. */
  abort(): void;
}

export type UploadErrorKind = 'http' | 'network' | 'aborted';

/**
 * Failure of an upload.
 * - http:    the server answered; `status` is set. `detail` carries the
 *            ProblemDetails `detail` (or the legacy `message`) when the
 *            body was JSON — a Gateway 413/400 body is not JSON and stays
 *            undefined.
 * - network: the request never got an answer (offline, DNS, timeout, …).
 * - aborted: the user cancelled the upload — not a failure, never "error".
 */
export class UploadError extends Error {
  constructor(
    readonly kind: UploadErrorKind,
    readonly status: number | undefined = undefined,
    readonly detail: string | undefined = undefined,
  ) {
    super(detail ?? `upload ${kind} failure${status !== undefined ? ` (${status})` : ''}`);
    this.name = 'UploadError';
  }
}

const UPLOAD_TIMEOUT_MS = 10 * 60 * 1000;
const UPLOAD_ENDPOINT = '/api/files/upload';

export function uploadFile(file: File, onProgress?: (progress: UploadProgress) => void): UploadHandle {
  const xhr = new XMLHttpRequest();

  let resolveDone: () => void = () => undefined;
  let rejectDone: (err: UploadError) => void = () => undefined;
  let settled = false;

  const done = new Promise<void>((resolve, reject) => {
    resolveDone = resolve;
    rejectDone = reject;
  });

  const settleWith = (fn: () => void) => {
    if (settled) {
      return;
    }
    settled = true;
    fn();
  };

  xhr.open('POST', UPLOAD_ENDPOINT);
  xhr.responseType = 'text';
  xhr.timeout = UPLOAD_TIMEOUT_MS;

  xhr.upload.onprogress = (event) => {
    if (event.lengthComputable && event.total > 0) {
      onProgress?.({ loaded: event.loaded, total: event.total });
    }
  };

  xhr.onload = () => {
    settleWith(() => {
      if (xhr.status >= 200 && xhr.status < 300) {
        resolveDone();
        return;
      }
      rejectDone(new UploadError('http', xhr.status, parseErrorDetail(xhr.responseText)));
    });
  };

  // No answer at all (offline, connection reset, timeout).
  xhr.onerror = () => {
    settleWith(() => rejectDone(new UploadError('network')));
  };
  xhr.ontimeout = () => {
    settleWith(() => rejectDone(new UploadError('network')));
  };
  xhr.onabort = () => {
    settleWith(() => rejectDone(new UploadError('aborted')));
  };

  const form = new FormData();
  form.append('file', file);
  xhr.send(form);

  return {
    done,
    abort: () => {
      if (!settled) {
        xhr.abort();
      }
    },
  };
}

/**
 * Pulls a human-readable message out of an error body: ProblemDetails
 * carries `detail` (step 3c.4 contract), the legacy shape carries
 * `message`. Non-JSON bodies (a bare Gateway 413) yield undefined.
 */
function parseErrorDetail(body: string): string | undefined {
  if (!body) {
    return undefined;
  }
  try {
    const parsed = JSON.parse(body) as { detail?: unknown; message?: unknown };
    if (typeof parsed.detail === 'string' && parsed.detail.length > 0) {
      return parsed.detail;
    }
    if (typeof parsed.message === 'string' && parsed.message.length > 0) {
      return parsed.message;
    }
    return undefined;
  } catch {
    return undefined;
  }
}
