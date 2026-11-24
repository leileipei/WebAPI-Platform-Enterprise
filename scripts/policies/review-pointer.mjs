import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {ownedDirectory, safeFile, validGuid} from '../runtime/state.mjs';
import {withRestoreTargetLock} from '../runtime/restore-target-lock.mjs';

const prefix = 'webapi-enterprise-e2e-policies-';

export async function readPolicyReview(root) {
  const pointer = path.join(root, '.runtime/policy-review.json');
  await safeFile(pointer);
  const record = JSON.parse(await fs.readFile(pointer, 'utf8'));
  if (!record.project?.startsWith(prefix) || !validGuid(record.project.slice(prefix.length)) ||
      record.directory !== path.join(root, '.runtime', record.project) ||
      await ownedDirectory(record.directory) !== record.directory) {
    throw Error('Owned policy review directory mismatch');
  }
  return record;
}

export async function claimPolicyReview(pointer, record) {
  // Publish the complete private record atomically without replacing an active review.
  const entry = path.join(record.directory, 'review-entry.json');
  await fs.writeFile(entry, JSON.stringify(record, null, 2), {mode: 0o600, flag: 'wx'});
  await fs.link(entry, pointer);
}

export async function changePolicyReview(pointer, record, replacement) {
  // Serialize all changes by holders of this project. A new review can claim only an absent pointer.
  return withRestoreTargetLock(record.directory, async () => {
    let current;
    try {
      await safeFile(pointer);
      current = JSON.parse(await fs.readFile(pointer, 'utf8'));
    } catch (error) {
      if (error.code === 'ENOENT') return false;
      throw error;
    }
    if (current.project !== record.project || current.directory !== record.directory) return false;
    if (!replacement) {
      await fs.unlink(pointer);
    } else {
      const staged = path.join(record.directory, 'review-update-' + randomUUID() + '.json');
      try {
        await fs.writeFile(staged, JSON.stringify(replacement), {mode: 0o600, flag: 'wx'});
        await fs.rename(staged, pointer);
      } finally {
        await fs.rm(staged, {force: true});
      }
    }
    return true;
  });
}
