import fs from 'node:fs/promises';
import path from 'node:path';
import {withAcceptanceSignals} from '../acceptance-credentials.mjs';
import {createSsoFixture,cleanupSsoFixture,ssoBuildCache} from './fixture.mjs';
import {runSsoScenario} from './scenario.mjs';
import {publishReviewPointer,releaseReviewPointer} from './review.mjs';

const root=path.resolve(import.meta.dirname,'../..'), publicDirectory=path.join(root,'docs/evidence/sso');
const retainForReview=process.argv.includes('--browser') || process.argv.includes('--retain-for-review');

await withAcceptanceSignals(async signal=>{
  let context,cleanup,pointer,stage='fixture';
  await fs.mkdir(publicDirectory,{recursive:true});
  try {
    context=await createSsoFixture({root,retainForReview,signal});
    signal.throwIfAborted();
    stage='real Keycloak scenario';
    console.log('Running real Keycloak closed-loop checks');
    const result=await runSsoScenario(context);
    await fs.writeFile(path.join(context.directory,'result.json'),JSON.stringify(result,null,2),{mode:0o600});
    stage='SSO end-to-end test';
    const images=JSON.parse(await fs.readFile(path.join(root,'deploy/images.lock.json')));
    const cache=ssoBuildCache();
    const output=await context.run('docker',['run','--rm',...cache.options,'--label','com.webapi.sso.owner='+context.owner,
      '--label','com.docker.compose.project='+context.project,'--workdir','/workspace','-v',context.source.sourceDirectory+':/workspace',
      '-v',context.directory+':/sso-results','-v',cache.mount,
      '-e','DOTNET_CLI_UI_LANGUAGE=en-US','-e','WEBAPI_SSO_RESULT_DIRECTORY=/sso-results',images.sdk,'bash','-ec',
      'mkdir -p /root/.nuget/packages; cp -a /nuget-seed/. /root/.nuget/packages/; dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj -c Release --filter Scenario=Sso -p:RestoreLockedMode=true']);
    signal.throwIfAborted();
    result.complete=true;
    const summary=/Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)/.exec(output);
    if (!summary || summary[1]!=='0' || summary[2]!=='1' || summary[3]!=='0' || summary[4]!=='1')
      throw Error('Incomplete SSO end-to-end test result');
    result.endToEndTests={passed:Number(summary[2]),failed:Number(summary[1]),lockedRestore:true};
    await fs.copyFile(context.source.manifestPath,path.join(publicDirectory,'source-manifest.json'));
    if (retainForReview) {
      const {run,compose,...stored}=context;
      await fs.writeFile(path.join(context.directory,'review-context.json'),JSON.stringify(stored,null,2),{mode:0o600});
      pointer=await publishReviewPointer(context);
      result.review={retained:true,project:context.project,url:context.endpoints.console};
    } else {
      stage='cleanup';
      result.cleanup=cleanup=await cleanupSsoFixture(context);
    }
    await fs.writeFile(path.join(publicDirectory,'verification.json'),JSON.stringify(result,null,2)+'\n');
    signal.throwIfAborted();
    if (pointer) console.log('Independent SSO review URL: '+context.endpoints.console);
    console.log('SSO real-IDP scenario passed: '+result.checks.length+' checks');
  } catch(error) {
    cleanup=error.cleanup ?? cleanup;
    if (context && !cleanup) {
      try { cleanup=await cleanupSsoFixture(context); } catch (failure) { cleanup=failure.cleanup; }
    }
    if (pointer && !cleanup?.failed) await releaseReviewPointer(root,pointer);
    const receipt={complete:false,stage,cleanup,time:new Date().toISOString()};
    await fs.writeFile(path.join(publicDirectory,'last-failure.json'),JSON.stringify(receipt,null,2)+'\n');
    // Public errors contain no HTTP response, database record, command line, or credential.
    console.error('SSO acceptance failed at '+stage+'.');
    if (/^(?:docker|node) [a-z ]*failed \([\w ,]+\)\.$/i.test(error.message)) console.error(error.message);
    process.exitCode=1;
  }
});
