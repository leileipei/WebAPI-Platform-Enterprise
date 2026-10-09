import fs from 'node:fs/promises';
import {createReadStream} from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {validateOwner} from './owner.mjs';
const sha=/^[a-f0-9]{64}$/,keyPattern=/^[a-z][a-z0-9-]{0,63}$/;
const postgresImage='postgres@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24';
function layout(state){
  if(!sha.test(state?.businessFingerprint??'')||!Array.isArray(state.tables)||!state.tables.length||!Array.isArray(state.indexes)||!Array.isArray(state.appliedMigrations))return null;
  if(new Set(state.tables.map(t=>t.name)).size!==state.tables.length||new Set(state.indexes.map(i=>i.table+':'+i.name)).size!==state.indexes.length)return null;
  if(!state.tables.every(t=>sha.test(t.dataHash??'')&&Number.isSafeInteger(t.rows)&&t.rows>=0&&Array.isArray(t.columns))||!state.indexes.every(i=>i.valid===true&&i.ready===true&&sha.test(i.definitionHash??'')))return null;
  return JSON.stringify({businessFingerprint:state.businessFingerprint,appliedMigrations:state.appliedMigrations,tables:state.tables.map(({name,columns,rows,dataHash})=>({name,columns,rows,dataHash})).sort((a,b)=>a.name.localeCompare(b.name)),indexes:state.indexes.map(({bytes,...i})=>i).sort((a,b)=>(a.table+':'+a.name).localeCompare(b.table+':'+b.name))});
}
// Restoration is same pinned PostgreSQL/schema data parity. Physical relation size
// may change under pg_restore; actual row bytes, IDs, history and indexes may not.
export function verifyFixtureRestore(before,after){const a=layout(before),b=layout(after);return a!==null&&b!==null&&a===b;}
async function fixtureDirectory(owner,key,create=false){
  if(!keyPattern.test(key??'')||!validateOwner(owner,{'com.docker.compose.project':owner?.project,'com.webapi.performance.owner':owner?.owner})||path.basename(owner.directory??'')!==owner.project)throw Error('Invalid database fixture identity');
  const directory=path.resolve(owner.directory),info=await fs.lstat(directory);
  if(info.isSymbolicLink()||!info.isDirectory()||(info.mode&0o777)!==0o700||await fs.realpath(directory)!==directory)throw Error('Invalid private fixture directory');
  const fixtures=path.join(directory,'database-fixtures');if(create)await fs.mkdir(fixtures,{mode:0o700}).catch(e=>{if(e.code!=='EEXIST')throw e;});
  const folder=await fs.lstat(fixtures);if(folder.isSymbolicLink()||!folder.isDirectory()||(folder.mode&0o777)!==0o700)throw Error('Invalid private fixture directory');
  return path.join(fixtures,key+'.dump');
}
async function ownedPostgres(owner,postgresId,docker){
  if(!sha.test(postgresId??''))throw Error('Invalid PostgreSQL container identity');
  const values=JSON.parse((await docker(['inspect',postgresId])).stdout);const info=values.length===1?values[0]:null;
  if(!validateOwner(owner,info?.Config?.Labels)||info.Config.Labels['com.docker.compose.service']!=='postgres'||info.Config.Image!==postgresImage||info.State?.Running!==true)throw Error('PostgreSQL ownership/runtime mismatch');
}
async function fileHash(file){const hash=createHash('sha256');for await(const chunk of createReadStream(file))hash.update(chunk);return hash.digest('hex');}
async function safeDocker(docker,args){try{return await docker(args);}catch{throw Error('Owned database fixture command failed; no command output published');}}
export async function captureDatabaseFixture({owner,postgresId,key,state,docker}){
  if(!keyPattern.test(key??'')||layout(state)===null)throw Error('Invalid database fixture state');
  await ownedPostgres(owner,postgresId,docker);
  const file=await fixtureDirectory(owner,key,true);const reserved=await fs.open(file,'wx',0o600);await reserved.close();
  const remote='/tmp/webapi-perf-'+owner.owner+'-'+key+'.dump';
  await safeDocker(docker,['exec','--user','postgres',postgresId,'sh','-c','umask 077; exec pg_dump --format=custom --username=webapi --dbname=webapi_performance --file='+remote]);
  await safeDocker(docker,['cp',postgresId+':'+remote,file]);await fs.chmod(file,0o600);
  await safeDocker(docker,['exec','--user','postgres',postgresId,'rm','--',remote]);
  const info=await fs.lstat(file);if(!info.isFile()||info.isSymbolicLink()||info.size<=0)throw Error('Invalid captured database fixture');
  return {owner:owner.owner,key,bytes:info.size,sha256:await fileHash(file),businessFingerprint:state.businessFingerprint};
}
export async function restoreDatabaseFixture({owner,postgresId,fixture,docker}){
  if(fixture?.owner!==owner?.owner||!keyPattern.test(fixture?.key??'')||!sha.test(fixture?.sha256??'')||!Number.isSafeInteger(fixture.bytes)||fixture.bytes<=0)throw Error('Invalid database fixture receipt');
  const file=await fixtureDirectory(owner,fixture.key),info=await fs.lstat(file);
  if(info.isSymbolicLink()||!info.isFile()||(info.mode&0o777)!==0o600||info.size!==fixture.bytes||await fileHash(file)!==fixture.sha256)throw Error('Private database fixture hash/permissions mismatch');
  // No database command occurs until private file integrity and actual owner pass.
  await ownedPostgres(owner,postgresId,docker);
  const remote='/tmp/webapi-perf-'+owner.owner+'-'+fixture.key+'.dump';
  await safeDocker(docker,['cp',file,postgresId+':'+remote]);
  // Docker copy retains host UID; pg_restore executes as container root while
  // authenticating the same isolated PostgreSQL superuser over its local socket.
  await safeDocker(docker,['exec',postgresId,'dropdb','--username=webapi','--if-exists','webapi_performance']);
  await safeDocker(docker,['exec',postgresId,'createdb','--username=webapi','webapi_performance']);
  await safeDocker(docker,['exec',postgresId,'pg_restore','--username=webapi','--dbname=webapi_performance','--single-transaction','--exit-on-error',remote]);
  await safeDocker(docker,['exec',postgresId,'rm','--',remote]);
  return {owner:owner.owner,key:fixture.key,sha256:fixture.sha256,restored:true};
}
