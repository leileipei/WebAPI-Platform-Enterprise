import test from 'node:test';
import assert from 'node:assert/strict';
import {credentialCreateResult} from '../src/applications/credential-create-result.mjs';

const id='10000000-0000-0000-0000-000000000001';
test('first creation preserves the one-time key and credential identity',()=>{
 assert.deepEqual(credentialCreateResult({credential:{id},apiKey:'access.secret',secret:'secret',secretUnavailable:false}),{credentialId:id,apiKey:'access.secret',secretUnavailable:false});
});
test('a receipt without its secret retains identity and explicitly reports unavailable',()=>{
 assert.deepEqual(credentialCreateResult({credential:{id},apiKey:null,secret:null,secretUnavailable:true}),{credentialId:id,apiKey:null,secretUnavailable:true});
});
test('a replay flag never permits an unexpected key to be displayed',()=>{
 assert.deepEqual(credentialCreateResult({credential:{id},apiKey:'must-not-display',secretUnavailable:true}),{credentialId:id,apiKey:null,secretUnavailable:true});
});
test('a missing result or key is an error rather than a silently successful creation',()=>{
 for(const value of [null,{}, {credential:{id}}, {credential:{id},apiKey:''}, {apiKey:'access.secret'}, {credential:{id:2},apiKey:'access.secret'}]){
  assert.throws(()=>credentialCreateResult(value),/创建结果/);
 }
});
