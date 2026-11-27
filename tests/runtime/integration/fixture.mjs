import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {openSync, closeSync, fstatSync, ftruncateSync, lstatSync, unlinkSync, writeFileSync} from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';
import {completeInitialPasswordChange} from '../../../scripts/account-credential/auth.mjs';
import {prepareContext} from '../../../scripts/runtime/context.mjs';
import {assertOwnership, inspectResources} from '../../../scripts/runtime/docker.mjs';
import {validateFixture} from '../../../scripts/runtime/fixture-guard.mjs';
import {initializeRuntime} from '../../../scripts/runtime/lifecycle.mjs';
import {prepareMigrationEvidenceVolume} from '../../../scripts/runtime/migration-evidence-volume.mjs';
import {cleanupDirectoryIdentity, assertCleanupDirectoryIdentity} from '../../../scripts/runtime/restore-target-lock.mjs';
import {attachScenarioFailure} from '../../../scripts/runtime/scenario-failure.mjs';
import {loadState, ownedDirectory, safeFile} from '../../../scripts/runtime/state.mjs';

export const localReleaseFile = path.resolve(process.env.WEBAPI_RUNTIME_RELEASE_FILE ?? '.runtime/local-build/release.json');

export async function installedContext(directory, state) {
  const release = JSON.parse(await fs.readFile(path.join(directory, 'release.json'), 'utf8'));
  return prepareContext(directory, state, release);
}

export async function installationCleanupGuard(directory, {projectName, ownerId}) {
  validateFixture(projectName, directory);
  const identity = await cleanupDirectoryIdentity(directory);
  if (!identity) throw Error('Installation fixture directory is missing');
  return async () => {
    await assertCleanupDirectoryIdentity(directory, identity);
    const state = await loadState(directory);
    if (state.ownerId !== ownerId || state.projectName !== projectName) throw Error('Installation fixture owner identity changed');
    return state;
  };
}

export async function removeInstallationFixture(directory, recheck, compose, migrationEvidenceSupported) {
  const state = await recheck();
  assertOwnership(state, await inspectResources(state));
  // Only a successful fresh UUID test disposes of its synthetic evidence, after checking its Ready nonce.
  if (migrationEvidenceSupported) await prepareMigrationEvidenceVolume(state, {directory});
  await recheck();
  await compose('--profile', 'tools', 'down', '--volumes', '--remove-orphans');
  assert.equal((await inspectResources(state)).length, 0);
  await recheck();
  await fs.rm(directory, {recursive:true});
}

export async function withInstalledFixture(options, verify) {
  const {directory, projectName} = options;
  validateFixture(projectName, directory);
  try {
    const state = await initializeRuntime({releaseFile:localReleaseFile, ...options});
    const recheck = await installationCleanupGuard(directory, state);
    await verify(state);
    const ctx = await installedContext(directory, await recheck());
    await removeInstallationFixture(directory, recheck, ctx.compose, ctx.migrationEvidenceSupported);
  } catch (error) {
    // A partial installation may already own evidence. Keep it available with the original error.
    console.error('Installation fixture retained for diagnosis: ' + directory);
    throw error;
  }
}

export async function withInstallationAdmin(directory, state, client, verify) {
  validateFixture(state.projectName, directory);
  directory = await ownedDirectory(directory);
  const bootstrapFile = path.join(directory, 'secrets', 'bootstrap-password');
  await safeFile(bootstrapFile);
  const initial = (await fs.readFile(bootstrapFile, 'utf8')).trimEnd();
  const identity = await client.login(state.bootstrapUsername, initial);
  assert.equal(identity.mustChangePassword, true);
  const password = randomBytes(32).toString('base64url') + 'Aa1!';
  await completeInitialPasswordChange({client, identity, currentPassword:initial, newPassword:password});

  // Keep this credential outside the secrets included in a cold backup.
  const file = path.join(directory, 'installation-admin.password');
  let descriptor, primaryError;
  function revoke() {
    if (descriptor === undefined) return;
    try {
      const owned = fstatSync(descriptor, {bigint:true});
      ftruncateSync(descriptor, 0);
      const named = lstatSync(file, {bigint:true, throwIfNoEntry:false});
      if (named && (owned.dev !== named.dev || owned.ino !== named.ino)) throw Error('Installation credential name was replaced');
      if (named) unlinkSync(file);
    } finally {
      closeSync(descriptor);
      descriptor = undefined;
    }
  }
  function onExit() {
    try { revoke(); }
    catch {
      console.error('Could not verify installation credential cleanup for ' + state.projectName);
      process.exitCode = 1;
    }
  }
  const handlers = new Map();
  process.on('exit', onExit);
  for (const name of ['SIGHUP', 'SIGINT', 'SIGTERM']) {
    const handler = () => {
      onExit();
      // A once listener has already been removed here. Preserve normal signal termination.
      if (!process.listenerCount(name)) process.kill(process.pid, name);
    };
    handlers.set(name, handler);
    process.prependOnceListener(name, handler);
  }
  try {
    descriptor = openSync(file, 'wx', 0o600);
    writeFileSync(descriptor, password);
    return await verify(file);
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    try {
      revoke();
    } catch (error) {
      if (primaryError) throw attachScenarioFailure(primaryError, 'cleanupError', error);
      throw error;
    } finally {
      process.off('exit', onExit);
      for (const [name, handler] of handlers) process.off(name, handler);
    }
  }
}
