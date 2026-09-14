/**
 * #710 — Pickup verification at kiosk checkout (e2e)
 *
 * Level 1: one @smoke golden-path test against the REAL stack with seeded
 * data (fixtures/test-data.ts — Smith family, phone 5551234567, child
 * Johnny, guardian Jane Smith). No page.route mocking: the security code is
 * generated at check-in (CheckinConfirmation) and read back from the page;
 * the supervisor PIN is Person.SupervisorPin (1234, seeded for John Smith).
 *
 * Level 2: the three #710 contract scenarios against a mocked API —
 * X-Supervisor-Session forwarding on record-pickup, AuthorizationLevel.Never
 * issuing no write, and pickup never touching the plain /checkin/checkout
 * route (which #710 deliberately leaves as-is).
 *
 * EXPECTED TO FAIL TODAY: the kiosk checkout has no pickup-verification
 * step yet. The tests must keep failing until the implement cards mount
 * PickupVerification into CheckoutFlow — do not weaken them.
 *
 * The print bridge is a LOCAL desktop dependency, not part of the stack —
 * mocked in every test BEFORE navigation (kiosk probes it on mount).
 */

import { test, expect } from '../fixtures/auth.fixture';
import { installPrintBridgeMock } from '../fixtures/print-bridge.fixture';
import { CheckinPage } from '../fixtures/page-objects/checkin.page';

test.describe('@smoke golden path — #710 pickup verification at checkout', () => {
  test('@smoke golden path: checked-in child is checked out through pickup verification', async ({ page }) => {
    await installPrintBridgeMock(page);
    const checkin = new CheckinPage(page);
    await checkin.goto();

    // Check Johnny in (Smith family, seeded) and capture the generated code.
    await checkin.enterPhone('5551234567');
    await checkin.submitPhone();
    await expect(page.getByText(/who.*checking in/i)).toBeVisible({ timeout: 5000 });
    await checkin.selectMemberByName('Johnny');
    await checkin.confirmCheckin();
    await checkin.expectConfirmation();
    const [code] = await checkin.getSecurityCodes();
    expect(code).toMatch(/^[A-Z0-9]+$/);
    await checkin.doneButton.click();

    // Supervisor PIN login (Person.SupervisorPin — seeded 1234 for #710).
    await page.getByRole('button', { name: /supervisor/i }).click();
    for (const d of ['1', '2', '3', '4']) {
      await page.getByRole('button', { name: d, exact: true }).click();
    }
    await page.getByRole('button', { name: 'Submit' }).click();
    await expect(page.getByText('Supervisor Mode')).toBeVisible({ timeout: 5000 });

    // Checkout tab → Johnny's row.
    await page.getByRole('button', { name: 'Checkout', exact: true }).click();
    await page.getByRole('button', { name: 'Check Out' }).first().click();

    // #710 — the pickup-verification step (PickupVerification in CheckoutFlow).
    // This is what does not exist yet and why the test fails today.
    await expect(page.getByLabel(/Security Code/)).toBeVisible({ timeout: 5000 });
    await page.getByPlaceholder('Enter code').fill(code);
    await page.getByPlaceholder('Enter name').fill('Jane Smith');
    await page.getByRole('button', { name: 'Verify Authorization' }).click();
    await expect(page.getByText('Authorized')).toBeVisible({ timeout: 5000 });

    await page.getByRole('button', { name: /complete checkout/i }).click();
    await expect(page.getByText('Checkout Complete!')).toBeVisible({ timeout: 5000 });
  });
});

test.describe('#710 level-2 (mocked API)', () => {
  // Shared state per test: requests seen by the record-pickup mock.
  let recordPickupRequests: { url: string; headers: Record<string, string> }[];

  test.beforeEach(async ({ page }) => {
    recordPickupRequests = [];

    // Print bridge is local-only — mock before any navigation.
    await installPrintBridgeMock(page);

    // Supervisor PIN login (same mock shape as supervisor-mode.spec.ts).
    await page.route('**/api/v1/checkin/supervisor/login', async (route) => {
      const request = route.request();
      const body = JSON.parse(request.postData() || '{}');
      if (body.pin === '1234') {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            sessionToken: 'test-supervisor-token-123',
            supervisor: { idKey: 'abc123xyz', fullName: 'Test Supervisor' },
            expiresAt: new Date(Date.now() + 120000).toISOString(),
          }),
        });
      } else {
        await route.fulfill({
          status: 401,
          contentType: 'application/json',
          body: JSON.stringify({ error: 'Invalid PIN' }),
        });
      }
    });

    await page.route('**/api/v1/checkin/supervisor/logout', async (route) => {
      await route.fulfill({ status: 204, contentType: 'application/json' });
    });

    // Roster feeding the supervisor checkout tab: one attendance for Johnny
    // carrying security code 'AB12' (RoomRosterDto envelope { data: [...] }).
    await page.route('**/api/v1/checkin/roster*', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          data: [
            {
              locationIdKey: 'loc-elementary-1',
              locationName: 'Elementary',
              totalCount: 1,
              capacity: 30,
              generatedAt: new Date().toISOString(),
              isAtCapacity: false,
              isNearCapacity: false,
              children: [
                {
                  attendanceIdKey: 'att-johnny-710',
                  personIdKey: '55555555-5555-5555-5555-555555555555',
                  fullName: 'Johnny Smith',
                  firstName: 'Johnny',
                  lastName: 'Smith',
                  age: 6,
                  hasCriticalAllergies: false,
                  securityCode: 'AB12',
                  checkInTime: new Date().toISOString(),
                  parentName: 'Jane Smith',
                  isFirstTime: false,
                },
              ],
            },
          ],
        }),
      });
    });
  });

  /** Enters the kiosk, logs in as supervisor, opens the Checkout tab and
   *  clicks Check Out on the first (only) checked-in child. */
  async function openCheckoutOnJohnny(page: import('@playwright/test').Page) {
    const checkin = new CheckinPage(page);
    await checkin.goto();
    await page.getByRole('button', { name: /supervisor/i }).click();
    for (const d of ['1', '2', '3', '4']) {
      await page.getByRole('button', { name: d, exact: true }).click();
    }
    await page.getByRole('button', { name: 'Submit' }).click();
    await expect(page.getByText('Supervisor Mode')).toBeVisible({ timeout: 5000 });
    await page.getByRole('button', { name: 'Checkout', exact: true }).click();
    await page.getByRole('button', { name: 'Check Out' }).first().click();
  }

  test('forwards X-Supervisor-Session on an override record-pickup', async ({ page }) => {
    await page.route('**/api/v1/checkin/verify-pickup', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          data: {
            isAuthorized: false,
            authorizationLevel: undefined,
            authorizedPickupIdKey: undefined,
            message: 'Person not on authorized pickup list. Supervisor approval required.',
            requiresSupervisorOverride: true,
          },
        }),
      });
    });

    let recordPickupBody: string | null = null;
    await page.route('**/api/v1/checkin/record-pickup', async (route) => {
      recordPickupBody = route.request().postData();
      recordPickupRequests.push({
        url: route.request().url(),
        headers: await route.request().allHeaders(),
      });
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          data: {
            idKey: 'pickup-log-1',
            attendanceIdKey: 'att-johnny-710',
            pickupPersonName: 'Jane Smith',
            pickedUpAt: new Date().toISOString(),
          },
        }),
      });
    });

    await openCheckoutOnJohnny(page);

    await expect(page.getByLabel(/Security Code/)).toBeVisible({ timeout: 5000 });
    await page.getByPlaceholder('Enter code').fill('AB12');
    await page.getByPlaceholder('Enter name').fill('Jane Smith');
    await page.getByRole('button', { name: 'Verify Authorization' }).click();
    await page.getByLabel(/supervisor override/i).check();
    await page.getByRole('button', { name: /complete checkout/i }).click();
    await expect(page.getByText('Checkout Complete!')).toBeVisible({ timeout: 5000 });

    // #710 contract: the override write carries the supervisor session the
    // PIN login returned — the audit trail must name the supervisor — and
    // flags itself as a supervisor override.
    expect(recordPickupRequests.length).toBeGreaterThanOrEqual(1);
    const session = recordPickupRequests[0]?.headers['x-supervisor-session'];
    expect(session).toBe('test-supervisor-token-123');
    const payload = recordPickupBody ? JSON.parse(recordPickupBody) : {};
    expect(payload.supervisorOverride).toBe(true);
  });

  test('AuthorizationLevel.Never guardian: no write is issued and an error is shown', async ({ page }) => {
    await page.route('**/api/v1/checkin/verify-pickup', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          data: {
            isAuthorized: false,
            authorizationLevel: 'Never',
            requiresSupervisorOverride: false,
          },
        }),
      });
    });

    // If the kiosk wrongly issues a write on a denied verification, the
    // captured request list betrays it.
    await page.route('**/api/v1/checkin/record-pickup', async (route) => {
      recordPickupRequests.push({
        url: route.request().url(),
        headers: await route.request().allHeaders(),
      });
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    await openCheckoutOnJohnny(page);

    await expect(page.getByLabel(/Security Code/)).toBeVisible({ timeout: 5000 });
    await page.getByPlaceholder('Enter code').fill('AB12');
    await page.getByPlaceholder('Enter name').fill('Jane Smith');
    await page.getByRole('button', { name: 'Verify Authorization' }).click();

    // Denied: a visible refusal, never a silent pass-through.
    await expect(page.getByText('Not Authorized')).toBeVisible({ timeout: 5000 });
    await expect(page.getByText('Checkout Complete!')).not.toBeVisible();
    expect(recordPickupRequests).toHaveLength(0);
  });

  test('pickup goes through record-pickup, never the poisoned plain checkout route', async ({ page }) => {
    // Poison the pre-#710 plain checkout endpoint: if anything tries the
    // old write path, it explodes and the test fails.
    await page.route('**/api/v1/checkin/checkout**', async (route) => {
      await route.fulfill({ status: 500, contentType: 'application/json', body: '{}' });
    });

    await page.route('**/api/v1/checkin/verify-pickup', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          data: {
            isAuthorized: true,
            authorizationLevel: 'Authorized',
            matchType: 'SecurityCode',
            requiresSupervisorOverride: false,
          },
        }),
      });
    });

    await page.route('**/api/v1/checkin/record-pickup', async (route) => {
      recordPickupRequests.push({
        url: route.request().url(),
        headers: await route.request().allHeaders(),
      });
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          data: {
            idKey: 'pickup-log-1',
            attendanceIdKey: 'att-johnny-710',
            pickupPersonName: 'Jane Smith',
            pickedUpAt: new Date().toISOString(),
          },
        }),
      });
    });

    await openCheckoutOnJohnny(page);

    await expect(page.getByLabel(/Security Code/)).toBeVisible({ timeout: 5000 });
    await page.getByPlaceholder('Enter code').fill('AB12');
    await page.getByPlaceholder('Enter name').fill('Jane Smith');
    await page.getByRole('button', { name: 'Verify Authorization' }).click();
    await expect(page.getByText('Authorized')).toBeVisible({ timeout: 5000 });

    await page.getByRole('button', { name: /complete checkout/i }).click();
    await expect(page.getByText('Checkout Complete!')).toBeVisible({ timeout: 5000 });

    // The pickup write used the #710 endpoint despite the poisoned plain one.
    expect(recordPickupRequests).toHaveLength(1);
    expect(recordPickupRequests[0]?.url).toContain('/api/v1/checkin/record-pickup');
  });
});
