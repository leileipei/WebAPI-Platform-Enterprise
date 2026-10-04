import test from'node:test';import assert from'node:assert/strict';import{spawnSync}from'node:child_process';
const valid=()=>({proof:{complete:true,chain:{passed:true},restart:{passed:true},failure:{passed:true},backupRestore:{passed:true},ui:{verified:true},preservedOldEnvironment:{unchanged:true},cleanup:{containersRemaining:0,volumesRemaining:0,secretFilesRemaining:0}},files:['README.md','console/package.json'],static_revision:'a'.repeat(40),source_revision:'a'.repeat(40),old_before:{'old.zip':'abc'},old_after:{'old.zip':'abc'}});
function check(input){return spawnSync('python3',['-c','import json,sys,runpy; m=runpy.run_path("scripts/package-local-runtime.py"); m["validate_inputs"](**json.load(sys.stdin))'],{input:JSON.stringify(input),encoding:'utf8'});}
test('packageRejectsIncompleteAcceptance',()=>{const v=valid();v.proof.complete=false;const r=check(v);assert.notEqual(r.status,0);assert.match(r.stderr,/Incomplete actual acceptance/);});
test('packageRejectsSecretsAndUserConfiguration',()=>{for(const file of['.runtime/local/runtime.json','.secrets/password','secrets/bootstrap-password','backup/postgres.dump','runtime.json']){const v=valid();v.files.push(file);const r=check(v);assert.notEqual(r.status,0);assert.match(r.stderr,/Forbidden delivery file/);}});
test('packageRejectsMismatchedStaticSource',()=>{const v=valid();v.static_revision='b'.repeat(40);const r=check(v);assert.notEqual(r.status,0);assert.match(r.stderr,/Static source mismatch/);});
test('packageRejectsReplacementOfOldArchive',()=>{const v=valid();v.old_after['old.zip']='def';const r=check(v);assert.notEqual(r.status,0);assert.match(r.stderr,/Old delivery changed/);});
test('completeInputsMayBePackaged',()=>{const r=check(valid());assert.equal(r.status,0,r.stderr);});
test('immutableSourceExportExcludesPreviouslyTrackedDeliveryManifests',()=>{const program=`import io,tarfile,runpy,json
m=runpy.run_path('scripts/package-local-runtime.py')
b=io.BytesIO()
with tarfile.open(fileobj=b,mode='w') as t:
 for name in ['README.md','deliverables/manifest-observability.json']:
  info=tarfile.TarInfo(name);info.size=2;t.addfile(info,io.BytesIO(b'{}'))
m['read_source'].__globals__['git']=lambda *args:b.getvalue()
print(json.dumps(list(m['read_source']('a'*40))))
`;const r=spawnSync('python3',['-c',program],{encoding:'utf8'});assert.equal(r.status,0,r.stderr);assert.deepEqual(JSON.parse(r.stdout),['README.md']);});
test('testedDeploymentAllowsOnlyTheVerifiedBrowserPortCorrection',()=>{const program=`import runpy
m=runpy.run_path('scripts/package-local-runtime.py')
validate=m['validate_deployment_inputs']
name='deploy/compose.runtime.yml'
old=b'127.0.0.1:'+bytes([36])+b'{WEBAPI_RUNTIME_CONSOLE_PORT:-4190}:8080 persistent'
new=old.replace(b':-4190',b':-4192')
validate({name:old},{name:old})
validate({name:new},{name:old})
for changed in [{name:new.replace(b'persistent',b'tmpfs')},{name:new,'deploy/extra.yml':b'new'}]:
 try:validate(changed,{name:old})
 except ValueError:pass
 else:raise AssertionError('unverified deployment change allowed')
`;const r=spawnSync('python3',['-c',program],{encoding:'utf8'});assert.equal(r.status,0,r.stderr);});
