/**
 * CheckinPage kiosk pickup tests (#710)
 *
 * Page-level integration through the real component chain
 * (CheckinPage → PinEntry → SupervisorMode → CheckoutFlow →
 * PickupVerification → authorized-pickup hooks), with only the API layer
 * mocked. Proves that:
 *  - the kiosk supervisor session token reaches recordPickup as its second
 *    argument (the API layer sends it as the X-Supervisor-Session header),
 *  - the supervisor roster refetches exactly once after a recorded pickup,
 *  - cancelling in the verify step records nothing and changes nothing.
 */

import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { CheckinPage } from '../CheckinPage';
import { createTestQueryClient } from '@/test-utils/queryTestHarness';
import * as checkinApi from '@/services/api/checkin';
import * as pickupApi from '@/features/authorized-pickup/api';
import type {
  PickupLog,
  PickupVerificationResult,
} from '@/features/authorized-pickup/api';
import type {
  CheckinConfigDto,
  RoomRosterDto,
} from '@/services/api/types';

vi.mock('@/services/api/checkin', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/services/api/checkin')>()),
  supervisorLogin: vi.fn(),
  getMultipleRoomRosters: vi.fn(),
  getCheckinConfiguration: vi.fn(),
  checkout: vi.fn(),
}));

// Real AuthorizationLevel enum must survive so PickupVerification's hard-deny
// logic sees genuine enum values (card step 3).
vi.mock('@/features/authorized-pickup/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/authorized-pickup/api')>()),
  verifyPickup: vi.fn(),
  recordPickup: vi.fn(),
}));

// The print bridge is a local device side channel (localhost:9632); keep it
// stubbed so rendering CheckinPage performs no real I/O in tests.
vi.mock('@/services/printing/PrintBridgeClient', () => ({
  printBridgeClient: {
    checkHealth: vi.fn(async () => ({
      success: false as const,
      error: 'print bridge unavailable in tests',
    })),
    getDefaultZebraPrinter: vi.fn(async () => ({
      success: false as const,
      error: 'print bridge unavailable in tests',
    })),
    printBatch: vi.fn(),
  },
}));

const supervisorLoginMock = vi.mocked(checkinApi.supervisorLogin);
const rostersMock = vi.mocked(checkinApi.getMultipleRoomRosters);
const configMock = vi.mocked(checkinApi.getCheckinConfiguration);
const verifyPickupMock = vi.mocked(pickupApi.verifyPickup);
const recordPickupMock = vi.mocked(pickupApi.recordPickup);

const roster: RoomRosterDto = {
  locationIdKey: 'loc-1',
  locationName: 'Room 101',
  children: [
    {
      attendanceIdKey: 'att-1',
      personIdKey: 'per-1',
      fullName: 'Billy Smith',
      firstName: 'Billy',
      lastName: 'Smith',
      hasCriticalAllergies: false,
      securityCode: '1234',
      checkInTime: '2026-09-17T10:00:00Z',
      isFirstTime: false,
    },
  ],
  totalCount: 1,
  generatedAt: '2026-09-17T10:05:00Z',
  isAtCapacity: false,
  isNearCapacity: false,
};

const minimalConfig: CheckinConfigDto = {
  areas: [],
  currentSchedules: [],
  searchType: 'PhoneAndName',
  securityCodeLength: 4,
  securityCodeAlphanumeric: false,
  autoSelectDaysBack: 7,
  autoSelectFamily: false,
  preventDuplicateCheckin: true,
  allowCheckout: true,
};

const authorizedResult: PickupVerificationResult = {
  isAuthorized: true,
  authorizationLevel: pickupApi.AuthorizationLevel.Always,
  authorizedPickupIdKey: 'ap-1',
  message: 'Authorized for pickup',
  requiresSupervisorOverride: false,
};

const pickupLog: PickupLog = {
  idKey: 'log-1',
  attendanceIdKey: 'att-1',
  childName: 'Billy Smith',
  pickupPersonName: 'Sarah Smith',
  wasAuthorized: true,
  supervisorOverride: false,
  checkoutDateTime: '2026-09-17T10:30:00Z',
};

type UserEvent = ReturnType<typeof userEvent.setup>;

function renderPage() {
  const client = createTestQueryClient();
  return render(
    <QueryClientProvider client={client}>
      <CheckinPage />
    </QueryClientProvider>,
  );
}

/** Supervisor trigger → PIN entry → supervisor panel with loaded roster. */
async function enterSupervisorMode(user: UserEvent) {
  await user.click(screen.getByRole('button', { name: 'Supervisor Mode' }));
  await screen.findByRole('heading', { name: 'Supervisor PIN' });
  for (const digit of ['1', '2', '3', '4']) {
    await user.click(screen.getByRole('button', { name: digit }));
  }
  await user.click(screen.getByRole('button', { name: 'Submit' }));
  await screen.findByRole('heading', { name: 'Supervisor Mode' });
  // Roster for the supervisor has loaded.
  await screen.findByText('Billy Smith');
}

/** Checkout tab → select the child → PickupVerification step. */
async function openVerifyStep(user: UserEvent) {
  await user.click(screen.getByRole('button', { name: 'Checkout' }));
  // The list step renders "Select a person to check out" twice (header +
  // info box); the row button is the unambiguous readiness signal.
  await screen.findByRole('button', { name: 'Check Out' });
  await user.click(screen.getByRole('button', { name: 'Check Out' }));
  await screen.findByText(/checkout: billy smith/i);
}

/** Enter code + pickup person, verify — resolves the authorized result. */
async function verifyAuthorizedPickup(user: UserEvent) {
  await user.type(
    screen.getByLabelText('Security Code (provided by parent)'),
    '1234',
  );
  await user.type(
    screen.getByLabelText('Who is picking up this child?'),
    'Sarah Smith',
  );
  await user.click(screen.getByRole('button', { name: 'Verify Authorization' }));
  await screen.findByRole('heading', { name: 'Authorized' });
}

describe('CheckinPage — kiosk pickup (#710)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    configMock.mockResolvedValue(minimalConfig);
    rostersMock.mockResolvedValue([roster]);
    supervisorLoginMock.mockResolvedValue({
      sessionToken: 'sess-1',
      expiresAt: '2099-01-01T00:00:00Z',
      supervisor: {
        idKey: 'sup-1',
        fullName: 'Sam Supervisor',
        firstName: 'Sam',
        lastName: 'Supervisor',
      },
    });
    verifyPickupMock.mockResolvedValue(authorizedResult);
    recordPickupMock.mockResolvedValue(pickupLog);
  });

  it('forwards the supervisor session token to recordPickup on an override', async () => {
    // On a plain authorized pickup the product sends no session token —
    // PickupVerification forwards it only when the supervisor override is on.
    // So drive the override path: verification comes back unauthorized but
    // overridable (EmergencyOnly), and the supervisor confirms the override.
    verifyPickupMock.mockResolvedValue({
      ...authorizedResult,
      isAuthorized: false,
      requiresSupervisorOverride: true,
      authorizationLevel: pickupApi.AuthorizationLevel.EmergencyOnly,
    });

    const user = userEvent.setup();
    renderPage();

    await enterSupervisorMode(user);
    await openVerifyStep(user);
    await user.type(
      screen.getByLabelText('Security Code (provided by parent)'),
      '1234',
    );
    await user.type(
      screen.getByLabelText('Who is picking up this child?'),
      'Sarah Smith',
    );
    await user.click(screen.getByRole('button', { name: 'Verify Authorization' }));
    await screen.findByRole('heading', { name: 'Supervisor Approval Required' });

    await user.click(screen.getByLabelText(/supervisor override/i));
    await user.click(screen.getByRole('button', { name: /complete checkout/i }));
    await screen.findByText(/checkout complete/i);

    expect(recordPickupMock).toHaveBeenCalledTimes(1);
    // 'sess-1' rides as the second argument — the API layer sends it as the
    // X-Supervisor-Session header; it must never be serialized into the body.
    expect(recordPickupMock).toHaveBeenCalledWith(
      expect.objectContaining({
        attendanceIdKey: 'att-1',
        supervisorOverride: true,
      }),
      'sess-1',
    );
  });

  it('refetches the roster exactly once after a recorded pickup', async () => {
    const user = userEvent.setup();
    renderPage();

    await enterSupervisorMode(user);
    await openVerifyStep(user);
    await verifyAuthorizedPickup(user);

    const rosterCallsBeforeCompletion = rostersMock.mock.calls.length;

    await user.click(
      screen.getByRole('button', { name: 'Complete Checkout' }),
    );
    await screen.findByText(/checkout complete/i);

    // Exactly one more roster fetch after the recorded pickup.
    await waitFor(() => {
      expect(rostersMock.mock.calls.length).toBe(
        rosterCallsBeforeCompletion + 1,
      );
    });

    // …and nothing further arrives afterwards.
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(rostersMock.mock.calls.length).toBe(
      rosterCallsBeforeCompletion + 1,
    );
  });

  it('a cancelled verification changes nothing', async () => {
    const user = userEvent.setup();
    renderPage();

    await enterSupervisorMode(user);
    const rosterCallsAtStart = rostersMock.mock.calls.length;

    await openVerifyStep(user);
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    // Back on the checkout list.
    await screen.findByRole('button', { name: 'Check Out' });

    expect(recordPickupMock).not.toHaveBeenCalled();
    expect(rostersMock.mock.calls.length).toBe(rosterCallsAtStart);
  });
});
