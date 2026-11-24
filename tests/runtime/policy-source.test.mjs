import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';import {execFileSync} from 'node:child_process';import {createHash} from 'node:crypto';import {buildPolicySource} from '../../scripts/policies/build-source.mjs';

test('specialized source and policy ZIP include the backend build dependencies and exclude unrelated tools',async t=>{
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'policy-backend-package-'));
 t.after(()=>fs.rm(root,{recursive:true,force:true}));
 const repo=path.resolve(import.meta.dirname,'../..');
 const dependencies=['third_party/json-everything/src/JsonSchema/JsonSchema.csproj','tools/WebApi.PerformanceHost/WebApi.PerformanceHost.csproj',
  'tests/contracts/unicode-provenance.test.mjs','src/WebApi.Infrastructure/Contracts/IdnaUnicodeData.cs',
  ...['source-manifest.json','17.0.0/DerivedBidiClass.txt','17.0.0/Scripts.txt','LICENSE.txt','generate.py'].map(name=>'third_party/unicode-idna/'+name)];
 const scripts=['scripts/check-policies.sh','scripts/contracts/run-with-completion.sh','scripts/contracts/test-completion.mjs','scripts/contracts/check-completion.mjs','scripts/contracts/runner/ContractCompletionLogger.cs','scripts/contracts/runner/packages.lock.json','scripts/contracts/runner/WebApi.Contracts.TestLogger.csproj'];
 const ignored='tools/OtherProject/Other.csproj';
 async function write(name,data){const full=path.join(root,name);await fs.mkdir(path.dirname(full),{recursive:true});await fs.writeFile(full,data);}
 for(const file of [...dependencies,...scripts,'scripts/package-policies.py'])await write(file,await fs.readFile(path.join(repo,file)));
 await write(ignored,'unrelated');
 await write('docs/evidence/policies/source-manifest.json','{}');
 // The external evidence verifier is independent of the ZIP input selector.
 await write('scripts/verify-policies-delivery.py','');
 execFileSync('git',['init','-q',root]);
 execFileSync('git',['-C',root,'-c','user.name=test','-c','user.email=test@invalid','commit','--allow-empty','-qm','base']);
 const frozen=await buildPolicySource({root,outputDirectory:path.join(root,'.runtime/freeze')});
 const manifest=JSON.parse(await fs.readFile(frozen.manifestPath));
 assert(dependencies.every(name=>name in manifest.files));
 assert(!(ignored in manifest.files));
 const zip=path.join(root,'delivery.zip');
 execFileSync('python3',['scripts/package-policies.py','--output',zip],{cwd:root});
 const entries=JSON.parse(execFileSync('python3',['-c','import json,sys,zipfile;print(json.dumps([n.split("/",1)[1] for n in zipfile.ZipFile(sys.argv[1]).namelist()]))',zip]));
 assert([...dependencies,...scripts].every(name=>entries.includes(name)));
 assert(!entries.includes(ignored));
 const unpacked=path.join(root,'unpacked');
 execFileSync('python3',['-c','import sys,zipfile;zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])',zip,unpacked]);
 const [project]=await fs.readdir(unpacked);
 const childEnv={...process.env};
 delete childEnv.NODE_TEST_CONTEXT;
 const result=execFileSync(process.execPath,['--test','--test-reporter=tap','tests/contracts/unicode-provenance.test.mjs'],
  {cwd:path.join(unpacked,project),env:childEnv,encoding:'utf8'});
 assert.match(result,/^# pass 1$/m);
});
test('snapshot includes new source and excludes secrets and build outputs',async()=>{const root=await fs.mkdtemp(path.join(os.tmpdir(),'pol-source-'));try{execFileSync('git',['init','-q',root]);execFileSync('git',['-C',root,'-c','user.name=test','-c','user.email=test@invalid','commit','--allow-empty','-qm','base']);for(const d of ['src/New','src/New/bin','console/node_modules','.secrets'])await fs.mkdir(path.join(root,d),{recursive:true});await fs.writeFile(path.join(root,'src/New/New.cs'),'actual uncommitted source');await fs.writeFile(path.join(root,'src/New/bin/cache'),'skip');await fs.writeFile(path.join(root,'.secrets/password'),'skip');const result=await buildPolicySource({root,outputDirectory:path.join(root,'.runtime/freeze')});const manifest=JSON.parse(await fs.readFile(result.manifestPath));assert.equal(manifest.files['src/New/New.cs'],createHash('sha256').update('actual uncommitted source').digest('hex'));assert.equal(manifest.files['.secrets/password'],undefined);assert.equal(manifest.files['src/New/bin/cache'],undefined);assert.equal(await fs.readFile(path.join(result.sourceDirectory,'src/New/New.cs'),'utf8'),'actual uncommitted source');}finally{await fs.rm(root,{recursive:true,force:true});}});
test('source symlink escape is rejected',async()=>{const root=await fs.mkdtemp(path.join(os.tmpdir(),'pol-escape-'));try{execFileSync('git',['init','-q',root]);execFileSync('git',['-C',root,'-c','user.name=test','-c','user.email=test@invalid','commit','--allow-empty','-qm','base']);await fs.mkdir(path.join(root,'src'));await fs.symlink('/etc/passwd',path.join(root,'src/leak'));await assert.rejects(buildPolicySource({root,outputDirectory:path.join(root,'.runtime/freeze')}),/symlink/);}finally{await fs.rm(root,{recursive:true,force:true});}});
