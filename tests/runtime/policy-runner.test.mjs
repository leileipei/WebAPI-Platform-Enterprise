import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

function run({foreign=false, failure=0, mode='domain'}={}) {
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),'policy-runner-'));
  const log=path.join(dir,'docker.jsonl');
  const fake=path.join(dir,'docker');
  fs.writeFileSync(fake,`#!${process.execPath}\nimport fs from 'node:fs';
const args=process.argv.slice(2); const root=process.env.WEBAPI_POLICY_TEST_DIRECTORY;
fs.appendFileSync(process.env.POLICY_RUNNER_LOG,JSON.stringify({args,root,owner:process.env.WEBAPI_POLICY_TEST_OWNER,project:process.env.WEBAPI_POLICY_TEST_PROJECT,secretMode:root&&fs.existsSync(root+'/postgres-password')?fs.statSync(root+'/postgres-password').mode&511:null})+'\\n');
if(args[0]==='inspect')console.log(JSON.stringify({'com.docker.compose.project':process.env.POLICY_RUNNER_FOREIGN==='yes'?'foreign':process.env.WEBAPI_POLICY_TEST_PROJECT,'com.webapi.policies.owner':process.env.WEBAPI_POLICY_TEST_OWNER}));
else if(args.includes('ps')) console.log('owned-container');
else if(args.includes('run'))process.exit(Number(process.env.POLICY_RUNNER_FAILURE));
`,{mode:0o700});
  const result=spawnSync('bash',['scripts/check-policies.sh',mode,'--filter','FullyQualifiedName~PolicyConfigurationTests'],{encoding:'utf8',env:{...process.env,PATH:dir+path.delimiter+process.env.PATH,WEBAPI_NODE:process.execPath,POLICY_RUNNER_LOG:log,POLICY_RUNNER_FOREIGN:foreign?'yes':'no',POLICY_RUNNER_FAILURE:String(failure),COMPOSE_PROJECT_NAME:'webapi-enterprise-local'}});
  const rows=fs.existsSync(log)?fs.readFileSync(log,'utf8').trim().split('\n').filter(Boolean).map(JSON.parse):[];
  fs.rmSync(dir,{recursive:true,force:true});
  return {...result,rows};
}

test('isolated runner uses private secret and overrides ambient project then cleans own resources',()=>{
  const r=run();assert.equal(r.status,0,r.stderr);
  const compose=r.rows.filter(x=>x.args[0]==='compose');assert.ok(compose.length>=3);
  for(const row of compose){const project=row.args[row.args.indexOf('-p')+1];assert.match(project,/^webapi-policies-test-/);assert.notEqual(project,'webapi-enterprise-local');assert.equal(row.secretMode,0o600);}
  assert.ok(compose.some(x=>x.args.includes('down')));
  assert.equal(fs.existsSync(compose[0].root+'/postgres-password'),false);
  assert.ok(compose.some(x=>x.args.includes('--filter')&&x.args.includes('FullyQualifiedName~PolicyConfigurationTests')));
});
test('foreign owner prevents destructive cleanup',()=>{
  const r=run({foreign:true});assert.notEqual(r.status,0);assert.match(r.stderr,/ownership/i);
  assert.equal(r.rows.some(x=>x.args.includes('down')),false);
});
test('failing product test preserves exit code while cleaning only own project',()=>{
  const r=run({failure:7});assert.equal(r.status,7,r.stderr);assert.ok(r.rows.some(x=>x.args.includes('down')));
});
test('unsupported mode does not invoke docker',()=>{
  const r=run({mode:'not-a-mode'});assert.equal(r.status,2);assert.equal(r.rows.length,0);
});
