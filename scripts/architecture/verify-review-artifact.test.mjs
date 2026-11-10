import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { generateKeyPairSync, createHash, sign } from 'node:crypto';
import {
  canonicalJson,
  sha256Hex,
  publicKeyFingerprint,
  verifyReviewArtifacts,
} from './verify-review-artifact.mjs';

function git(cwd, args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();
}

function makeRepo() {
  const root = mkdtempSync(join(tmpdir(), 'koinon-review-artifact-'));
  git(root, ['init']);
  git(root, ['checkout', '-b', 'dev']);
  git(root, ['config', 'user.email', 'test@example.invalid']);
  git(root, ['config', 'user.name', 'Test']);
  mkdirSync(join(root, 'src/Koinon.Domain/Entities'), { recursive: true });
  mkdirSync(join(root, 'docs/reference'), { recursive: true });
  mkdirSync(join(root, 'docs/architecture/reviews'), { recursive: true });
  mkdirSync(join(root, 'docs/architecture/signers'), { recursive: true });
  mkdirSync(join(root, 'tools/graph'), { recursive: true });
  writeFileSync(join(root, 'src/Koinon.Domain/Entities/Person.cs'), 'public class Person {}\n');
  writeFileSync(join(root, 'docs/reference/conventions.md'), '# Conventions\n\n## Layering\nKeep clean architecture.\n');
  writeFileSync(join(root, 'tools/graph/graph-baseline.json'), '{"version":"1"}\n');
  const { publicKey, privateKey } = generateKeyPairSync('ed25519');
  const publicPem = publicKey.export({ type: 'spki', format: 'pem' });
  const keyId = 'trusted-reviewer';
  const trust = { schemaVersion: 1, signers: [{ keyId, algorithm: 'ed25519', publicKeyPem: publicPem }] };
  writeFileSync(join(root, 'docs/architecture/signers/trusted-agent-signers.json'), JSON.stringify(trust, null, 2));
  git(root, ['add', '.']);
  git(root, ['commit', '-m', 'base']);
  const base = git(root, ['rev-parse', 'HEAD']);
  git(root, ['update-ref', 'refs/remotes/origin/dev', base]);
  writeFileSync(join(root, 'src/Koinon.Domain/Entities/Person.cs'), 'public class Person { public string IdKey { get; set; } = ""; }\n');
  git(root, ['add', '.']);
  git(root, ['commit', '-m', 'change']);
  const head = git(root, ['rev-parse', 'HEAD']);
  return { root, base, head, signer: { keyId, publicPem, privateKey } };
}

function diffHash(root, base, head) {
  const diff = execFileSync('git', ['diff', '--binary', '--full-index', `${base}...${head}`, '--', '.', ':(exclude)docs/architecture/reviews/**'], { cwd: root });
  return createHash('sha256').update(diff).digest('hex');
}

function writeSignedArtifact(root, base, head, signerKey, overrides = {}) {
  const { keyId, publicPem, privateKey } = signerKey;
  const changedPath = 'src/Koinon.Domain/Entities/Person.cs';
  const artifact = {
    schemaVersion: 1,
    reviewType: 'architect-code-review',
    repository: 'dev-brewery/koinon-rms',
    baseCommit: base,
    headCommit: head,
    createdAt: new Date().toISOString(),
    change: {
      diffSha256: diffHash(root, base, head),
      graphBaselineSha256: sha256Hex(Buffer.from('{"version":"1"}\n')),
      files: [{ path: changedPath, status: 'modified', sha256: sha256Hex(Buffer.from('public class Person { public string IdKey { get; set; } = ""; }\n')) }],
    },
    impact: {
      graph: { affectedFiles: [], layersAffected: ['Domain'], highImpact: false },
      semantic: { standardsRetrieved: [{ path: 'docs/reference/conventions.md', section: 'Layering', score: 0.91 }] },
    },
    architectRuling: {
      ruling: 'APPROVED',
      reasons: [{ standard: 'docs/reference/conventions.md#layering', finding: 'Maintains clean architecture boundary.' }],
      conditions: [],
    },
    verification: { commands: [{ command: 'npm run graph:validate', exitCode: 0 }] },
    ...overrides,
  };
  const payload = canonicalJson(artifact);
  const signature = sign(null, Buffer.from(payload), privateKey).toString('base64');
  artifact.agentSignature = {
    keyId,
    algorithm: 'ed25519',
    publicKeySha256: publicKeyFingerprint(publicPem),
    signedPayloadSha256: sha256Hex(Buffer.from(payload)),
    signature,
  };
  writeFileSync(join(root, 'docs/architecture/reviews/test-review.json'), JSON.stringify(artifact, null, 2));
}

test('accepts a signed review artifact bound to the exact PR diff', () => {
  const repo = makeRepo();
  try {
    writeSignedArtifact(repo.root, repo.base, repo.head, repo.signer);
    const result = verifyReviewArtifacts({ root: repo.root, base: repo.base, head: repo.head });
    assert.equal(result.passed, true, result.errors.join('\n'));
  } finally {
    rmSync(repo.root, { recursive: true, force: true });
  }
});

test('accepts an artifact-only follow-up commit when non-review diff is unchanged', () => {
  const repo = makeRepo();
  try {
    const reviewedHead = repo.head;
    writeSignedArtifact(repo.root, repo.base, reviewedHead, repo.signer);
    git(repo.root, ['add', 'docs/architecture/reviews/test-review.json']);
    git(repo.root, ['commit', '-m', 'add review artifact']);
    const finalHead = git(repo.root, ['rev-parse', 'HEAD']);
    const result = verifyReviewArtifacts({ root: repo.root, base: repo.base, head: finalHead });
    assert.equal(result.passed, true, result.errors.join('\n'));
  } finally {
    rmSync(repo.root, { recursive: true, force: true });
  }
});

test('rejects stale artifacts when code changes after review', () => {
  const repo = makeRepo();
  try {
    writeSignedArtifact(repo.root, repo.base, repo.head, repo.signer, {
      change: { diffSha256: '0'.repeat(64), graphBaselineSha256: '0'.repeat(64), files: [] },
    });
    const result = verifyReviewArtifacts({ root: repo.root, base: repo.base, head: repo.head });
    assert.equal(result.passed, false);
    assert.match(result.errors.join('\n'), /diffSha256|changed code file/);
  } finally {
    rmSync(repo.root, { recursive: true, force: true });
  }
});

test('rejects a signer trusted only by the changed tree', () => {
  const repo = makeRepo();
  try {
    const { publicKey, privateKey } = generateKeyPairSync('ed25519');
    const rogueSigner = {
      keyId: 'pr-tree-only-signer',
      publicPem: publicKey.export({ type: 'spki', format: 'pem' }),
      privateKey,
    };
    const rogueTrust = {
      schemaVersion: 1,
      signers: [{ keyId: rogueSigner.keyId, algorithm: 'ed25519', publicKeyPem: rogueSigner.publicPem }],
    };
    writeFileSync(
      join(repo.root, 'docs/architecture/signers/trusted-agent-signers.json'),
      JSON.stringify(rogueTrust, null, 2),
    );
    writeSignedArtifact(repo.root, repo.base, repo.head, rogueSigner);

    const result = verifyReviewArtifacts({ root: repo.root, base: repo.base, head: repo.head });
    assert.equal(result.passed, false);
    assert.match(result.errors.join('\n'), /keyId is not trusted: pr-tree-only-signer/);
  } finally {
    rmSync(repo.root, { recursive: true, force: true });
  }
});

test('rejects unsigned review artifacts for gated changes', () => {
  const repo = makeRepo();
  try {
    const artifact = {
      schemaVersion: 1,
      reviewType: 'architect-code-review',
      repository: 'dev-brewery/koinon-rms',
      baseCommit: repo.base,
      headCommit: repo.head,
      createdAt: new Date().toISOString(),
      change: { diffSha256: diffHash(repo.root, repo.base, repo.head), files: [] },
      architectRuling: { ruling: 'APPROVED', reasons: [], conditions: [] },
    };
    writeFileSync(join(repo.root, 'docs/architecture/reviews/test-review.json'), JSON.stringify(artifact, null, 2));
    const result = verifyReviewArtifacts({ root: repo.root, base: repo.base, head: repo.head });
    assert.equal(result.passed, false);
    assert.match(result.errors.join('\n'), /signature/i);
  } finally {
    rmSync(repo.root, { recursive: true, force: true });
  }
});
