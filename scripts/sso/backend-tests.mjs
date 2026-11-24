import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID,randomBytes} from 'node:crypto';
import {spawn} from 'node:child_process';
import {withAcceptanceSignals} from '../acceptance-credentials.mjs';
import {execFileAndWait} from '../runtime/exec-file.mjs';
import {cleanupSsoRun,createSsoDirectory,ssoNetwork,writeSsoCredential} from './owned-run.mjs';

const [testProject,...testArgs] = process.argv.slice(2);
const root=process.cwd(),owner=randomUUID(),project='webapi-sso-test-'+owner,directory=path.join(root,'.runtime',project);
const context={root,owner,project,directory};
const env={...process.env,WEBAPI_SSO_TEST_OWNER:owner,WEBAPI_SSO_TEST_PROJECT:project,WEBAPI_SSO_TEST_DIRECTORY:directory};
const args=['compose','-p',project,'-f','deploy/compose.test.yml','-f','deploy/compose.sso-test.yml',
  '-f',path.join(directory,'nuget-seed.yml')];

async function docker(binary,args,signal) {
  return (await execFileAndWait(binary,args,{cwd:root,env,signal})).stdout.trim();
}

async function compose(argsToRun,signal) {
  signal.throwIfAborted();
  const child=spawn('docker',[...args,...argsToRun],{cwd:root,env,signal,stdio:'inherit'});
  let failure;
  child.once('error',error=>{failure=error;});
  const code=await new Promise(resolve=>child.once('close',resolve));
  signal.throwIfAborted();
  if (failure || code!==0) throw Object.assign(new Error('SSO test command failed'),{exitCode:code>0?code:3});
}

await withAcceptanceSignals(async signal=>{
  let created=false,dockerAttempted=false,preserve;
  try {
    await createSsoDirectory(context);
    created=true;
    await writeSsoCredential(directory,'postgres-password',randomBytes(32).toString('base64url'));
    signal.throwIfAborted();
    const seed=env.WEBAPI_SSO_NUGET_SEED || 'webapi-enterprise-core-test_nuget-test';
    let seedAvailable=false;
    if (/^[a-zA-Z0-9_.-]+$/.test(seed)) {
      try { await docker('docker',['volume','inspect','--format','{{.Name}}',seed],signal); seedAvailable=true; }
      catch (error) {
        signal.throwIfAborted();
        if (error.code!==1 || !/no such volume/i.test(error.stderr || '')) throw error;
      }
    }
    const overlay={networks:{default:ssoNetwork(owner)}};
    if (seedAvailable) {
      overlay.services={sdk:{volumes:['sso-nuget-seed:/nuget-seed:ro']}};
      if (env.WEBAPI_SSO_OFFLINE==='1') overlay.services.sdk.environment={NuGetAudit:'false',RestoreSources:'/nuget-seed'};
      overlay.volumes={'sso-nuget-seed':{external:true,name:seed}};
    }
    await fs.writeFile(path.join(directory,'nuget-seed.yml'),JSON.stringify(overlay),{mode:0o600,flag:'wx'});
    signal.throwIfAborted();
    dockerAttempted=true;
    await compose(['up','-d','--wait','postgres','redis'],signal);
    const setup=['set -e'];
    if (seedAvailable) setup.push('mkdir -p /root/.nuget/packages','cp -a /nuget-seed/. /root/.nuget/packages/');
    if (/WebApi\.(?:Domain|Integration)\.Tests\.csproj$/.test(testProject)) {
      setup.push('dotnet publish src/WebApi.RuntimeTool/WebApi.RuntimeTool.csproj -c Release -o /runtime-tool -p:RestoreLockedMode=true',
        'export WEBAPI_RUNTIME_TOOL_DLL=/runtime-tool/WebApi.RuntimeTool.dll');
    }
    setup.push('exec dotnet test "$@"');
    await compose(['run','--rm','sdk','bash','-c',setup.join('\n'),'--',testProject,'--configuration','Release',...testArgs],signal);
  } catch (error) {
    preserve=error.preserveCredentials;
    process.exitCode=error.exitCode || 3;
    console.error('SSO backend initialization or execution failed.');
  } finally {
    if (created) {
      try { console.log(JSON.stringify(await cleanupSsoRun(context,docker,{dockerAttempted,backend:true,preserve}))); }
      catch (error) { console.log(JSON.stringify(error.cleanup)); process.exitCode ||= 3; }
    }
  }
});
