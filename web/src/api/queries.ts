import { useQuery } from '@tanstack/react-query';

import { useSession } from '../auth/AuthContext';

import type { Api } from './client';
import { keys } from './keys';
import { unwrap } from './problem';
import type { GrantTargetType, ObjectByType, ObjectType } from './types';

/** Fetches one business object by type, returning the DTO for that type. */
export function fetchObject<T extends ObjectType>(api: Api, type: T, id: string): Promise<ObjectByType[T]> {
  const path = { params: { path: { id } } };
  // Each branch returns the DTO for its own type; TypeScript cannot correlate the generic T with the branch, hence the cast.
  const request = (): Promise<ObjectByType[ObjectType]> => {
    switch (type) {
      case 'customer':
        return unwrap(api.GET('/api/customers/{id}', path));
      case 'domain':
        return unwrap(api.GET('/api/domains/{id}', path));
      case 'wallet':
        return unwrap(api.GET('/api/wallets/{id}', path));
      case 'subscription':
        return unwrap(api.GET('/api/subscriptions/{id}', path));
    }
  };
  return request() as Promise<ObjectByType[T]>;
}

export function useMe() {
  const { user, api } = useSession();
  return useQuery({ queryKey: keys.me(user.id), queryFn: () => unwrap(api.GET('/api/me')) });
}

export function useTree() {
  const { user, api } = useSession();
  return useQuery({ queryKey: keys.tree(user.id), queryFn: () => unwrap(api.GET('/api/tree')) });
}

export function useObject<T extends ObjectType>(type: T, id: string) {
  const { user, api } = useSession();
  return useQuery({ queryKey: keys.object(user.id, type, id), queryFn: () => fetchObject(api, type, id), retry: false });
}

export function useChildren(parentType: GrantTargetType, id: string, enabled = true) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.children(user.id, parentType, id),
    queryFn: () => unwrap(api.GET('/api/{parentType}/{id}/children', { params: { path: { parentType, id } } })),
    enabled,
  });
}

export function useGrants(type: GrantTargetType, id: string, enabled = true) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.grants(user.id, type, id),
    queryFn: () => unwrap(api.GET('/api/access/{type}/{id}/grants', { params: { path: { type, id } } })),
    enabled,
    retry: false,
  });
}

export function useGrantableGroups(type: GrantTargetType, id: string, enabled = true) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.grantableGroups(user.id, type, id),
    queryFn: () => unwrap(api.GET('/api/access/{type}/{id}/grantable-groups', { params: { path: { type, id } } })),
    enabled,
  });
}

export function useEffectiveAccess(type: GrantTargetType, id: string, permission: string | null) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.effective(user.id, type, id, permission ?? ''),
    queryFn: () =>
      unwrap(api.GET('/api/access/{type}/{id}/effective', { params: { path: { type, id }, query: { permission: permission ?? undefined } } })),
    enabled: permission !== null,
  });
}

export function useGrantAudit(type: GrantTargetType, id: string, enabled = true) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.audit(user.id, type, id),
    queryFn: () => unwrap(api.GET('/api/access/audit', { params: { query: { type, id } } })),
    enabled,
  });
}

/** Groups owned by a customer (`{customerId}`) or by the platform (`null`) that the caller can view. */
export function useGroups(customerId: string | null, enabled = true) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.groups(user.id, customerId ?? 'platform'),
    queryFn: () =>
      customerId === null
        ? unwrap(api.GET('/api/platform/groups'))
        : unwrap(api.GET('/api/customers/{id}/groups', { params: { path: { id: customerId } } })),
    enabled,
    retry: false,
  });
}

export function useGroup(id: string | null) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.group(user.id, id ?? ''),
    queryFn: () => unwrap(api.GET('/api/groups/{id}', { params: { path: { id: id ?? '' } } })),
    enabled: id !== null,
    retry: false,
  });
}

export function useUserSearch(query: string) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.users(user.id, query),
    queryFn: () => unwrap(api.GET('/api/users', { params: { query: { query: query || undefined } } })),
    staleTime: 30_000,
  });
}

export function useSystemStatus(enabled: boolean) {
  const { user, api } = useSession();
  return useQuery({
    queryKey: keys.system(user.id),
    queryFn: () => unwrap(api.GET('/api/system/outbox')),
    enabled,
    retry: false,
  });
}
