import {test} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
const root=path.resolve(import.meta.dirname,'../..');
async function run({exit=0,foreign=false}={}){
 const temp=await fs.mkdtemp(path.join(os.tmpdir(),'contract-runner-'));
 try{
  const capture=path.join(temp,'calls.json');
  await fs.writeFile(path.join(temp,'docker'),`#!${process.execPath}\nimport fs from 'node:fs';
const a=process.argv.slice(2),f=process.env.CONTRACT_TEST_CAPTURE,c=fs.existsSync(f)?JSON.parse(fs.readFileSync(f)):[];
c.push(a);fs.writeFileSync(f,JSON.stringify(c));
const previous=c.find(x=>x[0]==='compose'),project=previous?.[previous.indexOf('-p')+1];
if(a[0]==='volume')process.exit(1);
if(a[0]==='inspect'){console.log(JSON.stringify({'com.docker.compose.project':project,'com.webapi.contracts.owner':process.env.CONTRACT_TEST_FOREIGN==='1'?'foreign':project.replace('webapi-contracts-test-','')}));process.exit(0);}
if(a.includes('ps'))console.log('owned-container');
if(a.includes('run'))process.exit(Number(process.env.CONTRACT_TEST_EXIT));
`,{mode:0o700});
  const result=spawnSync('bash',[path.join(root,'scripts/check-contracts.sh'),'domain','--filter','ReaderTests'],{cwd:root,encoding:'utf8',env:{...process.env,PATH:temp+path.delimiter+process.env.PATH,WEBAPI_NODE:process.execPath,CONTRACT_TEST_CAPTURE:capture,CONTRACT_TEST_EXIT:String(exit),CONTRACT_TEST_FOREIGN:foreign?'1':'0'}});
  return {status:result.status,stderr:result.stderr,calls:JSON.parse(await fs.readFile(capture,'utf8').catch(()=> '[]'))};
 } finally{await fs.rm(temp,{recursive:true,force:true});}
}
test('runner uses UUID-owned project and cleans only that project',async()=>{
 const r=await run();assert.equal(r.status,0,r.stderr);
 const compose=r.calls.filter(x=>x[0]==='compose');assert(compose.length>0);
 const projects=compose.map(x=>x[x.indexOf('-p')+1]);assert.equal(new Set(projects).size,1);
 assert.match(projects[0],/^webapi-contracts-test-[0-9a-f-]{36}$/);
 assert(compose.some(x=>x.includes('down')&&x.includes('--volumes')));
 assert(compose.some(x=>x.includes('--filter')&&x.includes('ReaderTests')));
 await assert.rejects(fs.access(path.join(root,'.runtime',projects[0])));
});
test('failed test exit survives resource cleanup',async()=>{
 const r=await run({exit:42});assert.equal(r.status,42,r.stderr);
 assert(r.calls.some(x=>x.includes('down')));
});
test('foreign ownership prevents destructive cleanup',async()=>{
 const r=await run({foreign:true});assert.equal(r.status,3,r.stderr);
 assert(!r.calls.some(x=>x.includes('down')));
 // Remove only this test's empty private fixture directory, after the runner has retained it.
 const c=r.calls.find(x=>x[0]==='compose');if(c)await fs.rm(path.join(root,'.runtime',c[c.indexOf('-p')+1]),{recursive:true,force:true});
});
