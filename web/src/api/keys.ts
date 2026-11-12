/**
 * Every query key starts with the signed-in user's id, so cached data can never be shown to a different user, and one
 * prefix invalidates everything the current user has fetched.
 */
export const userKey = (userId: string) => ['u', userId] as const;

export const keys = {
  me: (u: string) => [...userKey(u), 'me'] as const,
  tree: (u: string) => [...userKey(u), 'tree'] as const,
  object: (u: string, type: string, id: string) => [...userKey(u), 'object', type, id] as const,
  children: (u: string, type: string, id: string) => [...userKey(u), 'children', type, id] as const,
  grants: (u: string, type: string, id: string) => [...userKey(u), 'access', type, id, 'grants'] as const,
  grantableGroups: (u: string, type: string, id: string) => [...userKey(u), 'access', type, id, 'grantable-groups'] as const,
  effective: (u: string, type: string, id: string, permission: string) =>
    [...userKey(u), 'access', type, id, 'effective', permission] as const,
  audit: (u: string, type: string, id: string) => [...userKey(u), 'access', type, id, 'audit'] as const,
  groups: (u: string, ownerKey: string) => [...userKey(u), 'groups', ownerKey] as const,
  group: (u: string, id: string) => [...userKey(u), 'group', id] as const,
  users: (u: string, query: string) => [...userKey(u), 'directory', query] as const,
  system: (u: string) => [...userKey(u), 'system'] as const,
};
