import { expect, type APIRequestContext, type Page } from '@playwright/test';

export const API = 'http://127.0.0.1:5080';
export const ISSUER = 'http://127.0.0.1:5090';

/** The display names the shell shows, so a test can wait until a switch has really happened. */
export const DISPLAY_NAMES: Record<string, string> = {
  priya: 'Priya Patel',
  sam: 'Sam Okafor',
  alice: 'Alice Armstrong',
  bob: 'Bob Baker',
  heidi: 'Heidi Hughes',
  carol: 'Carol Chen',
  dave: 'Dave Dunn',
  erin: 'Erin Evans',
  grace: 'Grace Green',
  frank: 'Frank Foster',
};

/** A token for a seeded user, fetched from the stub issuer exactly as the client does. */
export async function tokenFor(request: APIRequestContext, userId: string): Promise<string> {
  const response = await request.post(`${ISSUER}/dev/token`, { data: { userId } });
  expect(response.ok(), `token for ${userId}`).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

/** Puts the system back to the seed scenario, so every journey starts from the same data. */
export async function resetData(request: APIRequestContext): Promise<void> {
  const token = await tokenFor(request, 'priya');
  const reset = await request.post(`${API}/api/system/reset`, { headers: { Authorization: `Bearer ${token}` } });
  expect(reset.ok(), 'system reset').toBeTruthy();
  await expect.poll(async () => (await request.get(`${API}/health`)).status(), { message: 'API ready after reset' }).toBe(200);
}

/** Waits until the shell shows this person as the signed-in user: the select, and the name beside it. */
async function expectSignedInAs(page: Page, userId: string): Promise<void> {
  await expect(page.getByLabel('Signed in as')).toHaveValue(userId);
  await expect(page.getByTestId('signed-in-name')).toHaveText(DISPLAY_NAMES[userId] ?? userId);
}

/**
 * Opens the app signed in as the given user. The client remembers only the chosen user id (never a token), so the first
 * load is seeded with it. It is set only if nothing is remembered yet: an init script runs on every navigation, and
 * overwriting the choice each time would silently undo a later switch.
 */
export async function openAs(page: Page, userId: string, path = '/resources'): Promise<void> {
  await page.addInitScript((id) => {
    if (window.localStorage.getItem('poc.user') === null) window.localStorage.setItem('poc.user', id);
  }, userId);
  await page.goto(path);
  await expectSignedInAs(page, userId);
}

/** Switches user through the shell's selector, as someone demonstrating the system would, and waits for it to take effect. */
export async function switchTo(page: Page, userId: string): Promise<void> {
  await page.getByLabel('Signed in as').selectOption(userId);
  await expectSignedInAs(page, userId);
}
