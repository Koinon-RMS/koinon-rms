/**
 * Kiosk pickup-verification fixtures (#710)
 *
 * The kiosk verifies a pickup person BY NAME ONLY: PickupVerification posts
 * { attendanceIdKey, pickupPersonName, securityCode } and the backend matches
 * `AuthorizedPickup.Name == pickupPersonName` when no person id is sent
 * (AuthorizedPickupService.FindAuthorizedPickupAsync). The seeder's shared
 * Jane -> Johnny authorization (tools/Koinon.TestDataSeeder,
 * SeedAuthorizedPickupAsync) is PERSON-based with a NULL name, so a name-only
 * verify can never match it — each journey needs its own NAME-BASED row.
 *
 * These fixtures create exactly that row and remove exactly that row. The
 * shared seeded data is read (child GUID resolution only), never modified and
 * never deleted. Rows are scoped by child_person_id + name +
 * authorized_person_id IS NULL, so a stale row from a previously crashed run
 * is removed before the insert and person-based seeded rows can never match
 * these deletes.
 *
 * Authorization levels are the numeric contract (Always = 0, EmergencyOnly = 1,
 * Never = 2 — mirrors src/web/src/features/authorized-pickup/api.ts and
 * Koinon.Domain/Enums). Duplicated here rather than imported so the e2e
 * fixtures never pull the app's API client into Playwright's transpiler.
 */

import { randomUUID } from 'node:crypto';
import pg from 'pg';

/** Numeric AuthorizationLevel contract — mirrors src/web/src/features/authorized-pickup/api.ts. */
export const AUTHORIZATION_LEVEL = {
  Always: 0,
  EmergencyOnly: 1,
  Never: 2,
} as const;

/** Seeded child under test: Johnny Smith (tools/Koinon.TestDataSeeder). */
const JOHNNY_SMITH_GUID = '55555555-5555-5555-5555-555555555555';

/** The fixture-owned, name-based pickup persons the kiosk journeys verify with. */
export const AUTHORIZED_PICKUP_NAME = 'Kiosk Fixture Jane';
export const NEVER_PICKUP_NAME = 'Kiosk Fixture Nancy';

const ALL_FIXTURE_NAMES = [AUTHORIZED_PICKUP_NAME, NEVER_PICKUP_NAME];

// Same connection contract as e2e/global-setup.ts.
const DB = {
  host: process.env.PGHOST || 'localhost',
  port: Number(process.env.PGPORT || 5432),
  database: process.env.PGDATABASE || 'koinon',
  user: process.env.PGUSER || 'koinon',
  password: process.env.PGPASSWORD || 'koinon',
};

async function withClient<T>(run: (client: pg.Client) => Promise<T>): Promise<T> {
  const client = new pg.Client(DB);
  await client.connect();
  try {
    return await run(client);
  } finally {
    await client.end().catch(() => {});
  }
}

async function resolveSeededChildId(client: pg.Client): Promise<number> {
  const result = await client.query('SELECT id FROM person WHERE guid = $1::uuid', [
    JOHNNY_SMITH_GUID,
  ]);
  if (result.rowCount !== 1) {
    throw new Error(
      `Seeded child Johnny Smith (${JOHNNY_SMITH_GUID}) not found — run the test data seeder first.`
    );
  }
  return result.rows[0].id as number;
}

/**
 * Creates one name-based authorized-pickup row for the seeded child. Any
 * leftover row with the same name from a previously crashed run is removed
 * first (fixture-owned rows only — the seeder's person-based rows carry a
 * NULL name and can never match these deletes).
 */
export async function seedNameBasedPickupAuthorization(options: {
  name: string;
  authorizationLevel: number;
  /** PickupRelationship as a number (Parent = 0, Friend = 6). */
  relationship?: number;
}): Promise<void> {
  const { name, authorizationLevel, relationship = 0 } = options;
  await withClient(async (client) => {
    const childId = await resolveSeededChildId(client);
    await client.query(
      `DELETE FROM authorized_pickup
       WHERE child_person_id = $1 AND name = $2 AND authorized_person_id IS NULL`,
      [childId, name]
    );
    await client.query(
      `INSERT INTO authorized_pickup
         (guid, created_date_time, child_person_id, name, relationship, authorization_level, is_active)
       VALUES ($1::uuid, now(), $2, $3, $4, $5, true)`,
      [randomUUID(), childId, name, relationship, authorizationLevel]
    );
  });
}

/** Removes the fixture-owned name-based rows; idempotent and safe after failures. */
export async function removeNameBasedPickupAuthorizations(): Promise<void> {
  await withClient(async (client) => {
    const childId = await resolveSeededChildId(client);
    await client.query(
      `DELETE FROM authorized_pickup
       WHERE child_person_id = $1 AND name = ANY($2) AND authorized_person_id IS NULL`,
      [childId, ALL_FIXTURE_NAMES]
    );
  });
}
