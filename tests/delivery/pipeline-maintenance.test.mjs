import test from 'node:test';import assert from 'node:assert/strict';import {validatePipelineMaintenance} from '../../scripts/delivery/pipeline-maintenance.mjs';
const valid={state:{ownerId:'own',projectName:'webapi-enterprise-local',ports:{console:4192,gatewayA:4196,gatewayB:4197}},expectedOwner:'own',reportsSupported:true,resources:[{Kind:'volume',Name:'webapi-enterprise-local_verification-reports',Labels:{'com.webapi.runtime.owner':'own'}}],newEntry:'manage-pipeline.sh',existingEntries:['manage-delivery.sh']};
test('maintenance refuses foreign owner',()=>assert.throws(()=>validatePipelineMaintenance({...valid,expectedOwner:'foreign'}),/owner/));
test('maintenance requires protected report volume',()=>assert.throws(()=>validatePipelineMaintenance({...valid,resources:[]}),/report/));
test('maintenance refuses overwriting old registered entry',()=>assert.throws(()=>validatePipelineMaintenance({...valid,newEntry:'manage-delivery.sh'}),/overwrite/));
test('maintenance preserves owned original entry ports',()=>assert.equal(validatePipelineMaintenance(valid),true));
