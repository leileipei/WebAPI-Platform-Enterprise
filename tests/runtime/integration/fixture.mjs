import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import {completeInitialPasswordChange} from '../../../scripts/account-credential/auth.mjs';
import {prepareContext} from '../../../scripts/runtime/context.mjs';
import {assertOwnership, inspectResources} from '../../../scripts/runtime/docker.mjs';
import {validateFixture} from '../../../scripts/runtime/fixture-guard.mjs';
import {initializeRuntime} from '../../../scripts/runtime/lifecycle.mjs';
import {attachScenarioFailure, withScenarioHandle} from '../../../scripts/runtime/scenario-failure.mjs';
import {loadState, ownedDirectory, safeFile} from '../../../scripts/runtime/state.mjs';

export const localReleaseFile = path.resolve(process.env.WEBAPI_RUNTIME_RELEASE_FILE ?? '.runtime/local-build/release.json');

export async function installedContext(directory, state) {
  const release = JSON.parse(await fs.readFile(path.join(directory, 'release.json'), 'utf8'));
  return prepareContext(directory, state, release);
}

export async function withInstalledFixture(options, verify) {
  const {directory, projectName} = options;
  validateFixture(projectName, directory);
  try {
    const state = await initializeRuntime({releaseFile:localReleaseFile, ...options});
    await verify(state);
  } catch (error) {
    // A partial installation may already own evidence. Keep it available with the original error.
    console.error('Installation fixture retained for diagnosis: ' + directory);
    throw error;
  }
  const state = await loadState(directory);
  assertOwnership(state, await inspectResources(state));
  const ctx = await installedContext(directory, state);
  await ctx.compose('--profile', 'tools', 'down', '--volumes', '--remove-orphans');
  assert.equal((await inspectResources(state)).length, 0);
  await fs.rm(directory, {recursive:true});
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
  const handle = await fs.open(file, 'wx', 0o600);
  let primaryError;
  try {
    await withScenarioHandle(handle, h => h.writeFile(password));
    return await verify(file);
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    try {
      await fs.unlink(file);
    } catch (error) {
      if (primaryError) throw attachScenarioFailure(primaryError, 'cleanupError', error);
      throw error;
    }
  }
}
