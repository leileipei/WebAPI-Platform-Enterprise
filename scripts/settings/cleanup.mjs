import assert from 'node:assert/strict';
import {execFile} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import {promisify} from 'node:util';
import {cleanupAcceptanceRun, withAcceptanceSignals} from '../acceptance-credentials.mjs';
import {withRestoreTargetLock} from '../runtime/restore-target-lock.mjs';
import {safeFile, writePrivate} from '../runtime/state.mjs';
import {settingsCredentialFiles, validateOwnedResources, validateSettingsDirectory} from './evidence.mjs';

const exec = promisify(execFile);
const repository = path.resolve(import.meta.dirname, '../..');
const projectPrefix = 'webapi-enterprise-e2e-settings-';
const reviewPointer = root => path.join(root, '.runtime/settings-review.json');
const reviewFile = directory => path.join(directory, 'review-context.json');

async function optionalJson(file, privateFile = false) {
  try {
    if (privateFile) await safeFile(file);
    const value = JSON.parse(await fs.readFile(file, 'utf8'));
    if (privateFile) assert(value && typeof value === 'object' && !Array.isArray(value), 'Invalid private settings record');
    return value;
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    return null;
  }
}

export async function writePublicJson(file, value) {
  const temp = file + '.' + randomUUID() + '.tmp';
  try {
    await fs.writeFile(temp, JSON.stringify(value, null, 2) + '\n', {flag:'wx'});
    await fs.rename(temp, file);
  } finally {
    await fs.rm(temp, {force:true});
  }
}

// Serialize the short compare-and-update of the shared pointer and public receipts across processes.
export async function withSettingsRecords(root, action) {
  const lock = path.join(root, '.runtime/settings-public-records'), deadline = Date.now() + 2000;
  for (;;) {
    try {
      return await withRestoreTargetLock(lock, action);
    } catch (error) {
      if (error.code !== 'RESTORE_TARGET_LOCK_HELD' || Date.now() >= deadline) throw error;
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  }
}

export async function publishSettingsPointer(root, workspace, url, required) {
  const file = reviewPointer(root), current = await optionalJson(file, true);
  if (current) {
    if (current.project === workspace.project && current.directory === workspace.directory) return;
    if (required) throw Error('Another settings run is awaiting review');
    return;
  }
  await writePrivate(file, {directory:workspace.directory, project:workspace.project, ...(url ? {url} : {})});
}

export async function retireSettingsContext(root, workspace) {
  await fs.rm(path.join(workspace.directory, 'context.json'), {force:true});
  const file = reviewPointer(root), current = await optionalJson(file, true);
  if (current?.project === workspace.project && current.directory === workspace.directory) await fs.unlink(file);
  await fs.rm(reviewFile(workspace.directory), {force:true});
  await fs.rm(path.join(workspace.directory, 'cleanup-context.json'), {force:true});
}

function validateComposeArgs(workspace, args) {
  if (args === null) return;
  assert(Array.isArray(args) && args[0] === 'compose' && args[1] === '-p' && args[2] === workspace.project,
    'Owned compose arguments mismatch');
  for (let i = 3; i < args.length; i += 2) {
    const file = args[i + 1];
    assert(args[i] === '-f' && typeof file === 'string' && path.isAbsolute(file)
      && path.resolve(file).startsWith(workspace.directory + path.sep), 'Owned compose file mismatch');
  }
}

export async function cleanupSettingsRun(root, workspace, args, docker) {
  await validateSettingsDirectory(root, workspace);
  return cleanupAcceptanceRun(workspace.directory, settingsCredentialFiles, async () => {
    try {
      validateComposeArgs(workspace, args);
    } catch {
      return {failed:true, error:'Settings compose context could not be verified', containersRemaining:null, volumesRemaining:null};
    }
    if (args === null) return {};
    const filter = 'label=com.docker.compose.project=' + workspace.project;
    const kinds = [
      {field:'containersRemaining', list:['ps','-aq','--filter',filter], inspect:['inspect','--format','{{json .Config.Labels}}']},
      {field:'volumesRemaining', list:['volume','ls','-q','--filter',filter], inspect:['volume','inspect','--format','{{json .Labels}}']},
    ];
    const inventory = async kind => (await docker(kind.list)).split('\n').filter(Boolean);
    let failed = false;
    try {
      for (const kind of kinds) {
        for (const id of await inventory(kind)) {
          validateOwnedResources(workspace, [JSON.parse(await docker([...kind.inspect, id]))]);
        }
      }
      await docker([...args, 'down', '--volumes', '--remove-orphans']);
    } catch {
      failed = true;
    }
    const counts = {};
    for (const kind of kinds) {
      try {
        counts[kind.field] = (await inventory(kind)).length;
      } catch {
        counts[kind.field] = null;
      }
      if (counts[kind.field] !== 0) failed = true;
    }
    return failed ? {...counts, failed:true, error:'Settings Docker cleanup incomplete'} : counts;
  });
}

export async function cleanupSettingsReview(root, directory) {
  const info = directory
    ? {directory:path.resolve(directory), project:path.basename(path.resolve(directory))}
    : await optionalJson(reviewPointer(root), true);
  if (!info) throw Error('No settings run is awaiting cleanup');
  const workspace = {...info, owner:info.project.slice(projectPrefix.length)};
  await validateSettingsDirectory(root, workspace);
  const owner = await optionalJson(path.join(info.directory, 'owner.json'), true);
  if (owner) assert(owner.project === info.project && owner.owner === workspace.owner, 'Owned settings identity mismatch');
  let context;
  try {
    context = await optionalJson(path.join(info.directory, 'cleanup-context.json'), true)
      ?? await optionalJson(reviewFile(info.directory), true);
    assert(context?.project === info.project && context.directory === info.directory && context.owner === workspace.owner,
      'Owned review context mismatch');
  } catch (error) {
    if (!owner) throw error; // Legacy runs store their only independent identity in the review context.
    context = null;
  }
  const args = context?.args === null && context.composeStarted !== false ? undefined : context?.args;
  const env = {...process.env, WEBAPI_E2E_PROJECT:workspace.project, WEBAPI_E2E_DIRECTORY:workspace.directory,
    WEBAPI_E2E_RELATIVE_DIR:'.runtime/' + workspace.project, WEBAPI_ENVIRONMENT_ID:context?.environment?.WEBAPI_ENVIRONMENT_ID || ''};
  const docker = async args => (await exec('docker', args, {cwd:root, env, maxBuffer:16*1024*1024})).stdout.trim();
  const cleanup = await cleanupSettingsRun(root, workspace, args, docker);
  const receipt = {project:workspace.project, ...cleanup, time:new Date().toISOString()};
  await writePrivate(path.join(info.directory, 'cleanup.json'), receipt);
  const publicDirectory = path.join(root, 'docs/evidence/settings');
  await fs.mkdir(publicDirectory, {recursive:true});
  await withSettingsRecords(root, async () => {
    const verification = path.join(publicDirectory, 'verification.json'), result = await optionalJson(verification);
    if (result?.project === workspace.project) {
      const scenarioComplete = result.scenarioComplete ?? result.complete;
      await writePublicJson(verification, {...result, scenarioComplete, complete:scenarioComplete === true && !cleanup.failed, cleanup});
    }
    await writePublicJson(path.join(publicDirectory, 'cleanup.json'), receipt);
    if (!cleanup.failed) await retireSettingsContext(root, workspace);
  });
  if (cleanup.failed) throw Error(cleanup.error);
  return receipt;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  const args = process.argv.slice(2);
  if (args.length && (args.length !== 2 || args[0] !== '--directory')) throw Error('Use cleanup [--directory <owned-private-directory>]');
  await withAcceptanceSignals(async () => {
    await cleanupSettingsReview(repository, args[1]);
    console.log('Owned settings resources and credentials removed.');
  });
}
