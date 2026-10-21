/**
 * CheckoutFlow tests
 *
 * Issue #710: the kiosk checkout verify step runs PickupVerification (server-
 * verified guardian check + pickup audit log) instead of the legacy
 * client-side-only SecurityCodeVerify code compare.
 *
 * Catches:
 *  - Empty list message depends on whether a search query is active.
 *  - Search filters by person name / security code.
 *  - Security code display is partially masked (first 2 digits + dots).
 *  - "First Time" badge appears when flag is set.
 *  - Clicking Check Out advances to the verification step and renders
 *    PickupVerification for the selected attendance.
 *  - Cancel from verify step returns to list.
 */
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { CheckoutFlow } from '../CheckoutFlow';
import type { AttendanceResultDto } from '@/services/api/types';

// The verify step must render the shared kiosk verification component; its
// internals are covered by PickupVerification.test.tsx. Here we pin the mount
// contract: which props it receives.
vi.mock('@/features/authorized-pickup/PickupVerification', () => ({
  PickupVerification: ({
    attendanceIdKey,
    childName,
    sessionToken,
    onSuccess,
    onCancel,
  }: {
    attendanceIdKey: string;
    childName: string;
    sessionToken?: string;
    onSuccess: () => void;
    onCancel: () => void;
  }) => (
    <div data-testid="pickup-verification-stub">
      <span data-testid="pv-attendance">{attendanceIdKey}</span>
      <span data-testid="pv-child">{childName}</span>
      <span data-testid="pv-session">{sessionToken ?? 'none'}</span>
      <button onClick={onSuccess}>stub-complete</button>
      <button onClick={onCancel}>stub-cancel</button>
    </div>
  ),
}));

function attend(overrides: Partial<AttendanceResultDto> = {}): AttendanceResultDto {
  return {
    attendanceIdKey: 'ATT1',
    personIdKey: 'PER1',
    personName: 'Billy Smith',
    groupName: 'Kids',
    locationName: 'Room 101',
    scheduleName: '9am',
    securityCode: '1234',
    checkInTime: '2026-04-22T10:00:00Z',
    isFirstTime: false,
    ...overrides,
  };
}

async function openVerifyStep(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: /check out/i }));
  // Wait for the verification step to mount.
  await screen.findByTestId('pickup-verification-stub');
}

describe('CheckoutFlow — list step', () => {
  it('shows the empty-no-search message when there is nothing to check out', () => {
    render(<CheckoutFlow currentAttendance={[]} onCheckout={vi.fn()} />);
    expect(screen.getByText(/no one is currently checked in/i)).toBeInTheDocument();
  });

  it('lists checked-in people and masks the security code', () => {
    render(
      <CheckoutFlow
        currentAttendance={[attend({ securityCode: '4321' })]}
        onCheckout={vi.fn()}
      />,
    );
    expect(screen.getByText('Billy Smith')).toBeInTheDocument();
    // Only first 2 digits + bullets.
    expect(screen.getByText('43••')).toBeInTheDocument();
  });

  it('shows the First Time badge when isFirstTime=true', () => {
    render(
      <CheckoutFlow
        currentAttendance={[attend({ isFirstTime: true })]}
        onCheckout={vi.fn()}
      />,
    );
    expect(screen.getByText(/first time/i)).toBeInTheDocument();
  });

  it('filters the list by person name via search', async () => {
    const user = userEvent.setup();
    render(
      <CheckoutFlow
        currentAttendance={[
          attend({ attendanceIdKey: 'A', personName: 'Alice' }),
          attend({ attendanceIdKey: 'B', personName: 'Bob' }),
        ]}
        onCheckout={vi.fn()}
      />,
    );

    expect(screen.getByText('Alice')).toBeInTheDocument();
    expect(screen.getByText('Bob')).toBeInTheDocument();

    await user.type(screen.getByPlaceholderText(/search by name/i), 'ali');
    expect(screen.getByText('Alice')).toBeInTheDocument();
    expect(screen.queryByText('Bob')).toBeNull();
  });

  it('shows "no matching check-ins" when search matches nothing', async () => {
    const user = userEvent.setup();
    render(
      <CheckoutFlow
        currentAttendance={[attend({ personName: 'Alice' })]}
        onCheckout={vi.fn()}
      />,
    );
    await user.type(screen.getByPlaceholderText(/search by name/i), 'xyz');
    expect(screen.getByText(/no matching check-ins/i)).toBeInTheDocument();
  });

  it('filters by security code digits in search', async () => {
    const user = userEvent.setup();
    render(
      <CheckoutFlow
        currentAttendance={[
          attend({ attendanceIdKey: 'A', personName: 'Alice', securityCode: '1111' }),
          attend({ attendanceIdKey: 'B', personName: 'Bob', securityCode: '2222' }),
        ]}
        onCheckout={vi.fn()}
      />,
    );
    await user.type(screen.getByPlaceholderText(/search by name/i), '11');
    expect(screen.getByText('Alice')).toBeInTheDocument();
    expect(screen.queryByText('Bob')).toBeNull();
  });

  it('filters by an uppercase alphanumeric security code', async () => {
    const user = userEvent.setup();
    render(
      <CheckoutFlow
        currentAttendance={[
          attend({ attendanceIdKey: 'A', personName: 'Alice', securityCode: 'A7KQ' }),
          attend({ attendanceIdKey: 'B', personName: 'Bob', securityCode: 'B2M9' }),
        ]}
        onCheckout={vi.fn()}
      />,
    );
    await user.type(screen.getByPlaceholderText(/search by name/i), 'A7KQ');
    expect(screen.getByText('Alice')).toBeInTheDocument();
    expect(screen.queryByText('Bob')).toBeNull();
  });
});

describe('CheckoutFlow — verification step (#710)', () => {
  it('mounts PickupVerification with the attendance, child name and kiosk session', async () => {
    const user = userEvent.setup();
    render(
      <CheckoutFlow
        currentAttendance={[attend({ personName: 'Billy Smith' })]}
        onCheckout={vi.fn()}
        sessionToken="sess-token-9"
      />,
    );

    await openVerifyStep(user);

    expect(screen.getByTestId('pv-attendance')).toHaveTextContent('ATT1');
    expect(screen.getByTestId('pv-child')).toHaveTextContent('Billy Smith');
    expect(screen.getByTestId('pv-session')).toHaveTextContent('sess-token-9');
  });

  it('authorized completion shows the success screen and does NOT call the plain checkout endpoint', async () => {
    const user = userEvent.setup();
    const onCheckout = vi.fn();
    render(
      <CheckoutFlow
        currentAttendance={[attend()]}
        onCheckout={onCheckout}
        sessionToken="sess-token-9"
      />,
    );

    await openVerifyStep(user);
    await user.click(screen.getByRole('button', { name: 'stub-complete' }));

    expect(onCheckout).not.toHaveBeenCalled();

    expect(await screen.findByText(/checkout complete/i)).toBeInTheDocument();
  });

  it('returns to list when the verification step is cancelled', async () => {
    const user = userEvent.setup();
    render(
      <CheckoutFlow currentAttendance={[attend()]} onCheckout={vi.fn()} />,
    );

    await openVerifyStep(user);
    await user.click(screen.getByRole('button', { name: 'stub-cancel' }));

    expect(screen.getByPlaceholderText(/search by name/i)).toBeInTheDocument();
  });
});
