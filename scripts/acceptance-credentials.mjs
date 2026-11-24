import fs from 'node:fs/promises';
import {constants} from 'node:fs';
import {execFile} from 'node:child_process';
import path from 'node:path';
import {promisify} from 'node:util';
import {ownedDirectory} from './runtime/state.mjs';

const exec = promisify(execFile);
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
  let handle, identity;
  const unavailable = {failed:true, secretsRemaining:null, error:'Credential count unavailable'};
  try {
    directory = await ownedDirectory(directory);
    handle = await fs.open(directory, constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW);
    identity = await privateDirectoryId(handle);
  } catch {
    if (handle) await handle.close();
    return unavailable;
  }

  try {
    let resources;
    try {
      resources = await teardown();
    } catch {
      resources = {failed:true, error:'Resource cleanup failed'};
    }

    try {
      // Compose may read these files during down. Perform every unlink relative to a pinned
      // working directory, so replacing the pathname cannot redirect a deletion.
      const {stdout} = await exec(process.execPath, [import.meta.filename, '--unlink-credentials', identity, ...names], {cwd:directory});
      const remaining = (await fs.readdir(directory)).filter(name => names.has(name)).length;
      if (await privateDirectoryId(directory) !== identity || stdout.trim() !== String(remaining)) {
        return {...resources, ...unavailable};
      }
      const receipt = {...resources, secretsRemaining:remaining};
      if (remaining) {
        receipt.failed = true;
        receipt.error ??= 'Credential files remain';
      }
      return receipt;
    } catch {
      return {...resources, ...unavailable};
    }
  } finally {
    await handle.close();
  }
}

async function privateDirectoryId(directory) {
  const info = typeof directory === 'string'
    ? await fs.lstat(directory, {bigint:true})
    : await directory.stat({bigint:true});
  if (!info.isDirectory() || info.uid !== BigInt(process.getuid()) || (info.mode & 0o077n)) {
    throw Error('Invalid acceptance directory');
  }
  return `${info.dev}:${info.ino}`;
}

if (process.argv[1] === import.meta.filename && process.argv[2] === '--unlink-credentials') {
  const [identity, ...names] = process.argv.slice(3);
  if (await privateDirectoryId('.') !== identity || !names.every(name => /^[a-z][a-z0-9.-]*$/.test(name))) {
    throw Error('Invalid acceptance cleanup input');
  }
  await Promise.allSettled(names.map(name => fs.unlink(name)));
  const entries = await fs.readdir('.');
  console.log(entries.filter(name => names.includes(name)).length);
}
