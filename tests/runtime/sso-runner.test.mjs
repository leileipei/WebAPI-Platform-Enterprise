import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';import {randomUUID} from 'node:crypto';
import {assertOwnedSsoResources,validateSsoDirectory} from '../../scripts/sso/evidence.mjs';
test('owned disposable resources are accepted and foreign resources rejected',()=>{
 const owner=randomUUID(),ctx={owner,project:'webapi-sso-test-'+owner};const label={'com.docker.compose.project':ctx.project,'com.webapi.sso.owner':owner};
 assert.doesNotThrow(()=>assertOwnedSsoResources(ctx,[label]));
 assert.throws(()=>assertOwnedSsoResources({...ctx,project:'webapi-enterprise-local'},[label]),/owner|project/i);
 assert.throws(()=>assertOwnedSsoResources(ctx,[{...label,'com.webapi.sso.owner':randomUUID()}]),/owner/i);
});
test('directory rejects symlinks and foreign owner identity',async()=>{
 const parent=await fs.mkdtemp(path.join(os.tmpdir(),'sso-owner-'));const owner=randomUUID(),project='webapi-sso-test-'+owner,directory=path.join(parent,project);await fs.mkdir(directory,{mode:0o700});await fs.writeFile(path.join(directory,'owner.json'),JSON.stringify({owner,project}),{mode:0o600});
 try {await assert.doesNotReject(()=>validateSsoDirectory({directory,owner,project}));const link=path.join(parent,'link');await fs.symlink(directory,link);await assert.rejects(()=>validateSsoDirectory({directory:link,owner,project}),/symlink|directory/i);await assert.rejects(()=>validateSsoDirectory({directory,owner:randomUUID(),project}),/owner/i);}
 finally {await fs.rm(parent,{recursive:true,force:true});}
});
