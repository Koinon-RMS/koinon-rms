import { useState, useEffect, useRef, useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  KioskLayout,
  PhoneSearch,
  FamilySearch,
  FamilyMemberList,
  CheckinConfirmation,
  IdleWarningModal,
  PrintStatus,
  QrScanner,
  OfflineQueueIndicator,
  PinEntry,
  SupervisorMode,
  KioskFamilyRegistration,
} from '@/components/checkin';
import type { OpportunitySelection } from '@/components/checkin';
import { Button, Card } from '@/components/ui';
import {
  useCheckinSearch,
  useCheckinOpportunities,
  useCheckinConfiguration,
} from '@/hooks/useCheckin';
import {
  useSupervisorAttendance,
  extractLocationIdKeys,
} from '@/hooks/useSupervisorAttendance';
import { useOfflineCheckin } from '@/hooks/useOfflineCheckin';
import { useIdleTimeout } from '@/hooks/useIdleTimeout';
import { useSupervisorMode } from '@/hooks/useSupervisorMode';
import type {
  CheckinFamilyDto,
  CheckinRequestItem,
  LabelDto,
  BatchCheckinResultDto,
} from '@/services/api/types';
import { getLabels } from '@/services/api/checkin';
import { createSelectionKey, getTotalActivitiesCount } from '@/utils/checkinHelpers';
import { printBridgeClient, type PrinterInfo } from '@/services/printing/PrintBridgeClient';
import { OfflineIndicator } from '@/components/pwa';
import { supervisorLogin, supervisorLogout, supervisorReprint, checkout } from '@/services/api/checkin';
import { getErrorMessage } from '@/lib/errorMessages';
import { ApiClientError } from '@/services/api/client';
import { offlineFamilyCache } from '@/services/offline/OfflineFamilyCache';
import { offlineCheckinQueue } from '@/services/offline/OfflineCheckinQueue';

type CheckinStep = 'search' | 'select-family' | 'select-members' | 'confirmation' | 'register';
type SearchMode = 'phone' | 'name' | 'qr';

// Idle timeout configuration
// Dev uses a shorter timeout than production, but must be long enough
// for multi-step E2E tests (~15-20s) to complete without triggering.
const IS_DEV = import.meta.env.DEV;
const IDLE_CONFIG = {
  timeout: IS_DEV ? 18 * 1000 : 60 * 1000,
  warningTime: IS_DEV ? 13 * 1000 : 50 * 1000,
};

export function CheckinPage() {
  // Query client for cache management
  const queryClient = useQueryClient();

  // State
  const [step, setStep] = useState<CheckinStep>('search');
  const [searchMode, setSearchMode] = useState<SearchMode>('phone');
  const [searchValue, setSearchValue] = useState<string>('');
  const [qrScannedIdKey, setQrScannedIdKey] = useState<string | null>(null);
  const [showQrScanner, setShowQrScanner] = useState(false);
  const [selectedFamily, setSelectedFamily] = useState<CheckinFamilyDto | null>(null);
  const [selectedCheckins, setSelectedCheckins] = useState<
    Map<string, OpportunitySelection[]>
  >(new Map());
  const [checkinError, setCheckinError] = useState<string | null>(null);
  const [printerAvailable, setPrinterAvailable] = useState<PrinterInfo | null>(null);
  const [printStatus, setPrintStatus] = useState<'idle' | 'printing' | 'success' | 'error'>('idle');
  const [printError, setPrintError] = useState<string | null>(null);
  const [hasSearchInput, setHasSearchInput] = useState(false);
  const [isCheckingIn, setIsCheckingIn] = useState(false);

  // Supervisor mode state
  const [showPinEntry, setShowPinEntry] = useState(false);
  const [pinError, setPinError] = useState<string | null>(null);
  const [isPinLoading, setIsPinLoading] = useState(false);
  const supervisorMode = useSupervisorMode();

  // Admin menu state
  const [showAdminMenu, setShowAdminMenu] = useState(false);
  const [showClearCacheConfirm, setShowClearCacheConfirm] = useState(false);

  // Fetch check-in configuration for kiosk locations
  const configQuery = useCheckinConfiguration();

  // Extract location IdKeys from configuration for supervisor attendance
  const locationIdKeys = extractLocationIdKeys(configQuery.data?.areas);

  // Fetch current attendance for supervisor mode (only when active)
  const supervisorAttendanceQuery = useSupervisorAttendance(
    locationIdKeys,
    supervisorMode.isActive
  );

  // Store check-in results and fetched labels for confirmation page
  const checkinResultsRef = useRef<BatchCheckinResultDto | null>(null);
  const checkinLabelsRef = useRef<LabelDto[]>([]);

  // Track reset timeout for cleanup
  const resetTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Determine search type based on mode
  const getSearchType = (): 'Phone' | 'Name' | 'Auto' => {
    if (searchMode === 'phone') return 'Phone';
    if (searchMode === 'name') return 'Name';
    // QR mode uses 'Auto' which will interpret IdKey
    return 'Auto';
  };

  // Queries
  const searchQuery = useCheckinSearch(searchValue, getSearchType());

  const opportunitiesQuery = useCheckinOpportunities(selectedFamily?.idKey);

  const {
    recordCheckin,
    state: offlineState,
    syncQueue,
    isPending: isRecordingCheckin,
  } = useOfflineCheckin();

  // Handlers
  const handleSearch = (value: string) => {
    setSearchValue(value);
    // When query succeeds, move to family selection if multiple results
    // or directly to member selection if single result
  };

  const handleQrScan = (familyIdKey: string) => {
    // QR scan uses IdKey search - set mode to 'qr' and trigger search via effect
    // This prevents race condition where searchValue might be updated before searchMode
    setQrScannedIdKey(familyIdKey);
    setSearchMode('qr');
  };

  // Effect: Check printer availability on mount
  useEffect(() => {
    const checkPrinter = async () => {
      const result = await printBridgeClient.getDefaultZebraPrinter();
      if (result.success && result.data) {
        setPrinterAvailable(result.data);
      }
    };
    checkPrinter();
  }, []);

  // Effect: Cleanup reset timeout on unmount
  useEffect(() => {
    const ref = resetTimeoutRef;
    return () => {
      if (ref.current) {
        clearTimeout(ref.current);
      }
    };
  }, []);

  // Effect: Handle QR scan state sequencing to prevent race condition
  useEffect(() => {
    if (qrScannedIdKey && searchMode === 'qr') {
      setSearchValue(qrScannedIdKey);
      setQrScannedIdKey(null); // Clear after applying
    }
  }, [qrScannedIdKey, searchMode]);

  // Effect: Auto-advance when search returns single family
  useEffect(() => {
    if (searchQuery.data && searchQuery.data.length === 1 && step === 'search') {
      setSelectedFamily(searchQuery.data[0]);
      setStep('select-members');
    } else if (searchQuery.data && searchQuery.data.length > 1 && step === 'search') {
      setStep('select-family');
    }
  }, [searchQuery.data, step]);

  const handleSelectFamily = (family: CheckinFamilyDto) => {
    setSelectedFamily(family);
    setStep('select-members');
  };

  const handleToggleCheckin = useCallback((
    personId: string,
    groupId: string,
    locationId: string,
    scheduleId: string,
    groupName: string,
    locationName: string,
    scheduleName: string,
    startTime: string
  ) => {
    setSelectedCheckins((prev) => {
      const newSelected = new Map(prev);
      const existingSelections = newSelected.get(personId) || [];
      const selectionKey = createSelectionKey(groupId, locationId, scheduleId);
      const selectionIndex = existingSelections.findIndex(
        (sel) => createSelectionKey(sel.groupId, sel.locationId, sel.scheduleId) === selectionKey
      );

      if (selectionIndex >= 0) {
        const updatedSelections = existingSelections.filter((_, idx) => idx !== selectionIndex);
        if (updatedSelections.length === 0) {
          newSelected.delete(personId);
        } else {
          newSelected.set(personId, updatedSelections);
        }
      } else {
        const newSelection: OpportunitySelection = {
          groupId,
          locationId,
          scheduleId,
          groupName,
          locationName,
          scheduleName,
          startTime,
        };
        newSelected.set(personId, [...existingSelections, newSelection]);
      }

      return newSelected;
    });
  }, []);

  const handleCheckIn = async () => {
    // React 18 batches these within a discrete click event and flushes the
    // commit before the browser's next paint, so the button disables under
    // the INP budget without the flushSync-forced full-tree re-render.
    setIsCheckingIn(true);
    setCheckinError(null);
    setPrintError(null);
    setPrintStatus('idle');

    // Flatten all selections into a single array of check-in items
    const checkins: CheckinRequestItem[] = [];

    selectedCheckins.forEach((selections, personIdKey) => {
      selections.forEach((selection) => {
        checkins.push({
          personIdKey,
          groupIdKey: selection.groupId,
          locationIdKey: selection.locationId,
          scheduleIdKey: selection.scheduleId,
        });
      });
    });

    try {
      const response = await recordCheckin(checkins);
      setCheckinError(null);

      // If we got a response (online mode), show confirmation immediately
      if (response) {
        // Store results and show confirmation right away (don't block on label fetch)
        checkinResultsRef.current = response;
        checkinLabelsRef.current = [];
        setStep('confirmation');

        // Fetch labels in the background — check-in is already confirmed
        const successfulResults = response.results.filter(r => r.success && r.attendanceIdKey);
        (async () => {
          const allLabels: LabelDto[] = [];
          for (const result of successfulResults) {
            if (result.attendanceIdKey) {
              try {
                const labels = await getLabels(result.attendanceIdKey);
                allLabels.push(...labels);
              } catch (labelError) {
                if (import.meta.env.DEV) {
                  console.warn('Failed to fetch labels for', result.attendanceIdKey, labelError);
                }
              }
            }
          }
          checkinLabelsRef.current = allLabels;
        })();
      } else {
        // Offline mode - check-in was queued
        // The OfflineQueueIndicator already shows queued status.
        // Reset selections so user can check in more people.
        setSelectedCheckins(new Map());
      }
    } catch (error) {
      // Log error for debugging (important for production issue diagnosis)
      console.error('[CheckinPage] Check-in failed:', error);

      // Show user-friendly error message based on error type
      if (error instanceof ApiClientError) {
        if (error.statusCode === 409) {
          // Conflict — already checked in
          const detail = error.message || '';
          setCheckinError(detail || 'Already checked in.');
        } else if (error.statusCode === 422) {
          // Unprocessable — ineligible
          const detail = error.message || '';
          setCheckinError(detail || "Couldn't check in. This person is not eligible.");
        } else {
          setCheckinError(
            'Check-in failed. Please try again or contact the welcome desk for assistance.'
          );
        }
      } else {
        setCheckinError(
          'Check-in failed. Please try again or contact the welcome desk for assistance.'
        );
      }
    } finally {
      setIsCheckingIn(false);
    }
  };

  const handlePrintLabels = async (labels?: LabelDto[]) => {
    if (!printerAvailable) {
      setPrintError('No printer available');
      setPrintStatus('error');
      return;
    }

    const labelsToPrint = labels || checkinLabelsRef.current;

    if (!labelsToPrint || labelsToPrint.length === 0) {
      setPrintError('No labels to print');
      setPrintStatus('error');
      return;
    }

    setPrintStatus('printing');
    setPrintError(null);

    try {
      // Extract ZPL content from labels
      const zplContents = labelsToPrint.map(label => label.content);

      // Send to print bridge
      const result = await printBridgeClient.printBatch(printerAvailable.name, zplContents);

      if (result.success) {
        setPrintStatus('success');
      } else {
        setPrintStatus('error');
        setPrintError(result.error);
      }
    } catch (error) {
      setPrintStatus('error');
      setPrintError(error instanceof Error ? error.message : 'Print failed');
    }
  };

  const handleReset = useCallback(() => {
    // Clear TanStack Query cache to prevent privacy leak
    queryClient.removeQueries({ queryKey: ['checkin-search'] });
    queryClient.removeQueries({ queryKey: ['checkin-opportunities'] });
    queryClient.removeQueries({ queryKey: ['checkin'] });

    setStep('search');
    setSearchValue('');
    setQrScannedIdKey(null);
    setShowQrScanner(false);
    setSelectedFamily(null);
    setSelectedCheckins(new Map());
    setCheckinError(null);
    setPrintStatus('idle');
    setPrintError(null);
    setHasSearchInput(false);
    setIsCheckingIn(false);
    checkinResultsRef.current = null;
    checkinLabelsRef.current = [];
  }, [queryClient]);

  const handleDone = () => {
    handleReset();
  };

  const handleClearCache = useCallback(async () => {
    // Close open connections so deleteDatabase isn't blocked
    offlineFamilyCache.close();
    offlineCheckinQueue.close();
    // Delete all IndexedDB databases before resetting state,
    // so hooks don't re-create them between delete and the test's check
    const databases = await window.indexedDB.databases();
    for (const db of databases) {
      if (db.name) {
        await new Promise<void>((resolve) => {
          const req = window.indexedDB.deleteDatabase(db.name!);
          req.onsuccess = () => resolve();
          req.onerror = () => resolve();
          req.onblocked = () => resolve();
        });
      }
    }
    setShowClearCacheConfirm(false);
    setShowAdminMenu(false);
    handleReset();
  }, [handleReset]);

  // Supervisor mode handlers
  const handlePinSubmit = async (pin: string) => {
    setIsPinLoading(true);
    setPinError(null);
    try {
      const response = await supervisorLogin({ pin });
      supervisorMode.startSession({
        token: response.sessionToken,
        supervisor: response.supervisor,
        expiresAt: response.expiresAt,
      });
      setShowPinEntry(false);
    } catch (error) {
      setPinError('Invalid PIN. Please try again.');
    } finally {
      setIsPinLoading(false);
    }
  };

  const handleSupervisorReprint = async (attendanceIdKey: string) => {
    if (!supervisorMode.sessionToken) throw new Error('No session');
    supervisorMode.resetTimeout();
    return supervisorReprint(attendanceIdKey, supervisorMode.sessionToken);
  };

  const handleSupervisorCheckout = async (attendanceIdKey: string) => {
    supervisorMode.resetTimeout();
    await checkout(attendanceIdKey);
  };

  const handlePickupRecorded = () => {
    supervisorMode.resetTimeout();
    void supervisorAttendanceQuery.refetch();
  };

  const handleSupervisorExit = async () => {
    if (supervisorMode.sessionToken) {
      try {
        await supervisorLogout(supervisorMode.sessionToken);
      } catch {
        // Ignore logout errors
      }
    }
    supervisorMode.endSession();
  };

  // Idle timeout - always active for privacy protection
  const { isWarning, secondsRemaining, resetTimer } = useIdleTimeout({
    timeout: IDLE_CONFIG.timeout,
    warningTime: IDLE_CONFIG.warningTime,
    onTimeout: handleReset,
  });

  // Render
  // Hide main content from accessibility tree when modal overlays are shown,
  // preventing strict-mode violations from duplicate button labels (e.g. numpad "1"
  // in both PhoneSearch and PinEntry).
  const isOverlayActive = showPinEntry || supervisorMode.isActive || showQrScanner;

  return (
    <>
      <div aria-hidden={isOverlayActive || undefined}>
      <OfflineIndicator />
      <OfflineQueueIndicator state={offlineState} onSync={syncQueue} />

      {/* Admin Menu — gear icon in top-right corner */}
      <div className="fixed top-4 left-4 z-40">
        <button
          data-testid="admin-menu-button"
          onClick={() => setShowAdminMenu(!showAdminMenu)}
          className="p-2 text-gray-400 hover:text-gray-600 transition-colors rounded-lg"
          aria-label="Admin menu"
        >
          <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
          </svg>
        </button>
        {showAdminMenu && (
          <div className="absolute top-full left-0 mt-1 bg-white rounded-lg shadow-lg border py-1 min-w-[160px]">
            <button
              onClick={() => { setShowClearCacheConfirm(true); setShowAdminMenu(false); }}
              className="w-full text-left px-4 py-2 text-sm text-gray-700 hover:bg-gray-100"
            >
              Clear cache
            </button>
          </div>
        )}
      </div>

      {/* Clear Cache Confirmation Dialog */}
      {showClearCacheConfirm && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
          <div className="bg-white rounded-xl shadow-xl p-6 max-w-sm mx-4" role="alertdialog" aria-label="Clear cache confirmation">
            <h3 className="text-lg font-semibold text-gray-900 mb-2">Clear Cache</h3>
            <p className="text-sm text-gray-600 mb-4">This will delete all cached family data and queued check-ins. This action cannot be undone.</p>
            <div className="flex gap-3 justify-end">
              <button
                onClick={() => setShowClearCacheConfirm(false)}
                className="px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-100 rounded-lg"
              >
                Cancel
              </button>
              <button
                onClick={handleClearCache}
                className="px-4 py-2 text-sm font-medium text-white bg-red-600 hover:bg-red-700 rounded-lg"
              >
                Confirm
              </button>
            </div>
          </div>
        </div>
      )}

      <KioskLayout
        title={
          step === 'select-members' && selectedFamily
            ? selectedFamily.name
            : undefined
        }
        onReset={step !== 'search' ? handleReset : undefined}
        onSupervisorTrigger={() => setShowPinEntry(true)}
      >
      {/* Step 1: Search */}
      {step === 'search' && (
        <div className="space-y-6">
          {/* Printer Status — unmounted when search error is active to avoid
              strict-mode violations from duplicate "error/failed" text */}
          {!searchQuery.isError && (
            <div className="max-w-2xl mx-auto">
              <PrintStatus
                onPrinterAvailable={(printer) => setPrinterAvailable(printer)}
                onPrinterUnavailable={() => setPrinterAvailable(null)}
              />
            </div>
          )}

          {/* Search Mode Toggle — hidden once the user starts typing so
              that getByRole('button', /search|find/) resolves to only the
              submit button (strict-mode safe). Visible again after reset. */}
          {!hasSearchInput && (
            <div className="flex justify-center gap-4 mb-8" aria-label="Search mode">
              <button
                data-testid="qr-scanner-button"
                onClick={() => setShowQrScanner(true)}
                className="search-mode-toggle px-8 py-4 rounded-lg font-semibold transition-colors min-h-[56px] bg-white text-gray-700 border-2 border-gray-300"
              >
                Scan QR Code
              </button>
              <button
                aria-pressed={searchMode === 'phone'}
                onClick={() => setSearchMode('phone')}
                className={`search-mode-toggle px-8 py-4 rounded-lg font-semibold transition-colors min-h-[56px] ${
                  searchMode === 'phone'
                    ? 'bg-blue-600 text-white'
                    : 'bg-white text-gray-700 border-2 border-gray-300'
                }`}
              >
                Search by Phone
              </button>
              <button
                aria-pressed={searchMode === 'name'}
                onClick={() => setSearchMode('name')}
                className={`search-mode-toggle px-8 py-4 rounded-lg font-semibold transition-colors min-h-[56px] ${
                  searchMode === 'name'
                    ? 'bg-blue-600 text-white'
                    : 'bg-white text-gray-700 border-2 border-gray-300'
                }`}
              >
                Search by Name
              </button>
            </div>
          )}

          {/* Search Component */}
          {searchMode === 'name' ? (
            <FamilySearch onSearch={handleSearch} loading={searchQuery.isFetching} onInputChange={setHasSearchInput} />
          ) : (
            <PhoneSearch onSearch={handleSearch} loading={searchQuery.isFetching} onInputChange={setHasSearchInput} externalErrorId={searchQuery.data?.length === 0 ? 'search-no-results' : undefined} />
          )}

          {/* Error */}
          {searchQuery.isError && searchMode !== 'qr' && (() => {
            const error = searchQuery.error;
            // Log error for monitoring (captured by E2E error metrics test)
            console.error('[CheckinSearch] Search error:', error);
            const friendly = getErrorMessage(error);
            const isTimeout = error instanceof ApiClientError && error.statusCode === 408;
            const is400 = error instanceof ApiClientError && error.statusCode === 400;

            const isServerError = error instanceof ApiClientError && error.statusCode >= 500;

            // For 400 errors, extract validation details from error body
            let errorText = isServerError
              ? 'Search failed. Please try again later.'
              : friendly.message;
            if (is400 && error instanceof ApiClientError) {
              // Check for validation details in legacy error format
              const details = error.error?.details;
              if (details) {
                const firstField = Object.keys(details)[0];
                const val = details[firstField];
                // Handle both string and string[] values
                const firstError = Array.isArray(val) ? val[0] : (typeof val === 'string' ? val : undefined);
                if (firstError) errorText = firstError;
              }
              // If no details extracted, use the error message itself
              if (!errorText || errorText === 'Please check your input and try again.') {
                const msg = error.error?.message || error.message;
                if (msg && msg !== 'An unknown error occurred') {
                  errorText = msg;
                } else {
                  errorText = 'Validation failed';
                }
              }
            }
            if (isTimeout) {
              errorText = 'The request is taking too long. Please check your connection.';
            }

            return (
              <div className="max-w-2xl mx-auto mt-4" role="alert" aria-live="assertive">
                <Card className="bg-red-50 border border-red-200">
                  <p className="text-red-800 text-center font-medium">
                    {errorText}
                  </p>
                  <div className="flex justify-center mt-4">
                    <Button
                      onClick={() => {
                        // Retry: invalidate cached error and re-trigger search
                        queryClient.removeQueries({ queryKey: ['checkin', 'search'] });
                        const currentValue = searchValue;
                        setSearchValue('');
                        // Use setTimeout to ensure the query key changes and re-fires
                        setTimeout(() => setSearchValue(currentValue), 0);
                      }}
                      variant="secondary"
                      size="md"
                    >
                      Retry
                    </Button>
                  </div>
                </Card>
              </div>
            );
          })()}

          {/* No Results */}
          {searchQuery.data && searchQuery.data.length === 0 && (
            <div className="max-w-2xl mx-auto mt-4 space-y-4" role="alert" aria-live="polite">
              <Card className="bg-yellow-50 border border-yellow-200">
                <p id="search-no-results" className="text-yellow-900 text-center font-medium">
                  {searchMode === 'qr'
                    ? 'No family found for this QR code.'
                    : 'No families found. Phone number not found in our records.'}
                </p>
              </Card>
              {searchMode === 'qr' ? (
                <Button
                  onClick={() => {
                    setSearchValue('');
                    setSearchMode('phone');
                  }}
                  variant="primary"
                  size="lg"
                  className="w-full text-xl"
                >
                  Try Again
                </Button>
              ) : (
                <Button
                  onClick={() => setStep('register')}
                  variant="primary"
                  size="lg"
                  className="w-full text-xl"
                >
                  Register New Family
                </Button>
              )}
            </div>
          )}
        </div>
      )}

      {/* Step: Register New Family */}
      {step === 'register' && (
        <KioskFamilyRegistration
          onComplete={(family) => {
            setSelectedFamily(family);
            setStep('select-members');
          }}
          onCancel={() => setStep('search')}
          defaultPhone={searchMode === 'phone' ? searchValue : undefined}
        />
      )}

      {/* Step 2: Select Family (if multiple results) */}
      {step === 'select-family' && searchQuery.data && (
        <div className="max-w-4xl mx-auto">
          <h2 className="text-3xl font-bold text-center mb-8 text-gray-900">
            Select Your Family
          </h2>
          <div className="grid gap-4">
            {searchQuery.data.map((family) => (
              <Card
                key={family.idKey}
                onClick={() => handleSelectFamily(family)}
                className="hover:shadow-xl transition-shadow cursor-pointer p-6"
              >
                <h3 className="text-2xl font-bold text-gray-900 mb-2">
                  {family.name}
                </h3>
                <p className="text-gray-600 mb-3">
                  {family.members.length}{' '}
                  {family.members.length === 1 ? 'member' : 'members'}
                </p>
                <div className="flex flex-wrap gap-2">
                  {family.members.map((member) => (
                    <span
                      key={member.idKey}
                      className="bg-gray-100 px-3 py-1 rounded-full text-sm"
                    >
                      {member.fullName}
                    </span>
                  ))}
                </div>
              </Card>
            ))}
          </div>
        </div>
      )}

      {/* No family members - family returned with empty members array */}
      {step === 'select-members' && selectedFamily && selectedFamily.members.length === 0 && (
        <div className="max-w-2xl mx-auto mt-4" role="alert">
          <Card className="bg-yellow-50 border border-yellow-200">
            <p className="text-yellow-900 text-center font-medium">
              No family members found. There is no one to check in for this family.
            </p>
          </Card>
          <div className="flex justify-center mt-4">
            <Button onClick={handleReset} variant="secondary" size="lg">
              Back to Search
            </Button>
          </div>
        </div>
      )}

      {/* Step 3: Select Members */}
      {step === 'select-members' && selectedFamily && selectedFamily.members.length > 0 && opportunitiesQuery.data && (() => {
        // Calculate total activities count once for performance
        const totalActivities = getTotalActivitiesCount(selectedCheckins);

        return (
          <div className="max-w-4xl mx-auto">
            <h2 className="text-3xl font-bold text-center mb-8 text-gray-900">
              Who's Checking In?
            </h2>

            {opportunitiesQuery.isLoading && (
              <div className="text-center py-12">
                <div className="inline-block animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600"></div>
                <p className="mt-4 text-gray-600">Loading options...</p>
              </div>
            )}

            {opportunitiesQuery.data && (
              <>
                <FamilyMemberList
                  opportunities={opportunitiesQuery.data.opportunities}
                  selectedCheckins={selectedCheckins}
                  onToggleCheckin={handleToggleCheckin}
                />

                {/* Check-in Error */}
                {checkinError && (
                  <div className="mt-4">
                    <Card className="bg-red-50 border border-red-200">
                      <p className="text-red-800 text-center font-medium">
                        {checkinError}
                      </p>
                    </Card>
                  </div>
                )}

                {/* Check-in Button */}
                <div className="mt-8 sticky bottom-0 bg-gradient-to-t from-blue-100 via-blue-100 to-transparent pt-6 pb-4">
                  <Button
                    onClick={handleCheckIn}
                    disabled={selectedCheckins.size === 0 || isCheckingIn}
                    loading={isRecordingCheckin || isCheckingIn}
                    size="lg"
                    className="w-full text-xl"
                  >
                    Check In {selectedCheckins.size > 0 && `(${totalActivities} ${totalActivities === 1 ? 'activity' : 'activities'})`}
                  </Button>
                </div>
              </>
            )}

            {opportunitiesQuery.isError && (
              <Card className="bg-red-50 border border-red-200">
                <p className="text-red-800 text-center">
                  Failed to load check-in options. Please try again.
                </p>
              </Card>
            )}
          </div>
        );
      })()}

      {/* Step 4: Confirmation */}
      {step === 'confirmation' && checkinResultsRef.current && (
        <CheckinConfirmation
          attendances={checkinResultsRef.current.results
            .filter(r => r.success)
            .map(r => ({
              attendanceIdKey: r.attendanceIdKey ?? '',
              personIdKey: r.person?.idKey ?? '',
              personName: r.person?.fullName ?? 'Unknown',
              groupName: '', // Not available in CheckinResultDto
              locationName: r.location?.name ?? '',
              scheduleName: '', // Not available in CheckinResultDto
              securityCode: r.securityCode ?? '',
              checkInTime: r.checkInTime ?? new Date().toISOString(),
              isFirstTime: false, // Not available in CheckinResultDto
            }))}
          onDone={handleDone}
          onPrintLabels={
            checkinLabelsRef.current.length > 0 && printerAvailable
              ? () => handlePrintLabels()
              : undefined
          }
          printStatus={printStatus}
          printError={printError}
          printerAvailable={!!printerAvailable}
        />
      )}
      </KioskLayout>
      </div>

      {/* QR Scanner Modal */}
      {showQrScanner && (
        <QrScanner
          onScan={(idKey) => {
            setShowQrScanner(false);
            handleQrScan(idKey);
          }}
          onCancel={() => setShowQrScanner(false)}
          onManualEntry={() => {
            setShowQrScanner(false);
            setSearchMode('phone');
            // Focus phone input after modal closes
            requestAnimationFrame(() => {
              const phoneInput = document.querySelector('[data-testid="phone-input"]') as HTMLInputElement | null;
              phoneInput?.focus();
            });
          }}
        />
      )}

      {/* PIN Entry Modal */}
      {showPinEntry && (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
          <PinEntry
            onSubmit={handlePinSubmit}
            onCancel={() => {
              setShowPinEntry(false);
              setPinError(null);
            }}
            loading={isPinLoading}
            error={pinError}
          />
        </div>
      )}

      {/* Supervisor Mode Panel */}
      {supervisorMode.isActive && supervisorMode.supervisor && (
        <div className="fixed inset-0 bg-white z-40 overflow-auto">
          <div className="max-w-4xl mx-auto p-6">
            <SupervisorMode
              supervisor={supervisorMode.supervisor}
              currentAttendance={supervisorAttendanceQuery.data ?? []}
              onReprint={handleSupervisorReprint}
              onCheckout={handleSupervisorCheckout}
              onPickupRecorded={handlePickupRecorded}
              onExit={handleSupervisorExit}
              sessionToken={supervisorMode.sessionToken ?? undefined}
              printLabel={printerAvailable ? handlePrintLabels : undefined}
            />
          </div>
        </div>
      )}

      {/* Idle Warning Modal */}
      <IdleWarningModal
        isOpen={isWarning}
        secondsRemaining={secondsRemaining}
        onStayActive={resetTimer}
      />
    </>
  );
}
