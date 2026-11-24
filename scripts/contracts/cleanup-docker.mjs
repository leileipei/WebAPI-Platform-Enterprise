import {execFileSync} from 'node:child_process';
import fs from 'node:fs';
import {setTimeout as delay} from 'node:timers/promises';

const [phase, ...volumeKeys] = process.argv.slice(2);
const {WEBAPI_CONTRACT_TEST_OWNER: owner, WEBAPI_CONTRACT_TEST_PROJECT: project,
  WEBAPI_CONTRACT_TEST_DIRECTORY: directory} = process.env;
const projectLabel = 'com.docker.compose.project';
const ownerLabel = 'com.webapi.contracts.owner';
const kinds = {
  containers: {list: ['ps', '-aq', '--no-trunc'], inspect: ['inspect', '--format', '{{json .Config.Labels}}']},
  volumes: {list: ['volume', 'ls', '--format', '{{.Name}}'], inspect: ['volume', 'inspect', '--format', '{{json .Labels}}'],
    keys: new Map(volumeKeys.map(key => [project + '_' + key, key])), label: 'com.docker.compose.volume'},
  networks: {list: ['network', 'ls', '--format', '{{.Name}}'], inspect: ['network', 'inspect', '--format', '{{json .Labels}}'],
    keys: new Map([[project + '_default', 'default']]), label: 'com.docker.compose.network'},
};

function docker(args) {
  return execFileSync('docker', args, {encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit']}).trim();
}

function lines(args) {
  return docker(args).split('\n').filter(Boolean);
}

function list(kind) {
  const {list: args, keys} = kinds[kind];
  const ids = new Set(lines([...args, '--filter', 'label=' + projectLabel + '=' + project]));
  for (const id of lines([...args, '--filter', 'label=' + ownerLabel + '=' + owner])) ids.add(id);
  // Exact declared names also expose collisions without a project label. External seed volumes
  // and containers holding this project's volumes belong to their own projects.
  if (keys) {
    for (const id of lines([...args, '--filter', 'name=' + project])) {
      if (keys.has(id)) ids.add(id);
    }
  }
  return [...ids];
}

function inventory() {
  const resources = {};
  for (const kind of Object.keys(kinds)) {
    try { resources[kind] = list(kind); }
    catch { resources[kind] = null; }
  }
  return resources;
}

function report(resources) {
  const {containers, volumes, networks} = resources;
  console.error('Contract test Docker inventory ' + JSON.stringify({project,
    containersRemaining: containers?.length ?? null, volumesRemaining: volumes?.length ?? null,
    networksRemaining: networks?.length ?? null}));
}

function assertDirectory() {
  // The shell pinned the exclusive working directory before creating credentials or Docker resources.
  const pinned = fs.statSync('.', {bigint: true}), named = fs.lstatSync(directory, {bigint: true});
  if (!named.isDirectory() || named.dev !== pinned.dev || named.ino !== pinned.ino)
    throw Error('Contract test directory identity changed during Docker cleanup.');
}

function stillOwned(kind, id) {
  const {inspect, keys, label} = kinds[kind];
  let labels;
  try { labels = JSON.parse(docker([...inspect, id])); }
  catch {
    // A concurrent --rm can finish between list and inspect. Prove absence with a fresh listing.
    if (!list(kind).includes(id)) return false;
    throw Error('Cannot inspect contract test ' + kind + ' ownership.');
  }
  if (labels?.[projectLabel] !== project || labels?.[ownerLabel] !== owner
      || keys && (!keys.has(id) || labels[label] !== keys.get(id)))
    throw Error('Contract test ownership mismatch; destructive cleanup rejected.');
  return true;
}

function verify(resources) {
  for (const [kind, ids] of Object.entries(resources)) {
    if (ids === null) throw Error('Cannot inventory contract test ' + kind + '.');
    for (const id of ids) stillOwned(kind, id);
  }
  assertDirectory();
}

function empty(resources) {
  return Object.values(resources).every(ids => ids?.length === 0);
}

async function reapAfterDown() {
  // Compose may report success while an SDK container is still detaching. Wait only for
  // this teardown, and leave a persistently held volume and its external holders alone.
  for (const waitMs of [0, 250, 1000, 2500]) {
    if (waitMs) await delay(waitMs);
    const resources = inventory();
    verify(resources);
    if (empty(resources)) return resources;
    for (const name of resources.volumes) {
      const holders = lines(['ps', '-aq', '--no-trunc', '--filter', 'volume=' + name]);
      if (holders.length) {
        console.error('Retaining ' + name + ' while referenced by ' + holders.length + ' container(s).');
        continue;
      }
      if (!stillOwned('volumes', name)) continue;
      assertDirectory();
      // Non-forced removal also protects a container that attaches after our check.
      try { docker(['volume', 'rm', name]); } catch { /* Re-read before deciding or retrying. */ }
    }
  }
  const resources = inventory();
  if (!empty(resources)) throw Error('Contract test Docker teardown left resources or an unavailable inventory.');
  return resources;
}

try {
  if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(owner) || project !== 'webapi-contracts-test-' + owner)
    throw Error('Invalid contract test Docker project identity.');
  if (phase === 'before-down') verify(inventory());
  else if (phase === 'after-down') report(await reapAfterDown());
  else throw Error('Invalid contract test Docker cleanup phase.');
} catch (error) {
  console.error(error.message);
  report(inventory());
  process.exitCode = 1;
}
