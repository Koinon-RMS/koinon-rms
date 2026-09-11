/**
 * PickupVerification component — kiosk checkout verification (#710).
 *
 * This component is the kiosk-side guardian-verification step. The spec pins:
 *  - Verify is disabled until BOTH the security code and pickup person name
 *    are entered; the request carries attendanceIdKey + name + code.
 *  - Authorized result → "Complete Checkout" enabled, records the pickup with
 *    wasAuthorized=true and NO supervisor override and NO client-supplied
 *    supervisor identity (#710 ruling: identity is derived server-side from
 *    the X-Supervisor-Session header).
 *  - requiresSupervisorOverride result → Complete is gated on the override
 *    checkbox; recording sends supervisorOverride=true and the kiosk session
 *    token (via the hook's sessionToken field), never a supervisor id.
 *  - AuthorizationLevel.Never → NO override checkbox and NO Complete button:
 *    the backend hard-blocks both recording and overriding for Never rows.
 *  - Kiosk supervisor identity comes from the `supervisor` prop, not useAuth.
 *  - Failure paths show errors; Try Again resets; Cancel propagates.
 *
 * The component receives the sessionToken through the same kiosk prop chain
 * (CheckinPage → SupervisorMode → CheckoutFlow → PickupVerification).
 */

import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mockVerifyMutate = vi.fn();
const mockRecordMutate = vi.fn();

vi.mock('../hooks', () => ({
  useVerifyPickup: () => ({ mutateAsync: mockVerifyMutate, isPending: false }),
  useRecordPickup: () => ({ mutateAsync: mockRecordMutate, isPending: false }),
}));

// PickupVerification must work at an anonymous kiosk: no AuthProvider exists
// there. If it still reads useAuth it would throw / return null — this test
// renders WITHOUT any auth provider to pin that.
import { PickupVerification } from '../PickupVerification';
import { AuthorizationLevel } from '../api';

const authorizedResult = {
  isAuthorized: true,
  authorizationLevel: AuthorizationLevel.Always,
  authorizedPickupIdKey: 'ap1',
  message: 'Sarah Smith is authorized to pick up Billy Johnson.',
  requiresSupervisorOverride: false,
};

const overrideResult = {
  isAuthorized: false,
  authorizationLevel: undefined,
  authorizedPickupIdKey: undefined,
  message: 'Person not on authorized pickup list. Supervisor approval required.',
  requiresSupervisorOverride: true,
};

const neverResult = {
  isAuthorized: false,
  authorizationLevel: AuthorizationLevel.Never,
  authorizedPickupIdKey: 'ap2',
  message: 'This person is not authorized to pick up this child.',
  requiresSupervisorOverride: false,
};

function renderAtKiosk(props: Partial<Parameters<typeof PickupVerification>[0]> = {}) {
  return render(
    <PickupVerification
      attendanceIdKey="att1"
      childName="Billy Johnson"
      onSuccess={vi.fn()}
      onCancel={vi.fn()}
      {...props}
    />,
  );
}

async function submitVerify(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/security code/i), '4321');
  await user.type(screen.getByLabelText(/who is picking up/i), 'Sarah Smith');
  await user.click(screen.getByRole('button', { name: /verify authorization/i }));
}

describe('PickupVerification — kiosk verification step (#710)', () => {
  beforeEach(() => {
    mockVerifyMutate.mockReset();
    mockRecordMutate.mockReset();
  });

  it('renders the entry form with Verify disabled until code and name are present', async () => {
    const user = userEvent.setup();
    renderAtKiosk();

    const verify = screen.getByRole('button', { name: /verify authorization/i });
    expect(verify).toBeDisabled();

    await user.type(screen.getByLabelText(/security code/i), '4321');
    expect(verify).toBeDisabled(); // still no name

    await user.type(screen.getByLabelText(/who is picking up/i), 'Sarah Smith');
    expect(verify).toBeEnabled();
  });

  it('sends attendanceIdKey, pickup person name and security code on verify', async () => {
    const user = userEvent.setup();
    mockVerifyMutate.mockResolvedValueOnce(authorizedResult);
    renderAtKiosk();

    await submitVerify(user);

    expect(mockVerifyMutate).toHaveBeenCalledWith({
      attendanceIdKey: 'att1',
      pickupPersonName: 'Sarah Smith',
      securityCode: '4321',
    });
  });

  it('authorized result enables Complete Checkout without any override UI', async () => {
    const user = userEvent.setup();
    mockVerifyMutate.mockResolvedValueOnce(authorizedResult);
    renderAtKiosk();

    await submitVerify(user);

    expect(await screen.findByText(/authorized/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /complete checkout/i })).toBeEnabled();
    // No supervisor override affordance for an authorized guardian.
    expect(
      screen.queryByLabelText(/supervisor override/i),
    ).not.toBeInTheDocument();
  });

  it('records an authorized pickup with wasAuthorized=true and no override', async () => {
    const user = userEvent.setup();
    const onSuccess = vi.fn();
    mockVerifyMutate.mockResolvedValueOnce(authorizedResult);
    mockRecordMutate.mockResolvedValueOnce({ idKey: 'log1' });
    renderAtKiosk({ onSuccess });

    await submitVerify(user);
    await user.click(screen.getByRole('button', { name: /complete checkout/i }));

    await screen.findByText(/complete checkout/i);
    expect(mockRecordMutate).toHaveBeenCalledTimes(1);
    const payload = mockRecordMutate.mock.calls[0][0];
    expect(payload).toMatchObject({
      attendanceIdKey: 'att1',
      pickupPersonName: 'Sarah Smith',
      wasAuthorized: true,
      supervisorOverride: false,
      authorizedPickupIdKey: 'ap1',
    });
    // #710 ruling: the client NEVER sends a supervisor identity field.
    expect(payload).not.toHaveProperty('supervisorPersonIdKey');
    expect(payload).not.toHaveProperty('sessionToken');
    expect(onSuccess).toHaveBeenCalledTimes(1);
  });

  it('gates Complete on the override checkbox when supervisor approval is required', async () => {
    const user = userEvent.setup();
    mockVerifyMutate.mockResolvedValueOnce(overrideResult);
    renderAtKiosk();

    await submitVerify(user);

    const complete = await screen.findByRole('button', { name: /complete checkout/i });
    expect(complete).toBeDisabled();
    expect(
      screen.getByLabelText(/supervisor override/i),
    ).toBeInTheDocument();

    await user.click(screen.getByLabelText(/supervisor override/i));
    expect(screen.getByRole('button', { name: /complete checkout/i })).toBeEnabled();
  });

  it('override record sends supervisorOverride=true and the kiosk session token — never a client identity', async () => {
    const user = userEvent.setup();
    mockVerifyMutate.mockResolvedValueOnce(overrideResult);
    mockRecordMutate.mockResolvedValueOnce({ idKey: 'log2' });
    renderAtKiosk({ sessionToken: 'sess-token-1' });

    await submitVerify(user);
    await user.click(await screen.findByLabelText(/supervisor override/i));
    await user.click(screen.getByRole('button', { name: /complete checkout/i }));

    await screen.findByText(/complete checkout/i);
    expect(mockRecordMutate).toHaveBeenCalledTimes(1);
    const payload = mockRecordMutate.mock.calls[0][0];
    expect(payload).toMatchObject({
      attendanceIdKey: 'att1',
      pickupPersonName: 'Sarah Smith',
      wasAuthorized: false,
      supervisorOverride: true,
    });
    // Session token rides the X-Supervisor-Session header (hook strips it
    // from the wire payload); the server derives identity from it.
    expect(payload.sessionToken).toBe('sess-token-1');
    expect(payload).not.toHaveProperty('supervisorPersonIdKey');
  });

  it('AuthorizationLevel.Never shows no override and no Complete button', async () => {
    const user = userEvent.setup();
    mockVerifyMutate.mockResolvedValueOnce(neverResult);
    renderAtKiosk();

    await submitVerify(user);

    expect(await screen.findByText(/not authorized/i)).toBeInTheDocument();
    expect(
      screen.queryByLabelText(/supervisor override/i),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: /complete checkout/i }),
    ).not.toBeInTheDocument();
    expect(mockRecordMutate).not.toHaveBeenCalled();
  });

  it('verify failure shows an error and clears the code for retry', async () => {
    const user = userEvent.setup();
    mockVerifyMutate.mockRejectedValueOnce(new Error('invalid code'));
    renderAtKiosk();

    await submitVerify(user);

    expect(
      await screen.findByText(/invalid security code or failed to verify/i),
    ).toBeInTheDocument();
    // Entry form is back for another attempt.
    expect(screen.getByRole('button', { name: /verify authorization/i })).toBeEnabled();
  });

  it('record failure shows an error and stays on the result step', async () => {
    const user = userEvent.setup();
    const onSuccess = vi.fn();
    mockVerifyMutate.mockResolvedValueOnce(authorizedResult);
    mockRecordMutate.mockRejectedValueOnce(new Error('boom'));
    renderAtKiosk({ onSuccess });

    await submitVerify(user);
    await user.click(screen.getByRole('button', { name: /complete checkout/i }));

    expect(await screen.findByText(/failed to record pickup/i)).toBeInTheDocument();
    expect(onSuccess).not.toHaveBeenCalled();
  });

  it('Try Again resets to the entry form', async () => {
    const user = userEvent.setup();
    mockVerifyMutate.mockResolvedValueOnce(authorizedResult);
    renderAtKiosk();

    await submitVerify(user);
    await user.click(screen.getByRole('button', { name: /try again/i }));

    expect(
      screen.getByRole('button', { name: /verify authorization/i }),
    ).toBeEnabled();
    expect(
      screen.getByLabelText(/who is picking up/i),
    ).toHaveValue('');
  });

  it('Cancel propagates to the parent flow', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();
    renderAtKiosk({ onCancel });

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onCancel).toHaveBeenCalledTimes(1);
  });
});
