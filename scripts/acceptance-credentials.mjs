import fs from 'node:fs/promises';
import path from 'node:path';
import {ownedDirectory} from './runtime/state.mjs';

const rotatedPasswords = ['admin', 'reader', 'approver', 'security'].map(role => `password-e2e-${role}`);

export async function withAcceptanceSignals(run) {
  const controller = new AbortController();
  const handlers = new Map();
  let interruptedBy;
  for (const [name, code] of [['SIGHUP', 129], ['SIGINT', 130], ['SIGTERM', 143]]) {
    const handler = () => {
      if (controller.signal.aborted) return;
      interruptedBy = {name, code};
      controller.abort(new Error(`Acceptance interrupted by ${name}`));
    };
    handlers.set(name, handler);
    process.on(name, handler);
  }
  try {
    return await run(controller.signal);
  } catch (error) {
    if (!interruptedBy) throw error;
  } finally {
    for (const [name, handler] of handlers) process.off(name, handler);
    if (interruptedBy) {
      console.error(`Acceptance interrupted by ${interruptedBy.name}`);
      process.exitCode = interruptedBy.code;
    }
  }
}

export async function cleanupAcceptanceRun(directory, bootstrapNames, teardown) {
  const names = new Set([...bootstrapNames, 'credential', ...rotatedPasswords]);
  let resources;
  try {
    resources = await teardown();
  } catch {
    resources = {failed: true, error: 'Resource cleanup failed'};
  }

  let secretsRemaining = null;
  try {
    await ownedDirectory(directory);
    // Compose can need these files even for down. Count what is left after every unlink has settled.
    await Promise.allSettled(Array.from(names, name => fs.unlink(path.join(directory, name))));
    secretsRemaining = (await fs.readdir(directory)).filter(name => names.has(name)).length;
  } catch {
    // An unreadable or unverified directory cannot provide a residual count.
  }

  const receipt = {...resources, secretsRemaining};
  if (secretsRemaining !== 0) {
    receipt.failed = true;
    receipt.error ??= secretsRemaining === null ? 'Credential count unavailable' : 'Credential files remain';
  }
  return receipt;
}
