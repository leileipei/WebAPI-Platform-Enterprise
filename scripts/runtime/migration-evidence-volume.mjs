import {randomBytes} from 'node:crypto';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
import fs from 'node:fs/promises';
import {constants} from 'node:fs';
import {assertOwnership,docker,inspectResources} from './docker.mjs';
import {RuntimeError,ownedDirectory,writePrivate} from './state.mjs';
const exec=promisify(execFile);
const repository=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const markerName='migration-evidence-provision.json';

function validateState(state) {
  if(!state || !/^[a-z0-9][a-z0-9_-]{0,120}$/.test(state.projectName??'') ||
     !/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(state.ownerId??''))
    throw new RuntimeError('Invalid migration evidence owner.',3);
}
async function readMarker(directory,state) {
  const file=path.join(directory,markerName);
  let before;
  try {before=await fs.lstat(file);}catch(error){if(error.code==='ENOENT')return null;throw error;}
  if(!before.isFile()||before.isSymbolicLink()||before.uid!==process.getuid()||before.nlink!==1||
     (before.mode&0o077)!==0||before.size>2048)throw new RuntimeError('Migration evidence identity record is unsafe.',3);
  const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
  try{
    const opened=await handle.stat();
    if(opened.dev!==before.dev||opened.ino!==before.ino||opened.size!==before.size)
      throw new RuntimeError('Migration evidence identity record changed.',3);
    let marker;try{marker=JSON.parse(await handle.readFile('utf8'));}catch{throw new RuntimeError('Migration evidence identity record is invalid.',3);}
    const after=await handle.stat();
    if(after.dev!==opened.dev||after.ino!==opened.ino||after.size!==opened.size||after.mtimeMs!==opened.mtimeMs||
       !marker||Object.keys(marker).sort().join(',')!=='nonce,ownerId,phase,projectName,schemaVersion,volumeName'||
       marker.schemaVersion!==1||marker.ownerId!==state.ownerId||marker.projectName!==state.projectName||
       marker.volumeName!==state.projectName+'_migration-evidence'||!['Intent','Ready'].includes(marker.phase)||
       !/^[a-f0-9]{64}$/.test(marker.nonce??''))
      throw new RuntimeError('Migration evidence identity record is inconsistent.',3);
    return marker;
  }finally{await handle.close();}
}
async function writeIntent(directory,marker){
  const handle=await fs.open(path.join(directory,markerName),'wx',0o600);
  try{await handle.writeFile(JSON.stringify(marker)+'\n');await handle.sync();}finally{await handle.close();}
  const parent=await fs.open(directory,'r');try{await parent.sync();}finally{await parent.close();}
}
function verifyVolume(state,volume,nonce){
  if(!volume || volume.Name!==state.projectName+'_migration-evidence' || volume.Driver!=='local' || volume.Scope!=='local' ||
     (volume.Options && Object.keys(volume.Options).length!==0) ||
     volume.Labels?.['com.docker.compose.project']!==state.projectName ||
     volume.Labels?.['com.docker.compose.volume']!=='migration-evidence' ||
     volume.Labels?.['com.webapi.runtime.owner']!==state.ownerId ||
     volume.Labels?.['com.webapi.migration.evidence-nonce']!==nonce)
    throw new RuntimeError('Migration evidence volume is missing or its identity changed; inspect before retry.',3);
}

export async function migrationEvidenceMayBeCreated(state,{readPrior=async revision=>
  (await exec('git',['show',revision+':deploy/compose.runtime.yml'],{cwd:repository,maxBuffer:4*1024*1024})).stdout}={}) {
  if(!state?.initialized) return true;
  if(!/^[a-f0-9]{40}$/.test(state.releaseId??'')) throw new RuntimeError('Previous runtime release identity is unavailable.',3);
  let previous;
  try { previous=JSON.parse(await readPrior(state.releaseId)); }
  catch { throw new RuntimeError('Previous runtime release cannot be verified.',3); }
  if(!previous?.volumes || typeof previous.volumes!=='object' || Array.isArray(previous.volumes))
    throw new RuntimeError('Previous runtime volume definition is invalid.',3);
  return !Object.hasOwn(previous.volumes,'migration-evidence');
}

// A creation nonce distinguishes the volume created by this operation from
// an already existing name. Existing evidence is never reinitialized here.
export async function prepareMigrationEvidenceVolume(state,{directory,allowCreate=false,inspect=inspectResources,run=docker,nonce=()=>randomBytes(32).toString('hex')}={}) {
  validateState(state);
  if(!directory||!path.isAbsolute(directory))throw new RuntimeError('Migration evidence identity directory is required.',3);
  directory=await ownedDirectory(directory);
  const name=state.projectName+'_migration-evidence';
  const marker=await readMarker(directory,state);
  const resources=await inspect(state,run);
  assertOwnership(state,resources);
  const existing=resources.find(resource=>resource.Kind==='volume'&&resource.Name===name);
  if(marker?.phase==='Intent')throw new RuntimeError('Migration evidence creation is pending review.',3);
  if(marker?.phase==='Ready') {
    verifyVolume(state,existing,marker.nonce);
    return false;
  }
  if(existing)throw new RuntimeError('Existing migration evidence has no identity record; refuse adoption.',3);
  if(!allowCreate) throw new RuntimeError('Migration evidence volume is missing; restore the retained evidence.',3);
  const token=nonce();
  if(!/^[a-f0-9]{64}$/.test(token)) throw new RuntimeError('Invalid migration evidence creation identity.',3);
  // The durable intent is written before Docker can create anything. A later
  // interruption is left for inspection, even if the named volume is gone.
  await writeIntent(directory,{schemaVersion:1,ownerId:state.ownerId,projectName:state.projectName,volumeName:name,nonce:token,phase:'Intent'});
  const labels={
    'com.docker.compose.project':state.projectName,
    'com.docker.compose.volume':'migration-evidence',
    'com.webapi.runtime.owner':state.ownerId,
    'com.webapi.migration.evidence-nonce':token
  };
  const args=['volume','create'];
  for(const [key,value] of Object.entries(labels)) args.push('--label',key+'='+value);
  args.push(name);
  const created=await run(args);
  if(created.stdout.trim()!==name) throw new RuntimeError('Migration evidence creation is unverified.',3);
  const output=await run(['volume','inspect',name]);
  let volume;
  try { const parsed=JSON.parse(output.stdout); if(!Array.isArray(parsed)||parsed.length!==1) throw new Error(); volume=parsed[0]; }
  catch { throw new RuntimeError('Migration evidence inspection is invalid.',3); }
  verifyVolume(state,volume,token);
  return true;
}

export async function markMigrationEvidenceVolumeReady(state,{directory,inspect=inspectResources,run=docker}={}) {
  validateState(state);
  if(!directory||!path.isAbsolute(directory))throw new RuntimeError('Migration evidence identity directory is required.',3);
  directory=await ownedDirectory(directory);
  const marker=await readMarker(directory,state);
  if(!marker||marker.phase!=='Intent')throw new RuntimeError('Migration evidence creation intent is missing.',3);
  const resources=await inspect(state,run);assertOwnership(state,resources);
  verifyVolume(state,resources.find(resource=>resource.Kind==='volume'&&resource.Name===marker.volumeName),marker.nonce);
  await writePrivate(path.join(directory,markerName),{...marker,phase:'Ready'});
}
