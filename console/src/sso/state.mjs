export function createSsoState(){return {providerId:null,epoch:0,draft:null,loaded:null,etag:null,dirty:false,conflict:false,conflictReviewed:false,authority:null};}
export function beginSsoRequest(state,providerId){return {...createSsoState(),providerId,epoch:state.epoch+1,authority:state.authority};}
export function toSsoDraft(response){return {organizationId:response.organizationId,name:response.name,issuer:response.issuer,clientId:response.clientId,secretRef:response.secretRef,scopesText:response.scopes.join(' '),displayNameClaim:response.claimMapping.displayName||'',emailClaim:response.claimMapping.email||''};}
export function toSsoCommand(draft){return {organizationId:draft.organizationId||null,name:draft.name,issuer:draft.issuer,clientId:draft.clientId,secretRef:draft.secretRef,scopes:[...new Set(draft.scopesText.split(/\s+/).filter(Boolean))],claimMapping:{displayName:draft.displayNameClaim||null,email:draft.emailClaim||null}};}
export function applySsoResponse(state,response,epoch){if(state.epoch!==epoch||state.providerId!==response.id)return state;return {...state,loaded:response,etag:'"'+response.revision+'"',draft:state.dirty?state.draft:toSsoDraft(response),conflictReviewed:state.conflict};}
export function invalidateSsoState(state){return {...createSsoState(),epoch:state.epoch+1};}
export function applySsoFailure(state,status,epoch){if(state.epoch!==epoch)return state;return [401,403,404].includes(status)?invalidateSsoState(state):status===412?{...state,conflict:true,conflictReviewed:false}:state;}
export function ssoAuthorityKey(user){return JSON.stringify([user?.id,user?.permissions?.includes('system.sso.manage')||false]);}
export function canEnableSsoProvider(provider,now=Date.now()){const test=provider?.lastTest,age=test?now-Date.parse(test.testedAt):NaN;return !!test&&!provider.enabled&&test.providerRevision===provider.revision&&test.status==='Passed'&&age>=0&&age<=15*60*1000;}
function canonical(value){return Array.isArray(value)?value.map(canonical):value&&typeof value==='object'?Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])])):value;}
export function ssoSemanticKey(operation,body,etag){return JSON.stringify(canonical({operation,body,etag}));}
export function mayLeaveSso(state,confirmed=false){return !state.dirty||confirmed;}

// Mutation ownership is independent of detail epochs and authority refreshes.
export function createSsoMutationGate(){let current=null;return {get busy(){return current!==null;},begin(epoch){if(current)return null;return current={epoch};},finish(token,epoch){if(token!==current)return false;current=null;return true;},reset(){current=null;}};}
export const SSO_SECRET_REFERENCE_PATTERN='file://sso/[A-Za-z][A-Za-z0-9_.-]{0,127}';
export const SSO_PROVIDER_NAME_MAX=128;
