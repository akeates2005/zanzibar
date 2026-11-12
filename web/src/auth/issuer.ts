import { config } from '../config';

export interface SeededUser {
  id: string;
  displayName: string;
  email: string;
}

function isSeededUser(value: unknown): value is SeededUser {
  if (typeof value !== 'object' || value === null) return false;
  const v = value as Record<string, unknown>;
  return typeof v.id === 'string' && typeof v.displayName === 'string' && typeof v.email === 'string';
}

/** The users the stub issuer will sign tokens for (the switcher's choices). */
export async function fetchSeededUsers(): Promise<SeededUser[]> {
  const response = await fetch(`${config.issuerUrl}/dev/users`);
  if (!response.ok) throw new Error(`The token issuer answered ${String(response.status)}.`);
  const body: unknown = await response.json();
  if (!Array.isArray(body) || !body.every(isSeededUser)) throw new Error('The token issuer returned an unexpected user list.');
  return body;
}

/** Asks the stub issuer for a token for a seeded user. The token is returned to the caller and held in memory only. */
export async function fetchToken(userId: string): Promise<string> {
  const response = await fetch(`${config.issuerUrl}/dev/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userId }),
  });
  if (!response.ok) throw new Error(`The token issuer refused a token for '${userId}' (${String(response.status)}).`);
  const body: unknown = await response.json();
  if (typeof body === 'object' && body !== null && 'accessToken' in body && typeof body.accessToken === 'string') return body.accessToken;
  throw new Error('The token issuer returned an unexpected token response.');
}
