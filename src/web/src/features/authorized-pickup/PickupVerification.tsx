/**
 * Pickup Verification Component — kiosk checkout verification step (#710).
 *
 * Runs at an anonymous kiosk: no auth context exists there, so this component
 * never reads useAuth. Supervisor authority arrives only as a validated
 * session token via the `sessionToken` prop (forwarded by the parent flow and
 * sent as the X-Supervisor-Session header by the API layer); the client never
 * sends or forges a supervisor identity field. Overrides require that
 * validated session; AuthorizationLevel.Never is a hard deny with no override
 * affordance and no Complete button.
 */

import { useRef, useState } from 'react';
import { useVerifyPickup, useRecordPickup } from './hooks';
import { ApiClientError } from '@/services/api/client';
import { AuthorizationLevel, type RecordPickupRequest, type VerifyPickupRequest } from './api';
import type { IdKey } from '@/services/api/types';

export interface PickupVerificationProps {
  attendanceIdKey: IdKey;
  childName: string;
  onSuccess: () => void;
  onCancel: () => void;
  /**
   * Validated kiosk supervisor session token from the parent flow. Required
   * only to record an override; absence simply disables that path — the
   * client never fabricates supervisor authority.
   */
  sessionToken?: string;
}

export function PickupVerification({
  attendanceIdKey,
  childName,
  onSuccess,
  onCancel,
  sessionToken,
}: PickupVerificationProps) {
  const [pickupPersonName, setPickupPersonName] = useState('');
  const [supervisorOverride, setSupervisorOverride] = useState(false);
  const [enteredSecurityCode, setEnteredSecurityCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  // True while a verify or record mutation is in flight. A ref keeps the
  // submit handlers synchronous, so a second click / Enter keypress during
  // the request cannot start a duplicate submission (#710 kiosk anti-double-tap).
  const [isSubmitting, setIsSubmitting] = useState(false);
  const isSubmittingRef = useRef(false);
  const setSubmitting = (busy: boolean) => {
    isSubmittingRef.current = busy;
    setIsSubmitting(busy);
  };
  const [verificationResult, setVerificationResult] = useState<{
    isAuthorized: boolean;
    authorizationLevel?: AuthorizationLevel;
    authorizedPickupIdKey?: IdKey;
    message: string;
    requiresSupervisorOverride: boolean;
  } | undefined>(undefined);

  const verifyPickup = useVerifyPickup();
  const recordPickup = useRecordPickup();

  const isHardDeny =
    !!verificationResult &&
    !verificationResult.isAuthorized &&
    verificationResult.authorizationLevel === AuthorizationLevel.Never;

  const handleVerify = async () => {
    if (
      !pickupPersonName.trim() ||
      !enteredSecurityCode.trim() ||
      isSubmittingRef.current ||
      verifyPickup.isPending
    ) {
      return;
    }

    setError(null);
    setSubmitting(true);

    const request: VerifyPickupRequest = {
      attendanceIdKey,
      pickupPersonName,
      securityCode: enteredSecurityCode,
    };

    try {
      const result = await verifyPickup.mutateAsync(request);
      setVerificationResult(result);
    } catch (error) {
      setError(
        error instanceof ApiClientError && error.statusCode === 401
          ? 'Kiosk supervisor session has expired. Please re-authenticate at the kiosk.'
          : 'Invalid security code or failed to verify pickup. Please try again.'
      );
      setEnteredSecurityCode('');
    } finally {
      setSubmitting(false);
    }
  };

  const handleRecordPickup = async () => {
    if (!verificationResult || isSubmittingRef.current || recordPickup.isPending) {
      return;
    }

    // If requires supervisor override and not provided
    if (
      verificationResult.requiresSupervisorOverride &&
      !supervisorOverride
    ) {
      return;
    }

    // Never-level denials are recorded by no one: hard block, no exceptions.
    if (isHardDeny) {
      return;
    }

    // An override requires a validated kiosk supervisor session. Without one
    // the client has no authority to override — refuse rather than forge.
    if (supervisorOverride && !sessionToken) {
      setError(
        'No kiosk supervisor session. A supervisor must authenticate at the kiosk before overriding.'
      );
      return;
    }

    setError(null);
    setSubmitting(true);

    // Non-override path: NO sessionToken key at all — an explicit undefined
    // key still lands on the object; the #710 ruling is that the tokenless
    // payload must not carry the field. The widened variable type matches
    // useRecordPickup's mutation variables (request + separate sessionToken).
    let recordRequest: RecordPickupRequest & { sessionToken?: string };
    if (supervisorOverride && sessionToken) {
      recordRequest = {
        attendanceIdKey,
        pickupPersonName,
        wasAuthorized: verificationResult.isAuthorized,
        authorizedPickupIdKey: verificationResult.authorizedPickupIdKey,
        supervisorOverride,
        sessionToken,
        notes: 'Supervisor override recorded at kiosk.',
      };
    } else {
      recordRequest = {
        attendanceIdKey,
        pickupPersonName,
        wasAuthorized: verificationResult.isAuthorized,
        authorizedPickupIdKey: verificationResult.authorizedPickupIdKey,
        supervisorOverride,
      };
    }

    try {
      await recordPickup.mutateAsync(recordRequest);

      onSuccess();
    } catch (error) {
      if (error instanceof ApiClientError && error.statusCode === 401) {
        setError('Kiosk supervisor session has expired. Please re-authenticate at the kiosk.');
      } else {
        setError('Failed to record pickup. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  };

  // Override affordance exists only when the backend explicitly requires one.
  // AuthorizationLevel.Never is a hard deny: no checkbox, no Complete.
  const showSupervisorOverride =
    !!verificationResult && verificationResult.requiresSupervisorOverride;

  return (
    <div className="bg-white shadow sm:rounded-lg">
      <div className="px-4 py-5 sm:p-6">
        <h3 className="text-lg leading-6 font-medium text-gray-900 mb-4">
          Checkout: {childName}
        </h3>

        {/* Combined security code and pickup person entry */}
        {!verificationResult && (
          <div className="space-y-4">
            <div>
              <label
                htmlFor="securityCode"
                className="block text-sm font-medium text-gray-700 mb-2"
              >
                Security Code (provided by parent)
              </label>
              <input
                type="text"
                id="securityCode"
                value={enteredSecurityCode}
                onChange={(e) => {
                  setEnteredSecurityCode(e.target.value);
                  setError(null);
                }}
                className="block w-full text-center text-2xl tracking-widest border-2 border-gray-300 rounded-lg p-3 focus:outline-none focus:ring-blue-500 focus:border-blue-500"
                placeholder="Enter code"
                maxLength={6}
                autoFocus
              />
            </div>

            <div>
              <label
                htmlFor="pickupPersonName"
                className="block text-sm font-medium text-gray-700 mb-2"
              >
                Who is picking up this child?
              </label>
              <input
                type="text"
                id="pickupPersonName"
                value={pickupPersonName}
                onChange={(e) => {
                  setPickupPersonName(e.target.value);
                  setError(null);
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    handleVerify();
                  }
                }}
                className="block w-full border border-gray-300 rounded-md shadow-sm py-3 px-4 text-lg focus:outline-none focus:ring-blue-500 focus:border-blue-500"
                placeholder="Enter name"
              />
            </div>

            <div className="flex gap-3">
              <button
                type="button"
                onClick={handleVerify}
                disabled={
                  !pickupPersonName.trim() ||
                  !enteredSecurityCode.trim() ||
                  isSubmitting ||
                  verifyPickup.isPending
                }
                className="flex-1 inline-flex justify-center items-center px-6 py-3 border border-transparent text-base font-medium rounded-md shadow-sm text-white bg-blue-600 hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {verifyPickup.isPending ? (
                  <>
                    <svg
                      className="animate-spin -ml-1 mr-3 h-5 w-5 text-white"
                      fill="none"
                      viewBox="0 0 24 24"
                    >
                      <circle
                        className="opacity-25"
                        cx="12"
                        cy="12"
                        r="10"
                        stroke="currentColor"
                        strokeWidth="4"
                      />
                      <path
                        className="opacity-75"
                        fill="currentColor"
                        d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                      />
                    </svg>
                    Verifying...
                  </>
                ) : (
                  'Verify Authorization'
                )}
              </button>
              <button
                type="button"
                onClick={onCancel}
                className="px-6 py-3 border border-gray-300 text-base font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500"
              >
                Cancel
              </button>
            </div>

            {error && (
              <div className="rounded-md bg-red-50 p-4">
                <p className="text-sm text-red-700">{error}</p>
              </div>
            )}
          </div>
        )}

        {/* Step 2: Show verification result */}
        {verificationResult && (
          <div className="space-y-4">
            {/* Authorized */}
            {verificationResult.isAuthorized &&
              !verificationResult.requiresSupervisorOverride && (
              <div className="rounded-md bg-green-50 p-4 border-2 border-green-300">
                <div className="flex">
                  <div className="flex-shrink-0">
                    <svg
                      className="h-5 w-5 text-green-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"
                      />
                    </svg>
                  </div>
                  <div className="ml-3 flex-1">
                    <h3 className="text-lg font-medium text-green-800">
                      Authorized
                    </h3>
                    <p className="mt-1 text-sm text-green-700">
                      {verificationResult.message}
                    </p>
                  </div>
                </div>
              </div>
            )}

            {/* Requires Supervisor Override */}
            {verificationResult.requiresSupervisorOverride && (
              <div className="rounded-md bg-yellow-50 p-4 border-2 border-yellow-300">
                <div className="flex">
                  <div className="flex-shrink-0">
                    <svg
                      className="h-8 w-8 text-yellow-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"
                      />
                    </svg>
                  </div>
                  <div className="ml-3 flex-1">
                    <h3 className="text-lg font-medium text-yellow-800">
                      Supervisor Approval Required
                    </h3>
                    <p className="mt-1 text-sm text-yellow-700">
                      {verificationResult.message}
                    </p>
                  </div>
                </div>
              </div>
            )}

            {/* Not Authorized */}
            {!verificationResult.isAuthorized &&
              !verificationResult.requiresSupervisorOverride && (
              <div className="rounded-md bg-red-50 p-4 border-2 border-red-300">
                <div className="flex">
                  <div className="flex-shrink-0">
                    <svg
                      className="h-8 w-8 text-red-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M10 14l2-2m0 0l2-2m-2 2l-2-2m2 2l2 2m7-2a9 9 0 11-18 0 9 9 0 0118 0z"
                      />
                    </svg>
                  </div>
                  <div className="ml-3 flex-1">
                    <h3 className="text-lg font-medium text-red-800">
                      Not Authorized
                    </h3>
                    <p className="mt-1 text-sm text-red-700">
                      {verificationResult.message}
                    </p>
                  </div>
                </div>
              </div>
            )}

            {/* Supervisor Override Section — only for results the backend
                flagged as overridable; Never (hard deny) never reaches here */}
            {showSupervisorOverride && (
              <div className="border-t pt-4">
                <label className="flex items-center mb-3">
                  <input
                    type="checkbox"
                    checked={supervisorOverride}
                    onChange={(e) => setSupervisorOverride(e.target.checked)}
                    className="h-4 w-4 text-blue-600 focus:ring-blue-500 border-gray-300 rounded"
                  />
                  <span className="ml-2 text-sm font-medium text-gray-700">
                    {sessionToken
                      ? 'Supervisor Override (validated kiosk session)'
                      : 'Supervisor Override (requires Supervisor role)'}
                  </span>
                </label>
              </div>
            )}

            {/* Action buttons */}
            <div className="flex gap-3 pt-4">
              {!isHardDeny && (
              <button
                type="button"
                onClick={handleRecordPickup}
                disabled={
                  isSubmitting ||
                  recordPickup.isPending ||
                  (showSupervisorOverride && !supervisorOverride)
                }
                className="flex-1 inline-flex justify-center items-center px-6 py-3 border border-transparent text-base font-medium rounded-md shadow-sm text-white bg-green-600 hover:bg-green-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-green-500 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {recordPickup.isPending ? (
                  <>
                    <svg
                      className="animate-spin -ml-1 mr-3 h-5 w-5 text-white"
                      fill="none"
                      viewBox="0 0 24 24"
                    >
                      <circle
                        className="opacity-25"
                        cx="12"
                        cy="12"
                        r="10"
                        stroke="currentColor"
                        strokeWidth="4"
                      />
                      <path
                        className="opacity-75"
                        fill="currentColor"
                        d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                      />
                    </svg>
                    Recording...
                  </>
                ) : (
                  'Complete Checkout'
                )}
              </button>
              )}
              <button
                type="button"
                onClick={() => {
                  setVerificationResult(undefined);
                  setPickupPersonName('');
                  setEnteredSecurityCode('');
                  setSupervisorOverride(false);
                }}
                disabled={recordPickup.isPending}
                className="px-6 py-3 border border-gray-300 text-base font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 disabled:opacity-50"
              >
                Try Again
              </button>
              <button
                type="button"
                onClick={onCancel}
                disabled={recordPickup.isPending}
                className="px-6 py-3 border border-gray-300 text-base font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 disabled:opacity-50"
              >
                Cancel
              </button>
            </div>

            {error && (
              <div className="rounded-md bg-red-50 p-4">
                <p className="text-sm text-red-700">{error}</p>
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
