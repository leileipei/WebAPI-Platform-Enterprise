import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';
import {initializeState,withRuntimeLock} from '../../scripts/runtime/state.mjs';
// c107e93 made withRuntimeLock fail-closed: a same-target concurrent call is rejected by the
// restore-target lock instead of deadlocking. Verify the rejection and clean re-acquisition.
test('concurrentRuntimeLockFailsClosedThenReacquiresAfterRelease',async t=>{
 const parent=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-lock-race-'));t.after(()=>fs.rm(parent,{recursive:true,force:true}));const dir=path.join(parent,'local');await initializeState(dir,{bootstrapUsername:'test-admin'});
 const held=Promise.withResolvers(),release=Promise.withResolvers();let firstRan=false;
 const first=withRuntimeLock(dir,async()=>{held.resolve();await release.promise;firstRan=true;},{bypassQueue:true});await held.promise;
 await assert.rejects(withRuntimeLock(dir,async()=>{throw new Error('must not enter');},{bypassQueue:true}),{code:'RESTORE_TARGET_LOCK_HELD'});
 release.resolve();await first;assert.equal(firstRan,true);
 let reacquired=false;await withRuntimeLock(dir,async()=>{reacquired=true;},{bypassQueue:true});assert.equal(reacquired,true);
 await assert.rejects(fs.access(path.join(dir,'operation.lock')),{code:'ENOENT'});
});
