import * as stateModule from '../src/contracts/schema-state.mjs';
import test from 'node:test';import assert from 'node:assert/strict';
import {applySchemaPatch,buildSchemaNodes,validationMatches,schemaDraftState,parseSchemaDocument,serializeSchemaDocument,exampleOptions} from '../src/contracts/schema-state.mjs';
test('renaming escaped properties updates the same parent required and preserves unknown constraints',()=>{
 const doc={type:'object',required:['a/b~c'],properties:{'a/b~c':{type:'string','x-vendor':{keep:true}},other:{type:'integer'}},'x-root':[false,0]};
 const next=applySchemaPatch(doc,{op:'rename',pointer:'/properties/a~1b~0c',newName:'new/name'});assert.deepEqual(next.required,['new/name']);assert.equal(next.properties['new/name']['x-vendor'].keep,true);assert.deepEqual(next['x-root'],[false,0]);assert.ok(doc.properties['a/b~c']);
 assert.throws(()=>applySchemaPatch(doc,{op:'rename',pointer:'/properties/a~1b~0c',newName:'other'}),/已存在/);
});
test('removing a property updates only its own parent required and array branches use exact indices',()=>{
 const doc={required:['same'],properties:{same:{type:'string'},nested:{required:['same'],properties:{same:false}}},allOf:[true,false]};
 const next=applySchemaPatch(doc,{op:'remove',pointer:'/properties/nested/properties/same'});assert.deepEqual(next.required,['same']);assert.deepEqual(next.properties.nested.required,[]);assert.deepEqual(applySchemaPatch(next,{op:'replace',pointer:'/allOf/1',value:true}).allOf,[true,true]);
 assert.throws(()=>applySchemaPatch(doc,{op:'remove',pointer:'/allOf/01'}));
});
test('patches do not cross prototype or missing paths and boolean schemas remain real values',()=>{
 assert.equal(applySchemaPatch(true,{op:'replace',pointer:'',value:false}),false);const doc={properties:{}};
 const next=applySchemaPatch(doc,{op:'add',pointer:'/properties/__proto__',value:{type:'integer'}});assert.ok(Object.hasOwn(next.properties,'__proto__'));assert.equal({}.type,undefined);assert.throws(()=>applySchemaPatch(doc,{op:'replace',pointer:'/missing/type',value:'string'}));
});
test('bounded trees include boolean, composition and reference entries without expanding recursive refs',()=>{
 const {nodes,truncated}=buildSchemaNodes({$defs:{Loop:{$ref:'#/$defs/Loop'}},allOf:[false,{properties:{'a/b':true}}]},{budget:500});assert.equal(truncated,false);assert.ok(nodes.some(x=>x.pointer==='/allOf/0'&&x.kind==='boolean'));assert.ok(nodes.some(x=>x.pointer==='/allOf/1/properties/a~1b'));assert.ok(nodes.some(x=>x.reference==='#/$defs/Loop'));
 const wide={properties:Object.fromEntries(Array.from({length:600},(_,i)=>['p'+i,{type:'string'}]))};const small=buildSchemaNodes(wide,{budget:500});assert.equal(small.nodes.length,500);assert.equal(small.truncated,true);
});
test('DirtyRawTextSurvivesDefinitionSwitchAttempt',()=>{
 const state={definitionId:'old',raw:'{"type":',dirty:true};const next=schemaDraftState(state,{type:'switch',definitionId:'new',raw:'false'});assert.equal(next.definitionId,'old');assert.equal(next.raw,'{"type":');assert.equal(next.blocked,true);assert.equal(next.pending.definitionId,'new');
 const discarded=schemaDraftState(next,{type:'discard-switch'});assert.equal(discarded.raw,'false');assert.equal(discarded.definitionId,'new');
});
test('LateValidationCannotOverwriteNewDraft or another revision, format, direction or request',()=>{
 const identity={versionId:'v',definitionId:'d',revision:2,schemaHash:'s',exampleHash:'e',formatMode:'Strict',direction:'request',sequence:3};const response={identity,evaluatedVersionRevision:2,result:{formatMode:'Strict'}};assert.equal(validationMatches(response,identity),true);
 for(const [key,value] of Object.entries({versionId:'other',definitionId:'other',revision:3,schemaHash:'changed',exampleHash:'changed',formatMode:'Annotation',direction:'response',sequence:4}))assert.equal(validationMatches(response,{...identity,[key]:value}),false,key);
 assert.equal(validationMatches({...response,evaluatedVersionRevision:1},identity),false);
});
test('FalsyInstancesAreShownAndValidated and external examples do not fetch or disappear',()=>{
 const options=exampleOptions({exampleJson:'false',examples:[{name:'zero',value:0},{name:'empty',value:''},{name:'null',value:null},{name:'remote',externalValue:'https://external.invalid/example'}]});assert.deepEqual(options.map(x=>x.json),['false','0','""','null',undefined]);assert.equal(options[4].unverified,true);
});
test('schema patches preserve large and tiny numeric literals while rejecting duplicate keys',()=>{
 const doc=parseSchemaDocument('{"enum":[9007199254740993,1e-400],"properties":{"old":{"type":"string"}},"required":["old"]}');const next=applySchemaPatch(doc,{op:'rename',pointer:'/properties/old',newName:'new'});const text=serializeSchemaDocument(next);assert.match(text,/9007199254740993/);assert.match(text,/1e-400/);assert.match(text,/"new"/);
 assert.throws(()=>parseSchemaDocument('{"type":"string","type":"integer"}'),/重复/);assert.throws(()=>parseSchemaDocument('{"type":'),/JSON/);
});
test('tree summaries expose constraints and exact numeric bounds without dropping extensions',()=>{
 const doc=parseSchemaDocument('{"type":"integer","minimum":9007199254740993,"readOnly":true,"x-vendor":{"custom":1}}');const node=buildSchemaNodes(doc).nodes[0];assert.ok(node.constraints.some(x=>x.key==='minimum'&&x.value==='9007199254740993'));assert.ok(node.constraints.some(x=>x.key==='readOnly'&&x.value==='true'));assert.ok(node.keywords.includes('x-vendor'));
});

test('result display is invalidated synchronously by raw, sample, authority or media changes',()=>{
 const context={versionId:'v',definitionId:'d',revision:2,rawSchema:'{}',example:'null',formatMode:'Annotation',direction:'request',authority:'user',contentType:'application/json',schemaType:'request'};
 assert.equal(stateModule.validationContextMatches(context,{...context}),true);
 for(const key of Object.keys(context))assert.equal(stateModule.validationContextMatches(context,{...context,[key]:'changed'}),false,key);
 assert.equal(stateModule.validationContextMatches(undefined,context),false);
});
test('unavailable source examples remain visible as unverified choices',()=>{
 const options=exampleOptions({examples:[{name:'Too large',unverifiedReason:'示例超过预算。'},{name:'Broken',unverifiedReason:'固定引用无法解析。'}]});assert.equal(options.length,2);assert.ok(options.every(x=>x.unverified));assert.equal(options[0].unverifiedReason,'示例超过预算。');
});
