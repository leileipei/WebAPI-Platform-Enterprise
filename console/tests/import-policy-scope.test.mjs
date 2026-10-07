import test from 'node:test';import assert from 'node:assert/strict';import * as state from '../src/contracts/import-state.mjs';
const a={organizationId:'org',projectId:'a'},b={organizationId:'org',projectId:'b'};
for(const revision of [1,9])test(`project switch rejects another project override at revision ${revision}`,()=>{
 const remote={projectId:'b',revision},override={scope:state.importPolicyScope(a),generation:1,value:{projectId:'a',revision:9}};
 assert.equal(state.selectImportPolicy(b,remote,override),remote);
 assert.equal(state.selectImportPolicy(a,{projectId:'a',revision:1},override),override.value);
 assert.equal(state.selectImportPolicy(b,{projectId:'a',revision:99},override),undefined);
});
test('late save response cannot restore the previous project or an earlier visit',()=>{
 const captured={scope:state.importPolicyScope(a),generation:1},value={projectId:'a',revision:2};
 assert.equal(state.acceptImportPolicySave({...captured,generation:3},captured,value),undefined);
 assert.equal(state.acceptImportPolicySave({scope:state.importPolicyScope(b),generation:2},captured,value),undefined);
 assert.deepEqual(state.acceptImportPolicySave(captured,captured,value),{...captured,value});
 assert.equal(state.acceptImportPolicySave(captured,captured,{projectId:'b',revision:2}),undefined);
});
