import fs from 'node:fs/promises';
import path from 'node:path';
import {randomBytes, randomUUID} from 'node:crypto';
import {spawn} from 'node:child_process';
import {withAcceptanceSignals} from '../acceptance-credentials.mjs';
import {execFileAndWait} from '../runtime/exec-file.mjs';
import {writePrivate} from '../runtime/state.mjs';
import {withTestCompletion} from '../contracts/test-completion.mjs';

const root = process.cwd();
const [testProject, ...testArgs] = process.argv.slice(2);
const owner = randomUUID();
const project = 'webapi-policies-test-' + owner;
const runtime = path.join(root, '.runtime');
const directory = path.join(runtime, project);
const env = {...process.env, WEBAPI_POLICY_TEST_OWNER:owner, WEBAPI_POLICY_TEST_PROJECT:project,
  WEBAPI_POLICY_TEST_DIRECTORY:directory};
const compose = ['compose', '-p', project, '-f', 'deploy/compose.test.yml', '-f', 'deploy/compose.policies-test.yml',
  '-f', path.join(directory, 'nuget-seed.yml')];
const ownerKey = 'com.webapi.policies.owner';
const projectKey = 'com.docker.compose.project';
const resources = [
  {count:'containersRemaining', list:['ps', '-aq'], inspect:['inspect', '--format', '{{json .Config.Labels}}']},
  {count:'volumesRemaining', list:['volume', 'ls', '-q'], inspect:['volume', 'inspect', '--format', '{{json .Labels}}'],
    names:[project + '_pg-test', project + '_nuget-test']},
  {count:'networksRemaining', list:['network', 'ls', '--format', '{{.Name}}'], inspect:['network', 'inspect', '--format', '{{json .Labels}}'],
    names:[project + '_default']},
];

async function docker(args, signal) {
  const result = await execFileAndWait('docker', args, {cwd:root, env, signal});
  return result.stdout.trim();
}

async function runCompose(args, signal) {
  signal.throwIfAborted();
  const child = spawn('docker', [...compose, ...args], {cwd:root, env, stdio:'inherit', signal});
  let failure;
  child.once('error', error => { failure = error; });
  const code = await new Promise(resolve => child.once('close', resolve));
  signal.throwIfAborted();
  if (failure || code !== 0) {
    throw Object.assign(new Error('Policy test Compose command failed'), {exitCode:code > 0 ? code : 3});
  }
}

async function listResources(resource) {
  const filtered = async filter => (await docker([...resource.list, '--filter', filter])).split('\n').filter(Boolean);
  const ids = new Set(await filtered('label=' + projectKey + '=' + project));
  for (const id of await filtered('label=' + ownerKey + '=' + owner)) ids.add(id);
  for (const name of resource.names || []) {
    for (const id of await filtered('name=^' + name + '$')) if (id === name) ids.add(id);
  }
  return ids;
}

async function teardown(receipt) {
  try {
    for (const resource of resources) {
      for (const id of await listResources(resource)) {
        const labels = JSON.parse(await docker([...resource.inspect, id]));
        if (labels?.[projectKey] !== project || labels?.[ownerKey] !== owner) throw Error('Ownership mismatch');
      }
    }
    await docker([...compose, 'down', '--volumes', '--remove-orphans']);
  } catch {
    receipt.error = 'Policy test ownership inspection or Docker cleanup failed';
  }
  for (const resource of resources) {
    try {
      receipt[resource.count] = (await listResources(resource)).size;
      if (receipt[resource.count]) receipt.error ??= 'Policy test Docker resources remain';
    } catch {
      receipt.error ??= 'Policy test Docker inventory unavailable';
    }
  }
}

await withAcceptanceSignals(async signal => {
  const receipt = {kind:'policy-test-cleanup', project, containersRemaining:null, volumesRemaining:null,
    networksRemaining:null, secretsRemaining:null};
  let identity, credentialOwned = false, overlayOwned = false, composeStarted = false;
  async function sameDirectory() {
    try {
      const current = await fs.lstat(directory, {bigint:true});
      return current.isDirectory() && current.uid === BigInt(process.getuid()) && (current.mode & 0o077n) === 0n
        && current.dev === identity.dev && current.ino === identity.ino;
    } catch { return false; }
  }
  try {
    signal.throwIfAborted();
    await fs.mkdir(runtime, {recursive:true, mode:0o700});
    if (!(await fs.lstat(runtime)).isDirectory()) throw Error('Invalid policy test runtime directory');
    await fs.mkdir(directory, {mode:0o700});
    // Relative file operations stay in the directory we created even if its public pathname changes.
    process.chdir(directory);
    identity = await fs.lstat('.', {bigint:true});
    if (!await sameDirectory()) throw Error('Invalid policy test directory');
    credentialOwned = true;
    try {
      await fs.writeFile('postgres-password', randomBytes(32).toString('base64url'), {mode:0o600, flag:'wx'});
    } catch (error) {
      if (error.code === 'EEXIST') credentialOwned = false;
      throw error;
    }
    signal.throwIfAborted();
    const seed = env.WEBAPI_POLICY_NUGET_SEED || 'webapi-enterprise-core-test_nuget-test';
    let seedAvailable = false;
    if (/^[a-zA-Z0-9_.-]+$/.test(seed)) {
      try { await docker(['volume', 'inspect', seed], signal); seedAvailable = true; }
      catch (error) {
        signal.throwIfAborted();
        if (error.code !== 1 || !/\bno such volume\b/i.test(error.stderr || '')) throw error;
      }
    }
    const labels = {[ownerKey]:owner};
    const overlay = {networks:{default:{labels}}, volumes:{'pg-test':{labels}, 'nuget-test':{labels}}};
    if (seedAvailable) {
      overlay.services = {sdk:{volumes:['policy-nuget-seed:/nuget-seed:ro']}};
      overlay.volumes['policy-nuget-seed'] = {external:true, name:seed};
    }
    overlayOwned = true;
    try {
      await fs.writeFile('nuget-seed.yml', JSON.stringify(overlay), {mode:0o600, flag:'wx'});
    } catch (error) {
      if (error.code === 'EEXIST') overlayOwned = false;
      throw error;
    }
    signal.throwIfAborted();
    composeStarted = true;
    await runCompose(['up', '-d', '--wait', 'postgres', 'redis'], signal);
    const test = ['bash', '/workspace/scripts/contracts/run-with-completion.sh', testProject, '--configuration', 'Release', ...testArgs];
    const command = seedAvailable
      ? ['bash', '-ec', 'mkdir -p /root/.nuget/packages; cp -a /nuget-seed/. /root/.nuget/packages/; exec "$@"', '--', ...test]
      : test;
    await withTestCompletion(directory, owner, options => runCompose(['run', '--rm', ...options, 'sdk', ...command], signal));
  } catch (error) {
    process.exitCode = error.exitCode || 3;
    console.error('Policy test initialization or execution failed.');
  } finally {
    if (identity) {
      if (composeStarted) {
        if (await sameDirectory()) await teardown(receipt);
        else receipt.error = 'Policy test directory identity changed';
      }
      if (credentialOwned) {
        try { await fs.unlink('postgres-password'); }
        catch (error) { if (error.code !== 'ENOENT') receipt.error ??= 'Policy test credential cleanup failed'; }
      }
      try {
        const names = await fs.readdir('.');
        if (await sameDirectory()) receipt.secretsRemaining = names.includes('postgres-password') ? 1 : 0;
      } catch { /* An unavailable directory keeps its unknown count. */ }
      if (receipt.secretsRemaining !== 0) receipt.error ??= 'Policy test credential count unavailable or nonzero';
      if (!receipt.error) {
        try {
          if (overlayOwned) await fs.rm('nuget-seed.yml', {force:true});
          if (await sameDirectory()) await fs.rmdir(directory);
          else throw Error('Policy test directory identity changed');
        } catch { receipt.error = 'Policy test directory cleanup incomplete'; }
      }
    } else receipt.error = 'Policy test directory identity unavailable';
    if (receipt.error) {
      receipt.failed = true;
      if (identity) {
        try { await writePrivate('cleanup.json', receipt); } catch { /* Stdout remains available. */ }
      }
      console.error(receipt.error);
      process.exitCode ||= 3;
    }
    process.chdir(root);
    console.log(JSON.stringify(receipt));
  }
});
