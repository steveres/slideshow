// Every call to the Slideshow API goes through here: base URL, access token, errors, expired sign-ins.

import { APP_CONFIG } from '../config.js';
import { currentPath, getAuth, NotSignedInError } from '../auth/auth.js';

/** An error response from the API (RFC 7807 problem details). `code` is the API's machine-readable code. */
export class ApiError extends Error {
  constructor(readonly status: number, readonly code: string | undefined, message: string) {
    super(message);
    this.name = 'ApiError';
  }
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'DELETE';
  json?: unknown;
  form?: FormData;
  /** Don't send the user to sign in on 401 (the caller handles it). */
  noRedirect?: boolean;
}

/** The access token, or (when there is none) a trip to the login page. */
async function token(noRedirect = false): Promise<string> {
  const auth = await getAuth();
  try {
    return await auth.getAccessToken();
  } catch (err) {
    if (err instanceof NotSignedInError && !noRedirect) await auth.signIn(currentPath());
    throw err;
  }
}

/** Turns a failed response into an ApiError, or into a sign-in trip for 401 (expired or revoked token). */
async function failure(status: number, bodyText: string, noRedirect = false): Promise<Error> {
  if (status === 401 && !noRedirect) {
    await (await getAuth()).signIn(currentPath());
    return new NotSignedInError();
  }
  let problem: { detail?: string; title?: string; code?: string } | null = null;
  try { problem = JSON.parse(bodyText); } catch { /* not JSON */ }
  return new ApiError(status, problem?.code, problem?.detail ?? problem?.title ?? `Request failed (${status}).`);
}

async function send(path: string, options: RequestOptions = {}): Promise<Response> {
  const headers: Record<string, string> = { Authorization: `Bearer ${await token(options.noRedirect)}` };
  let body: BodyInit | undefined;
  if (options.json !== undefined) { headers['Content-Type'] = 'application/json'; body = JSON.stringify(options.json); }
  else if (options.form) body = options.form; // the browser sets the multipart boundary

  const res = await fetch(`${APP_CONFIG.apiBaseUrl}${path}`, { method: options.method ?? 'GET', headers, body });
  if (res.ok) return res;
  throw await failure(res.status, await res.text(), options.noRedirect);
}

export const api = {
  async get<T>(path: string, options?: RequestOptions): Promise<T> {
    return (await send(path, options)).json() as Promise<T>;
  },
  async post<T>(path: string, json?: unknown, options?: RequestOptions): Promise<T | undefined> {
    const res = await send(path, { ...options, method: 'POST', json });
    return res.status === 204 ? undefined : res.json() as Promise<T>;
  },
  async delete(path: string, options?: RequestOptions): Promise<void> {
    await send(path, { ...options, method: 'DELETE' });
  },
  /** Downloads a file (e.g. an image) as a Blob. */
  async blob(path: string): Promise<Blob> {
    return (await send(path)).blob();
  },

  /** Uploads a multipart form, reporting progress (0..1). fetch can't report upload progress, so this uses XHR. */
  async upload<T>(path: string, form: FormData, onProgress?: (fraction: number) => void): Promise<T> {
    const bearer = await token();
    const xhr = new XMLHttpRequest();
    xhr.open('POST', `${APP_CONFIG.apiBaseUrl}${path}`);
    xhr.setRequestHeader('Authorization', `Bearer ${bearer}`);
    if (onProgress) xhr.upload.onprogress = e => { if (e.lengthComputable) onProgress(e.loaded / e.total); };
    await new Promise<void>((resolve, reject) => {
      xhr.onload = () => resolve();
      xhr.onerror = () => reject(new TypeError('Network error during upload.'));
      xhr.send(form);
    });
    if (xhr.status >= 200 && xhr.status < 300) return JSON.parse(xhr.responseText) as T;
    throw await failure(xhr.status, xhr.responseText);
  },
};
