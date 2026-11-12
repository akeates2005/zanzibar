import { expect, test, type Page } from '@playwright/test';

import { openAs, resetData, switchTo } from './support';

/**
 * The README's ten-minute demonstration, automated, so the script cannot drift from what the application does. Each test is one
 * numbered step of that script (steps 1 and 8, which need a person or a stopped engine, are covered by other tests).
 */
test.beforeEach(async ({ request }) => {
  await resetData(request);
});

const dialog = (page: Page) => page.getByRole('dialog');

test('demo step 2: Alice sees Acme and Grace is told nothing about a wallet that is not hers', async ({ page }) => {
  await openAs(page, 'alice');
  await expect(page.getByRole('link', { name: 'Acme Ltd' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Globex Corporation' })).toHaveCount(0);

  await page.goto('/resources/domain/acme-uk/access');
  const inherited = page.getByTestId('inherited-grants');
  await expect(inherited.getByText('Alice Armstrong (alice)')).toBeVisible();
  await expect(inherited.getByText('Priya Patel (priya)')).toBeVisible();

  await switchTo(page, 'grace');
  await page.goto('/resources/wallet/uk-ops');
  await expect(page.getByTestId('not-found')).toContainText('does not exist, or you do not have access to it');
});

test('demo step 4: the nesting cycle is refused, removing a nested group takes Heidi\'s access, Bob keeps his', async ({ page }) => {
  await openAs(page, 'heidi', '/resources/domain/acme-uk');
  await expect(page.getByRole('heading', { name: 'Acme UK' })).toBeVisible();
  await expect(page.getByTestId('permission-admin')).toHaveAttribute('data-held', 'true');

  await switchTo(page, 'alice');
  await page.goto('/resources/customer/acme');
  await page.getByRole('link', { name: 'Groups of this customer' }).click();
  await page.getByRole('list', { name: 'Groups' }).getByRole('link', { name: 'UK contractors' }).click();

  // adding UK IT inside UK contractors would close a loop
  const panel = page.getByTestId('group-panel');
  await panel.getByText('A group').click();
  await panel.getByLabel('Group', { exact: true }).selectOption({ label: 'UK IT' });
  await panel.getByRole('button', { name: 'Add member' }).click();
  await expect(page.getByText(/cycle/i)).toBeVisible();

  // remove UK contractors from UK IT instead
  await page.getByRole('list', { name: 'Groups' }).getByRole('link', { name: 'UK IT' }).click();
  await page.getByRole('button', { name: 'Remove UK contractors' }).click();
  await expect(page.getByRole('table', { name: 'Members' }).getByText('UK contractors')).toHaveCount(0);

  await switchTo(page, 'heidi');
  await page.goto('/resources');
  await expect(page.getByTestId('empty-state')).toContainText('You do not have access to anything yet');

  await switchTo(page, 'bob');
  await page.goto('/resources/domain/acme-uk');
  await expect(page.getByTestId('permission-admin')).toHaveAttribute('data-held', 'true');
});

test('demo step 5: a spender gets exactly the operator role on what she created, and the audit says who gave it', async ({ page }) => {
  await openAs(page, 'carol');
  await page.getByRole('link', { name: 'UK Operations' }).click();
  await expect(page.getByRole('button', { name: 'Top up' })).toBeDisabled();
  await page.getByRole('button', { name: 'Add subscription' }).click();
  await dialog(page).getByLabel('Product name').fill('Demo subscription');
  await dialog(page).getByRole('button', { name: 'Add subscription' }).click();
  await page.getByRole('link', { name: 'Demo subscription' }).click();
  await expect(page.getByRole('button', { name: 'Change quantity' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Cancel subscription' })).toBeDisabled();
  const path = new URL(page.url()).pathname;

  await switchTo(page, 'alice');
  await page.goto(`${path}/access`);
  const audit = page.getByTestId('audit');
  await expect(audit.getByRole('row', { name: /Grant.*operator.*carol.*system.*Applied/ })).toBeVisible();
});

test('demo step 6: moving UK R&D takes it from Bob but leaves him UK Operations', async ({ page }) => {
  await openAs(page, 'bob');
  await expect(page.getByRole('link', { name: 'UK R&D' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'GitHub Enterprise' })).toBeVisible();

  await switchTo(page, 'alice');
  await page.getByRole('link', { name: 'UK R&D' }).click();
  await page.getByRole('button', { name: 'Move to another domain' }).click();
  await dialog(page).getByLabel('Target domain').click();
  await page.getByRole('option', { name: 'Acme Ireland' }).click();
  await dialog(page).getByRole('button', { name: 'Move' }).click();
  await expect(page.getByRole('navigation', { name: 'Where this is' }).getByText('Acme Ireland')).toBeVisible();

  await switchTo(page, 'bob');
  await page.goto('/resources');
  await expect(page.getByRole('link', { name: 'UK Operations' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'UK R&D' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'GitHub Enterprise' })).toHaveCount(0);
});

test('demo step 7: the explorer says why Heidi may spend from UK Operations', async ({ page }) => {
  await openAs(page, 'alice', '/explorer');

  await page.getByRole('combobox', { name: 'User' }).click();
  await page.getByRole('combobox', { name: 'User' }).fill('Heidi');
  await page.getByRole('listbox').getByRole('option', { name: 'Heidi Hughes (heidi)' }).click();
  await page.getByLabel('Object').selectOption({ label: 'Wallet: UK Operations' });
  await page.getByLabel('Permission').selectOption('spend');
  await page.getByRole('button', { name: 'Check' }).click();

  await expect(page.getByTestId('verdict')).toHaveText('Allowed');
  const why = page.getByRole('list', { name: 'Why' });
  await expect(why).toContainText('uk-contractors'); // the nested group is on the path
  await expect(why).toContainText('uk-it');
});
