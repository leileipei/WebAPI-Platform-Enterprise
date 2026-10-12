import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {validateOwner} from './owner.mjs';
export const postgresImage='postgres@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24';
const database='webapi_performance';
export const canonical=value=>Array.isArray(value)?value.map(canonical):value&&typeof value==='object'?Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])])):value;
export const digest=value=>createHash('sha256').update(JSON.stringify(canonical(value))).digest('hex');
export const contractIdentity=contract=>({...contract,indexes:contract.indexes.map(({bytes,...index})=>index)});
export const inspectionIdentity=inspection=>({appliedMigrations:inspection.appliedMigrations,pendingMigrations:inspection.pendingMigrations,pendingModelChanges:inspection.pendingModelChanges,businessFingerprint:inspection.businessFingerprint});
const keyName=key=>{if(key!=='gateway-controlplane-100'&&!/^(ordinary|scope|reference)-(100|1000|10000)$/.test(key??''))throw Error('Invalid template key');return database+'_t_'+key.replaceAll('-','_');};
export async function assertOwnedPostgres(owner,postgresId,docker){
 if(!validateOwner(owner,{'com.docker.compose.project':owner?.project,'com.webapi.performance.owner':owner?.owner})||!/^[a-f0-9]{64}$/.test(postgresId??''))throw Error('Invalid PostgreSQL owner');
 const actual=JSON.parse((await docker(['inspect',postgresId])).stdout);
 if(actual.length!==1||actual[0].Id!==postgresId||!validateOwner(owner,actual[0].Config?.Labels)||actual[0].Config.Labels['com.docker.compose.service']!=='postgres'||actual[0].Config.Image!==postgresImage||actual[0].State?.Running!==true)throw Error('PostgreSQL ownership or image mismatch');
}
export async function sql(docker,postgresId,statement,target='postgres'){if(!['postgres','webapi_performance'].includes(target))throw Error('Untrusted database target');return (await docker(['exec','-i',postgresId,'psql','-X','-A','-t','-U','webapi','-d',target,'-v','ON_ERROR_STOP=1','-c',statement])).stdout.trim();}
async function privateDirectory(directory,mode){const info=await fs.lstat(directory);if(info.isSymbolicLink()||!info.isDirectory()||(info.mode&0o777)!==mode||await fs.realpath(directory)!==path.resolve(directory))throw Error('Template directory is not private or contains a symlink');}
function validateMetadata(metadata){
 const d=metadata?.database;if(d?.owner!=='webapi'||d.allowConnections!==true||d.isTemplate!==false||d.encoding!=='UTF8'||d.connectionLimit!==-1)throw Error('Unsupported database owner or configuration');
 if(!Array.isArray(d.acl)||!Array.isArray(metadata.settings))throw Error('Invalid database privileges');
 const aclKeys=new Set();for(const a of d.acl){const k=[a.grantee,a.privilege].join(':');if(a.grantor!=='webapi'||!['webapi','PUBLIC'].includes(a.grantee)||!['CONNECT','CREATE','TEMPORARY'].includes(a.privilege)||typeof a.grantable!=='boolean'||aclKeys.has(k))throw Error('Unsupported database ACL role or privilege');aclKeys.add(k);}
 const roles=new Set();for(const setting of metadata.settings){if(!['ALL','webapi'].includes(setting.role)||roles.has(setting.role)||!Array.isArray(setting.settings))throw Error('Unsupported database setting role');roles.add(setting.role);const keys=new Set();for(const entry of setting.settings){const [name,value,...extra]=entry.split('=');const normalized=name.toLowerCase();if(extra.length||keys.has(normalized)||!((normalized==='timezone'&&value==='UTC')||(normalized==='work_mem'&&value==='16MB')))throw Error('Unsupported database setting');keys.add(normalized);}}
 if(!Array.isArray(metadata.migrationRows)||!metadata.objectPermissions||!Array.isArray(metadata.effective))throw Error('Missing complete metadata');
}
export function validateTemplateState(state,{requireModel=true}={}){
 const c=state?.contract,i=state?.inspection;if(c?.schemaVersion!==1||!Array.isArray(c.tables)||!c.tables.length||!Array.isArray(c.indexes)||!Array.isArray(c.appliedMigrations)||!i||i.pendingModelChanges!==false||i.pendingMigrations?.length!==0||(requireModel&&i.modelMatched!==true))throw Error('Incomplete target-model contract');
 const names=new Set();for(const t of c.tables){if(names.has(t.name)||!t.columns?.length||t.columnTypes?.length!==t.columns.length||t.notNull?.length!==t.columns.length||!t.primary?.length||!Number.isSafeInteger(t.rows)||t.rows<0||!/^[a-f0-9]{64}$/.test(t.dataHash??''))throw Error('Invalid full schema contract');names.add(t.name);}
 if(digest(c.appliedMigrations)!==digest(i.appliedMigrations)||digest(c.appliedMigrations)!==digest(state.metadata?.migrationRows?.map(x=>x.migration)))throw Error('Migration history mismatch');
 if(c.indexes.some(x=>!x.valid||!x.ready))throw Error('Invalid or unready indexes');validateMetadata(state.metadata);return state;
}
async function noSessions(docker,id,names){const rows=JSON.parse(await sql(docker,id,"SELECT COALESCE(jsonb_agg(jsonb_build_object('database',datname,'state',state)),'[]'::jsonb) FROM pg_stat_activity WHERE datname IN ("+names.map(n=>"'"+n+"'").join(',')+')'));if(!Array.isArray(rows)||rows.length)throw Error('Template/work database still has sessions; no sessions were killed');}
async function stopOwnerApps(owner,docker){
 const ids=(await docker(['ps','-q','--no-trunc','--filter','label=com.docker.compose.project='+owner.project])).stdout.trim().split(/\s+/).filter(Boolean);
 if(!ids.length)return;const containers=JSON.parse((await docker(['inspect',...ids])).stdout);if(containers.length!==ids.length||containers.some(c=>!ids.includes(c.Id)||!validateOwner(owner,c.Config?.Labels)))throw Error('Application owner mismatch');
 const apps=containers.filter(c=>['controlplane','gateway-a','gateway-b','worker'].includes(c.Config.Labels['com.docker.compose.service']));if(apps.length)await docker(['stop',...apps.map(c=>c.Id)]);
}
function receiptFor(owner,key,sourceProduct,state,privateContractSha256){return {schemaVersion:1,owner:owner.owner,key,sourceProduct,postgresImage,contractSha256:digest(contractIdentity(state.contract)),metadataSha256:digest(state.metadata),privateContractSha256};}
export async function captureTemplateFixture({owner,postgresId,key,sourceProduct,readState,docker}){
 const template=keyName(key);if(!/^[a-f0-9]{40}$/.test(sourceProduct??'')||typeof readState!=='function')throw Error('Invalid source product or reader');await privateDirectory(owner.directory,0o700);await assertOwnedPostgres(owner,postgresId,docker);
 const state=validateTemplateState(await readState());await stopOwnerApps(owner,docker);await noSessions(docker,postgresId,[database,template]);
 const directory=path.join(owner.directory,'templates');await fs.mkdir(directory,{mode:0o700,recursive:true});await privateDirectory(directory,0o700);
 const bytes=Buffer.from(JSON.stringify({schemaVersion:1,owner:owner.owner,key,sourceProduct,state}));const file=path.join(directory,key+'.json');await fs.writeFile(file,bytes,{mode:0o600,flag:'wx'});
 await sql(docker,postgresId,'ALTER DATABASE '+database+' RENAME TO '+template);await sql(docker,postgresId,'ALTER DATABASE '+template+' ALLOW_CONNECTIONS false');
 return receiptFor(owner,key,sourceProduct,state,createHash('sha256').update(bytes).digest('hex'));
}
function restoreSql(metadata){
 const statements=['ALTER DATABASE '+database+' RESET ALL','ALTER ROLE webapi IN DATABASE '+database+' RESET ALL'];
 for(const setting of metadata.settings)for(const entry of setting.settings){const [name,value]=entry.split('=');const prefix=setting.role==='ALL'?'ALTER DATABASE '+database:'ALTER ROLE webapi IN DATABASE '+database;statements.push(prefix+' SET '+name+" TO '"+value+"'");}
 for(const role of ['webapi','PUBLIC'])statements.push('REVOKE ALL ON DATABASE '+database+' FROM '+role);
 for(const a of metadata.database.acl)statements.push('GRANT '+a.privilege+' ON DATABASE '+database+' TO '+a.grantee+(a.grantable?' WITH GRANT OPTION':''));return statements.join('; ')+';';
}
export async function restoreTemplateFixture({owner,postgresId,fixture,readState,docker}){
 const template=keyName(fixture?.key);if(fixture.schemaVersion!==1||fixture.owner!==owner.owner||fixture.postgresImage!==postgresImage||!/^[a-f0-9]{40}$/.test(fixture.sourceProduct??''))throw Error('Invalid template receipt');
 await privateDirectory(owner.directory,0o700);const directory=path.join(owner.directory,'templates');await privateDirectory(directory,0o700);const file=path.join(directory,fixture.key+'.json'),info=await fs.lstat(file);
 if(info.isSymbolicLink()||!info.isFile()||(info.mode&0o777)!==0o600||info.size>32*1024*1024)throw Error('Template contract is not private');
 const bytes=await fs.readFile(file);if(createHash('sha256').update(bytes).digest('hex')!==fixture.privateContractSha256)throw Error('Private template contract hash mismatch');const input=JSON.parse(bytes);const expected=validateTemplateState(input.state);
 if(input.owner!==owner.owner||input.key!==fixture.key||input.sourceProduct!==fixture.sourceProduct||digest(receiptFor(owner,input.key,input.sourceProduct,expected,fixture.privateContractSha256))!==digest(fixture))throw Error('Template receipt contract mismatch');
 await assertOwnedPostgres(owner,postgresId,docker);await stopOwnerApps(owner,docker);await noSessions(docker,postgresId,[database,template]);
 const frozen=JSON.parse(await sql(docker,postgresId,"SELECT jsonb_build_object('owner',pg_get_userbyid(datdba),'allowConnections',datallowconn,'isTemplate',datistemplate,'sessions',(SELECT count(*) FROM pg_stat_activity WHERE datname='"+template+"')) FROM pg_database WHERE datname='"+template+"'"));
 if(frozen?.owner!=='webapi'||frozen.allowConnections!==false||frozen.isTemplate!==false||frozen.sessions!==0)throw Error('Template is not frozen or ownership mismatch');
 await sql(docker,postgresId,'DROP DATABASE IF EXISTS '+database);await sql(docker,postgresId,'CREATE DATABASE '+database+' TEMPLATE '+template+' OWNER webapi');await sql(docker,postgresId,restoreSql(expected.metadata));
 const state=validateTemplateState(await readState());if(digest(contractIdentity(state.contract))!==fixture.contractSha256||digest(state.metadata)!==fixture.metadataSha256||digest(inspectionIdentity(state.inspection))!==digest(inspectionIdentity(expected.inspection)))throw Error('Restored template contract or metadata mismatch');
 return {restored:true,state,receipt:fixture};
}
