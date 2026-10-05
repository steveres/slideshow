// /api/v1/account: the user's account in the Slideshow API.

import { api, ApiError } from './api-client.js';

export interface Usage { albums: number; files: number; bytes: number; quotaBytes: number; }
export interface AccountInfo { id: string; displayName: string | null; email: string | null; createdAt: string; usage: Usage; }

export const AccountApi = {
  /** Creates the API account for the signed-in user; harmless if it already exists. */
  async register(): Promise<AccountInfo> {
    return (await api.post<AccountInfo>('/api/v1/account'))!;
  },

  /** The account, or null if the signed-in user hasn't registered with the API yet. */
  async get(): Promise<AccountInfo | null> {
    try {
      return await api.get<AccountInfo>('/api/v1/account');
    } catch (err) {
      if (err instanceof ApiError && err.code === 'account_not_registered') return null;
      throw err;
    }
  },

  /** Rejects every token issued so far: signs the user out on all devices. */
  async logout(): Promise<void> {
    await api.post('/api/v1/account/logout', undefined, { noRedirect: true });
  },

  /** Deletes the account and everything in it. Irreversible. */
  async remove(): Promise<void> {
    await api.delete('/api/v1/account');
  },
};
