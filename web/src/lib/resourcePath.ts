/**
 * Where an object opens: this application's types (`platform` and the business objects) on their own page, any other
 * resource type (another team's) on the generic Access page, which needs nothing but the type and id.
 */
export function objectHref(type: string, id: string, ownTypes: readonly string[]): string {
  return ownTypes.includes(type)
    ? `/resources/${type}/${id}`
    : `/access?type=${encodeURIComponent(type)}&id=${encodeURIComponent(id)}`;
}

export interface ResourcePath {
  type: string;
  id: string;
  tab: 'details' | 'access' | 'groups';
}

/** The types that own groups (a customer's groups, the platform's groups), so the only ones with a Groups tab. */
export const GROUP_OWNER_TYPES: readonly string[] = ['platform', 'customer'];

/**
 * Reads the part of `/resources/*` after `/resources/`. An object type may contain a slash (`commerce/wallet`), so the
 * type cannot be one route segment: the path is matched against the known types instead. Ids never contain a slash.
 *
 *   `customer/acme`                    -> customer acme, details
 *   `commerce/wallet/uk-ops/access`    -> commerce/wallet uk-ops, access
 *   `customer/acme/groups`             -> customer acme, groups (group owners only)
 *
 * Anything else (an unknown type, a missing id, extra segments) is `null`, which the page shows as not found.
 */
export function parseResourcePath(rest: string, knownTypes: readonly string[]): ResourcePath | null {
  const segments = rest.split('/');
  for (const type of knownTypes) {
    const typeSegments = type.split('/');
    if (typeSegments.some((s, i) => segments[i] !== s)) continue;
    const id = segments[typeSegments.length];
    const after = segments.slice(typeSegments.length + 1);
    if (!id) return null;
    if (after.length === 0) return { type, id, tab: 'details' };
    if (after.length === 1 && after[0] === 'access') return { type, id, tab: 'access' };
    if (after.length === 1 && after[0] === 'groups' && GROUP_OWNER_TYPES.includes(type)) return { type, id, tab: 'groups' };
    return null;
  }
  return null;
}
