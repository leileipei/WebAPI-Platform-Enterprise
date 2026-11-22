export function usageCountText(count:string|null|undefined,state:string):string;
export function visibleCredentialLastUsed(row:{visibleLastUsedAt?:string|null;lastUsedAt?:string|null}|null|undefined):string|null;
export function credentialInstantTicks(value:unknown):bigint|null;
export function credentialWindowState(credential:{status?:string;validFrom?:string|null;expiresAt?:string|null},nowMs:number):
  {state:'Pending'|'Active'|'Expired'|'Revoked'|'Unknown';nextChangeAt:number|null};
export function credentialActors(applicationId:string,credentialId:string,audits:unknown):{createdBy:string|null;revokedBy:string|null};
