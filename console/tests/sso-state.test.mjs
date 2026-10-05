import test from 'node:test';import assert from 'node:assert/strict';
import {createSsoState,beginSsoRequest,applySsoResponse,applySsoFailure,invalidateSsoState,ssoAuthorityKey,canEnableSsoProvider,ssoSemanticKey,mayLeaveSso,createSsoMutationGate,SSO_SECRET_REFERENCE_PATTERN,SSO_PROVIDER_NAME_MAX} from '../src/sso/state.mjs';
const response={id:'provider-a',organizationId:null,name:'企业身份源',issuer:'https://id.example.test',clientId:'console',secretRef:'file://sso/enterprise',scopes:['openid','profile','email'],claimMapping:{displayName:'name',email:'email'},revision:1,authRevision:1,enabled:false,isDefault:false,lastTest:null};
test('late result cannot replace another provider',()=>{const first=beginSsoRequest(createSsoState(),response.id);const second=beginSsoRequest(first,'provider-b');assert.deepEqual(applySsoResponse(second,response,first.epoch),second);assert.deepEqual(applySsoResponse(second,response,second.epoch),second);});
test('revision conflict retains draft and requires explicit review',()=>{const initial=beginSsoRequest(createSsoState(),response.id);const loaded=applySsoResponse(initial,response,initial.epoch);const draft={...loaded,dirty:true,draft:{...loaded.draft,secretRef:'file://sso/draft'}};const conflict=applySsoFailure(draft,412,draft.epoch);assert.equal(conflict.draft.secretRef,'file://sso/draft');assert.equal(conflict.conflict,true);const latest=applySsoResponse(conflict,{...response,revision:2},conflict.epoch);assert.equal(latest.draft.secretRef,'file://sso/draft');assert.equal(latest.conflictReviewed,true);assert.equal(latest.conflict,true);assert.equal(latest.etag,'"2"');});
test('revoked authority clears references and late responses',()=>{const request=beginSsoRequest(createSsoState(),response.id);const loaded=applySsoResponse(request,response,request.epoch);for(const status of [401,403]){const invalid=applySsoFailure({...loaded,dirty:true},status,loaded.epoch);assert.equal(invalid.draft,null);assert.equal(invalid.loaded,null);assert.equal(invalid.etag,null);assert.deepEqual(applySsoResponse(invalid,response,loaded.epoch),invalid);}assert.equal(invalidateSsoState(loaded).providerId,null);});
test('unrelated business scopes do not remount platform identity but SSO authority changes do',()=>{const before={id:'actor',permissions:['system.sso.manage'],scopes:[]};assert.equal(ssoAuthorityKey(before),ssoAuthorityKey({...before,scopes:[{organizationId:'other'}],permissions:[...before.permissions,'api.read']}));assert.notEqual(ssoAuthorityKey(before),ssoAuthorityKey({...before,permissions:[]}));assert.notEqual(ssoAuthorityKey(before),ssoAuthorityKey({...before,id:'other'}));});
test('historical failed expired and future tests cannot enable a source',()=>{const now=Date.now();const tested={...response,lastTest:{providerRevision:1,status:'Passed',testedAt:new Date(now).toISOString()}};assert.equal(canEnableSsoProvider(tested,now),true);assert.equal(canEnableSsoProvider({...tested,revision:2},now),false);assert.equal(canEnableSsoProvider({...tested,lastTest:{...tested.lastTest,status:'Failed'}},now),false);assert.equal(canEnableSsoProvider(tested,now+16*60*1000),false);assert.equal(canEnableSsoProvider(tested,now-1000),false);});
test('retry semantics separate operations and preserve equivalent request ordering',()=>{assert.equal(ssoSemanticKey('save',{name:'A',issuer:'X'},'"1"'),ssoSemanticKey('save',{issuer:'X',name:'A'},'"1"'));assert.notEqual(ssoSemanticKey('test',{id:'A'},'"1"'),ssoSemanticKey('enable',{id:'A'},'"1"'));assert.notEqual(ssoSemanticKey('save',{name:'A'},'"1"'),ssoSemanticKey('save',{name:'B'},'"1"'));});
test('dirty navigation needs explicit discard decision',()=>{assert.equal(mayLeaveSso({dirty:true},false),false);assert.equal(mayLeaveSso({dirty:true},true),true);assert.equal(mayLeaveSso({dirty:false},false),true);});

import fs from 'node:fs';
test('provider native form accepts the same names and secret aliases as the API',()=>{
 const component=fs.readFileSync(new URL('../src/pages/SsoProviders.tsx',import.meta.url),'utf8');
 const pattern=component.match(/pattern="([^"]+)"/)?.[1]||component.match(/pattern=\{SSO_SECRET_REFERENCE_PATTERN\}/)&&SSO_SECRET_REFERENCE_PATTERN;
 const nameLimit=component.match(/maxLength=\{(\d+)\}/)?.[1]||component.match(/maxLength=\{SSO_PROVIDER_NAME_MAX\}/)&&SSO_PROVIDER_NAME_MAX;
 const accepted=new RegExp('^(?:'+pattern+')$','u');assert.equal(accepted.test('file://sso/team.prod-2'),true);assert.equal(accepted.test('file://sso/'+'A'.repeat(128)),true);
 for(const alias of ['file://sso/2bad','file://sso/.hidden','file://sso/'+'A'.repeat(129),'file://sso/team/secret'])assert.equal(accepted.test(alias),false);
 assert.equal(Number(nameLimit),128);
});

test('a completed mutation releases its own busy gate after detail epoch advances',async()=>{
 const gate=createSsoMutationGate();let detailEpoch=1;const token=gate.begin(detailEpoch);assert.equal(gate.busy,true);assert.equal(gate.begin(detailEpoch),null);
 let release;const operation=new Promise(resolve=>release=resolve);const finished=operation.finally(()=>gate.finish(token,detailEpoch));
 detailEpoch++;release();await finished;assert.equal(gate.busy,false);
 // Clearing authority invalidates the old owner, which must not unlock a later command.
 const old=gate.begin(detailEpoch);gate.reset();const newer=gate.begin(++detailEpoch);assert.equal(gate.finish(old,detailEpoch),false);assert.equal(gate.busy,true);assert.equal(gate.finish(newer,detailEpoch),true);
});
