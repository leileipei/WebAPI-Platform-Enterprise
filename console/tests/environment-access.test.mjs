import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {normalizeAccessSettings,buildAddressTemplate,buildAddressExample} from '../src/environments/access-address.mjs';
import {createAccessState,accessReducer,mayCopyAddress,releaseEntrySummary} from '../src/environments/access-state.mjs';
const vectors=JSON.parse(fs.readFileSync(new URL('../../tests/fixtures/environment-access-addresses.json',import.meta.url),'utf8'));
for(const vector of vectors)test('shared server address vector '+vector.origin+vector.route,()=>{
 const settings={gatewayPublicUrl:vector.origin,basePath:vector.prefix};const normalized=normalizeAccessSettings(settings);
 assert.equal(normalized.gatewayPublicUrl,vector.normalizedOrigin);assert.equal(normalized.basePath,vector.normalizedPrefix);assert.equal(buildAddressTemplate(settings,vector.route),vector.template);
});
test('unsafe origins and prefixes are never displayed as executable previews',()=>{
 for(const origin of ['https://user:pass@api.test','https://api.test/path','https://api.test?','https://api.test#','https://api.test\\evil','javascript:bad','https://api.test:70000','https://api.test/%2e%2e'])assert.throws(()=>normalizeAccessSettings({gatewayPublicUrl:origin,basePath:'/'}));
 for(const basePath of ['/a//b','/a/../b','/%61','/a?b','a','/a\\b'])assert.throws(()=>normalizeAccessSettings({gatewayPublicUrl:'https://api.test',basePath}));
 assert.throws(()=>normalizeAccessSettings({gatewayPublicUrl:'http://api.test'},true));
 assert.equal(buildAddressTemplate({gatewayPublicUrl:'',basePath:'/'},'/orders'),null);
});
test('examples encode each path value and leave missing and catch-all parameters as templates',()=>{
 const settings={gatewayPublicUrl:'https://api.test',basePath:'/gateway'};
 assert.equal(buildAddressExample(settings,'/orders/{id}',{id:'a/b'}),'https://api.test/gateway/orders/a%2Fb');
 assert.equal(buildAddressExample(settings,'/orders/{id}',{}),'https://api.test/gateway/orders/{id}');
 assert.equal(buildAddressExample(settings,'/files/{**rest}',{rest:'a/b'}),'https://api.test/gateway/files/{**rest}');
});
test('revoked permissions immediately clear drafts and internal addresses',()=>{
 let state=createAccessState('env-a',true);state=accessReducer(state,{type:'loaded',identity:'env-a',epoch:state.epoch,detail:{id:'env-a',gatewayInternalUrl:'http://private.test',gatewayPublicUrl:'https://api.test'}});
 assert.equal(state.internalUrl,'http://private.test');state=accessReducer(state,{type:'context',identity:'env-a-read',authorized:false});assert.equal(state.internalUrl,undefined);assert.equal(state.detail,undefined);assert.equal(state.draft,undefined);
});
test('late responses from another environment or old authority are ignored',()=>{
 let state=createAccessState('env-a',true);const oldEpoch=state.epoch;state=accessReducer(state,{type:'context',identity:'env-b',authorized:true});
 const next=accessReducer(state,{type:'loaded',identity:'env-a',epoch:oldEpoch,detail:{gatewayInternalUrl:'http://private.test'}});assert.deepEqual(next,state);
 const invalidated=accessReducer(state,{type:'invalidate'});assert.deepEqual(accessReducer(invalidated,{type:'loaded',identity:'env-b',epoch:state.epoch,detail:{gatewayInternalUrl:'http://private.test'}}),invalidated);
});
test('412 preserves the draft for explicit merge and restricted data is cleared on denied',()=>{
 let state=createAccessState('env-a',true);state=accessReducer(state,{type:'loaded',identity:'env-a',epoch:0,detail:{gatewayPublicUrl:'https://api.test',gatewayInternalUrl:'http://private.test'}});
 state=accessReducer(state,{type:'edit',key:'gatewayPublicUrl',value:'https://draft.test'});const conflicted=accessReducer(state,{type:'failed',status:412,message:'修订冲突'});assert.equal(conflicted.draft.gatewayPublicUrl,'https://draft.test');assert.equal(conflicted.conflict,true);
 const denied=accessReducer(conflicted,{type:'failed',status:403});assert.equal(denied.draft,undefined);assert.equal(denied.internalUrl,undefined);
});
test('copy requires a configured confirmed result while path templates never become links',()=>{
 assert.equal(mayCopyAddress({configured:false,routes:[]},null),false);assert.equal(mayCopyAddress({configured:true,view:'Running'},'https://api.test/orders/{id}'),true);
 assert.equal(mayCopyAddress({configured:true,view:'Running',error:'running_state_unconfirmed'},'https://api.test/orders'),false);
});
test('release entry labels keep immutable history separate from current metadata',()=>{
 assert.deepEqual(releaseEntrySummary({accessContext:null,currentPublicOrigin:'https://now.test'}),{recorded:'历史未记录',current:'https://now.test',changed:false});
 assert.deepEqual(releaseEntrySummary({accessContext:{publicOrigin:'https://old.test',basePath:'/gateway'},currentPublicOrigin:'https://now.test',accessAddressChanged:true}),{recorded:'https://old.test/gateway',current:'https://now.test',changed:true});
});
test('curl example quotes route literals without turning them into shell commands',async()=>{
 const {buildCurlExample}=await import('../src/environments/access-address.mjs');assert.equal(buildCurlExample('GET',"https://api.test/a'b"),"curl --request GET 'https://api.test/a'\\''b'");assert.throws(()=>buildCurlExample('GET;echo bad','https://api.test/orders'));
});

test('shared control character prefix rejection vectors',()=>{
 const values=JSON.parse(fs.readFileSync(new URL('../../tests/fixtures/environment-access-prefix-rejections.json',import.meta.url),'utf8'));
 for(const basePath of values)assert.throws(()=>normalizeAccessSettings({gatewayPublicUrl:'https://api.test',basePath}));
});
test('current and historical entry use the same prefix rule including prefix-only changes',()=>{
 const same={accessContext:{publicOrigin:'https://api.test',basePath:'/gateway'},currentPublicOrigin:'https://api.test',currentBasePath:'/gateway',accessAddressChanged:false};
 assert.deepEqual(releaseEntrySummary(same),{recorded:'https://api.test/gateway',current:'https://api.test/gateway',changed:false});
 assert.deepEqual(releaseEntrySummary({...same,currentBasePath:'/new-entry',accessAddressChanged:true}),{recorded:'https://api.test/gateway',current:'https://api.test/new-entry',changed:true});
});
