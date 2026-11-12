import { useQuery } from '@tanstack/react-query';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { unwrap } from '../api/problem';
import { newQueryClient, renderWithProviders } from '../test/render';

import { AuthProvider, useAuth } from './AuthContext';

const USERS = [
  { id: 'priya', displayName: 'Priya Patel', email: 'priya@x.test' },
  { id: 'alice', displayName: 'Alice Armstrong', email: 'alice@x.test' },
  { id: 'bob', displayName: 'Bob Baker', email: 'bob@x.test' },
];

/** What the API was asked: the bearer token of each request, in order. */
let apiCalls: string[];

function installFetch(options: { issuerDown?: boolean } = {}) {
  apiCalls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = input instanceof Request ? input : null;
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
      if (options.issuerDown && url.includes('/dev/')) return Promise.reject(new TypeError('Failed to fetch'));
      if (url.endsWith('/dev/users')) return Promise.resolve(Response.json(USERS));
      if (url.endsWith('/dev/token')) {
        const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as { userId: string };
        return Promise.resolve(Response.json({ accessToken: `token-for-${body.userId}` }));
      }
      apiCalls.push(request?.headers.get('Authorization') ?? '(none)');
      return Promise.resolve(Response.json({ userId: 'whoever', name: null, emails: [], platformPermissions: {} }));
    }),
  );
}

function Probe() {
  const { session, switchUser } = useAuth();
  const me = useQuery({
    queryKey: ['u', session.user.id, 'me'],
    queryFn: () => unwrap(session.api.GET('/api/me')),
  });
  return (
    <div>
      <p data-testid="user">{session.user.id}</p>
      <p data-testid="loaded">{me.isSuccess ? 'yes' : 'no'}</p>
      <button onClick={() => void switchUser('bob')}>switch to bob</button>
    </div>
  );
}

const renderProbe = (client = newQueryClient()) =>
  renderWithProviders(
    <AuthProvider fallback={(state) => <p data-testid="fallback">{state === 'loading' ? 'loading' : state.error}</p>}>
      <Probe />
    </AuthProvider>,
    { client },
  );

/** Everything the page could have stored: both storages and cookies. */
function dump(storage: Storage): Record<string, string | null> {
  const all: Record<string, string | null> = {};
  for (let i = 0; i < storage.length; i++) {
    const key = storage.key(i);
    if (key !== null) all[key] = storage.getItem(key);
  }
  return all;
}
const storedText = () => JSON.stringify({ local: dump(window.localStorage), session: dump(window.sessionStorage), cookie: document.cookie });

describe('AuthProvider', () => {
  beforeEach(() => {
    window.localStorage.clear();
    window.sessionStorage.clear();
    installFetch();
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('signs in as the default user when nobody has been chosen before', async () => {
    renderProbe();
    expect(await screen.findByTestId('user')).toHaveTextContent('priya');
  });

  it('signs in as the remembered user', async () => {
    window.localStorage.setItem('poc.user', 'alice');
    renderProbe();
    expect(await screen.findByTestId('user')).toHaveTextContent('alice');
  });

  it('never writes the access token to any storage', async () => {
    renderProbe();
    await screen.findByTestId('user');
    await waitFor(() => { expect(screen.getByTestId('loaded')).toHaveTextContent('yes'); });

    expect(storedText()).not.toContain('token-for-');
    expect(window.localStorage.getItem('poc.user')).toBeNull(); // only a switch remembers the user; the token is still not stored
  });

  it('sends the signed-in user\'s own token on every API call', async () => {
    renderProbe();
    await waitFor(() => { expect(screen.getByTestId('loaded')).toHaveTextContent('yes'); });
    expect(apiCalls).toEqual(['Bearer token-for-priya']);
  });

  it('switching user replaces the token, clears every cached query and remembers only the user id', async () => {
    const client = newQueryClient();
    renderProbe(client);
    await waitFor(() => { expect(screen.getByTestId('loaded')).toHaveTextContent('yes'); });
    expect(client.getQueryCache().getAll().map((q) => q.queryKey)).toContainEqual(['u', 'priya', 'me']);

    await userEvent.click(screen.getByRole('button', { name: 'switch to bob' }));

    await waitFor(() => { expect(screen.getByTestId('user')).toHaveTextContent('bob'); });
    await waitFor(() => { expect(screen.getByTestId('loaded')).toHaveTextContent('yes'); });
    // Nothing of the previous user remains cached, and the new user's data was fetched with the new user's token.
    expect(client.getQueryCache().getAll().map((q) => q.queryKey)).toEqual([['u', 'bob', 'me']]);
    expect(apiCalls).toEqual(['Bearer token-for-priya', 'Bearer token-for-bob']);
    expect(window.localStorage.getItem('poc.user')).toBe('bob');
    expect(storedText()).not.toContain('token-for-');
  });

  it('reports an unreachable token issuer instead of leaving a blank screen', async () => {
    installFetch({ issuerDown: true });
    renderProbe();
    expect(await screen.findByTestId('fallback')).toHaveTextContent('Failed to fetch');
  });
});
