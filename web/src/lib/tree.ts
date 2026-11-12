import type { BreadcrumbDto, TreeNodeDto } from '../api/types';

/** One row of the resource browser. A row with no `id` is a plain label: an ancestor the user cannot open. */
export interface TreeItem {
  key: string;
  type: string;
  id: string | null;
  name: string;
  children: TreeItem[];
}

function crumbKey(type: string, id: string | null, name: string): string {
  // A viewable object is identified by its id. One the user cannot view has no id to share, so it is identified by what is
  // all they are told: its type and name.
  return id === null ? `${type}~${name}` : `${type}:${id}`;
}

/**
 * Builds the nested browser tree from the API's flat list. Every node carries its ancestors root-first; ancestors the
 * user can view become links, the rest plain labels. Siblings are sorted by name so the screen is stable.
 *
 * Two different customers the user cannot view that share a name would merge into one label: the API deliberately tells
 * the client nothing else about them (names only, PRD open question 2).
 */
export function buildTree(nodes: readonly TreeNodeDto[]): TreeItem[] {
  const roots: TreeItem[] = [];

  const child = (siblings: TreeItem[], type: string, id: string | null, name: string): TreeItem => {
    const key = crumbKey(type, id, name);
    let item = siblings.find((s) => s.key === key);
    if (!item) {
      item = { key, type, id, name, children: [] };
      siblings.push(item);
    }
    return item;
  };

  for (const node of nodes) {
    let level = roots;
    const path: { type: string; id: string | null; name: string }[] = [
      ...node.ancestors.map((a: BreadcrumbDto) => ({ type: a.type, id: a.id, name: a.name })),
      { type: node.type, id: node.id, name: node.name },
    ];
    for (const step of path) level = child(level, step.type, step.id, step.name).children;
  }

  const sort = (items: TreeItem[]): TreeItem[] =>
    items
      .sort((a, b) => a.name.localeCompare(b.name))
      .map((i) => ({ ...i, children: sort(i.children) }));
  return sort(roots);
}

/** Total number of rows that open an object (labels excluded). */
export function countOpenable(items: readonly TreeItem[]): number {
  return items.reduce((n, i) => n + (i.id === null ? 0 : 1) + countOpenable(i.children), 0);
}

/**
 * The domains a wallet could be moved to: other domains the user can see under the same customer. The server is the only
 * judge (it also needs `create_wallet` on the target and refuses a move across customers), so this only narrows the
 * choices to the ones that can possibly succeed.
 */
export function moveTargets(
  tree: readonly TreeNodeDto[],
  walletAncestors: readonly BreadcrumbDto[],
  currentDomainId: string,
): { id: string; name: string }[] {
  const customer = walletAncestors.find((a) => a.type === 'customer');
  if (!customer) return [];
  return tree
    .filter((n) => n.type === 'domain' && n.id !== currentDomainId)
    .filter((n) => {
      const owner = n.ancestors.find((a) => a.type === 'customer');
      return owner !== undefined && (customer.id !== null && owner.id !== null ? owner.id === customer.id : owner.name === customer.name);
    })
    .map((n) => ({ id: n.id, name: n.name }))
    .sort((a, b) => a.name.localeCompare(b.name));
}
