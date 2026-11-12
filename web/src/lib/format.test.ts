import { describe, expect, it } from 'vitest';

import { describeSubject, formatDateTime, formatMoney, humanise, toNumber, typeLabel } from './format';

describe('format', () => {
  it('shows a user by name and identifier and a group as a group', () => {
    expect(describeSubject({ type: 'user', id: 'heidi', displayName: 'Heidi Hughes' })).toBe('Heidi Hughes (heidi)');
    expect(describeSubject({ type: 'group', id: 'uk-it', displayName: 'UK IT' })).toBe('UK IT (group uk-it)');
  });

  it('makes permission names readable', () => {
    expect(humanise('change_quantity')).toBe('change quantity');
    expect(humanise('view')).toBe('view');
  });

  it('formats money with two decimals', () => {
    expect(formatMoney(5000)).toBe('5,000.00');
    expect(formatMoney(250.5)).toBe('250.50');
  });

  it('labels object types and passes unknown ones through', () => {
    expect(typeLabel('subscription')).toBe('Subscription');
    expect(typeLabel('mystery')).toBe('mystery');
  });

  it('formats dates and tolerates blank or invalid ones', () => {
    expect(formatDateTime(null)).toBe('');
    expect(formatDateTime('not a date')).toBe('not a date');
    expect(formatDateTime('2026-11-12T09:30:00Z')).toMatch(/2026/);
  });
});

describe('toNumber', () => {
  it('reads a clean number and a string that still looks like one', () => {
    expect(toNumber(5)).toBe(5);
    expect(toNumber('250.50')).toBe(250.5); // the case that used to leave the Top up button disabled
    expect(toNumber(' 9 ')).toBe(9);
  });

  it('is NaN for blank or non-numeric input so a form can refuse it', () => {
    expect(toNumber('')).toBeNaN();
    expect(toNumber('  ')).toBeNaN();
    expect(toNumber('12abc')).toBeNaN();
    expect(toNumber('-')).toBeNaN();
  });
});
