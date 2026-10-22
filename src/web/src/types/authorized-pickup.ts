/**
 * Authorized Pickup Types
 *
 * TypeScript definitions for the authorized pickup and custody verification system.
 */

import type { IdKey, DateTime } from '@/services/api/types';

/**
 * Relationship between the authorized person and the child
 */
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

/**
 * Authorization level for pickup
 */
export enum AuthorizationLevel {
  /** Authorized to pick up at any time */
  Always = 0,
  /** Only authorized in emergency situations */
  EmergencyOnly = 1,
  /** Never authorized (custody restriction) */
  Never = 2,
}

/**
 * Represents an authorized person who can pick up a child
 */
export interface AuthorizedPickupDto {
  /** Unique identifier for this authorization record */
  idKey: IdKey;
  /** IdKey of the child */
  childIdKey: IdKey;
  /** Name of the child */
  childName: string;
  /** IdKey of the authorized person (if they exist in the system) */
  authorizedPersonIdKey?: IdKey;
  /** Name of the authorized person (from Person record if exists) */
  authorizedPersonName?: string;
  /** Name of the authorized person (manual entry if not in system) */
  name?: string;
  /** Phone number for contact verification */
  phoneNumber?: string;
  /** Relationship to the child */
  relationship: PickupRelationship;
  /** Level of authorization */
  authorizationLevel: AuthorizationLevel;
  /** URL to photo for visual verification */
  photoUrl?: string;
  /** Whether this authorization is currently active */
  isActive: boolean;
}

/**
 * Result of verifying a pickup request
 */
export interface PickupVerificationResultDto {
  /** Whether the person is authorized to pick up the child */
  isAuthorized: boolean;
  /** The authorization level if authorized */
  authorizationLevel?: AuthorizationLevel;
  /** IdKey of the matching authorization record if found */
  authorizedPickupIdKey?: IdKey;
  /** Human-readable message about the verification result */
  message: string;
  /** Whether supervisor override is required */
  requiresSupervisorOverride: boolean;
}

/**
 * Log entry for a pickup event
 */
export interface PickupLogDto {
  /** Unique identifier for this log entry */
  idKey: IdKey;
  /** IdKey of the attendance record being checked out */
  attendanceIdKey: IdKey;
  /** Name of the child being picked up */
  childName: string;
  /** Name of the person picking up the child */
  pickupPersonName: string;
  /** Whether the person was authorized */
  wasAuthorized: boolean;
  /** Whether a supervisor override was used */
  supervisorOverride: boolean;
  /** Name of the supervisor who approved override (if applicable) */
  supervisorName?: string;
  /** When the checkout occurred */
  checkoutDateTime: DateTime;
  /** Additional notes about the pickup */
  notes?: string;
}

/**
 * Request to create a new authorized pickup record
 */
export interface CreateAuthorizedPickupRequest {
  /** IdKey of the authorized person if they exist in the system */
  authorizedPersonIdKey?: IdKey;
  /** Name of the authorized person (manual entry if not in system) */
  name?: string;
  /** Phone number for contact verification */
  phoneNumber?: string;
  /** Relationship to the child */
  relationship: PickupRelationship;
  /** Level of authorization */
  authorizationLevel: AuthorizationLevel;
  /** URL to photo for visual verification */
  photoUrl?: string;
  /** Notes about custody arrangements or restrictions */
  custodyNotes?: string;
}

/**
 * Request to update an existing authorized pickup record
 */
export interface UpdateAuthorizedPickupRequest {
  /** Updated relationship to the child */
  relationship?: PickupRelationship;
  /** Updated authorization level */
  authorizationLevel?: AuthorizationLevel;
  /** Updated photo URL */
  photoUrl?: string;
  /** Updated custody notes */
  custodyNotes?: string;
  /** Whether this authorization should be active */
  isActive?: boolean;
}

/**
 * Request to verify if a person is authorized for pickup
 */
export interface VerifyPickupRequest {
  /** IdKey of the attendance record being checked out */
  attendanceIdKey: IdKey;
  /** IdKey of the person picking up (if they exist in the system) */
  pickupPersonIdKey?: IdKey;
  /** Name of the person picking up (manual entry) */
  pickupPersonName?: string;
  /** Security code for verification */
  securityCode: string;
}

/**
 * Request to record a completed pickup event
 */
export interface RecordPickupRequest {
  /** IdKey of the attendance record being checked out */
  attendanceIdKey: IdKey;
  /** IdKey of the person picking up (if they exist in the system) */
  pickupPersonIdKey?: IdKey;
  /** Name of the person picking up (manual entry) */
  pickupPersonName?: string;
  /** Whether the person was authorized */
  wasAuthorized: boolean;
  /** IdKey of the matching authorization record if found */
  authorizedPickupIdKey?: IdKey;
  /** Whether a supervisor override was used */
  supervisorOverride: boolean;
  /** IdKey of the supervisor who approved override */
  supervisorPersonIdKey?: IdKey;
  /** Additional notes about the pickup */
  notes?: string;
  /** The security code typed at the kiosk; record-pickup re-verifies with it (#710) */
  securityCode?: string;
}
