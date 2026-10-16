import {digest,contractIdentity,inspectionIdentity,validateTemplateState,assertOwnedPostgres,sql} from './template-fixture.mjs';
export const approvedIndexes=Object.freeze([{"columns": ["organization_id", "project_id", "started_at", "id"], "definitionHash": "a3c7ab01d654494f10b5e9cb5c76a0dfd9d5755e86c644aa6a5944cff9fc9bad", "descending": [false, false, true, true], "name": "ix_alert_events_organization_project_started_id", "ready": true, "table": "alert_events", "unique": false, "valid": true}, {"columns": ["organization_id", "normalized_name", "id"], "definitionHash": "fa2b4d1865c1f3910fc404969d2910b5b9fd1cab8e390ae1274289aab3e421b9", "descending": [false, false, false], "name": "ix_alert_rules_organization_name_id", "ready": true, "table": "alert_rules", "unique": false, "valid": true}, {"columns": ["environment_id", null, "path", "id"], "definitionHash": "a552f9d4edb81648f93a204b59d85e4e1db041439f35a7d4d9885949e18b89cd", "descending": [false, false, false, false], "name": "ix_api_routes_environment_order_path_id", "ready": true, "table": "api_routes", "unique": false, "valid": true}, {"columns": ["organization_id", "name", "id"], "definitionHash": "93a2cfa4ac8939d9a476d63099732bd6528a56d325471082677faaeebe4c9fe1", "descending": [false, false, false], "name": "ix_policies_organization_name_id", "ready": true, "table": "policies", "unique": false, "valid": true}].map(i=>Object.freeze(i)));
const indexKey=i=>i.table+':'+i.name;
const indexIdentity=values=>values.map(({bytes,...i})=>i).sort((a,b)=>indexKey(a).localeCompare(indexKey(b),'en'));
function assertReference(reference){
 validateTemplateState(reference);const identities=indexIdentity(reference.contract.indexes);
 if(new Set(identities.map(indexKey)).size!==identities.length)throw Error('Duplicate index identity');
 for(const approved of approvedIndexes){const actual=identities.find(i=>indexKey(i)===indexKey(approved));const relation=reference.metadata.objectPermissions.relations.filter(r=>r.name===approved.name);if(relation.length!==1||relation[0].kind!=='i'||relation[0].owner!=='webapi'||relation[0].acl!==null)throw Error('Approved index relation owner or ACL mismatch');if(!actual||digest(actual)!==digest(approved)||actual.unique||!actual.valid||!actual.ready)throw Error('Approved index definition or constraint mismatch');}
}
export function verifyExperimentRole({role,reference,current}){
 if(!['baseline','candidate'].includes(role))throw Error('Invalid experimental role');assertReference(reference);validateTemplateState(current,{requireModel:role==='candidate'});
 const expected=contractIdentity(reference.contract),actual=contractIdentity(current.contract);
 const {indexes:referenceIndexes,...referenceContract}=expected,{indexes:actualIndexes,...currentContract}=actual;
 const expectedMetadata=structuredClone(reference.metadata);if(role==='baseline')expectedMetadata.objectPermissions.relations=expectedMetadata.objectPermissions.relations.filter(r=>!approvedIndexes.some(a=>a.name===r.name));
 if(digest(referenceContract)!==digest(currentContract)||digest(expectedMetadata)!==digest(current.metadata)||digest(inspectionIdentity(reference.inspection))!==digest(inspectionIdentity(current.inspection)))throw Error('Experimental schema, data, history or metadata mismatch');
 const permitted=role==='baseline'?referenceIndexes.filter(i=>!approvedIndexes.some(a=>indexKey(a)===indexKey(i))):referenceIndexes;
 if(digest(indexIdentity(permitted))!==digest(indexIdentity(actualIndexes))||current.inspection.modelMatched!==(role==='candidate'))throw Error('Experimental index role mismatch');
 return {role,modelMatched:current.inspection.modelMatched,experimentRoleVerified:true,missingApprovedIndexes:role==='baseline'?approvedIndexes.map(i=>i.name).sort():[],contractSha256:digest(actual),metadataSha256:digest(current.metadata),indexesSha256:digest(indexIdentity(actualIndexes)),complete:true};
}
export async function prepareIndexExperiment({owner,postgresId,role,reference,readState,docker}){
 if(!['baseline','candidate'].includes(role))throw Error('Invalid experimental role');assertReference(reference);await assertOwnedPostgres(owner,postgresId,docker);
 const before=await readState();verifyExperimentRole({role:'candidate',reference,current:before});
 if(role==='baseline'){
  // All four drops are a single PostgreSQL transaction; errors roll back the
  // complete batch. Never accept SQL, names, CASCADE or IF EXISTS from callers.
  const names=approvedIndexes.map(i=>"'public.\""+i.name+"\"'::regclass").join(',');
  const check="DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_constraint WHERE conindid IN ("+names+")) THEN RAISE EXCEPTION 'Experimental index backs a constraint'; END IF; END $$;";
  await sql(docker,postgresId,'BEGIN; '+check+' '+approvedIndexes.map(i=>'DROP INDEX public."'+i.name+'";').join(' ')+' COMMIT;','webapi_performance');
 }
 return verifyExperimentRole({role,reference,current:await readState()});
}
