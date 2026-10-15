import assert from 'node:assert/strict';
import {verifyGatewayPolicySuites} from '../gateway-policies/verify-suites.mjs';

export function accountVerificationOptions({revision,directory,pnpm,...options}) {
  assert.match(revision??'',/^[a-f0-9]{40}$/,'Fixed source revision required');
  assert(typeof directory==='string'&&directory.length>0,'Verification directory required');
  assert(typeof pnpm==='string'&&pnpm.length>0,'Explicit pnpm executable required');
  return {...options,revision,directory,pnpm,
    extraTestDirectories:['tests/network-policies','tests/account-credential'],
    maximumTestThreads:2};
}

export async function verifyAccountCredentialSuites(options) {
  return verifyGatewayPolicySuites(accountVerificationOptions(options));
}

if(process.argv[1]===import.meta.filename) {
  const [revision,directory,pnpm]=process.argv.slice(2);
  await verifyAccountCredentialSuites({revision,directory,pnpm});
}
