import assert from 'node:assert/strict';
import test from 'node:test';
import {compareSnapshots} from '../src/pages/snapshot-diff.mjs';

test('compares resources by ID and ignores object key order and snapshot bookkeeping',()=>{
  const before={schemaVersion:'2.3',configVersion:8,routes:[{id:'r',path:'/a',priority:1},{id:'old'}],clusters:[{id:'c',name:'ordered'}],policies:[],applications:[]};
  const after={...before,schemaVersion:'2.4',configVersion:10,routes:[{id:'new'},{priority:3,id:'r',path:'/a'}],clusters:[{name:'ordered',id:'c'}]};
  assert.deepEqual(compareSnapshots(before,after).map(x=>[x.kind,x.id,x.change]),[
    ['schemaVersion','schemaVersion','修改'],['routes','new','新增'],['routes','old','移除'],['routes','r','修改']]);
  assert.equal(compareSnapshots(before,after).at(-1).after.priority,3);
});
