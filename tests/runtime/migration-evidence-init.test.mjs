import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import path from 'node:path';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const sdk='sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317';
function run(script){
  return spawnSync('docker',['run','--rm','--pull','never','--network','none','--read-only','--memory','128m','--cpus','1',
    '--cap-drop','ALL','--cap-add','CHOWN','--cap-add','DAC_OVERRIDE','--cap-add','FOWNER',
    '--tmpfs','/scratch:rw,size=4m,mode=0755','--mount',`type=bind,src=${path.join(root,'deploy/runtime')},dst=/scripts,readonly`,
    sdk,'bash','-c','set -euo pipefail\n'+script],{encoding:'utf8'});
}
function successful(script){const r=run(script);assert.equal(r.status,0,`${r.stdout}\n${r.stderr}`);}

test('only attested new empty volume is initialized',()=>successful(`
mkdir /scratch/v
WEBAPI_MIGRATION_EVIDENCE_NEW_VOLUME=1 bash /scripts/init-migration-evidence.sh /scratch/v
test "$(stat -c %u:%g:%a /scratch/v)" = 10001:10001:700
`));
test('existing correct volume and its contents remain unchanged',()=>successful(`
mkdir /scratch/v
chown 10001:10001 /scratch/v
chmod 700 /scratch/v
printf keep > /scratch/v/sentinel
before=$(stat -c %u:%g:%a /scratch/v)
bash /scripts/init-migration-evidence.sh /scratch/v
test "$(stat -c %u:%g:%a /scratch/v)" = "$before"
test "$(cat /scratch/v/sentinel)" = keep
`));
test('wrong existing ownership fails without touching contents',()=>successful(`
mkdir /scratch/v
printf keep > /scratch/v/sentinel
before=$(stat -c %u:%g:%a /scratch/v)
if bash /scripts/init-migration-evidence.sh /scratch/v; then exit 4; fi
test "$(stat -c %u:%g:%a /scratch/v)" = "$before"
test "$(cat /scratch/v/sentinel)" = keep
`));
test('nonempty or symlinked volume cannot be initialized',()=>successful(`
mkdir /scratch/v /scratch/target
printf keep > /scratch/v/sentinel
ln -s /scratch/target /scratch/link
if WEBAPI_MIGRATION_EVIDENCE_NEW_VOLUME=1 bash /scripts/init-migration-evidence.sh /scratch/v; then exit 4; fi
if WEBAPI_MIGRATION_EVIDENCE_NEW_VOLUME=1 bash /scripts/init-migration-evidence.sh /scratch/link; then exit 5; fi
test "$(stat -c %u:%g:%a /scratch/target)" = 0:0:755
test "$(cat /scratch/v/sentinel)" = keep
`));
