import type { SubjectView } from '../api/types';

const NUMBER = new Intl.NumberFormat('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const DATE_TIME = new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short' });

export const formatMoney = (value: number): string => NUMBER.format(value);

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : DATE_TIME.format(date);
}

const TYPE_LABELS: Record<string, string> = {
  platform: 'Platform',
  customer: 'Customer',
  domain: 'Domain',
  wallet: 'Wallet',
  subscription: 'Subscription',
  group: 'Group',
  user: 'User',
};

export const typeLabel = (type: string): string => TYPE_LABELS[type] ?? type;

/** "Heidi Hughes (heidi)" or "UK IT (group uk-it)": the name and the identifier, as the PRD asks the shell to show. */
export function describeSubject(subject: Pick<SubjectView, 'type' | 'id' | 'displayName'>): string {
  return subject.type === 'group' ? `${subject.displayName} (group ${subject.id})` : `${subject.displayName} (${subject.id})`;
}

/** The role names the API uses, made readable: `change_quantity` becomes "change quantity". */
export const humanise = (permission: string): string => permission.replaceAll('_', ' ');

/**
 * Reads a number input's value. Mantine reports a string while the text is not a clean number (for example `250.50`
 * keeps its trailing zero), so treating any string as "not a number" would stop a user submitting a perfectly good amount.
 */
export function toNumber(value: number | string): number {
  if (typeof value === 'number') return value;
  return value.trim() === '' ? Number.NaN : Number(value);
}
