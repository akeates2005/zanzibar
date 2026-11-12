import { expect, test, type Page } from '@playwright/test';

import { API, openAs, resetData, switchTo, tokenFor } from './support';

/** The five journeys from the PRD, each starting from the seed scenario. */
test.beforeEach(async ({ request }) => {
  await resetData(request);
});

const dialog = (page: Page) => page.getByRole('dialog');

test('an administrator manages a wallet: top up, rename, add a subscription, move it', async ({ page }) => {
  await openAs(page, 'alice');
  await page.getByRole('link', { name: 'UK Operations' }).click();
  await expect(page.getByRole('heading', { name: 'UK Operations' })).toBeVisible();
  await expect(page.getByTestId('wallet-balance')).toHaveText('5,000.00');

  // Top up
  await page.getByRole('button', { name: 'Top up' }).click();
  await dialog(page).getByLabel('Amount').fill('250.50');
  await dialog(page).getByRole('button', { name: 'Top up' }).click();
  await expect(page.getByTestId('wallet-balance')).toHaveText('5,250.50');

  // Rename
  await page.getByRole('button', { name: 'Rename' }).click();
  await dialog(page).getByLabel('New name').fill('UK Ops (renamed)');
  await dialog(page).getByRole('button', { name: 'Rename' }).click();
  await expect(page.getByRole('heading', { name: 'UK Ops (renamed)' })).toBeVisible();

  // Add a subscription: it appears in the wallet's list
  await page.getByRole('button', { name: 'Add subscription' }).click();
  await dialog(page).getByLabel('Product name').fill('Visio');
  await dialog(page).getByRole('button', { name: 'Add subscription' }).click();
  await expect(page.getByRole('list', { name: 'Subscriptions' }).getByRole('link', { name: 'Visio' })).toBeVisible();

  // Move to the other domain under the same customer: the breadcrumb follows
  await expect(page.getByRole('navigation', { name: 'Where this is' }).getByText('Acme UK')).toBeVisible();
  await page.getByRole('button', { name: 'Move to another domain' }).click();
  await dialog(page).getByLabel('Target domain').click();
  await page.getByRole('option', { name: 'Acme Ireland' }).click();
  await dialog(page).getByRole('button', { name: 'Move' }).click();
  const crumbs = page.getByRole('navigation', { name: 'Where this is' });
  await expect(crumbs.getByText('Acme Ireland')).toBeVisible();
  await expect(crumbs.getByText('Acme UK')).toHaveCount(0);
});

test('a spender creates a subscription, sees it, changes its quantity, and cannot cancel it', async ({ page }) => {
  await openAs(page, 'carol');

  // Carol sees the wallet she spends from, under ancestors she cannot open (plain labels, not links).
  await expect(page.getByText('Acme Ltd')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Acme Ltd' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Acme UK' })).toHaveCount(0);
  await page.getByRole('link', { name: 'UK Operations' }).click();

  await expect(page.getByRole('button', { name: 'Add subscription' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Top up' })).toBeDisabled();
  await expect(page.getByRole('list', { name: 'Subscriptions' })).toHaveCount(0); // spenders do not inherit view of subscriptions

  await page.getByRole('button', { name: 'Add subscription' }).click();
  await dialog(page).getByLabel('Product name').fill('Power BI');
  await dialog(page).getByLabel('Quantity').fill('2');
  await dialog(page).getByRole('button', { name: 'Add subscription' }).click();

  // The default grant makes her the new subscription's operator, so it appears for her
  await page.getByRole('link', { name: 'Power BI' }).click();
  await expect(page.getByRole('heading', { name: 'Power BI' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Change quantity' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Cancel subscription' })).toBeDisabled();

  await page.getByRole('button', { name: 'Change quantity' }).click();
  await dialog(page).getByLabel('Quantity').fill('9');
  await dialog(page).getByRole('button', { name: 'Change quantity' }).click();
  await expect(page.getByTestId('subscription-quantity')).toHaveText('9');
});

test('a viewer sees disabled actions that name what is missing, and the API refuses them too', async ({ page, request }) => {
  await openAs(page, 'erin');
  await page.getByRole('link', { name: 'Acme Ltd' }).click();
  await expect(page.getByRole('heading', { name: 'Acme Ltd' })).toBeVisible();

  // The actions are shown but disabled, each saying which permission it needs.
  for (const [label, permission] of [['Rename', 'edit'], ['Add domain', 'create_domain'], ['Delete', 'delete']] as const) {
    const control = page.getByRole('button', { name: label });
    await expect(control).toBeDisabled();
    await control.locator('xpath=..').hover();
    await expect(page.getByText(`Requires the '${permission.replaceAll('_', ' ')}' permission`)).toBeVisible();
  }

  // "Your access" lists every permission with a tick or a cross
  await expect(page.getByTestId('permission-view')).toHaveAttribute('data-held', 'true');
  await expect(page.getByTestId('permission-edit')).toHaveAttribute('data-held', 'false');
  // and a viewer has no Access tab
  await expect(page.getByRole('tab', { name: 'Access' })).toHaveCount(0);

  // A direct call to the API for each disabled action is still refused, naming the permission.
  const headers = { Authorization: `Bearer ${await tokenFor(request, 'erin')}` };
  const rename = await request.patch(`${API}/api/customers/acme`, { headers, data: { name: 'Hacked' } });
  expect(rename.status()).toBe(403);
  expect(((await rename.json()) as { permission: string }).permission).toBe('edit');
  const addDomain = await request.post(`${API}/api/customers/acme/domains`, { headers, data: { name: 'Nope' } });
  expect(addDomain.status()).toBe(403);
  expect(((await addDomain.json()) as { permission: string }).permission).toBe('create_domain');
  const remove = await request.delete(`${API}/api/customers/acme`, { headers });
  expect(remove.status()).toBe(403);
  expect(((await remove.json()) as { permission: string }).permission).toBe('delete');
});

test('an administrator grants and revokes access through the Access tab and the grantee sees it at once', async ({ page }) => {
  await openAs(page, 'alice', '/resources/domain/acme-ie/access');
  const directGrants = page.getByTestId('direct-grants');
  await expect(directGrants.getByText('Nobody has been granted a role on this object directly')).toBeVisible();

  // Inherited access is shown read-only, with where it comes from
  await expect(page.getByTestId('inherited-grants').getByText('Alice Armstrong (alice)')).toBeVisible();

  // Grant Erin the viewer role
  const form = page.getByTestId('add-grant');
  const userBox = form.getByRole('combobox', { name: 'User' });
  await userBox.click();
  await userBox.fill('Erin');
  await page.getByRole('listbox').getByRole('option', { name: 'Erin Evans (erin)' }).click();
  await form.getByLabel('Role').selectOption('viewer');
  await form.getByRole('button', { name: 'Grant access' }).click();
  const row = directGrants.getByTestId('grant-erin-viewer');
  await expect(row).toBeVisible();
  await expect(row).toContainText('alice'); // who granted it

  // "Who can": Erin now holds `view` here
  await page.getByTestId('who-can').getByLabel('Permission').selectOption('view');
  await expect(page.getByRole('list', { name: 'Users who hold this permission' }).getByText('Erin Evans (erin)')).toBeVisible();

  // Erin, switched to through the shell, can now open the domain (and the shell really shows her, not Alice)
  await switchTo(page, 'erin');
  await page.goto('/resources');
  await expect(page.getByTestId('signed-in-name')).toHaveText('Erin Evans');
  await expect(page.getByRole('link', { name: 'Acme Ireland' })).toBeVisible();

  // Alice revokes it
  await switchTo(page, 'alice');
  await page.goto('/resources/domain/acme-ie/access');
  await page.getByRole('button', { name: 'Remove Erin Evans viewer' }).click();
  await expect(directGrants.getByText('Nobody has been granted a role on this object directly')).toBeVisible();
  await expect(page.getByTestId('audit').getByRole('row', { name: /Revoke/ })).toBeVisible();

  // ...and Erin loses the domain on her next request
  await switchTo(page, 'erin');
  await page.goto('/resources');
  await expect(page.getByTestId('signed-in-name')).toHaveText('Erin Evans');
  await expect(page.getByRole('link', { name: 'Acme Ltd' })).toBeVisible(); // she still sees the customer
  await expect(page.getByRole('link', { name: 'Acme Ireland' })).toHaveCount(0);
});

test('a user with no access sees the empty state', async ({ page }) => {
  await openAs(page, 'frank');

  await expect(page.getByTestId('empty-state')).toContainText('You do not have access to anything yet');
  await expect(page.getByRole('link', { name: 'System' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'New customer' })).toBeDisabled();

  // Even guessing a URL reveals nothing: the page cannot tell frank whether the object exists.
  await page.goto('/resources/customer/acme');
  await expect(page.getByTestId('not-found')).toBeVisible();
});
