import { describe, expect, it } from 'vitest';

import { parseResourcePath } from './resourcePath';

const TYPES = ['platform', 'customer', 'commerce/domain', 'commerce/wallet'];

describe('parseResourcePath', () => {
  it('reads an unprefixed type, its id and the tab', () => {
    expect(parseResourcePath('customer/acme', TYPES)).toEqual({ type: 'customer', id: 'acme', tab: 'details' });
    expect(parseResourcePath('customer/acme/access', TYPES)).toEqual({ type: 'customer', id: 'acme', tab: 'access' });
  });

  it('reads a type that contains a slash', () => {
    expect(parseResourcePath('commerce/wallet/uk-ops', TYPES)).toEqual({ type: 'commerce/wallet', id: 'uk-ops', tab: 'details' });
    expect(parseResourcePath('commerce/wallet/uk-ops/access', TYPES)).toEqual({ type: 'commerce/wallet', id: 'uk-ops', tab: 'access' });
  });

  it('treats an id called "access" as an id, not as the tab', () => {
    expect(parseResourcePath('customer/access', TYPES)).toEqual({ type: 'customer', id: 'access', tab: 'details' });
  });

  it('rejects unknown types, missing ids and extra segments', () => {
    expect(parseResourcePath('wallet/uk-ops', TYPES)).toBeNull(); // the unprefixed name is not a known type
    expect(parseResourcePath('commerce/uk-ops', TYPES)).toBeNull(); // a prefix alone is not a type
    expect(parseResourcePath('commerce/wallet', TYPES)).toBeNull();
    expect(parseResourcePath('commerce/wallet/', TYPES)).toBeNull();
    expect(parseResourcePath('customer/acme/grants', TYPES)).toBeNull();
    expect(parseResourcePath('customer/acme/access/more', TYPES)).toBeNull();
    expect(parseResourcePath('', TYPES)).toBeNull();
  });
});
