import test from 'node:test';import assert from 'node:assert/strict';import {derivePipelineFixtureBehavior} from '../../scripts/delivery/pipeline-behavior.mjs';
const candidate={versions:[{api:{id:'api'},version:{id:'v',version:'1.0.0',revision:2},parameters:[],schemas:[]}],routes:[{id:'r',apiVersionId:'v',path:'/orders/{**path}',methods:['GET'],priority:100,enabled:true}],policies:[]};
const snapshot={routes:[{id:'r',apiId:'api',apiVersionId:'v',path:'/orders/{**path}',methods:['GET'],matchOrder:3999900,requireApiKey:true}],policies:[]};
test('actual deployed behavior detects changed runtime route',()=>{assert.throws(()=>derivePipelineFixtureBehavior(candidate,{...snapshot,routes:[{...snapshot.routes[0],path:'/other'}]}),/runtime/);});
test('actual deployed authentication changes behavior hash',()=>{assert.notEqual(derivePipelineFixtureBehavior(candidate,snapshot),derivePipelineFixtureBehavior(candidate,{...snapshot,routes:[{...snapshot.routes[0],requireApiKey:false}]}));});
test('unsupported business fixture cannot pretend canonical verification',()=>{assert.throws(()=>derivePipelineFixtureBehavior({...candidate,policies:[{}]},snapshot),/fixture/);});
test('runtime route ordering is part of observed behavior',()=>{assert.throws(()=>derivePipelineFixtureBehavior(candidate,{...snapshot,routes:[{...snapshot.routes[0],matchOrder:1}]}),/runtime order/);});
