import test from 'node:test';
import assert from 'node:assert/strict';
import {assertOwnership,ownedDocker} from '../../scripts/runtime/docker.mjs';
const state={projectName:'webapi-enterprise-local',ownerId:'11111111-1111-4111-8111-111111111111'};
test('unknownProjectIsNeverAdopted',async()=>{for(const owner of [undefined,'22222222-2222-4222-8222-222222222222']){const resources=[{Name:'existing',Labels:{'com.docker.compose.project':state.projectName,...(owner?{'com.webapi.runtime.owner':owner}:{})}}];assert.throws(()=>assertOwnership(state,resources),/owner/i);let mutations=0;await assert.rejects(ownedDocker(state,['stop'],{inspect:async()=>resources,run:async()=>{mutations++;}}),/owner/i);assert.equal(mutations,0);}});
test('ownedResourceAllowsMutationButAnotherProjectDoesNot',async()=>{const resource={Name:'local-pg',Labels:{'com.docker.compose.project':state.projectName,'com.webapi.runtime.owner':state.ownerId}};assert.doesNotThrow(()=>assertOwnership(state,[resource]));assert.throws(()=>assertOwnership(state,[{...resource,Labels:{...resource.Labels,'com.docker.compose.project':'other'}}]),/project/i);assert.doesNotThrow(()=>assertOwnership(state,[]));});
