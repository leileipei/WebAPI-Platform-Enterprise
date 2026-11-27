import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {withRestoreCliStorage} from '../runtime/restore-cli.mjs';

export async function runDeliveryCli(args,{verifyDirectory,configureStorage},run){
 const [mode,value,...flags]=args;
 if(mode==='verify'){
  if(!value||flags.length)throw Error('One evidence directory is required for verification.');
  return verifyDirectory(path.resolve(value));
 }
 if(!['e2e','faults','browser'].includes(mode)||!/^[a-f0-9]{40}$/.test(value??''))
  throw Error('Explicit acceptance mode and immutable 40-character revision required.');
 return withRestoreCliStorage(flags,storage=>run({
  mode,sourceRevision:value,directory:path.resolve('.runtime/tests','webapi-enterprise-local-test-'+randomUUID()),
  nodeRuntime:process.env.WEBAPI_NODE_RUNTIME??'/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node',
  chromiumExecutable:process.env.WEBAPI_CHROMIUM_EXECUTABLE??'/Users/leo.cui/Library/Caches/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-mac-arm64/chrome-headless-shell',
  ...storage
 }),{configureStorage});
}
