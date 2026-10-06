import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';
import {initializeState,withRuntimeLock} from '../../scripts/runtime/state.mjs';
// Exercises the real lock file; only schedule the holder's normal release after EEXIST.
test('waiterRetriesWhenHolderReleasesBetweenCollisionAndInspection',async t=>{
 const parent=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-lock-race-'));t.after(()=>fs.rm(parent,{recursive:true,force:true}));const dir=path.join(parent,'local');await initializeState(dir,{bootstrapUsername:'test-admin'});
 const held=Promise.withResolvers(),release=Promise.withResolvers();let firstFinished=false,secondEntered=false,scheduled=false;
 const first=withRuntimeLock(dir,async()=>{held.resolve();await release.promise;firstFinished=true;});await held.promise;
 const lock=path.join(await fs.realpath(dir),'operation.lock');const open=fs.open.bind(fs);t.mock.method(fs,'open',async(...args)=>{try{return await open(...args);}catch(e){if(args[0]===lock&&args[1]==='wx'&&e.code==='EEXIST'&&!scheduled){scheduled=true;release.resolve();await first;}throw e;}});
 try {await withRuntimeLock(dir,async()=>{assert.equal(firstFinished,true);secondEntered=true;});} finally {release.resolve();await first;}
 assert.equal(scheduled,true);assert.equal(secondEntered,true);await assert.rejects(fs.access(path.join(dir,'operation.lock')),{code:'ENOENT'});
});
