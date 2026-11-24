import path from 'node:path';
import assert from 'node:assert/strict';
import {cleanupAcceptanceRun} from '../acceptance-credentials.mjs';

export const secretNames = ['password', 'postgres-password', 'node-a', 'node-b', 'ip-hmac', 'cursor-key', 'login-hmac'];
const cleanupNames = [...secretNames, 'original.snapshot', 'rollback.snapshot'];

export function policyComposeArguments(directory, project) {
  const deploy = path.join(directory, 'build/source/deploy');
  return ['compose', '-p', project, '-f', path.join(deploy, 'compose.e2e.yml'),
    '-f', path.join(deploy, 'compose.observability.yml'), '-f', path.join(deploy, 'compose.observability-e2e.yml'),
    '-f', path.join(directory, 'policy-overlay.yml'), '-f', path.join(directory, 'policy-volumes.yml')];
}

export function cleanupPolicyRun(directory, prepareTeardown) {
  return cleanupAcceptanceRun(directory, cleanupNames, async () => {
    const {project, owner, args, docker} = await prepareTeardown();
    if (!args) return {};
    assert.deepEqual(args, policyComposeArguments(directory, project), 'Owned policy Compose arguments mismatch');
    const projectLabel = 'label=com.docker.compose.project=' + project;
    const ownerLabel = 'label=com.webapi.policies.owner=' + owner;
    const legacyNames = new Set([project + '_evidence', project + '_lpm']);
    const resources = [
      {kind: 'container', count: 'containersRemaining', list: ['ps', '-aq'], inspect: ['inspect', '--format', '{{json .Config.Labels}}']},
      {kind: 'volume', count: 'volumesRemaining', list: ['volume', 'ls', '-q'], inspect: ['volume', 'inspect', '--format', '{{json .Labels}}']},
    ];
    const filtered = async (resource, filter) => (await docker([...resource.list, '--filter', filter])).split('\n').filter(Boolean);
    async function list(resource) {
      const ids = await filtered(resource, projectLabel);
      if (resource.kind === 'volume') {
        ids.push(...await filtered(resource, ownerLabel));
        const named = await filtered(resource, 'name=^' + project + '_(evidence|lpm)$');
        ids.push(...named.filter(id => legacyNames.has(id)));
      }
      return new Set(ids);
    }
    const inspect = async (resource, id) => JSON.parse(await docker([...resource.inspect, id]));
    const legacyOwned = (id, labels) => legacyNames.has(id) && labels?.['com.webapi.policies.owner'] === owner
      && labels['com.docker.compose.project'] === undefined;
    const legacy = new Set();
    for (const resource of resources) {
      for (const id of await list(resource)) {
        const labels = await inspect(resource, id);
        assert.equal(labels['com.webapi.policies.owner'], owner);
        // Older runners created precisely these two volumes with only their private owner label.
        if (resource.kind === 'volume' && legacyOwned(id, labels)) {
          legacy.add(id);
        } else {
          assert.equal(labels['com.docker.compose.project'], project);
        }
      }
    }
    await docker([...args, 'down', '--volumes', '--remove-orphans']);
    const receipt = {};
    if (legacy.size) {
      const volume = resources.find(resource => resource.kind === 'volume');
      try {
        const remaining = await list(volume);
        for (const id of legacy) {
          if (!remaining.has(id)) continue;
          assert(legacyOwned(id, await inspect(volume, id)), 'Legacy policy volume identity changed');
          await docker(['volume', 'rm', id]);
        }
      } catch {
        receipt.failed = true;
        receipt.error = 'Legacy policy volume cleanup incomplete';
      }
    }
    for (const resource of resources) {
      try {
        receipt[resource.count] = (await list(resource)).size;
        if (receipt[resource.count]) {
          receipt.failed = true;
          receipt.error = 'Owned policy resources remain';
        }
      } catch {
        receipt.failed = true;
        receipt.error = 'Owned policy resource count unavailable';
      }
    }
    return receipt;
  });
}
