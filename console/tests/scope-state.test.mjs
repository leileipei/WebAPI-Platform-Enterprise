import {test} from 'node:test';
import assert from 'node:assert/strict';
import {access} from 'node:fs/promises';
let m={};
try{await access(new URL('../src/governance/scope-state.mjs',import.meta.url));m=await import('../src/governance/scope-state.mjs');}catch(e){if(e.code!=='ENOENT')throw e;}
const tree={organizations:[{id:'o',name:'组织'}],projects:[{id:'p',organizationId:'o',name:'项目'},{id:'p2',organizationId:'o2',name:'另一项目'}],environments:[{id:'e',projectId:'p',name:'环境'}]};
const grant=(organizationId='o',projectId='p',environmentId='e',accessMode='read')=>({scope:{organizationId,projectId,environmentId},accessMode});
function ready(){assert.equal(typeof m.toDraft,'function','structured scope model is implemented');}
test('existing grants round-trip losslessly, including overlapping access modes and invisible scopes',()=>{
 ready();const grants=[grant(),grant('o',null,null,'read_write'),grant('hidden','hidden-p',null),grant('o','p','e','read_write')];
 const draft=m.toDraft(grants);assert.deepEqual(m.payload(draft).scopes,grants);assert.deepEqual(m.validate(draft,tree),[]);
 assert.deepEqual(m.validate(draft,{organizations:[],projects:[],environments:[]}),[]);
});
test('new scopes start with a single environment and read access; incomplete selection cannot become organization-wide',()=>{
 ready();let row=m.newRule('x');assert.equal(row.level,'environment');assert.equal(row.accessMode,'read');
 row=m.updateRule(row,'organizationId','o');assert.equal(row.level,'environment');assert.ok(m.validate([row],tree).length);
 row=m.updateRule(row,'projectId','p');assert.ok(m.validate([row],tree).length);
 row=m.updateRule(row,'environmentId','e');assert.deepEqual(m.validate([row],tree),[]);
});
test('changing a parent clears descendants but retains the intended level until the user selects replacements',()=>{
 ready();const row=m.toDraft([grant()])[0];const changed=m.updateRule(row,'organizationId','o2');
 assert.equal(changed.level,'environment');assert.equal(changed.scope.projectId,null);assert.equal(changed.scope.environmentId,null);
 assert.ok(m.validate([changed],tree).length);assert.deepEqual(row.scope,grant().scope);
});
test('only an explicit level change creates organization or project-wide grants',()=>{
 ready();const row=m.toDraft([grant()])[0];
 assert.deepEqual(m.payload([m.updateRule(row,'level','organization')]).scopes,[grant('o',null,null)]);
 assert.deepEqual(m.payload([m.updateRule(row,'level','project')]).scopes,[grant('o','p',null)]);
});
test('known hierarchy mismatches and arbitrary invisible replacements are rejected while inherited IDs survive other edits',()=>{
 ready();const row=m.toDraft([grant()])[0];assert.ok(m.validate([m.updateRule(row,'projectId','p2')],tree).length);
 const hidden=m.toDraft([grant('hidden','hidden-p',null)])[0];assert.deepEqual(m.validate([hidden,row],tree),[]);
 assert.ok(m.validate([m.updateRule(hidden,'projectId','forged')],tree).length);
 assert.ok(m.validate([{...row,accessMode:'admin'}],tree).length);
});
test('summary counts mode changes separately and represents empty scopes as revocation, without collapsing overlaps',()=>{
 ready();const before=[grant(),grant('o',null,null,'read_write')];
 const after=[grant('o','p','e','read_write'),grant('o','p',null)];
 assert.deepEqual(m.summarize(before,after),{added:1,removed:1,changed:1});
 assert.deepEqual(m.summarize(before,[]),{added:0,removed:2,changed:0});assert.deepEqual(m.payload([]),{scopes:[]});
});
test('stable reads reject revision drift instead of pairing stale scopes with a newer writable revision',async()=>{
 ready();let calls=0;
 await assert.rejects(m.loadScopeSnapshot(async()=>({revision:++calls}),async()=>[grant()]),/修订/);
 const stable=await m.loadScopeSnapshot(async()=>({revision:7,displayName:'用户'}),async()=>[grant()]);
 assert.equal(stable.revision,7);assert.deepEqual(stable.grants,[grant()]);
});
test('exact duplicate grants cannot overstate saved counts; overlapping scopes with different access modes remain valid',()=>{
 ready();assert.ok(m.validate(m.toDraft([grant(),grant()]),tree).some(e=>e.includes('重复')));
 assert.deepEqual(m.validate(m.toDraft([grant(),grant('o','p','e','read_write')]),tree),[]);
});
