import { screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { TreeNodeDto } from '../../api/types';
import { renderWithProviders } from '../../test/render';

import { ResourceBrowser } from './ResourceBrowser';

let nodes: TreeNodeDto[] = [];

// The browser's behaviour is what is under test, not the network, so the two queries it reads are stubbed.
vi.mock('../../api/queries', () => ({
  useTree: () => ({ isPending: false, isError: false, data: nodes }),
  useMe: () => ({ isPending: false, isError: false, data: { platformPermissions: { create_customer: false } } }),
}));
vi.mock('../../api/commands', () => ({
  createCustomer: vi.fn(),
  useCommand: () => ({ mutate: vi.fn(), isPending: false }),
}));

const crumb = (type: string, name: string, id: string | null) => ({ type, name, id, viewable: id !== null });

describe('ResourceBrowser', () => {
  beforeEach(() => {
    nodes = [];
  });

  it('shows the empty state for a user who can open nothing', () => {
    renderWithProviders(<ResourceBrowser />);
    expect(screen.getByTestId('empty-state')).toHaveTextContent('You do not have access to anything yet');
  });

  it('shows an ancestor the user cannot view as plain text and the objects they can open as links', () => {
    nodes = [
      { type: 'wallet', id: 'uk-ops', name: 'UK Operations', ancestors: [crumb('platform', 'Platform', null), crumb('customer', 'Acme Ltd', null), crumb('domain', 'Acme UK', null)] },
    ];
    renderWithProviders(<ResourceBrowser />);

    const link = screen.getByRole('link', { name: 'UK Operations' });
    expect(link).toHaveAttribute('href', '/resources/wallet/uk-ops');
    // The three ancestors are plain text (titled as unopenable), in order, and none of them is a link.
    const labels = screen.getAllByTitle('You cannot open this');
    expect(labels.map((l) => l.textContent)).toEqual(['Platform', 'Acme Ltd', 'Acme UK']);
    expect(labels.every((l) => l.closest('a') === null)).toBe(true);
    expect(screen.getAllByRole('link')).toHaveLength(1); // only the wallet can be opened
  });

  it('nests a child under its parent and links both when both are viewable', () => {
    nodes = [
      { type: 'customer', id: 'acme', name: 'Acme Ltd', ancestors: [] },
      { type: 'domain', id: 'acme-uk', name: 'Acme UK', ancestors: [crumb('customer', 'Acme Ltd', 'acme')] },
    ];
    renderWithProviders(<ResourceBrowser />);

    const customerRow = screen.getByTestId('tree-customer-acme').closest('li');
    expect(customerRow).not.toBeNull();
    expect(within(customerRow as HTMLElement).getByRole('link', { name: 'Acme UK' })).toHaveAttribute('href', '/resources/domain/acme-uk');
  });

  it('offers New customer disabled when the user may not create one', () => {
    nodes = [{ type: 'customer', id: 'acme', name: 'Acme Ltd', ancestors: [] }];
    renderWithProviders(<ResourceBrowser />);
    expect(screen.getByRole('button', { name: 'New customer' })).toBeDisabled();
  });
});
