import type {Scope, ScopeTree} from '../api/types';

export type RotationApplication = {id:string; organizationId:string; projectId?:string|null};
export type RotationCredential = {id:string; applicationId:string; environmentId?:string|null; bindingMode?:string|null;
  accessKey:string; status:string; revision:number; validFrom:string; expiresAt:string};
export type RotationWindow = {environmentId:string; replacementCredentialId:string; newValidFrom:string; newExpiresAt:string;
  overlapUntil:string; hardDeadline:string};
export type RotationReason = 'credential_retired'|'owner_request'|'policy_change';
export type RotationTarget = {nodeId:string; instanceId:string};
export type RotationPreview = Omit<RotationWindow,'replacementCredentialId'> & {credentialId:string; applicationId:string;
  credentialRevision:number; maskedAccessKey:string; previewHash:string; configVersion:number; deploymentSequence:number;
  lastWorkingCredentialAtDeadline:boolean; lastPublishedCredentialAtDeadline:boolean; targets:RotationTarget[]};
export type RotationStatus = 'Scheduled'|'ManualRevocationRecorded'|'AbortPending'|'Aborted'|'RevocationRecorded';
export type RotationReceipt = {rotationId:string; credentialId:string; environmentId:string; replacementCredentialId:string;
  hardDeadline:string; recordedAt:string; status:RotationStatus; revision:number; replayed:boolean;
  revocationOperationId?:string|null; abortRevocationOperationId?:string|null};
export type RotationVerification = {rotationId:string; state:'Verified'|'AwaitingProof'|'Unavailable'|'Closed';
  configVersion:number|null; deploymentSequence:number|null; targets:(RotationTarget & {state:'Verified'|'Missing'})[]};
export type RotationCommand = {fingerprint:string; etag:string; body:unknown; idempotencyKey:string};

export function rotationGuid(value:unknown):string|null;
export function formalRotationScope(application:RotationApplication, credential:RotationCredential|undefined, tree:ScopeTree,
  can:(code:string, scope:Scope, write:boolean)=>boolean):Scope|null;
export function formalRotationCandidates(old:RotationCredential, credentials:RotationCredential[]):RotationCredential[];
export function formalRotationWindow(replacement:RotationCredential|undefined, overlapInput:string, deadlineInput:string):RotationWindow|null;
export function matchesFormalRotationPreview(value:unknown, old:RotationCredential, window:RotationWindow):value is RotationPreview;
export function matchesFormalRotationReceipt(value:unknown, old:RotationCredential, rotationId?:string|null,
  replacementId?:string|null, deadline?:string|null):value is RotationReceipt;
export function formalRotationVerification(value:unknown, rotationId:string):{result:RotationVerification|null; complete:boolean};
export function formalRotationCommand(previous:RotationCommand|undefined, path:string, revision:number, body:unknown):RotationCommand;
