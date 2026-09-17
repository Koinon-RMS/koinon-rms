/**
 * #710 — Kiosk pickup verification journeys (e2e, real stack)
 *
 * Two journeys against the REAL API and seeded data:
 *   1. authorized pickup completes at the kiosk
 *   2. never-authorized pickup is denied at the kiosk
 *
 * The kiosk verifies a pickup person by NAME only, so each journey relies on
 * a name-based authorized-pickup row created by the #710 fixtures
 * (e2e/fixtures/pickup-verification.fixture.ts) against the seeded child
 * Johnny Smith. The fixtures remove only their own rows; the seeder's shared
 * data (Jane -> Johnny authorization, supervisor PIN 1234, Smith family) is
 * read, never modified. Authorization levels are the numeric contract
 * (Always = 0, EmergencyOnly = 1, Never = 2 — see
 * src/web/src/features/authorized-pickup/api.ts).
 *
 * The print bridge is a LOCAL desktop dependency, not part of the stack —
 * mocked in every test BEFORE navigation (kiosk probes it on mount).
 */

import { test, expect } from '../fixtures/auth.fixture';
import { installPrintBridgeMock } from '../fixtures/print-bridge.fixture';
import { CheckinPage } from '../fixtures/page-objects/checkin.page';
import {
  AUTHORIZED_PICKUP_NAME,
  AUTHORIZATION_LEVEL,
  NEVER_PICKUP_NAME,
  removeNameBasedPickupAuthorizations,
  seedNameBasedPickupAuthorization,
} from '../fixtures/pickup-verification.fixture';

test.describe('#710 kiosk pickup journeys — real stack', () => {
  // The fixtures remove only their own rows, even when a journey fails midway.
  test.afterEach(async () => {
    await removeNameBasedPickupAuthorizations();
  });

  /** Checks the seeded child in for real, enters supervisor mode and walks the
   *  checkout flow to the pickup-verification step. Returns the generated
   *  security code; the roster is filtered by it so exactly the fresh
   *  check-in's row is selected even if an older attendance lingers. */
  async function openVerifyStep(page: import('@playwright/test').Page): Promise<string> {
    const checkin = new CheckinPage(page);
    await installPrintBridgeMock(page);
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

    // Checkout tab → the fresh check-in's row (codes are unique per check-in).
    await page.getByRole('button', { name: 'Checkout', exact: true }).click();
    await page.getByPlaceholder('Search by name or security code...').fill(code);
    await page.getByRole('button', { name: 'Check Out' }).first().click();

    // PickupVerification is mounted as the checkout verify step (#710).
    await expect(page.getByLabel(/Security Code/)).toBeVisible({ timeout: 5000 });
    return code;
  }

  test('authorized pickup completes at the kiosk', async ({ page }) => {
    await seedNameBasedPickupAuthorization({
      name: AUTHORIZED_PICKUP_NAME,
      authorizationLevel: AUTHORIZATION_LEVEL.Always,
    });
    const code = await openVerifyStep(page);

    await page.getByPlaceholder('Enter code').fill(code);
    await page.getByPlaceholder('Enter name').fill(AUTHORIZED_PICKUP_NAME);
    await page.getByRole('button', { name: 'Verify Authorization' }).click();
    await expect(page.getByText('Authorized', { exact: true })).toBeVisible({ timeout: 5000 });

    await page.getByRole('button', { name: /complete checkout/i }).click();
    await expect(page.getByText('Checkout Complete!')).toBeVisible({ timeout: 5000 });
  });

  test('never-authorized pickup is denied at the kiosk', async ({ page }) => {
    await seedNameBasedPickupAuthorization({
      name: NEVER_PICKUP_NAME,
      authorizationLevel: AUTHORIZATION_LEVEL.Never,
      relationship: 6, // Friend
    });
    const code = await openVerifyStep(page);

    await page.getByPlaceholder('Enter code').fill(code);
    await page.getByPlaceholder('Enter name').fill(NEVER_PICKUP_NAME);
    await page.getByRole('button', { name: 'Verify Authorization' }).click();

    // Hard deny: a visible refusal, no Complete affordance, no success screen.
    await expect(page.getByText('Not Authorized')).toBeVisible({ timeout: 5000 });
    await expect(page.getByRole('button', { name: /complete checkout/i })).toHaveCount(0);
    await expect(page.getByText('Checkout Complete!')).not.toBeVisible();
  });
});
