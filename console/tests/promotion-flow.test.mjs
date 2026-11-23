import test from 'node:test';import assert from 'node:assert/strict';
import {promotionMaySubmit,promotionExecutionLabel,promotionUrl,mappingReady,promotionRouteChoices,confirmationRequired,mayRecordProduction,promotionReturnUrl,deliveryReducer,createDeliveryState} from '../src/delivery/delivery-state.mjs';
test('actual environment identity guards public entry across source to target changes',()=>{const p={sourceEnvironmentId:'test',targetEnvironmentId:'prod'};assert.equal(promotionUrl(p,{id:'test',gatewayPublicUrl:'https://test.example',basePath:'/v1'},'target'),undefined);assert.equal(promotionUrl(p,{id:'prod',gatewayPublicUrl:'https://prod.example',basePath:'/v1'},'target'),'https://prod.example/v1');});
test('submission needs server authority and current matching successful precheck',()=>{const p={status:'Draft',mappingRevision:2,candidateHash:'h',revision:5,eligibility:{canSubmit:true}},proof={canSubmit:true,mappingRevision:2,candidateHash:'h',revision:5};assert(promotionMaySubmit(p,proof));assert(!promotionMaySubmit(p,{...proof,canSubmit:false}));assert(!promotionMaySubmit(p,{...proof,mappingRevision:1}));assert(!promotionMaySubmit({...p,eligibility:{canSubmit:false}},proof));});
test('missing target route can be prepared with an explicit target cluster and policy slots',()=>{assert(mappingReady({routes:[{artifactRouteKey:'r',targetRouteId:null,clusterId:'prod-c',timeoutMs:30000,policies:[]}],applications:[]}));assert(!mappingReady({routes:[{artifactRouteKey:'r',targetRouteId:null,clusterId:'',timeoutMs:30000,policies:[]}],applications:[]}));});
const governedRoute={key:'r',apiId:'api',versionId:'v2',enabled:true,authenticationMode:'ApiKey'};
const governedOptions={governedTarget:true,routes:[
 {id:'matching',apiId:'api',apiVersionId:'v2'},
 {id:'old-version',apiId:'api',apiVersionId:'v1'},
 {id:'legacy-choice',apiId:'api'},
 {id:'another-api',apiId:'other',apiVersionId:'v2'}
]};
const targetMapping=targetRouteId=>({routes:[{artifactRouteKey:'r',targetRouteId,clusterId:'prod-c',timeoutMs:30000,policies:[]}],applications:[]});

test('governed protected routes block saving without an existing same-version target',()=>{
 for(const authenticationMode of ['ApiKey','JWT']){
  const artifact=[{...governedRoute,authenticationMode}];
  for(const id of [null,'old-version','legacy-choice','another-api','removed-route'])
   assert.equal(mappingReady(targetMapping(id),artifact,governedOptions),false);
  assert.equal(mappingReady(targetMapping('matching'),artifact,governedOptions),true);
 }
});
test('governed target proof cannot be satisfied by an omitted route or a restricted route list',()=>{
 const artifact=[governedRoute,{...governedRoute,key:'another'}];
 assert.equal(mappingReady(targetMapping('matching'),artifact,governedOptions),false);
 assert.equal(mappingReady(targetMapping('matching'),[governedRoute],{...governedOptions,routes:null}),false);
});
test('the explicit new route option remains available in legacy, anonymous and disabled mappings',()=>{
 assert.equal(mappingReady(targetMapping(null),[governedRoute],{routes:governedOptions.routes}),true);
 assert.equal(mappingReady(targetMapping(null),[{...governedRoute,authenticationMode:'Anonymous'}],governedOptions),true);
 assert.equal(mappingReady(targetMapping(null),[{...governedRoute,enabled:false}],governedOptions),true);
});
test('governed route options show only the source API and version while legacy options keep all API versions',()=>{
 assert.deepEqual(promotionRouteChoices(governedRoute,governedOptions).map(x=>x.id),['matching']);
 assert.deepEqual(promotionRouteChoices(governedRoute,{routes:governedOptions.routes}).map(x=>x.id),['matching','old-version','legacy-choice']);
 assert.deepEqual(promotionRouteChoices({...governedRoute,versionId:undefined},governedOptions),[]);
});
test('shared credentials require visible explicit acknowledgment',()=>{assert(confirmationRequired({sharedAcrossEnvironments:true}));assert(confirmationRequired({requiredCredentialIds:['retained-key']}));assert(!confirmationRequired({sharedAcrossEnvironments:false,requiredCredentialIds:[]}));});
test('a retained route using a keyless application can still be acknowledged',()=>{
 const choice={sharedAcrossEnvironments:false,requiredCredentialIds:[],retainedApiIds:['orders']};
 assert.equal(confirmationRequired(choice),true);
 assert.equal(confirmationRequired({...choice,retainedApiIds:[]}),false);
});
test('all ACKs still means waiting for independent production verification',()=>{assert.match(promotionExecutionLabel({status:'Verifying',deployment:{nodes:[{status:'Applied'},{status:'Applied'}]}}),/待生产验证/);assert.match(promotionExecutionLabel({status:'VerificationFailed'}),/验证失败/);assert.match(promotionExecutionLabel({status:'DeploymentFailed'}),/下发失败/);assert.match(promotionExecutionLabel({status:'Completed'}),/历史交付完成/);});
test('server production verification eligibility never permits applicant or actual publisher',()=>{const p={requestedBy:'applicant',status:'Verifying',eligibility:{canVerify:true,publisherId:'publisher'}};assert(!mayRecordProduction(p,'applicant'));assert(!mayRecordProduction(p,'publisher'));assert(mayRecordProduction(p,'independent'));assert(!mayRecordProduction({...p,eligibility:{canVerify:false}},'independent'));});
test('approval return preserves encoded cross-environment filters and rejects foreign return',()=>{const q='/approvals?projectId=p&environmentId=test&view=PendingMine';assert.equal(promotionReturnUrl('promotion','?returnTo='+encodeURIComponent(q)),'/delivery/promotions/promotion?returnTo='+encodeURIComponent(q));assert.equal(promotionReturnUrl('promotion','?returnTo=https%3A%2F%2Fforeign'),'/delivery/promotions/promotion');});
test('mapping 409 and 412 keep input while revocation and late source responses clear or ignore it',()=>{const context={actorAuthority:'a',artifactId:'artifact',sourceEnvironmentId:'test',targetEnvironmentId:'prod',authorized:true};let s=createDeliveryState(context);s=deliveryReducer(s,{type:'draft',draft:{routes:[{clusterId:'prod-c'}]}});for(const status of [409,412])assert.deepEqual(deliveryReducer(s,{type:'failed',tag:{...context,epoch:0},status}).draft,s.draft);const revoked=deliveryReducer(s,{type:'failed',tag:{...context,epoch:0},status:403});assert.equal(revoked.draft,undefined);assert.equal(deliveryReducer(revoked,{type:'loaded',tag:{...context,epoch:0},data:{url:'https://test.example'}}).data,undefined);});
test('restricted risk and eligibility reasons are readable without invented zero counts',async()=>{const {promotionRiskLabel,deliveryReasonLabel}=await import('../src/delivery/delivery-state.mjs');assert.match(promotionRiskLabel({visibility:'Restricted'}),/受限/);assert.doesNotMatch(promotionRiskLabel({visibility:'Restricted'}),/0/);assert.match(deliveryReasonLabel('current_required_passed_evidence_incomplete'),/证据/);assert.doesNotMatch(deliveryReasonLabel('unrecognized_backend_code'),/unrecognized/);});
test('late mapping input cannot revive a revoked or different draft',()=>{const c={actorAuthority:'a',artifactId:'old',sourceEnvironmentId:'test',targetEnvironmentId:'prod',authorized:true};const tag={...c,epoch:0};const revoked=deliveryReducer(createDeliveryState(c),{type:'failed',tag,status:403});assert.equal(deliveryReducer(revoked,{type:'draft',tag,draft:{applicationId:'private'}}).draft,undefined);const changed=deliveryReducer(createDeliveryState(c),{type:'context',context:{...c,artifactId:'new'}});assert.equal(deliveryReducer(changed,{type:'draft',tag,draft:{applicationId:'old-private'}}).draft,undefined);});
test('explicit fresh revision adoption keeps input and cannot revive revoked authority',()=>{const c={actorAuthority:'a',artifactId:'p',sourceEnvironmentId:'test',targetEnvironmentId:'prod',authorized:true},tag={...c,epoch:0};const draft={clusterId:'prod'};let s=deliveryReducer(createDeliveryState(c),{type:'draft',draft});s=deliveryReducer(s,{type:'failed',tag,status:412});const next=deliveryReducer(s,{type:'adopt',tag,data:{revision:7},draft});assert.equal(next.conflict,false);assert.equal(next.data.revision,7);assert.deepEqual(next.draft,draft);const revoked=deliveryReducer(s,{type:'failed',tag,status:403});assert.equal(deliveryReducer(revoked,{type:'adopt',tag,data:{revision:7},draft}).data,undefined);});
test('precheck success text does not claim conditions are unmet and unknown remains explicit',async()=>{const {promotionCheckLabel}=await import('../src/delivery/delivery-state.mjs');assert.match(promotionCheckLabel({status:'Passed',reasonCode:'current_accepted_source'}),/来源/);assert.doesNotMatch(promotionCheckLabel({status:'Passed',reasonCode:'future_passed_check'}),/未满足/);assert.match(promotionCheckLabel({status:'Unknown',reasonCode:'future_unknown_check'}),/未知/);});
test('a blocked precheck explains which change conflicts with the retained target route',async()=>{
 const {promotionCheckLabel}=await import('../src/delivery/delivery-state.mjs');
 const failure=reasonCode=>promotionCheckLabel({status:'Failed',reasonCode});
 assert.match(failure('promotion_retained_version_conflict'),/路由.*版本/);
 assert.match(failure('promotion_retained_access_changed'),/路由.*(授权|访问范围)/);
});
test('pipeline stage return remains attached to original promotion link',()=>{const path='/delivery/pipeline-stages/stage';assert.equal(promotionReturnUrl('promotion','?returnTo='+encodeURIComponent(path)),'/delivery/promotions/promotion?returnTo='+encodeURIComponent(path));});
