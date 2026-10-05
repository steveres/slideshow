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

async function send(path: string, options: RequestOptions = {}): Promise<Response> {
  const auth = await getAuth();
  let token: string;
  try {
    token = await auth.getAccessToken();
  } catch (err) {
    if (err instanceof NotSignedInError && !options.noRedirect) await auth.signIn(currentPath());
    throw err;
  }

  const headers: Record<string, string> = { Authorization: `Bearer ${token}` };
  let body: BodyInit | undefined;
  if (options.json !== undefined) { headers['Content-Type'] = 'application/json'; body = JSON.stringify(options.json); }
  else if (options.form) body = options.form; // the browser sets the multipart boundary

  const res = await fetch(`${APP_CONFIG.apiBaseUrl}${path}`, { method: options.method ?? 'GET', headers, body });
  if (res.ok) return res;

  // Token expired or revoked (e.g. signed out on another device): sign in again.
  if (res.status === 401 && !options.noRedirect) {
    await auth.signIn(currentPath());
    throw new NotSignedInError();
  }
  const problem = await res.json().catch(() => null) as { detail?: string; title?: string; code?: string } | null;
  throw new ApiError(res.status, problem?.code, problem?.detail ?? problem?.title ?? `Request failed (${res.status}).`);
}

export const api = {
  async get<T>(path: string, options?: RequestOptions): Promise<T> {
    return (await send(path, options)).json() as Promise<T>;
  },
  async post<T>(path: string, json?: unknown, options?: RequestOptions): Promise<T | undefined> {
    const res = await send(path, { ...options, method: 'POST', json });
    return res.status === 204 ? undefined : res.json() as Promise<T>;
  },
  async postForm<T>(path: string, form: FormData): Promise<T> {
    return (await send(path, { method: 'POST', form })).json() as Promise<T>;
  },
  async delete(path: string, options?: RequestOptions): Promise<void> {
    await send(path, { ...options, method: 'DELETE' });
  },
  /** Downloads a file (e.g. an image) as a Blob. */
  async blob(path: string): Promise<Blob> {
    return (await send(path)).blob();
  },
};
