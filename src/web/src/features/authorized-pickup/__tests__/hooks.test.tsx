/**
 * features/authorized-pickup/hooks.ts
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { waitFor, act } from '@testing-library/react';

vi.mock('../api', () => ({
  getAuthorizedPickups: vi.fn(),
  addAuthorizedPickup: vi.fn(),
  updateAuthorizedPickup: vi.fn(),
  deleteAuthorizedPickup: vi.fn(),
  autoPopulateFamilyMembers: vi.fn(),
  verifyPickup: vi.fn(),
  recordPickup: vi.fn(),
  getPickupHistory: vi.fn(),
}));

import * as api from '../api';
import {
  useAddAuthorizedPickup,
  useAuthorizedPickups,
  useAutoPopulateFamilyMembers,
  useDeleteAuthorizedPickup,
  usePickupHistory,
  useRecordPickup,
  useUpdateAuthorizedPickup,
  useVerifyPickup,
} from '../hooks';
import { renderHookWithQuery } from '@/test-utils/queryTestHarness';

beforeEach(() => vi.clearAllMocks());

describe('useAuthorizedPickups', () => {
  it('fetches by childIdKey', async () => {
    vi.mocked(api.getAuthorizedPickups).mockResolvedValueOnce([] as never);
    const { result } = renderHookWithQuery(() => useAuthorizedPickups('c1'));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.getAuthorizedPickups).toHaveBeenCalledWith('c1');
  });
});

describe('usePickupHistory', () => {
  it('forwards date filters', async () => {
    vi.mocked(api.getPickupHistory).mockResolvedValueOnce([] as never);
    const { result } = renderHookWithQuery(() =>
      usePickupHistory('c1', '2024-01-01', '2024-02-01')
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.getPickupHistory).toHaveBeenCalledWith(
      'c1',
      '2024-01-01',
      '2024-02-01'
    );
  });
});

describe('CRUD mutations invalidate the child-scoped list', () => {
  it('add invalidates', async () => {
    vi.mocked(api.addAuthorizedPickup).mockResolvedValueOnce({ childIdKey: 'c1' } as never);
    const { result, client } = renderHookWithQuery(() => useAddAuthorizedPickup());
    const inv = vi.spyOn(client, 'invalidateQueries');
    await act(async () => {
      await result.current.mutateAsync({ childIdKey: 'c1', request: {} as never });
    });
    expect(inv).toHaveBeenCalledWith({
      queryKey: ['authorized-pickups', 'list', 'c1'],
    });
  });

  it('update invalidates by returned childIdKey', async () => {
    vi.mocked(api.updateAuthorizedPickup).mockResolvedValueOnce({
      idKey: 'ap1',
      childIdKey: 'c1',
    } as never);
    const { result, client } = renderHookWithQuery(() => useUpdateAuthorizedPickup());
    const inv = vi.spyOn(client, 'invalidateQueries');
    await act(async () => {
      await result.current.mutateAsync({
        pickupIdKey: 'ap1',
        request: { isActive: true } as never,
      });
    });
    expect(inv).toHaveBeenCalledWith({
      queryKey: ['authorized-pickups', 'list', 'c1'],
    });
  });

  it('delete uses the provided childIdKey for invalidation', async () => {
    vi.mocked(api.deleteAuthorizedPickup).mockResolvedValueOnce(undefined as never);
    const { result, client } = renderHookWithQuery(() => useDeleteAuthorizedPickup());
    const inv = vi.spyOn(client, 'invalidateQueries');
    await act(async () => {
      await result.current.mutateAsync({ pickupIdKey: 'ap1', childIdKey: 'c1' });
    });
    expect(api.deleteAuthorizedPickup).toHaveBeenCalledWith('ap1');
    expect(inv).toHaveBeenCalledWith({
      queryKey: ['authorized-pickups', 'list', 'c1'],
    });
  });

  it('autoPopulate invalidates child-scoped list', async () => {
    vi.mocked(api.autoPopulateFamilyMembers).mockResolvedValueOnce({
      message: 'ok',
      count: 1,
      pickups: [],
    } as never);
    const { result, client } = renderHookWithQuery(() => useAutoPopulateFamilyMembers());
    const inv = vi.spyOn(client, 'invalidateQueries');
    await act(async () => {
      await result.current.mutateAsync('c1');
    });
    expect(inv).toHaveBeenCalledWith({
      queryKey: ['authorized-pickups', 'list', 'c1'],
    });
  });
});

describe('useVerifyPickup / useRecordPickup', () => {
  it('verifyPickup is a plain mutation', async () => {
    vi.mocked(api.verifyPickup).mockResolvedValueOnce({ isAuthorized: true } as never);
    const { result } = renderHookWithQuery(() => useVerifyPickup());
    await act(async () => {
      await result.current.mutateAsync({
        attendanceIdKey: 'a1',
        securityCode: '1234',
      } as never);
    });
    expect(api.verifyPickup).toHaveBeenCalled();
  });

  it('recordPickup invalidates all pickup history', async () => {
    vi.mocked(api.recordPickup).mockResolvedValueOnce({ idKey: 'log1' } as never);
    const { result, client } = renderHookWithQuery(() => useRecordPickup());
    const inv = vi.spyOn(client, 'invalidateQueries');
    await act(async () => {
      await result.current.mutateAsync({
        attendanceIdKey: 'a1',
        wasAuthorized: true,
        supervisorOverride: false,
      } as never);
    });
    expect(inv).toHaveBeenCalledWith({
      queryKey: ['authorized-pickups', 'history'],
    });
  });

  it('useRecordPickup forwards the kiosk supervisor session token to the api layer (#710)', async () => {
    vi.mocked(api.recordPickup).mockResolvedValueOnce({ idKey: 'log1' } as never);
    const { result } = renderHookWithQuery(() => useRecordPickup());
    await act(async () => {
      await result.current.mutateAsync({
        attendanceIdKey: 'a1',
        pickupPersonName: 'Unknown Person',
        wasAuthorized: false,
        supervisorOverride: true,
        sessionToken: 'sess-token-1',
      } as never);
    });
    // The hook must strip the sessionToken from the wire payload (identity is
    // derived server-side from the header) and pass it as the second arg.
    expect(api.recordPickup).toHaveBeenCalledWith(
      expect.not.objectContaining({ sessionToken: expect.anything() }),
      'sess-token-1'
    );
  });

  it('useRecordPickup calls the api without a token when none is given (#710)', async () => {
    vi.mocked(api.recordPickup).mockResolvedValueOnce({ idKey: 'log1' } as never);
    const { result } = renderHookWithQuery(() => useRecordPickup());
    await act(async () => {
      await result.current.mutateAsync({
        attendanceIdKey: 'a1',
        wasAuthorized: true,
        supervisorOverride: false,
      } as never);
    });
    expect(api.recordPickup).toHaveBeenCalledWith(
      expect.not.objectContaining({ sessionToken: expect.anything() })
    );
  });
});
