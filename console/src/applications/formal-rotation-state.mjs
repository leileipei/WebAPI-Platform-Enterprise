import {credentialInstantTicks as instant} from './application-state.mjs';

const receiptStates = new Set(['Scheduled', 'ManualRevocationRecorded', 'AbortPending', 'Aborted', 'RevocationRecorded']);
const verificationStates = new Set(['Verified', 'AwaitingProof', 'Unavailable', 'Closed']);
const positiveRevision = value => Number.isSafeInteger(value) && value > 0;

export function rotationGuid(value) {
  return typeof value === 'string' && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value) &&
    value !== '00000000-0000-0000-0000-000000000000' ? value.toLowerCase() : null;
}

export function formalRotationScope(application, credential, tree, can) {
  if (credential?.bindingMode !== 'EnvironmentBound' || credential.applicationId !== application.id ||
      !rotationGuid(credential.id) || !rotationGuid(credential.environmentId)) return null;
  const environment = tree.environments.find(item => item.id === credential.environmentId);
  const project = environment && tree.projects.find(item => item.id === environment.projectId &&
    item.organizationId === application.organizationId && (!application.projectId || item.id === application.projectId));
  if (!project) return null;
  const scope = {organizationId:application.organizationId, projectId:project.id, environmentId:environment.id};
  return can('credential.manage', scope, true) && can('credential.revoke', scope, true) ? scope : null;
}

export function formalRotationCandidates(old, credentials) {
  return credentials.filter(item => item.id !== old.id && rotationGuid(item.id) && item.status === 'Active' &&
    item.bindingMode === 'EnvironmentBound' && item.environmentId === old.environmentId && item.applicationId === old.applicationId);
}

function fromUtcInput(value) {
  return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value) ? value + ':00.000Z' : '';
}

function sameInstant(left, right) {
  const rightInstant = instant(right);
  return rightInstant !== null && instant(left) === rightInstant;
}

export function formalRotationWindow(replacement, overlapInput, deadlineInput) {
  if (!replacement || !rotationGuid(replacement.environmentId) || !rotationGuid(replacement.id)) return null;
  const overlapUntil = fromUtcInput(overlapInput), hardDeadline = fromUtcInput(deadlineInput);
  const start = instant(replacement.validFrom), end = instant(replacement.expiresAt);
  const overlap = instant(overlapUntil), deadline = instant(hardDeadline);
  if (start === null || end === null || overlap === null || deadline === null ||
      start > overlap || overlap > deadline || deadline >= end) return null;
  return {environmentId:replacement.environmentId, replacementCredentialId:replacement.id,
    newValidFrom:replacement.validFrom, newExpiresAt:replacement.expiresAt, overlapUntil, hardDeadline};
}

function validTargets(targets) {
  return Array.isArray(targets) && targets.every(target => rotationGuid(target?.nodeId) && rotationGuid(target?.instanceId)) &&
    new Set(targets.map(target => rotationGuid(target.nodeId))).size === targets.length;
}

export function matchesFormalRotationPreview(value, old, window) {
  return !!value && value.credentialId === old.id && value.applicationId === old.applicationId &&
    value.environmentId === window.environmentId && positiveRevision(value.credentialRevision) && value.credentialRevision === old.revision &&
    typeof value.previewHash === 'string' && /^[a-f0-9]{64}$/.test(value.previewHash) &&
    positiveRevision(value.configVersion) && positiveRevision(value.deploymentSequence) &&
    sameInstant(value.newValidFrom, window.newValidFrom) && sameInstant(value.newExpiresAt, window.newExpiresAt) &&
    sameInstant(value.overlapUntil, window.overlapUntil) && sameInstant(value.hardDeadline, window.hardDeadline) &&
    typeof value.lastWorkingCredentialAtDeadline === 'boolean' && typeof value.lastPublishedCredentialAtDeadline === 'boolean' &&
    validTargets(value.targets) && value.targets.length >= 2;
}

export function matchesFormalRotationReceipt(value, old, rotationId, replacementId, deadline) {
  return !!value && !!rotationGuid(value.rotationId) && rotationGuid(value.rotationId) === value.rotationId &&
    (!rotationId || value.rotationId === rotationId) &&
    value.credentialId === old.id && value.environmentId === old.environmentId &&
    !!rotationGuid(value.replacementCredentialId) && rotationGuid(value.replacementCredentialId) !== old.id &&
    (!replacementId || value.replacementCredentialId === replacementId) &&
    instant(value.hardDeadline) !== null && instant(value.recordedAt) !== null &&
    (!deadline || sameInstant(value.hardDeadline, deadline)) &&
    receiptStates.has(value.status) && positiveRevision(value.revision) &&
    (value.revocationOperationId == null || !!rotationGuid(value.revocationOperationId)) &&
    (value.abortRevocationOperationId == null || !!rotationGuid(value.abortRevocationOperationId)) &&
    (value.status !== 'Scheduled' || value.revocationOperationId == null && value.abortRevocationOperationId == null) &&
    (!['ManualRevocationRecorded','RevocationRecorded'].includes(value.status) || value.revocationOperationId != null) &&
    (!['AbortPending','Aborted'].includes(value.status) || value.revocationOperationId == null && value.abortRevocationOperationId != null);
}

export function formalRotationVerification(value, rotationId) {
  if (!value || value.rotationId !== rotationId || !verificationStates.has(value.state) ||
      (value.configVersion !== null && !positiveRevision(value.configVersion)) ||
      (value.deploymentSequence !== null && !positiveRevision(value.deploymentSequence)) ||
      !validTargets(value.targets) || !value.targets.every(target => target.state === 'Verified' || target.state === 'Missing'))
    return {result:null, complete:false};
  const complete = value.state === 'Verified' && value.configVersion !== null && value.deploymentSequence !== null &&
    value.targets.length >= 2 && value.targets.every(target => target.state === 'Verified');
  return {result:value, complete};
}

export function formalRotationCommand(previous, path, revision, body) {
  const etag = `"${revision}"`;
  const fingerprint = JSON.stringify([path, etag, body]);
  if (previous?.fingerprint === fingerprint) return previous;
  return {fingerprint, etag, body, idempotencyKey:crypto.randomUUID()};
}
