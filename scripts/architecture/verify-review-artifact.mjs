#!/usr/bin/env node
// Verify committed architecture-review artifacts without calling any private model.
// CI's job is tamper-evidence: prove the local architect review happened for the
// exact PR diff, was signed by a trusted agent key, cited real standards, and
// produced an approving structured ruling.

import { createHash, createPublicKey, verify as verifySignature } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve, relative } from 'node:path';
import { pathToFileURL } from 'node:url';

const DEFAULT_REVIEWS_DIR = 'docs/architecture/reviews';
const DEFAULT_SIGNERS_FILE = 'docs/architecture/signers/trusted-agent-signers.json';
// #738: trust anchor must be an explicit ref (default origin/dev), NEVER the
// PR checkout — otherwise a PR can add its own key to trusted-agent-signers.json
// and self-satisfy the gate. Overridable for local development only via
// ARTIFACT_TRUST_REF; CI never sets it.
const TRUST_REF = process.env.ARTIFACT_TRUST_REF || 'origin/dev';
const REVIEW_EXCLUDE = ':(exclude)docs/architecture/reviews/**';
const CODE_FILE = /\.(cs|csproj|props|targets|ts|tsx|js|jsx|mjs|cjs|py|ps1|psm1|sh|sql|ipynb)$|(^|[\\/])(package\.json|tsconfig[^\\/]*\.json|appsettings[^\\/]*\.json|docker-compose[^\\/]*\.ya?ml)$/i;
const PROTECTED_FILE = /^(\.github\/workflows\/|\.claude\/|\.husky\/|scripts\/hooks\/|scripts\/architecture\/|docs\/adr\/|docs\/reference\/|tools\/graph\/|tools\/mcp-koinon-dev\/)/i;
const HASH_RE = /^[a-f0-9]{64}$/i;

export function sha256Hex(data) {
  return createHash('sha256').update(data).digest('hex');
}

export function canonicalJson(value) {
  return JSON.stringify(sortForCanonical(value));
}

function sortForCanonical(value) {
  if (Array.isArray(value)) return value.map(sortForCanonical);
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value)
        .filter(([key]) => key !== 'agentSignature')
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([key, child]) => [key, sortForCanonical(child)]),
    );
  }
  return value;
}

export function publicKeyFingerprint(publicKeyPem) {
  const der = createPublicKey(publicKeyPem).export({ type: 'spki', format: 'der' });
  return sha256Hex(der);
}

function git(root, args, opts = {}) {
  return execFileSync('git', args, { cwd: root, encoding: opts.encoding ?? 'utf8', maxBuffer: 64 * 1024 * 1024 });
}

function gitBuffer(root, args) {
  return execFileSync('git', args, { cwd: root, maxBuffer: 128 * 1024 * 1024 });
}

function repoRoot(cwd = process.cwd()) {
  return git(cwd, ['rev-parse', '--show-toplevel']).trim();
}

function listReviewArtifacts(root, reviewsDir = DEFAULT_REVIEWS_DIR) {
  const dir = join(root, reviewsDir);
  if (!existsSync(dir)) return [];
  const out = [];
  const walk = (abs) => {
    for (const name of readdirSync(abs)) {
      const p = join(abs, name);
      const st = statSync(p);
      if (st.isDirectory()) walk(p);
      else if (name.endsWith('.json')) out.push(p);
    }
  };
  walk(dir);
  return out;
}

function parseNameStatus(root, base, head) {
  const raw = gitBuffer(root, ['diff', '--name-status', '-z', `${base}...${head}`, '--', '.', REVIEW_EXCLUDE]);
  const parts = raw.toString('utf8').split('\0').filter(Boolean);
  const files = [];
  for (let i = 0; i < parts.length;) {
    const status = parts[i++];
    if (status.startsWith('R') || status.startsWith('C')) {
      const oldPath = parts[i++];
      const newPath = parts[i++];
      files.push({ status: status[0], path: newPath, oldPath });
    } else {
      const path = parts[i++];
      files.push({ status: status[0], path });
    }
  }
  return files.map((f) => ({ ...f, path: f.path.replaceAll('\\', '/'), oldPath: f.oldPath?.replaceAll('\\', '/') }));
}

function changedFileHash(root, rel, status) {
  if (status === 'D') return null;
  const abs = join(root, rel);
  if (!existsSync(abs)) return null;
  return sha256Hex(readFileSync(abs));
}

function computeDiffSha(root, base, head) {
  return sha256Hex(gitBuffer(root, ['diff', '--binary', '--full-index', `${base}...${head}`, '--', '.', REVIEW_EXCLUDE]));
}

function loadTrustedSigners(root, signersFile = DEFAULT_SIGNERS_FILE, trustRef = TRUST_REF) {
  // Read the signers file from the trust ref (origin/dev), not the working
  // tree. If the ref is unavailable (e.g. bare local dev), fail closed.
  let content;
  try {
    content = gitBuffer(root, ['show', `${trustRef}:${signersFile}`]).toString('utf8');
  } catch (e) {
    return { errors: [`Trusted signers unavailable from trust ref ${trustRef}: ${e.message}`], signers: new Map() };
  }
  let parsed;
  try {
    parsed = JSON.parse(content);
  } catch (e) {
    return { errors: [`Trusted signers file at ${trustRef} is invalid JSON: ${e.message}`], signers: new Map() };
  }
  const errors = [];
  const signers = new Map();
  if (parsed.schemaVersion !== 1) errors.push('Trusted signers schemaVersion must be 1.');
  if (!Array.isArray(parsed.signers) || parsed.signers.length === 0) errors.push('Trusted signers must include at least one signer.');
  for (const signer of parsed.signers ?? []) {
    if (!signer.keyId || !signer.publicKeyPem) {
      errors.push('Every trusted signer needs keyId and publicKeyPem.');
      continue;
    }
    if (signer.algorithm !== 'ed25519') errors.push(`${signer.keyId}: only ed25519 is supported.`);
    try {
      const fp = publicKeyFingerprint(signer.publicKeyPem);
      signers.set(signer.keyId, { ...signer, publicKeySha256: fp });
    } catch (e) {
      errors.push(`${signer.keyId}: publicKeyPem is not a valid public key (${e.message}).`);
    }
  }
  return { errors, signers };
}

function standardPathFromRef(ref) {
  return String(ref).split(/[§#]/)[0].trim();
}

function artifactPathForDisplay(root, abs) {
  return relative(root, abs).replaceAll('\\', '/');
}

function validateShape(artifact, artifactFile, errors) {
  const prefix = artifactPathForDisplay(process.cwd(), artifactFile);
  const req = (cond, msg) => { if (!cond) errors.push(`${prefix}: ${msg}`); };
  req(artifact && typeof artifact === 'object', 'artifact must be a JSON object.');
  if (!artifact || typeof artifact !== 'object') return;
  req(artifact.schemaVersion === 1, 'schemaVersion must be 1.');
  req(artifact.reviewType === 'architect-code-review', 'reviewType must be architect-code-review.');
  req(typeof artifact.repository === 'string' && artifact.repository.length > 0, 'repository is required.');
  req(typeof artifact.baseCommit === 'string' && artifact.baseCommit.length >= 7, 'baseCommit is required.');
  req(typeof artifact.headCommit === 'string' && artifact.headCommit.length >= 7, 'headCommit is required.');
  req(!Number.isNaN(Date.parse(artifact.createdAt)), 'createdAt must be an ISO timestamp.');
  req(artifact.change && typeof artifact.change === 'object', 'change object is required.');
  req(HASH_RE.test(artifact.change?.diffSha256 ?? ''), 'change.diffSha256 must be a sha256 hex digest.');
  req(Array.isArray(artifact.change?.files), 'change.files must be an array.');
  req(artifact.architectRuling && typeof artifact.architectRuling === 'object', 'architectRuling object is required.');
  req(['APPROVED', 'APPROVED_WITH_CONDITIONS', 'REJECTED'].includes(artifact.architectRuling?.ruling), 'architectRuling.ruling is invalid.');
  req(Array.isArray(artifact.architectRuling?.reasons), 'architectRuling.reasons must be an array.');
  req(Array.isArray(artifact.architectRuling?.conditions), 'architectRuling.conditions must be an array.');
  req(artifact.agentSignature && typeof artifact.agentSignature === 'object', 'agentSignature object is required.');
  req(artifact.agentSignature?.algorithm === 'ed25519', 'agentSignature.algorithm must be ed25519.');
  req(typeof artifact.agentSignature?.keyId === 'string' && artifact.agentSignature.keyId.length > 0, 'agentSignature.keyId is required.');
  req(HASH_RE.test(artifact.agentSignature?.publicKeySha256 ?? ''), 'agentSignature.publicKeySha256 must be a sha256 hex digest.');
  req(HASH_RE.test(artifact.agentSignature?.signedPayloadSha256 ?? ''), 'agentSignature.signedPayloadSha256 must be a sha256 hex digest.');
  req(typeof artifact.agentSignature?.signature === 'string' && artifact.agentSignature.signature.length > 0, 'agentSignature.signature is required.');
}

function verifyArtifactSignature(artifact, trustedSigners, errors, displayPath) {
  const sig = artifact.agentSignature;
  if (!sig) return;
  const signer = trustedSigners.get(sig.keyId);
  if (!signer) {
    errors.push(`${displayPath}: signature keyId is not trusted: ${sig.keyId}.`);
    return;
  }
  if (sig.publicKeySha256 !== signer.publicKeySha256) {
    errors.push(`${displayPath}: signature publicKeySha256 does not match trusted signer ${sig.keyId}.`);
    return;
  }
  const payload = canonicalJson(artifact);
  const payloadSha = sha256Hex(Buffer.from(payload));
  if (sig.signedPayloadSha256 !== payloadSha) {
    errors.push(`${displayPath}: signedPayloadSha256 does not match artifact payload.`);
    return;
  }
  let ok = false;
  try {
    ok = verifySignature(null, Buffer.from(payload), createPublicKey(signer.publicKeyPem), Buffer.from(sig.signature, 'base64'));
  } catch (e) {
    errors.push(`${displayPath}: signature verification errored (${e.message}).`);
    return;
  }
  if (!ok) errors.push(`${displayPath}: signature verification failed.`);
}

function validateArtifactAgainstDiff({ root, artifact, artifactFile, base, head, changedFiles, gatedFiles, diffSha, trustedSigners, maxAgeDays }) {
  const errors = [];
  const warnings = [];
  const displayPath = artifactPathForDisplay(root, artifactFile);
  validateShape(artifact, artifactFile, errors);
  if (errors.length) return { errors, warnings, coversGated: false };

  verifyArtifactSignature(artifact, trustedSigners, errors, displayPath);

  if (artifact.baseCommit !== base) errors.push(`${displayPath}: baseCommit does not match CI base (${artifact.baseCommit} != ${base}).`);
  if (artifact.headCommit !== head) {
    let equivalentReviewedDiff = false;
    try {
      execFileSync('git', ['merge-base', '--is-ancestor', artifact.headCommit, head], { cwd: root });
      equivalentReviewedDiff = computeDiffSha(root, base, artifact.headCommit) === diffSha;
    } catch { /* not an ancestor or invalid commit */ }
    if (!equivalentReviewedDiff) {
      errors.push(`${displayPath}: headCommit is neither the CI head nor an ancestor with the same non-review diff (${artifact.headCommit} != ${head}).`);
    }
  }
  if (artifact.change.diffSha256 !== diffSha) errors.push(`${displayPath}: change.diffSha256 does not match the current PR diff.`);

  const ageMs = Date.now() - Date.parse(artifact.createdAt);
  if (ageMs < 0) errors.push(`${displayPath}: createdAt is in the future.`);
  if (ageMs > maxAgeDays * 24 * 60 * 60 * 1000) errors.push(`${displayPath}: createdAt is older than ${maxAgeDays} day(s).`);

  if (artifact.architectRuling.ruling === 'REJECTED') errors.push(`${displayPath}: architect ruling is REJECTED.`);
  if (artifact.architectRuling.ruling === 'APPROVED_WITH_CONDITIONS' && artifact.architectRuling.conditions.length > 0) {
    errors.push(`${displayPath}: APPROVED_WITH_CONDITIONS is not mergeable until conditions are resolved in a new APPROVED artifact.`);
  }
  if (artifact.architectRuling.reasons.length === 0) errors.push(`${displayPath}: approving ruling must include cited reasons.`);

  const changedByPath = new Map(changedFiles.map((f) => [f.path, f]));
  const artifactFiles = new Map();
  for (const f of artifact.change.files) {
    if (!f?.path || typeof f.path !== 'string') {
      errors.push(`${displayPath}: every change.files entry needs path.`);
      continue;
    }
    const normalized = f.path.replaceAll('\\', '/');
    artifactFiles.set(normalized, f);
    const changed = changedByPath.get(normalized);
    if (!changed) {
      errors.push(`${displayPath}: artifact lists file not present in current PR diff: ${normalized}.`);
      continue;
    }
    const expectedSha = changedFileHash(root, normalized, changed.status);
    if ((f.sha256 ?? null) !== expectedSha) {
      errors.push(`${displayPath}: sha256 mismatch for ${normalized}.`);
    }
  }

  for (const f of gatedFiles) {
    if (!artifactFiles.has(f.path)) errors.push(`${displayPath}: changed code/protected file is not covered: ${f.path}.`);
  }

  const standards = artifact.impact?.semantic?.standardsRetrieved ?? [];
  if (!Array.isArray(standards) || standards.length === 0) {
    errors.push(`${displayPath}: impact.semantic.standardsRetrieved must contain at least one cited standard.`);
  } else {
    for (const s of standards) {
      if (!s.path || !existsSync(join(root, s.path))) errors.push(`${displayPath}: cited standard path does not exist: ${s.path ?? '<missing>'}.`);
    }
  }

  for (const reason of artifact.architectRuling.reasons) {
    const p = standardPathFromRef(reason.standard ?? '');
    if (!p || !existsSync(join(root, p))) errors.push(`${displayPath}: ruling reason cites missing standard: ${reason.standard ?? '<missing>'}.`);
    if (!reason.finding) errors.push(`${displayPath}: ruling reason for ${reason.standard ?? '<missing>'} has no finding.`);
  }

  return { errors, warnings, coversGated: gatedFiles.every((f) => artifactFiles.has(f.path)) };
}

function isGatedFile(path) {
  return CODE_FILE.test(path) || PROTECTED_FILE.test(path);
}

export function verifyReviewArtifacts(options = {}) {
  const root = resolve(options.root ?? repoRoot());
  const base = options.base ?? git(root, ['merge-base', 'HEAD', `origin/${process.env.GITHUB_BASE_REF || 'dev'}`]).trim();
  const head = options.head ?? git(root, ['rev-parse', 'HEAD']).trim();
  const reviewsDir = options.reviewsDir ?? DEFAULT_REVIEWS_DIR;
  const signersFile = options.signersFile ?? DEFAULT_SIGNERS_FILE;
  const maxAgeDays = Number(options.maxAgeDays ?? 14);
  const changedFiles = parseNameStatus(root, base, head);
  const gatedFiles = changedFiles.filter((f) => isGatedFile(f.path));
  const warnings = [];
  const errors = [];

  if (gatedFiles.length === 0) {
    return { passed: true, skipped: true, errors, warnings, changedFiles, gatedFiles, artifacts: [] };
  }

  const trust = loadTrustedSigners(root, signersFile);
  errors.push(...trust.errors);
  const diffSha = computeDiffSha(root, base, head);
  const artifactFiles = listReviewArtifacts(root, reviewsDir);
  if (artifactFiles.length === 0) errors.push(`No architecture review artifact found under ${reviewsDir}.`);

  const artifacts = [];
  let validCoveringArtifacts = 0;
  for (const artifactFile of artifactFiles) {
    let artifact;
    try {
      artifact = JSON.parse(readFileSync(artifactFile, 'utf8'));
    } catch (e) {
      errors.push(`${artifactPathForDisplay(root, artifactFile)}: invalid JSON (${e.message}).`);
      continue;
    }
    const result = validateArtifactAgainstDiff({ root, artifact, artifactFile, base, head, changedFiles, gatedFiles, diffSha, trustedSigners: trust.signers, maxAgeDays });
    artifacts.push({ path: artifactPathForDisplay(root, artifactFile), result });
    errors.push(...result.errors);
    warnings.push(...result.warnings);
    if (result.errors.length === 0 && result.coversGated) validCoveringArtifacts += 1;
  }

  if (validCoveringArtifacts === 0) errors.push('No valid signed architecture review artifact covers all changed code/protected files.');
  if (validCoveringArtifacts > 1) errors.push('Multiple valid architecture review artifacts cover this diff; keep exactly one current artifact.');

  return { passed: errors.length === 0, skipped: false, errors, warnings, changedFiles, gatedFiles, artifacts, diffSha };
}

function parseCli(argv) {
  const opts = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--base') opts.base = argv[++i];
    else if (a === '--head') opts.head = argv[++i];
    else if (a === '--reviews') opts.reviewsDir = argv[++i];
    else if (a === '--signers') opts.signersFile = argv[++i];
    else if (a === '--max-age-days') opts.maxAgeDays = Number(argv[++i]);
    else if (a === '--json') opts.json = true;
    else throw new Error(`Unknown argument: ${a}`);
  }
  return opts;
}

function main() {
  const opts = parseCli(process.argv.slice(2));
  const result = verifyReviewArtifacts(opts);
  if (opts.json) {
    console.log(JSON.stringify(result, null, 2));
  } else if (result.skipped) {
    console.log('✓ Architecture review artifact not required: no gated code/protected files changed.');
  } else if (result.passed) {
    console.log(`✓ Architecture review artifact verified (${result.gatedFiles.length} gated file(s), diff ${result.diffSha}).`);
    for (const a of result.artifacts) console.log(`  ${a.path}`);
  } else {
    console.error('✗ Architecture review artifact verification failed:');
    for (const e of result.errors) console.error(`  - ${e}`);
  }
  process.exit(result.passed ? 0 : 1);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { main(); } catch (e) { console.error(`✗ ${e.stack || e.message}`); process.exit(2); }
}
