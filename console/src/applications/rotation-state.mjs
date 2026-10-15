const present=value=>typeof value==='string'&&value.length>0;
const positive=value=>typeof value==='string'&&/^[1-9][0-9]*$/.test(value);
const usageStates=new Set(['Available','NoData','Partial','Unknown','Unavailable']);
function acknowledged(ack){
 return ack&&positive(ack.configVersion)&&positive(ack.deploymentSequence)&&typeof ack.payloadHash==='string'&&/^[a-f0-9]{64}$/.test(ack.payloadHash)
  &&Array.isArray(ack.nodeIds)&&ack.nodeIds.length>=2&&ack.nodeIds.every(present)&&new Set(ack.nodeIds).size===ack.nodeIds.length;
}
function greater(a,b){return a.length>b.length||a.length===b.length&&a>b;}
// ACK aggregates must be constructed from freshly authorized server responses for the selected environment.
// This presentation state never authorizes revocation: the server revalidates C1 impact confirmation and If-Match.
export function rotationStep(state){
 const s=state??{};
 if(!present(s.oldCredentialId)||!present(s.environmentId))return 'choose';
 if(!present(s.newCredentialId)||s.newCredentialId===s.oldCredentialId)return 'create';
 if(!present(s.firstReleaseId)||!acknowledged(s.firstAck))return 'publish';
 if(s.consumerConfirmed!==true)return 'consumer';
 if(!usageStates.has(s.usageState))return 'observe';
 if(s.oldRevoked!==true)return 'revoke';
 if(!present(s.secondReleaseId)||s.secondReleaseId===s.firstReleaseId||!acknowledged(s.secondAck)
   ||!greater(s.secondAck.deploymentSequence,s.firstAck.deploymentSequence)||!greater(s.secondAck.configVersion,s.firstAck.configVersion))return 'republish';
 return 'done';
}
// `revoke` means ready to open the explicit risk confirmation, never a claim that the old key is unused.
export function canAdvanceRotation(state){return ['revoke','done'].includes(rotationStep(state));}
export function maskAccessKey(value){return present(value)?`••••${value.slice(-4)}`:'—';}
