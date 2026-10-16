import test from 'node:test';
import assert from 'node:assert/strict';
import {describePermission,permissionGroups,mayConfigurePermissions} from '../src/governance/permission-display.mjs';

test('known permissions get Chinese labels without replacing authoritative codes or custom names',()=>{
 const p={id:'p',code:'api.version.write',name:'api.version.write',module:'api',description:'服务器说明'};
 assert.deepEqual(describePermission(p),{...p,label:'维护 API 版本',moduleLabel:'API 管理',column:'configure'});
 assert.equal(p.name,'api.version.write');
 assert.equal(describePermission({...p,name:'研发专用版本维护'}).label,'研发专用版本维护');
});
test('extension permissions retain server metadata and use an explicit other column',()=>{
 const p={code:'vendor.delete',name:'删除扩展记录',module:'vendor',description:'自定义说明'};
 assert.deepEqual(describePermission(p),{...p,label:'删除扩展记录',moduleLabel:'vendor',column:'other'});
 assert.equal(describePermission({code:'vendor.read',module:'vendor'}).column,'other');
});
test('matrix groups every code exactly once and searches labels, codes, modules and descriptions',()=>{
 const items=[{code:'api.read',name:'api.read',module:'api'},{code:'api.schema.write',module:'api'},{code:'system.sso.manage',module:'system'},{code:'vendor.read',module:'vendor',description:'特定扩展'}];
 assert.deepEqual(permissionGroups(items).flatMap(g=>g.items.map(p=>p.code)).sort(),items.map(p=>p.code).sort());
 for(const q of ['维护 API Schema','API.SCHEMA.WRITE','API 管理'])assert(permissionGroups(items,q).some(g=>g.items.some(p=>p.code==='api.schema.write')));
 assert.equal(permissionGroups(items,'特定扩展')[0].items[0].code,'vendor.read');
 assert.equal(items.length,4);
});
test('permission configuration requires a complete usable dictionary and an editable role',()=>{
 const role={isSystem:false,permissions:['api.read']},dictionary=[{code:'api.read'}];
 assert.equal(mayConfigurePermissions(role,dictionary,{loading:false,error:''}),true);
 for(const [r,p,s] of [[{...role,isSystem:true},dictionary,{}],[role,undefined,{}],[role,[],{}],[role,dictionary,{loading:true}],[role,dictionary,{error:'失联'}],[{...role,permissions:['unknown']},dictionary,{}]])assert.equal(mayConfigurePermissions(r,p,s),false);
 assert.equal(mayConfigurePermissions({...role,permissions:[]},dictionary,{}),true);
});
