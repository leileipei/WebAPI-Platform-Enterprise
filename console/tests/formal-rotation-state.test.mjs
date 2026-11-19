import {test} from 'node:test';
import assert from 'node:assert/strict';
import {
  formalRotationScope, formalRotationCandidates, formalRotationWindow, matchesFormalRotationPreview,
  matchesFormalRotationReceipt, formalRotationVerification, formalRotationCommand, rotationGuid
} from '../src/applications/formal-rotation-state.mjs';

const id = suffix => '10000000-0000-0000-0000-' + String(suffix).padStart(12, '0');
const app = {id:id(1), organizationId:id(2), projectId:id(3)};
const environment = id(4), otherEnvironment = id(5), rotation = id(6);
const old = {id:id(7), applicationId:app.id, environmentId:environment, bindingMode:'EnvironmentBound', status:'Active', revision:9};
const replacement = {
  ...old, id:id(8), validFrom:'2026-11-20T09:00:00.123456+00:00', expiresAt:'2026-12-20T09:00:00.654321+00:00'
};
const scope = {organizationId:app.organizationId, projectId:app.projectId, environmentId:environment};
const tree = {
  projects:[{id:app.projectId, organizationId:app.organizationId}],
  environments:[{id:environment, projectId:app.projectId}, {id:otherEnvironment, projectId:app.projectId}]
};
const targets = [{nodeId:id(10), instanceId:id(11)}, {nodeId:id(12), instanceId:id(13)}];
const window = {
  environmentId:environment, replacementCredentialId:replacement.id, newValidFrom:replacement.validFrom,
  newExpiresAt:replacement.expiresAt, overlapUntil:'2026-11-21T10:00:00.000Z', hardDeadline:'2026-11-22T10:00:00.000Z'
};
const preview = {
  credentialId:old.id, applicationId:app.id, environmentId:environment, credentialRevision:9, previewHash:'a'.repeat(64),
  maskedAccessKey:'••••1234', configVersion:15, deploymentSequence:23, newValidFrom:'2026-11-20T09:00:00.1234560Z',
  newExpiresAt:'2026-12-20T09:00:00.6543210Z', overlapUntil:'2026-11-21T10:00:00+00:00',
  hardDeadline:'2026-11-22T10:00:00+00:00', lastWorkingCredentialAtDeadline:true, lastPublishedCredentialAtDeadline:false, targets
};
const receipt = {
  rotationId:rotation, credentialId:old.id, environmentId:environment, replacementCredentialId:replacement.id,
  hardDeadline:window.hardDeadline, recordedAt:'2026-11-19T12:00:00Z', revision:1, status:'Scheduled',
  revocationOperationId:null, abortRevocationOperationId:null, replayed:false
};

test('formal rotation requires both write permissions in the old credential environment', () => {
  const grants = [];
  const can = (code, requestedScope, write) => {
    grants.push([code, requestedScope, write]);
    return requestedScope.environmentId === environment && write === true;
  };
  assert.deepEqual(formalRotationScope(app, old, tree, can), scope);
  assert.deepEqual(grants, [['credential.manage', scope, true], ['credential.revoke', scope, true]]);
  assert.equal(formalRotationScope(app, old, tree, code => code === 'credential.manage'), null);
  assert.equal(formalRotationScope(app, old, tree, (_code, _scope, write) => !write), null);
  assert.equal(formalRotationScope(app, {...old, bindingMode:'LegacyGlobal'}, tree, () => true), null);
  assert.equal(formalRotationScope(app, {...old, environmentId:id(99)}, tree, () => true), null);
  assert.equal(formalRotationScope(app, old, {...tree, projects:[{...tree.projects[0], organizationId:id(99)}]}, () => true), null);
});

test('a replacement must be another active environment-bound key in the same application and environment', () => {
  assert.deepEqual(formalRotationCandidates(old, [
    old, replacement, {...replacement, id:id(20), status:'Revoked'},
    {...replacement, id:id(21), environmentId:otherEnvironment},
    {...replacement, id:id(22), bindingMode:'LegacyGlobal'},
    {...replacement, id:id(23), applicationId:id(99)}
  ]), [replacement]);
});

test('the UTC window preserves the replacement credential expiry and start precision', () => {
  assert.deepEqual(formalRotationWindow(replacement, '2026-11-21T10:00', '2026-11-22T10:00'), window);
  assert.equal(formalRotationWindow(replacement, '2026-11-20T09:00', '2026-11-22T10:00'), null);
  assert.equal(formalRotationWindow(replacement, '2026-11-23T10:00', '2026-11-22T10:00'), null);
  assert.equal(formalRotationWindow(replacement, '2026-11-21T10:00', '2026-12-20T09:01'), null);
  assert.equal(formalRotationWindow(replacement, '2026-11-31T10:00', '2026-12-02T10:00'), null);
});

test('preview must bind the old credential, its revision, window, risk and full target identities', () => {
  assert.equal(matchesFormalRotationPreview(preview, old, window), true);
  for (const wrong of [
    {credentialId:replacement.id}, {applicationId:id(90)}, {environmentId:otherEnvironment}, {credentialRevision:8},
    {hardDeadline:'2026-11-22T10:00:00.000001Z'}, {newExpiresAt:'2026-12-20T09:00:00.654322Z'},
    {targets:[targets[0], {...targets[0], instanceId:id(90)}]}, {lastWorkingCredentialAtDeadline:null}
  ]) assert.equal(matchesFormalRotationPreview({...preview, ...wrong}, old, window), false);
});

test('resume rejects a receipt for another rotation, old key, replacement or environment', () => {
  assert.equal(matchesFormalRotationReceipt(receipt, old, rotation), true);
  assert.equal(matchesFormalRotationReceipt(receipt, old, null, replacement.id), true);
  for (const wrong of [
    {rotationId:id(90)}, {credentialId:replacement.id}, {environmentId:otherEnvironment},
    {replacementCredentialId:old.id}, {replacementCredentialId:null}, {rotationId:null}, {status:'Completed'}, {revision:0},
    {status:'AbortPending'}, {status:'Aborted'}, {status:'ManualRevocationRecorded'}, {status:'RevocationRecorded'},
    {revocationOperationId:id(91)}
  ]) assert.equal(matchesFormalRotationReceipt({...receipt, ...wrong}, old, rotation), false);
  assert.equal(matchesFormalRotationReceipt({...receipt, replacementCredentialId:id(90)}, old, null, replacement.id), false);
  assert.equal(rotationGuid(rotation), rotation);
  assert.equal(rotationGuid(rotation + '?secret=unsafe'), null);
  assert.equal(rotationGuid('00000000-0000-0000-0000-000000000000'), null);
});

test('completion requires all current targets to have successful replacement credential proofs', () => {
  const verified = {rotationId:rotation, state:'Verified', configVersion:15, deploymentSequence:23,
    targets:targets.map(target => ({...target, state:'Verified'}))};
  assert.deepEqual(formalRotationVerification(verified, rotation), {result:verified, complete:true});
  for (const wrong of [
    {state:'AwaitingProof'}, {state:'Unavailable'}, {state:'Closed'}, {configVersion:null}, {deploymentSequence:null},
    {targets:[]}, {targets:[verified.targets[0]]}, {targets:[verified.targets[0], verified.targets[0]]},
    {targets:[verified.targets[0], {...verified.targets[1], state:'Missing'}]}, {rotationId:id(90)}
  ]) assert.equal(formalRotationVerification({...verified, ...wrong}, rotation).complete, false);
});

test('only the same command, revision and body reuse an idempotency key', () => {
  const path = `/credential-rotations/${rotation}/abort`;
  const body = {confirm:true, reason:'owner_request'};
  const command = formalRotationCommand(undefined, path, 1, body);
  assert.equal(command.etag, '"1"');
  assert.ok(rotationGuid(command.idempotencyKey));
  assert.equal(formalRotationCommand(command, path, 1, {...body}).idempotencyKey, command.idempotencyKey);
  assert.notEqual(formalRotationCommand(command, path, 2, body).idempotencyKey, command.idempotencyKey);
  assert.notEqual(formalRotationCommand(command, path, 1, {...body, reason:'policy_change'}).idempotencyKey, command.idempotencyKey);
  assert.notEqual(formalRotationCommand(command, path.replace('abort','complete'), 1, body).idempotencyKey, command.idempotencyKey);
});
