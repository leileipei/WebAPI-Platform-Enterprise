import * as importState from '../src/contracts/import-state.mjs';
import test from 'node:test';import assert from 'node:assert/strict';import {importIdentity,invalidatePreview,canCommitPreview,mapImportTarget,previewRequest,readImportFiles,recoverImportFailure} from '../src/contracts/import-state.mjs';
const input={mode:'text',text:'openapi: 3.1.0',url:'https://allowed.invalid/openapi.yaml',format:'auto',files:[],projectId:'p',environmentId:'e',clusterId:'c',authority:'user',policyRevision:1};
const state=()=>({input,identity:importIdentity(input),preview:{previewId:'preview',bundleHash:'h',sourcePolicyRevision:1,expiresAt:'2026-10-07T10:20:00Z',operations:[{operationId:'ok',suggestedCode:'OK',summary:'Okay',supported:true},{operationId:'bad',supported:false}]},targets:[{operationId:'ok',newApiCode:'OK',newApiName:'Okay',version:'1'}],authorized:true});
test('file/text/url requests are exclusive and source identity includes scope, content and permissions',()=>{
 const text=previewRequest(input);assert.equal(text.sourceText,input.text);assert.equal(text.sourceUrl,undefined);
 const url=previewRequest({...input,mode:'url',files:[{name:'extra.json',content:'{}'}]});assert.equal(url.sourceUrl,input.url);assert.equal(url.sourceText,undefined);assert.equal(url.files,undefined);
 const file=previewRequest({...input,mode:'file',text:'{}',files:[{name:'schemas/type.json',content:'false'}]});assert.equal(file.files[0].content,'false');assert.equal(file.sourceUrl,undefined);
 for(const key of ['projectId','environmentId','clusterId','text','format','authority','policyRevision'])assert.notEqual(importIdentity(input),importIdentity({...input,[key]:'changed'}),key);
});
test('scope/source/format changes invalidate preview and mappings while retaining input',()=>{
 const next={...input,text:'broken YAML:'};const invalid=invalidatePreview(state(),next);assert.equal(invalid.preview,undefined);assert.deepEqual(invalid.targets,[]);assert.equal(invalid.input.text,next.text);assert.equal(invalidatePreview(state(),input).preview.previewId,'preview');
});
test('expired preview cannot commit and input is retained; unsupported operations cannot map',()=>{
 assert.equal(canCommitPreview(state(),Date.parse('2026-10-07T10:19:59Z')),true);assert.equal(canCommitPreview(state(),Date.parse('2026-10-07T10:20:00Z')),false);
 assert.equal(canCommitPreview({...state(),targets:[{operationId:'bad'}]},Date.parse('2026-10-07T10:19:00Z')),false);assert.equal(canCommitPreview({...state(),authorized:false},0),false);assert.equal(canCommitPreview({...state(),busy:true},0),false);
 assert.throws(()=>mapImportTarget({operationId:'bad',supported:false},{}),/不可导入/);
});
test('mapping clears new API fields for an existing API and validates revisions and ownership fields',()=>{
 const apiId='11111111-1111-4111-8111-111111111111',versionId='22222222-2222-4222-8222-222222222222';
 const mapped=mapImportTarget({operationId:'ok',supported:true},{apiId,newApiCode:'ignore',newApiName:'ignore',existingVersionId:versionId,expectedRevision:3,version:'2'});assert.equal(mapped.newApiCode,null);assert.equal(mapped.newApiName,null);assert.equal(mapped.apiId,null);assert.equal(mapped.expectedRevision,3);
 assert.equal(mapImportTarget({operationId:'ok',supported:true},{existingVersionId:versionId,expectedRevision:1,version:'1'}).apiId,null);assert.throws(()=>mapImportTarget({operationId:'ok',supported:true},{apiId,existingVersionId:versionId,version:'1',expectedRevision:0}),/修订/);
});
test('bounded files reject zip, unsafe logical names, duplicates and oversized content before reading',async()=>{
 const file=(name,content,size=new TextEncoder().encode(content).length)=>({name,size,text:async()=>content});assert.equal((await readImportFiles([file('types/a.yaml','type: string')]))[0].name,'types/a.yaml');
 for(const files of [[file('../a.json','{}')],[file('a.zip','{}')],[file('same.json','{}'),file('same.json','false')],[{name:'huge.json',size:3*1024*1024,text(){throw Error('must not read');}}]])await assert.rejects(readImportFiles(files));
});
test('403/412 and policy-change recovery preserve source and provide the correct re-preview boundary',()=>{
 const denied=recoverImportFailure(state(),{status:403});assert.equal(denied.preview,undefined);assert.equal(denied.input.text,input.text);assert.equal(denied.authorized,false);
 const stale=recoverImportFailure(state(),{status:412});assert.equal(stale.preview.previewId,'preview');assert.equal(stale.targets.length,1);assert.match(stale.recovery,/修订/);
 const policy=recoverImportFailure(state(),{status:409,code:'import_preview_changed'});assert.equal(policy.preview,undefined);assert.equal(policy.targets.length,0);assert.match(policy.recovery,/重新预览/);
});

test('a late preview cannot restore stale scope or overwrite a newer preview',()=>{
 const preview=state().preview;const pending={...state(),requestSequence:2};assert.equal(importState.acceptImportPreview(pending,input,preview,1),pending);
 const changed={...pending,input:{...input,text:'changed'}};assert.equal(importState.acceptImportPreview(changed,input,preview,2),changed);
 const accepted=importState.acceptImportPreview({...pending,preview:undefined,targets:[]},input,preview,2);assert.equal(accepted.preview.previewId,'preview');assert.equal(accepted.targets.length,1);assert.equal(accepted.targets[0].operationId,'ok');
});
