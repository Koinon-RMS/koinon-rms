/**
 * Authorized Pickup API service
 */

import { get, post, put, del } from '@/services/api/client';
import type { IdKey, DateTime } from '@/services/api/types';

// ============================================================================
// Types
// ============================================================================

export enum PickupRelationship {
  Parent = 0,
  Grandparent = 1,
  Sibling = 2,
  Guardian = 3,
  Aunt = 4,
  Uncle = 5,
  Friend = 6,
  Other = 7,
}

export enum AuthorizationLevel {
  Always = 0,
  EmergencyOnly = 1,
  Never = 2,
}

export interface AuthorizedPickup {
  idKey: IdKey;
  childIdKey: IdKey;
  childName: string;
  authorizedPersonIdKey?: IdKey;
  authorizedPersonName?: string;
  name?: string;
  phoneNumber?: string;
  relationship: PickupRelationship;
  authorizationLevel: AuthorizationLevel;
  photoUrl?: string;
  isActive: boolean;
}

export interface PickupLog {
  idKey: IdKey;
  attendanceIdKey: IdKey;
  childName: string;
  pickupPersonName: string;
  wasAuthorized: boolean;
  supervisorOverride: boolean;
  supervisorName?: string;
  checkoutDateTime: DateTime;
  notes?: string;
}

export interface PickupVerificationResult {
  isAuthorized: boolean;
  authorizationLevel?: AuthorizationLevel;
  authorizedPickupIdKey?: IdKey;
  message: string;
  requiresSupervisorOverride: boolean;
}

export interface CreateAuthorizedPickupRequest {
  authorizedPersonIdKey?: IdKey;
  name?: string;
  phoneNumber?: string;
  relationship: PickupRelationship;
  authorizationLevel: AuthorizationLevel;
  photoUrl?: string;
  custodyNotes?: string;
}

export interface UpdateAuthorizedPickupRequest {
  relationship?: PickupRelationship;
  authorizationLevel?: AuthorizationLevel;
  photoUrl?: string;
  custodyNotes?: string;
  isActive?: boolean;
}

export interface VerifyPickupRequest {
  attendanceIdKey: IdKey;
  pickupPersonIdKey?: IdKey;
  pickupPersonName?: string;
  securityCode: string;
}

export interface RecordPickupRequest {
  attendanceIdKey: IdKey;
  pickupPersonIdKey?: IdKey;
  pickupPersonName?: string;
  wasAuthorized: boolean;
  authorizedPickupIdKey?: IdKey;
  supervisorOverride: boolean;
  supervisorPersonIdKey?: IdKey;
  notes?: string;
  /** The security code typed at the kiosk; record-pickup re-verifies with it (#710). */
  securityCode?: string;
}

// ============================================================================
// API Functions
// ============================================================================

// The backend wraps responses in { data: ... } envelopes — unwrap here so
// the feature hooks see plain payloads.
type Envelope<T> = { data: T };

/**
 * Get all authorized pickups for a child
 */
export async function getAuthorizedPickups(
  childIdKey: IdKey
): Promise<AuthorizedPickup[]> {
  const response = await get<Envelope<AuthorizedPickup[]>>(
    `/people/${childIdKey}/authorized-pickups`
  );
  return response.data;
}

/**
 * Add a new authorized pickup for a child
 */
export async function addAuthorizedPickup(
  childIdKey: IdKey,
  request: CreateAuthorizedPickupRequest
): Promise<AuthorizedPickup> {
  const response = await post<Envelope<AuthorizedPickup>>(
    `/people/${childIdKey}/authorized-pickups`,
    request
  );
  return response.data;
}

/**
 * Update an existing authorized pickup
 */
export async function updateAuthorizedPickup(
  pickupIdKey: IdKey,
  request: UpdateAuthorizedPickupRequest
): Promise<AuthorizedPickup> {
  const response = await put<Envelope<AuthorizedPickup>>(
    `/authorized-pickups/${pickupIdKey}`,
    request
  );
  return response.data;
}

/**
 * Delete (deactivate) an authorized pickup
 */
export async function deleteAuthorizedPickup(pickupIdKey: IdKey): Promise<void> {
  await del(`/authorized-pickups/${pickupIdKey}`);
}

/**
 * Auto-populate authorized pickups with family members
 */
export async function autoPopulateFamilyMembers(
  childIdKey: IdKey
): Promise<{ message: string; count: number; pickups: AuthorizedPickup[] }> {
  const response = await post<
    Envelope<{ message: string; count: number; pickups: AuthorizedPickup[] }>
  >(`/people/${childIdKey}/authorized-pickups/auto-populate`);
  return response.data;
}

/**
 * Verify if a person is authorized to pick up a child
 */
export async function verifyPickup(
  request: VerifyPickupRequest
): Promise<PickupVerificationResult> {
  const response = await post<Envelope<PickupVerificationResult>>(
    '/checkin/verify-pickup',
    request
  );
  return response.data;
}

/**
 * Record a pickup event and checkout the child.
 *
 * Kiosk flow (#710): an optional supervisor session token is sent as the
 * `X-Supervisor-Session` header — never serialized into the request body —
 * so the backend derives the supervisor identity server-side (see
 * services/api/checkin.ts supervisorLogout/supervisorReprint precedent).
 */
export async function recordPickup(
  request: RecordPickupRequest,
  sessionToken?: string
): Promise<PickupLog> {
  const response = sessionToken
    ? await post<Envelope<PickupLog>>(
        '/checkin/record-pickup',
        request,
        {
          headers: { 'X-Supervisor-Session': sessionToken },
        }
      )
    : await post<Envelope<PickupLog>>('/checkin/record-pickup', request);
  return response.data;
}

/**
 * Get pickup history for a child
 */
export async function getPickupHistory(
  childIdKey: IdKey,
  fromDate?: DateTime,
  toDate?: DateTime
): Promise<PickupLog[]> {
  const queryParams = new URLSearchParams();

  if (fromDate) {
    queryParams.append('fromDate', fromDate);
  }

  if (toDate) {
    queryParams.append('toDate', toDate);
  }

  const query = queryParams.toString();
  const endpoint = `/people/${childIdKey}/pickup-history${query ? `?${query}` : ''}`;

  const response = await get<Envelope<PickupLog[]>>(endpoint);
  return response.data;
}
