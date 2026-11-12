import { describe, expect, it } from 'vitest';

import type { TreeNodeDto } from '../api/types';

import { buildTree, countOpenable, moveTargets } from './tree';

const crumb = (type: string, name: string, id: string | null) => ({ type, name, id, viewable: id !== null });
const node = (type: string, id: string, name: string, ancestors: TreeNodeDto['ancestors']): TreeNodeDto => ({ type, id, name, ancestors });

describe('buildTree', () => {
  it('nests each object under its ancestors', () => {
    const tree = buildTree([
      node('customer', 'acme', 'Acme', [crumb('platform', 'Platform', 'main')]),
      node('domain', 'acme-uk', 'Acme UK', [crumb('platform', 'Platform', 'main'), crumb('customer', 'Acme', 'acme')]),
    ]);

    expect(tree).toHaveLength(1);
    expect(tree[0]?.name).toBe('Platform');
    expect(tree[0]?.children[0]?.children[0]).toMatchObject({ id: 'acme-uk', name: 'Acme UK' });
  });

  it('shows ancestors the user cannot view as labels with no id, so they cannot become links', () => {
    const tree = buildTree([node('domain', 'acme-uk', 'Acme UK', [crumb('platform', 'Platform', null), crumb('customer', 'Acme Ltd', null)])]);

    const platform = tree[0];
    const customer = platform?.children[0];
    expect(platform?.id).toBeNull();
    expect(customer?.id).toBeNull();
    expect(customer?.children[0]?.id).toBe('acme-uk');
    expect(countOpenable(tree)).toBe(1); // only the domain can be opened
  });

  it('merges siblings that share the same viewable ancestor and keeps sibling order stable by name', () => {
    const ancestors = [crumb('customer', 'Acme', 'acme')];
    const tree = buildTree([node('domain', 'b', 'Beta', ancestors), node('domain', 'a', 'Alpha', ancestors)]);

    expect(tree).toHaveLength(1);
    expect(tree[0]?.children.map((c) => c.name)).toEqual(['Alpha', 'Beta']);
  });

  it('does not merge two different viewable objects that happen to share a name', () => {
    const tree = buildTree([node('customer', 'one', 'Same', []), node('customer', 'two', 'Same', [])]);
    expect(tree.map((t) => t.id).sort()).toEqual(['one', 'two']);
  });

  it('gives a viewable object that is also someone else\'s ancestor a single row', () => {
    const tree = buildTree([
      node('customer', 'acme', 'Acme', []),
      node('domain', 'acme-uk', 'Acme UK', [crumb('customer', 'Acme', 'acme')]),
    ]);
    expect(tree).toHaveLength(1);
    expect(tree[0]?.id).toBe('acme');
    expect(tree[0]?.children[0]?.id).toBe('acme-uk');
  });

  it('is empty for no nodes', () => {
    expect(buildTree([])).toEqual([]);
    expect(countOpenable([])).toBe(0);
  });
});

describe('moveTargets', () => {
  const tree = [
    node('domain', 'acme-uk', 'Acme UK', [crumb('customer', 'Acme', 'acme')]),
    node('domain', 'acme-ie', 'Acme Ireland', [crumb('customer', 'Acme', 'acme')]),
    node('domain', 'globex-main', 'Globex Main', [crumb('customer', 'Globex', 'globex')]),
    node('wallet', 'uk-ops', 'UK Ops', [crumb('customer', 'Acme', 'acme'), crumb('domain', 'Acme UK', 'acme-uk')]),
  ];

  it('offers only other domains under the same customer', () => {
    expect(moveTargets(tree, [crumb('customer', 'Acme', 'acme'), crumb('domain', 'Acme UK', 'acme-uk')], 'acme-uk')).toEqual([
      { id: 'acme-ie', name: 'Acme Ireland' },
    ]);
  });

  it('matches by customer name when the customer cannot be viewed', () => {
    const labelled = [node('domain', 'acme-ie', 'Acme Ireland', [crumb('customer', 'Acme Ltd', null)]), node('domain', 'x', 'Elsewhere', [crumb('customer', 'Other', null)])];
    expect(moveTargets(labelled, [crumb('customer', 'Acme Ltd', null)], 'acme-uk')).toEqual([{ id: 'acme-ie', name: 'Acme Ireland' }]);
  });

  it('offers nothing when the wallet has no customer ancestor', () => {
    expect(moveTargets(tree, [], 'acme-uk')).toEqual([]);
  });
});
