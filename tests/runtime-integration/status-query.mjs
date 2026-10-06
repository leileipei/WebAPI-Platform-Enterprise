// Real PostgreSQL characterization of the maintenance query; no original runtime DB is used.
import assert from 'node:assert/strict';import {randomUUID} from 'node:crypto';
import {docker} from '../../scripts/runtime/docker.mjs';import {getRuntimeStatus} from '../../scripts/runtime/status.mjs';
const pg='webapi-enterprise-core-test-postgres-1',db='test_readiness_'+randomUUID().replaceAll('-','');
const labels=JSON.parse((await docker(['inspect','--format','{{json .Config.Labels}}',pg])).stdout);assert.equal(labels['com.docker.compose.project'],'webapi-enterprise-core-test');
const sql=(text,database=db)=>docker(['exec',pg,'psql','-U','webapi','-d',database,'-At','-v','ON_ERROR_STOP=1','-c',text]);
const env='11111111-1111-4111-8111-111111111111',release='22222222-2222-4222-8222-222222222222',owner=randomUUID();
const state={schemaVersion:1,projectName:'webapi-enterprise-local-test-'+owner,ownerId:owner,binding:{environmentId:env},releaseId:'a'.repeat(40)};
const services=['postgres','redis','collector','prometheus','loki','tempo','control-plane','worker','console','gateway-a','gateway-b'];
const resources=services.map(name=>({Kind:'container',Id:name,Config:{Labels:{'com.docker.compose.project':state.projectName,'com.webapi.runtime.owner':owner,'com.docker.compose.service':name}},State:{Running:true,Health:{Status:'healthy'}}}));
let checked=0;
const status=()=>getRuntimeStatus(state,{inspect:async()=>resources,run:async(args)=>args[2]==='psql'?sql(args.at(-1)):{exitCode:0,stdout:'',stderr:''}});
async function expect(phase){const r=await status();assert.deepEqual(r.errors,[]);assert.equal(r.phase,phase);checked++;return r;}
await sql('CREATE DATABASE '+db,'postgres');
try {
 await sql(`CREATE TABLE environments(id uuid,status text,desired_config_version bigint,deployment_sequence bigint);
 CREATE TABLE gateway_nodes(id uuid,environment_id uuid,node_name text,instance_id text,enabled boolean,status text,last_heartbeat_at timestamptz,current_config_version bigint,current_deployment_sequence bigint,metadata jsonb);
 CREATE TABLE gateway_acks(node_id uuid,release_id uuid,instance_id text,config_version bigint,deployment_sequence bigint,success boolean,payload_hash text);
 CREATE TABLE release_records(id uuid,environment_id uuid,to_config_version bigint,deployment_sequence bigint,status text);
 CREATE TABLE gateway_config_versions(environment_id uuid,version_no bigint,snapshot_hash text,status text);
 INSERT INTO environments VALUES('${env}','Active',4,4);INSERT INTO release_records VALUES('${release}','${env}',4,4,'Succeeded');INSERT INTO gateway_config_versions VALUES('${env}',4,repeat('a',64),'Published');
 INSERT INTO gateway_nodes SELECT ('33333333-3333-4333-8333-33333333333'||i)::uuid,'${env}','local-gateway-'||CASE i WHEN 1 THEN 'a' ELSE 'b' END,'instance-'||i,true,'Ready',now(),4,4,jsonb_build_object('runtimeApplication',jsonb_build_object('schemaVersion',1,'instanceId','instance-'||i,'releaseId','${release}','configVersion',4,'deploymentSequence',4,'payloadHash',repeat('a',64))) FROM generate_series(1,2) AS i;`);
 const r=await expect('Ready');assert.ok(r.nodes.every(n=>n.runtimeConfirmed===true&&n.publicationAcknowledged===false));
 for(const [path,value] of [['instanceId','"old"'],['releaseId','"99999999-9999-4999-8999-999999999999"'],['configVersion','3'],['deploymentSequence','3'],['payloadHash','"wrong"'],['schemaVersion','2'],['schemaVersion','"1"'],['configVersion','"4"'],['deploymentSequence','"4"'],['configVersion','null'],['schemaVersion','[]']]) {
  await sql(`UPDATE gateway_nodes SET metadata=jsonb_set(metadata,'{runtimeApplication,${path}}','${value}'::jsonb) WHERE node_name='local-gateway-a'`);await expect('Degraded');
  await sql(`UPDATE gateway_nodes SET metadata=(SELECT metadata FROM gateway_nodes WHERE node_name='local-gateway-b') WHERE node_name='local-gateway-a';UPDATE gateway_nodes SET metadata=jsonb_set(metadata,'{runtimeApplication,instanceId}','"instance-1"'::jsonb) WHERE node_name='local-gateway-a'`);
 }
 await sql("UPDATE gateway_nodes SET metadata=metadata #- '{runtimeApplication,payloadHash}' WHERE node_name='local-gateway-a'");await expect('Degraded');
 await sql("UPDATE gateway_nodes SET metadata=(SELECT metadata FROM gateway_nodes WHERE node_name='local-gateway-b') WHERE node_name='local-gateway-a';UPDATE gateway_nodes SET metadata=jsonb_set(metadata,'{runtimeApplication,instanceId}','\"instance-1\"'::jsonb) WHERE node_name='local-gateway-a'");
 await sql("UPDATE gateway_nodes SET last_heartbeat_at=now()-interval '121 seconds' WHERE node_name='local-gateway-a'");await expect('Degraded');await sql('UPDATE gateway_nodes SET last_heartbeat_at=now()');
 await sql("UPDATE gateway_nodes SET enabled=false WHERE node_name='local-gateway-a'");await expect('Degraded');await sql('UPDATE gateway_nodes SET enabled=true');
 await sql("UPDATE release_records SET status='Failed'");await expect('Degraded');await sql("UPDATE release_records SET status='Succeeded'");
 await sql("UPDATE gateway_config_versions SET snapshot_hash=repeat('b',64)");await expect('Degraded');await sql("UPDATE gateway_config_versions SET snapshot_hash=repeat('a',64)");
 await sql('UPDATE environments SET deployment_sequence=5');await expect('Degraded');await sql('UPDATE environments SET deployment_sequence=4');
 await sql(`UPDATE gateway_nodes SET metadata='{}'::jsonb;INSERT INTO gateway_acks SELECT id,'${release}',instance_id,4,4,true,repeat('a',64) FROM gateway_nodes`);
 const original=await expect('Ready');assert.ok(original.nodes.every(n=>n.publicationAcknowledged===true&&n.runtimeConfirmed===false));
 await sql("UPDATE gateway_acks SET instance_id='old-instance'");await expect('Degraded');
 console.log(JSON.stringify({complete:true,realPostgres:true,checks:checked,originalRuntimeDatabaseUntouched:true,temporaryDatabaseRemovedOnExit:true}));
} finally {await sql('DROP DATABASE '+db+' WITH (FORCE)','postgres');}
