/**
 * features/authorized-pickup/api.ts tests.
 *
 * Issue #710 spec pins:
 *  - Every function unwraps the `{ data: T }` envelope and resolves the inner
 *    DTO. In particular `recordPickup` must resolve a real `PickupLog` — the
 *    "silent undefined" regression the issue was opened for.
 *  - `recordPickup` accepts an optional kiosk supervisor session token and
 *    forwards it as the `X-Supervisor-Session` header. The server derives the
 *    supervisor identity from that session; the client NEVER sends
 *    `supervisorPersonIdKey` (architect ruling on #710).
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';

vi.mock('@/services/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
}));

import * as client from '@/services/api/client';
import {
  addAuthorizedPickup,
  AuthorizationLevel,
  autoPopulateFamilyMembers,
  deleteAuthorizedPickup,
  getAuthorizedPickups,
  getPickupHistory,
  PickupRelationship,
  recordPickup,
  updateAuthorizedPickup,
  verifyPickup,
} from '../api';
import type { RecordPickupRequest } from '../api';

const mockGet = vi.mocked(client.get);
const mockPost = vi.mocked(client.post);
const mockPut = vi.mocked(client.put);
const mockDel = vi.mocked(client.del);

// The kiosk contract (#710): recordPickup carries the supervisor session as a
// header so the backend can derive the override identity server-side. Cast to
// the target signature so the spec compiles before the implementation lands.
type RecordPickupWithSession = (
  request: RecordPickupRequest,
  sessionToken?: string
) => Promise<import('../api').PickupLog>;
const recordPickupWithSession = recordPickup as unknown as RecordPickupWithSession;

beforeEach(() => {
  mockGet.mockReset();
  mockPost.mockReset();
  mockPut.mockReset();
  mockDel.mockReset();
});

describe('authorized-pickup api (features tier)', () => {
  it('getAuthorizedPickups unwraps data envelope', async () => {
    mockGet.mockResolvedValueOnce({ data: [{ idKey: 'ap1' }] } as never);
    const out = await getAuthorizedPickups('c1');
    expect(mockGet).toHaveBeenCalledWith('/people/c1/authorized-pickups');
    expect(out).toEqual([{ idKey: 'ap1' }]);
  });

  it('addAuthorizedPickup posts and unwraps the created pickup', async () => {
    mockPost.mockResolvedValueOnce({ data: { idKey: 'ap1', childIdKey: 'c1' } } as never);
    const out = await addAuthorizedPickup('c1', {
      name: 'Bob',
      relationship: PickupRelationship.Parent,
      authorizationLevel: AuthorizationLevel.Always,
    });
    const [url, body] = mockPost.mock.calls[0];
    expect(url).toBe('/people/c1/authorized-pickups');
    expect(body).toMatchObject({
      name: 'Bob',
      relationship: PickupRelationship.Parent,
      authorizationLevel: AuthorizationLevel.Always,
    });
    expect(out).toEqual({ idKey: 'ap1', childIdKey: 'c1' });
  });

  it('updateAuthorizedPickup puts', async () => {
    mockPut.mockResolvedValueOnce({ data: { idKey: 'ap1' } } as never);
    await updateAuthorizedPickup('ap1', { isActive: false });
    expect(mockPut).toHaveBeenCalledWith('/authorized-pickups/ap1', {
      isActive: false,
    });
  });

  it('deleteAuthorizedPickup deletes', async () => {
    mockDel.mockResolvedValueOnce(undefined as never);
    await deleteAuthorizedPickup('ap1');
    expect(mockDel).toHaveBeenCalledWith('/authorized-pickups/ap1');
  });

  it('autoPopulateFamilyMembers posts without body', async () => {
    mockPost.mockResolvedValueOnce({
      data: { message: 'ok', count: 0, pickups: [] },
    } as never);
    await autoPopulateFamilyMembers('c1');
    expect(mockPost).toHaveBeenCalledWith(
      '/people/c1/authorized-pickups/auto-populate'
    );
  });
});

describe('verifyPickup / recordPickup envelope contract (#710)', () => {
  it('verifyPickup resolves the unwrapped verification result', async () => {
    const verification = {
      isAuthorized: true,
      authorizationLevel: AuthorizationLevel.Always,
      authorizedPickupIdKey: 'ap1',
      message: 'Sarah Smith is authorized to pick up Billy.',
      requiresSupervisorOverride: false,
    };
    mockPost.mockResolvedValueOnce({ data: verification } as never);
    const req = {
      attendanceIdKey: 'a1',
      pickupPersonName: 'Sarah Smith',
      securityCode: '4321',
    };
    const out = await verifyPickup(req);
    expect(mockPost).toHaveBeenCalledWith('/checkin/verify-pickup', req);
    // No silent undefined: the inner DTO must come back whole.
    expect(out).toEqual(verification);
  });

  it('recordPickup resolves the unwrapped PickupLog DTO (issue #710 acceptance)', async () => {
    const pickupLog = {
      idKey: 'log1',
      attendanceIdKey: 'a1',
      childName: 'Billy Johnson',
      pickupPersonName: 'Sarah Smith',
      wasAuthorized: true,
      supervisorOverride: false,
      supervisorName: undefined,
      checkoutDateTime: '2026-09-11T12:00:00Z',
      notes: undefined,
    };
    mockPost.mockResolvedValueOnce({ data: pickupLog } as never);
    const req = {
      attendanceIdKey: 'a1',
      wasAuthorized: true,
      supervisorOverride: false,
    };
    const out = await recordPickup(req);
    expect(mockPost).toHaveBeenCalledWith('/checkin/record-pickup', req);
    // The exact regression #710 was opened for: a flat (unwrapped) backend
    // response made this resolve to `undefined` silently.
    expect(out).toEqual(pickupLog);
  });

  it('recordPickup rejects when the backend returns an error envelope', async () => {
    mockPost.mockRejectedValueOnce(new Error('403') as never);
    await expect(
      recordPickup({
        attendanceIdKey: 'a1',
        wasAuthorized: false,
        supervisorOverride: true,
      })
    ).rejects.toThrow('403');
  });
});

describe('recordPickup kiosk supervisor session header (#710)', () => {
  const overrideRequest = {
    attendanceIdKey: 'a1',
    pickupPersonName: 'Unknown Person',
    wasAuthorized: false,
    supervisorOverride: true,
  };

  it('forwards the session token as X-Supervisor-Session', async () => {
    mockPost.mockResolvedValueOnce({ data: { idKey: 'log1' } } as never);
    await recordPickupWithSession(overrideRequest, 'sess-token-1');
    expect(mockPost).toHaveBeenCalledWith('/checkin/record-pickup', overrideRequest, {
      headers: { 'X-Supervisor-Session': 'sess-token-1' },
    });
  });

  it('omits the header entirely when no session token is provided', async () => {
    mockPost.mockResolvedValueOnce({ data: { idKey: 'log1' } } as never);
    await recordPickupWithSession(overrideRequest);
    const options = mockPost.mock.calls[0]?.[2];
    expect(options).toBeUndefined();
  });
});

describe('getPickupHistory', () => {
  it('with/without date range', async () => {
    mockGet.mockResolvedValueOnce({ data: [] } as never);
    await getPickupHistory('c1');
    expect(mockGet).toHaveBeenCalledWith('/people/c1/pickup-history');

    mockGet.mockResolvedValueOnce({ data: [] } as never);
    await getPickupHistory('c1', '2024-01-01', '2024-02-01');
    const url = mockGet.mock.calls.at(-1)?.[0] as string;
    expect(url).toContain('fromDate=2024-01-01');
    expect(url).toContain('toDate=2024-02-01');
  });
});
