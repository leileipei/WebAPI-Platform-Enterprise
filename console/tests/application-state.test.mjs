import {test} from 'node:test';import assert from 'node:assert/strict';
import {usageCountText,visibleCredentialLastUsed} from '../src/applications/application-state.mjs';
test('credential count stays a decimal string beyond JavaScript and Int64 limits',()=>{
 for(const value of ['1','9007199254740993','9223372036854775808','1234567890123456789012345678901234567890'])assert.equal(usageCountText(value,'Available'),value);
});
test('partial count is a lower bound including an observed zero',()=>{for(const count of ['0','7','9007199254740993']){assert.match(usageCountText(count,'Partial'),/已采集|至少/);assert.ok(usageCountText(count,'Partial').includes(count));}});
test('only proven NoData zero is presented as zero',()=>{assert.equal(usageCountText('0','NoData'),'0');assert.notEqual(usageCountText(null,'NoData'),'0');assert.notEqual(usageCountText('7','NoData'),'0');});
test('unknown unavailable and unrecognized source states never expose a fake zero',()=>{
 for(const state of ['Unknown','Unavailable','not-a-state'])for(const count of [null,'0','99']){const result=usageCountText(count,state);assert.notEqual(result,'0');assert.match(result,/未知|不可用|尚无|不完整/);assert.ok(!result.includes('99'));}
});
test('missing partial or available counts retain uncertainty',()=>{for(const state of ['Partial','Available'])assert.match(usageCountText(null,state),/尚无|不完整/);});
test('count presentation never coerces numbers floats signs or leading zeros',()=>{for(const value of [0,42,9007199254740992,'01','-1','+1','1e3','1.0','NaN','Infinity','',' 1'])assert.match(usageCountText(value,'Available'),/不可用|不合法/);});
test('visible scoped LastUsed never falls back to a global credential field',()=>{
 const old='2026-10-14T00:00:00Z',visible='2026-10-15T01:00:00Z';
 assert.equal(visibleCredentialLastUsed({lastUsedAt:old,visibleLastUsedAt:null}),null);
 assert.equal(visibleCredentialLastUsed({lastUsedAt:old}),null);
 assert.equal(visibleCredentialLastUsed({lastUsedAt:old,visibleLastUsedAt:visible}),visible);
 assert.equal(visibleCredentialLastUsed({visibleLastUsedAt:'not-a-date'}),null);
});

test('credential authorship uses matching trusted audit fields and never owner inference', async()=>{
 const {credentialActors}=await import('../src/applications/application-state.mjs');assert.equal(typeof credentialActors,'function');
 const create={action:'credential.create',userId:'creator',afterJson:JSON.stringify([{Type:'ApplicationCredential',Fields:{Id:'key',ApplicationId:'app',Status:'Active'}}])};
 const revoke={action:'credential.update',userId:'revoker',beforeJson:create.afterJson,afterJson:JSON.stringify([{Type:'ApplicationCredential',Fields:{Id:'key',ApplicationId:'app',Status:'Revoked'}}])};
 assert.deepEqual(credentialActors('app','key',[revoke,create]),{createdBy:'creator',revokedBy:'revoker'});
 assert.deepEqual(credentialActors('app','other',[revoke,create]),{createdBy:null,revokedBy:null});
 assert.deepEqual(credentialActors('foreign','key',[revoke,create]),{createdBy:null,revokedBy:null});
});
test('missing malformed or anonymous audit actor remains unknown', async()=>{
 const {credentialActors}=await import('../src/applications/application-state.mjs');assert.equal(typeof credentialActors,'function');
 assert.deepEqual(credentialActors('app','key',[{action:'credential.create',userId:null,afterJson:'[]'},{action:'credential.update',userId:'someone',afterJson:'{broken'}]),{createdBy:null,revokedBy:null});
});
