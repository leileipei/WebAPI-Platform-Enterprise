import fs from 'node:fs/promises';
import path from 'node:path';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {safeFile} from '../runtime/state.mjs';
import {withAcceptanceSignals} from '../acceptance-credentials.mjs';
import {cleanupPolicyRun} from './cleanup.mjs';
import {changePolicyReview, readPolicyReview} from './review-pointer.mjs';

await withAcceptanceSignals(cleanupReview);

async function cleanupReview() {

const root = path.resolve(import.meta.dirname, '../..');
const pointer = path.join(root, '.runtime/policy-review.json');
const record = await readPolicyReview(root);
const {directory, project} = record;
const contextFile = path.join(directory, 'review-context.json');
async function readPrivate(file) {
  await safeFile(file);
  return JSON.parse(await fs.readFile(file, 'utf8'));
}
let identity;
try { identity = await readPrivate(path.join(directory, 'owner.json')); }
catch (error) {
  if (error.code !== 'ENOENT') throw error;
  // Historical reviews store their private identity in the original context.
  identity = await readPrivate(contextFile);
  if (identity.directory !== directory) throw Error('Owned policy review directory mismatch');
}
if (identity.project !== project || project !== 'webapi-enterprise-e2e-policies-' + identity.owner) {
  throw Error('Owned policy review identity mismatch');
}
const cleanup = await cleanupPolicyRun(directory, async () => {
  const context = await readPrivate(contextFile);
  if (context.directory !== directory || context.project !== project || context.owner !== identity.owner ||
      !Array.isArray(context.args) || context.environment?.WEBAPI_E2E_PROJECT !== project ||
      context.environment.WEBAPI_E2E_DIRECTORY !== directory || context.environment.WEBAPI_E2E_RELATIVE_DIR !== '.runtime/' + project) {
    throw Error('Owned policy review context mismatch');
  }
  const env = {...process.env, WEBAPI_E2E_PROJECT: project, WEBAPI_E2E_DIRECTORY: directory,
    WEBAPI_E2E_RELATIVE_DIR: '.runtime/' + project, WEBAPI_ENVIRONMENT_ID: context.environment.WEBAPI_ENVIRONMENT_ID};
  const exec = promisify(execFile);
  const docker = async args => (await exec('docker', args, {cwd: root, env, maxBuffer: 4 * 1024 * 1024})).stdout.trim();
  return {project, owner: identity.owner, args: context.args, docker};
});
const receipt = {project, time: new Date().toISOString(), ...cleanup};
const publicFile = path.join(root, 'docs/evidence/policies/cleanup.json');
async function saveReceipt() {
  const json = JSON.stringify(receipt);
  await fs.appendFile(path.join(directory, 'cleanup-attempts.jsonl'), json + '\n', {mode: 0o600});
  await fs.writeFile(publicFile, JSON.stringify(receipt, null, 2) + '\n');
}
await saveReceipt();
if (receipt.failed) throw Error(receipt.error);
try {
  await changePolicyReview(pointer, record);
} catch {
  Object.assign(receipt, {failed: true, error: 'Policy review pointer release failed'});
  await saveReceipt();
  throw Error(receipt.error);
}
console.log('Policy review cleanup receipt: ' + publicFile);
}
