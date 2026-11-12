import createClient from 'openapi-fetch';

import { config } from '../config';

import type { paths } from './schema';

/**
 * An API client bound to one user's access token. The token is captured here, in memory, and nowhere else: it is never
 * written to storage, and a query cached under one user's key can only ever have been fetched with that user's token.
 */
export function createApi(token: string) {
  return createClient<paths>({ baseUrl: config.apiUrl, headers: { Authorization: `Bearer ${token}` } });
}

export type Api = ReturnType<typeof createApi>;
