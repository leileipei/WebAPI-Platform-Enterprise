import fs from 'node:fs/promises';
import path from 'node:path';
import {withAcceptanceSignals} from '../acceptance-credentials.mjs';
import {commandFor} from './fixture.mjs';
import {cleanupSsoRun} from './owned-run.mjs';
import {ownedReview,releaseReviewPointer,reviewContext} from './review.mjs';

const root=path.resolve(import.meta.dirname,'../..');
await withAcceptanceSignals(async()=>{
  let result;
  try {
    const {pointer,identity}=await ownedReview(root);
    let resourceError;
    try { await reviewContext(identity); }
    catch { resourceError='SSO retained review context is unavailable or disagrees with the owner marker'; }
    result=await cleanupSsoRun(identity,commandFor(identity),{resourceError});
    await releaseReviewPointer(root,pointer);
  } catch (error) {
    result=error.cleanup ?? {...result,schemaVersion:2,failed:true,error:'SSO review ownership or pointer release could not be verified',
      containersRemaining:result?.containersRemaining ?? null,volumesRemaining:result?.volumesRemaining ?? null,
      networksRemaining:result?.networksRemaining ?? null,secretsRemaining:result?.secretsRemaining ?? null};
    process.exitCode=3;
  }
  console.log(JSON.stringify(result));
  await fs.writeFile(path.join(root,'docs/evidence/sso/cleanup.json'),JSON.stringify(result,null,2)+'\n');
});
