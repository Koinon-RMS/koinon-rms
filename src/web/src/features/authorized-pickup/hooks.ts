/**
 * Authorized Pickup hooks using TanStack Query
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import * as authorizedPickupApi from './api';
import type {
  CreateAuthorizedPickupRequest,
  UpdateAuthorizedPickupRequest,
  VerifyPickupRequest,
  RecordPickupRequest,
} from './api';
import type { IdKey, DateTime } from '@/services/api/types';

/**
 * Query key factory for authorized pickups
 */
const authorizedPickupKeys = {
  all: ['authorized-pickups'] as const,
  lists: () => [...authorizedPickupKeys.all, 'list'] as const,
  list: (childIdKey: IdKey) =>
    [...authorizedPickupKeys.lists(), childIdKey] as const,
  history: (childIdKey: IdKey, fromDate?: DateTime, toDate?: DateTime) =>
    [
      ...authorizedPickupKeys.all,
      'history',
      childIdKey,
      { fromDate, toDate },
    ] as const,
};

/**
 * Get authorized pickups for a child
 */
export function useAuthorizedPickups(childIdKey: IdKey) {
  return useQuery({
    queryKey: authorizedPickupKeys.list(childIdKey),
    queryFn: () => authorizedPickupApi.getAuthorizedPickups(childIdKey),
    staleTime: 30 * 1000, // 30 seconds
  });
}

/**
 * Add a new authorized pickup
 */
export function useAddAuthorizedPickup() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({
      childIdKey,
      request,
    }: {
      childIdKey: IdKey;
      request: CreateAuthorizedPickupRequest;
    }) => authorizedPickupApi.addAuthorizedPickup(childIdKey, request),
    onSuccess: (_, variables) => {
      // Invalidate the list for this child
      queryClient.invalidateQueries({
        queryKey: authorizedPickupKeys.list(variables.childIdKey),
      });
    },
  });
}

/**
 * Update an existing authorized pickup
 */
export function useUpdateAuthorizedPickup() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({
      pickupIdKey,
      request,
    }: {
      pickupIdKey: IdKey;
      request: UpdateAuthorizedPickupRequest;
    }) => authorizedPickupApi.updateAuthorizedPickup(pickupIdKey, request),
    onSuccess: (updatedPickup) => {
      // Invalidate the list for this child
      queryClient.invalidateQueries({
        queryKey: authorizedPickupKeys.list(updatedPickup.childIdKey),
      });
    },
  });
}

/**
 * Delete an authorized pickup
 */
export function useDeleteAuthorizedPickup() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({
      pickupIdKey,
      // childIdKey needed for cache invalidation in onSuccess
    }: {
      pickupIdKey: IdKey;
      childIdKey: IdKey;
    }) => authorizedPickupApi.deleteAuthorizedPickup(pickupIdKey),
    onSuccess: (_, variables) => {
      // Invalidate the list for this child
      queryClient.invalidateQueries({
        queryKey: authorizedPickupKeys.list(variables.childIdKey),
      });
    },
  });
}

/**
 * Auto-populate authorized pickups with family members
 */
export function useAutoPopulateFamilyMembers() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (childIdKey: IdKey) =>
      authorizedPickupApi.autoPopulateFamilyMembers(childIdKey),
    onSuccess: (_, childIdKey) => {
      // Invalidate the list for this child
      queryClient.invalidateQueries({
        queryKey: authorizedPickupKeys.list(childIdKey),
      });
    },
  });
}

/**
 * Verify pickup authorization
 */
export function useVerifyPickup() {
  return useMutation({
    mutationFn: (request: VerifyPickupRequest) =>
      authorizedPickupApi.verifyPickup(request),
  });
}

/**
 * Record a pickup event.
 *
 * Kiosk flow (#710): the optional kiosk supervisor session token travels
 * separately from the request DTO — the hook strips it from the payload and
 * forwards it as its own argument so the api layer sends it as the
 * `X-Supervisor-Session` header. The token is never serialized into JSON.
 */
export function useRecordPickup() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({
      sessionToken,
      ...request
    }: RecordPickupRequest & { sessionToken?: string }) =>
      sessionToken === undefined
        ? authorizedPickupApi.recordPickup(request)
        : authorizedPickupApi.recordPickup(request, sessionToken),
    onSuccess: () => {
      // Invalidate all history queries since we can't know which child
      queryClient.invalidateQueries({
        queryKey: [...authorizedPickupKeys.all, 'history'],
      });
    },
  });
}

/**
 * Get pickup history for a child
 */
export function usePickupHistory(
  childIdKey: IdKey,
  fromDate?: DateTime,
  toDate?: DateTime
) {
  return useQuery({
    queryKey: authorizedPickupKeys.history(childIdKey, fromDate, toDate),
    queryFn: () =>
      authorizedPickupApi.getPickupHistory(childIdKey, fromDate, toDate),
    staleTime: 60 * 1000, // 1 minute
  });
}
