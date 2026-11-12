import { useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';

import { createApi, type Api } from '../api/client';

import { fetchSeededUsers, fetchToken, type SeededUser } from './issuer';

/** Only the chosen user's id is remembered across reloads. The token is never stored anywhere. */
const REMEMBERED_USER_KEY = 'poc.user';
const DEFAULT_USER = 'priya';

interface Session {
  user: SeededUser;
  api: Api;
}

interface AuthState {
  users: SeededUser[];
  session: Session;
  /** Fetches a token for another user, replaces the session and clears every cached query. */
  switchUser: (userId: string) => Promise<void>;
  /** Fetches a fresh token for the current user (used when the API says the token has expired). */
  reissue: () => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

function remembered(): string | null {
  try {
    return window.localStorage.getItem(REMEMBERED_USER_KEY);
  } catch {
    return null; // storage can be blocked; the remembered user is a convenience only
  }
}

function remember(userId: string): void {
  try {
    window.localStorage.setItem(REMEMBERED_USER_KEY, userId);
  } catch {
    // ignore: see remembered()
  }
}

export function AuthProvider({ children, fallback }: { children: ReactNode; fallback: (state: 'loading' | { error: string }) => ReactNode }) {
  const queryClient = useQueryClient();
  const [users, setUsers] = useState<SeededUser[]>([]);
  const [session, setSession] = useState<Session | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const open = useCallback(async (list: SeededUser[], userId: string): Promise<Session> => {
    const user = list.find((u) => u.id === userId) ?? list[0];
    if (!user) throw new Error('The token issuer has no users.');
    return { user, api: createApi(await fetchToken(user.id)) };
  }, []);

  useEffect(() => {
    // A property, not a bare variable: the cleanup below mutates it, which flow analysis cannot see through a closure.
    const run = { cancelled: false };
    void (async () => {
      try {
        const list = await fetchSeededUsers();
        const first = await open(list, remembered() ?? DEFAULT_USER);
        if (run.cancelled) return;
        setUsers(list);
        setSession(first);
      } catch (error) {
        if (!run.cancelled) setFailure(error instanceof Error ? error.message : 'Could not sign in.');
      }
    })();
    return () => {
      run.cancelled = true;
    };
  }, [open]);

  const switchUser = useCallback(
    async (userId: string) => {
      const next = await open(users, userId);
      remember(next.user.id);
      // The cache is keyed by user id, so the old user's data could never be shown as the new user's; clearing it as well
      // means none of it lingers in memory (PRD: switching must clear all cached data).
      await queryClient.cancelQueries();
      setSession(next);
      queryClient.clear();
    },
    [open, queryClient, users],
  );

  const currentUserId = session?.user.id;
  const reissue = useCallback(async () => {
    if (currentUserId === undefined) return;
    const next = await open(users, currentUserId);
    setSession(next);
    await queryClient.invalidateQueries();
  }, [currentUserId, open, queryClient, users]);

  // A 401 from the API means the one-hour token expired. Fetch a fresh one for the same user and refetch; do it once at a time.
  const reissuing = useRef(false);
  useEffect(() => {
    const onExpired = () => {
      if (reissuing.current) return;
      reissuing.current = true;
      void reissue().finally(() => {
        reissuing.current = false;
      });
    };
    window.addEventListener('poc:token-expired', onExpired);
    return () => {
      window.removeEventListener('poc:token-expired', onExpired);
    };
  }, [reissue]);

  const value = useMemo(() => (session ? { users, session, switchUser, reissue } : null), [users, session, switchUser, reissue]);

  if (failure) return <>{fallback({ error: failure })}</>;
  if (!value) return <>{fallback('loading')}</>;
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside AuthProvider.');
  return value;
}

/** The signed-in user's id and an API client holding their token. */
export function useSession(): Session {
  return useAuth().session;
}
