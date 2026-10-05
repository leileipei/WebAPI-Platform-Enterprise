import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';
import {rebindRestoredViewer} from '../../scripts/runtime/sso-demo-platform.mjs';
test('explicit recovery disables the old provider and viewer before audited binding replacement',async t=>{
 const directory=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-sso-rebind-'));t.after(()=>fs.rm(directory,{recursive:true,force:true}));
 const ctx={directory,state:{subject:'stable-subject',recovery:{sourceOwnerId:'source-owner',sourceProviderId:'old-provider'}}},user={id:'viewer-id',username:'sso-demo-viewer',authSource:'sso',displayName:'Viewer',status:'Active',email:'v@example.test',revision:1},provider={id:'new-provider'},binding={providerId:'old-provider',subject:'stable-subject',revision:1,enabled:true},writes=[];
 const admin={request:async(route,o={})=>{if(o.method){writes.push({route,body:o.body});if(route==='/users/viewer-id')Object.assign(user,o.body,{revision:user.revision+1});if(route.endsWith('/external-identity'))Object.assign(binding,o.body,{revision:binding.revision+1});return user;}if(route.endsWith('/external-identity'))return {...binding};if(route==='/settings/sso/providers/old-provider')return{id:'old-provider',enabled:true,revision:1};if(route.startsWith('/users'))return{items:[{...user}]};throw Error('Unexpected route');}};
 await rebindRestoredViewer(ctx,admin,{...user},provider);
 assert.deepEqual(writes.map(w=>w.route),['/settings/sso/providers/old-provider/disable','/users/viewer-id','/users/viewer-id/external-identity']);
 assert.equal(writes[1].body.status,'Disabled');assert.deepEqual(writes[2].body,{providerId:'new-provider',subject:'stable-subject',enabled:true});
 const journal=JSON.parse(await fs.readFile(path.join(directory,'rebind-journal.json'),'utf8'));assert.equal(journal.priorStatus,'Active');assert.equal(journal.userId,'viewer-id');
});
test('ordinary or mismatched recovery never changes an existing account',async()=>{
 const admin={request:async()=>{throw Error('must not mutate');}};
 await assert.rejects(rebindRestoredViewer({state:{subject:'subject',recovery:null}},admin,{username:'sso-demo-viewer',authSource:'sso'},{id:'provider'}),/recovery/i);
});
