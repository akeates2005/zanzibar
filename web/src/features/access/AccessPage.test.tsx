import { screen } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { AccessObjectView } from '../../api/types';
import { objectHref } from '../../lib/resourcePath';
import { renderWithProviders } from '../../test/render';

import { AccessPage } from './AccessPage';

let view: AccessObjectView | null = null;
const requested: { type: string; id: string }[] = [];
const tabProps: unknown[] = [];

// The page's job is to load the object for any type and hand it to the Access tab; the tab and network are stubbed.
vi.mock('../../api/queries', () => ({
  useAccessObject: (type: string, id: string) => {
    requested.push({ type, id });
    return { isPending: false, isError: false, data: view };
  },
}));
vi.mock('./AccessTab', () => ({
  AccessTab: (props: unknown) => {
    tabProps.push(props);
    return <div data-testid="access-tab-stub" />;
  },
}));

const render = (route: string) =>
  renderWithProviders(
    <Routes>
      <Route path="/access" element={<AccessPage />} />
    </Routes>,
    { route },
  );

describe('AccessPage', () => {
  beforeEach(() => {
    view = null;
    requested.length = 0;
    tabProps.length = 0;
  });

  it('opens the Access tab for a type this client has no page for, named by the query string', () => {
    view = {
      type: 'shipping/parcel',
      id: 'p1',
      name: 'p1',
      permissions: { view: true, manage_access: true, admin: true },
      ancestors: [
        { type: 'platform', id: null, name: 'Platform', viewable: false },
        { type: 'customer', id: 'acme', name: 'Acme Ltd', viewable: true },
      ],
    };
    render('/access?type=shipping%2Fparcel&id=p1');

    expect(requested).toContainEqual({ type: 'shipping/parcel', id: 'p1' });
    expect(screen.getByTestId('access-tab-stub')).toBeInTheDocument();
    expect(tabProps.at(-1)).toMatchObject({ type: 'shipping/parcel', id: 'p1', permissions: view.permissions, ancestors: view.ancestors });
    expect(screen.getByRole('link', { name: 'Acme Ltd' })).toHaveAttribute('href', '/resources/customer/acme');
    expect(screen.getByTitle('You cannot open this')).toHaveTextContent('Platform');
  });

  it('is not found without a type or an id, and asks for nothing', () => {
    render('/access?type=shipping%2Fparcel');
    expect(screen.getByTestId('not-found')).toBeInTheDocument();
    render('/access?id=p1');
    expect(screen.getAllByTestId('not-found')).toHaveLength(2);
    expect(requested).toHaveLength(0);
  });
});

describe('objectHref', () => {
  const own = ['platform', 'customer', 'commerce/wallet'];

  it('opens this application’s types on their own page and any other type on the Access page', () => {
    expect(objectHref('commerce/wallet', 'uk-ops', own)).toBe('/resources/commerce/wallet/uk-ops');
    expect(objectHref('shipping/parcel', 'p1', own)).toBe('/access?type=shipping%2Fparcel&id=p1');
  });
});
