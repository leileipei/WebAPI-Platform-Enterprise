export interface RotationAck {configVersion:string;deploymentSequence:string;payloadHash:string;nodeIds:string[]}
export interface RotationState {oldCredentialId:string|null;newCredentialId:string|null;environmentId:string|null;firstReleaseId:string|null;secondReleaseId:string|null;firstAck:RotationAck|null;secondAck:RotationAck|null;consumerConfirmed:boolean;usageState:string|null;oldRevoked:boolean}
export function rotationStep(state:RotationState|null|undefined):'choose'|'create'|'publish'|'consumer'|'observe'|'revoke'|'republish'|'done';
export function canAdvanceRotation(state:RotationState|null|undefined):boolean;
export function maskAccessKey(value:string|null|undefined):string;
