import test from 'node:test';
import assert from 'node:assert/strict';
import {parseApprovalQuery,approvalQuery,createInboxState,inboxReducer,approvalMayAct,approvalOperationMatches,safeApprovalReturnTo,approvalScope,switchApprovalView,approvalAuthority} from '../src/approvals/inbox-state.mjs';
const org='10000000-0000-4000-8000-000000000001',project='20000000-0000-4000-8000-000000000002',env='30000000-0000-4000-8000-000000000003';
const item={id:'r',state:'WaitingApproval',organization:{id:org},project:{id:project},environment:{id:env},approvalEligibility:{canAct:true,currentStepOrder:1}};
const payload={page:{items:[item],total:1,page:1,pageSize:50},counts:{pendingMine:1,handledMine:0,allVisible:1}};
function loaded(){let s=createInboxState('actor',parseApprovalQuery(''));s=inboxReducer(s,{type:'request',authority:'actor',epoch:0,requestId:1});return inboxReducer(s,{type:'result',authority:'actor',epoch:0,requestId:1,payload});}
test('pendingAcrossShellEnvironmentsRetainsUrlFilter',()=>{
 const filter=parseApprovalQuery(`?view=HandledMine&environmentId=${env}&page=2&pageSize=100`);
 assert.equal(parseApprovalQuery('').view,'PendingMine');assert.equal(filter.page,2);
 const state=createInboxState('actor',filter),after=inboxReducer(state,{type:'shell-scope',environmentId:'different'});
 assert.deepEqual(after.filter,filter);assert.deepEqual(parseApprovalQuery(approvalQuery(filter)),filter);
 assert.deepEqual(approvalScope(item),{organizationId:org,projectId:project,environmentId:env});
});
test('lateResponseCannotReplaceNewAuthority',()=>{
 let s=loaded();s=inboxReducer(s,{type:'authority',authority:'new'});
 const after=inboxReducer(s,{type:'result',authority:'actor',epoch:0,requestId:1,payload});
 assert.equal(after.authority,'new');assert.deepEqual(after.rows,[]);assert.equal(after.dialog,null);
});
test('lateFilterAndPollingResponsesCannotReplaceNewRequest',()=>{
 let s=loaded();s=inboxReducer(s,{type:'filter',filter:parseApprovalQuery('?view=AllVisible')});
 s=inboxReducer(s,{type:'result',authority:'actor',epoch:0,requestId:1,payload});assert.deepEqual(s.rows,[]);
 s=inboxReducer(s,{type:'request',authority:'actor',epoch:1,requestId:2});s=inboxReducer(s,{type:'request',authority:'actor',epoch:1,requestId:3});
 const old=inboxReducer(s,{type:'result',authority:'actor',epoch:1,requestId:2,payload});assert.deepEqual(old.rows,[]);
 const current=inboxReducer(s,{type:'result',authority:'actor',epoch:1,requestId:3,payload});assert.equal(current.rows.length,1);
});
test('conflictKeepsCommentButLostReadClearsIt',()=>{
 for(const status of [409,412]){
   let s=inboxReducer(loaded(),{type:'open',item,action:'approve',key:'one-operation'});
   s=inboxReducer(s,{type:'comment',comment:'未提交批注'});
   s=inboxReducer(s,{type:'action-error',status,message:'抢先办理'});
   assert.equal(s.dialog.comment,'未提交批注');assert.equal(s.dialog.key,'one-operation');
   s=inboxReducer(s,{type:'comment',comment:'原步骤的补充意见',key:'edited-operation'});
   assert.equal(s.dialog.conflict,true);
   s=inboxReducer(s,{type:'action-error',status:0,message:'仍有未提交批注'});
   assert.equal(s.dialog.conflict,true,'only a new dialog may act after a conflict');
   s=inboxReducer(s,{type:'lost-read',message:'读取权限已撤销'});
   assert.equal(s.dialog,null);assert.deepEqual(s.rows,[]);assert.equal(s.counts,null);
 }
});
test('serverEligibilityControlsBothListAndDetail',()=>{
 assert.equal(approvalMayAct(item),true);
 for(const value of [{...item,approvalEligibility:{canAct:false}},{...item,approvalEligibility:null},{...item,state:'Ready'},null])assert.equal(approvalMayAct(value),false);
 assert.equal(inboxReducer(loaded(),{type:'open',item:{...item,approvalEligibility:{canAct:false}},action:'approve'}).dialog,null);
});
test('commentChangesStartNewSemanticOperationAndNeverExceedBudget',()=>{
 let s=inboxReducer(loaded(),{type:'open',item,action:'reject',key:'first'});
 assert.ok(s.dialog);
 s=inboxReducer(s,{type:'comment',comment:'新说明',key:'second'});assert.equal(s.dialog.key,'second');assert.equal(s.dialog.comment,'新说明');
 assert.throws(()=>inboxReducer(s,{type:'comment',comment:'x'.repeat(10001),key:'third'}));
});
test('switchingTabClearsHistoricalStatusOnlyAndPreservesScope',()=>{
 const filter=parseApprovalQuery(`?view=AllVisible&status=Rejected&environmentId=${env}&page=3`),next=switchApprovalView(filter,'PendingMine');
 assert.equal(next.status,null);assert.equal(next.page,1);assert.equal(next.environmentId,env);
});
test('externalOrAmbiguousReturnToIsRejected',()=>{
 for(const value of ['https://foreign.invalid/approvals','//foreign.invalid/approvals','/releases','/approvals/extra','/approvals#fragment','/approvals?view=Bad','/approvals?view=PendingMine&view=AllVisible','/approvals\\foreign'])assert.equal(safeApprovalReturnTo(value),null);
 assert.equal(safeApprovalReturnTo('/approvals?view=HandledMine&page=2'),'/approvals?view=HandledMine&page=2&pageSize=50');
});
test('invalidUrlFilterCannotSilentlyBroadenScope',()=>{
 for(const value of ['?environmentId=other','?view=Invalid','?page=0','?pageSize=101','?page=abc','?view=AllVisible&view=PendingMine'])assert.throws(()=>parseApprovalQuery(value));
});
test('authorityIncludesScopeModesAndIgnoresIrrelevantOrdering',()=>{
 const user={id:'actor',permissions:['release.read','approval.act'],scopes:[{scope:{organizationId:org},accessMode:'read'}]};
 assert.equal(approvalAuthority(user),approvalAuthority({...user,permissions:[...user.permissions].reverse()}));
 assert.notEqual(approvalAuthority(user),approvalAuthority({...user,scopes:[{scope:{organizationId:org},accessMode:'read_write'}]}));
});

test('an approval belongs to the eligible release, step and candidate seen on opening',()=>{
 const opened={releaseId:'r',stepOrder:1,candidateHash:'a'.repeat(64)},detail={...item,candidateHash:'a'.repeat(64)};
 assert.equal(approvalOperationMatches(opened,detail),true);
 for(const changed of [{...detail,id:'another-release'},{...detail,approvalEligibility:{canAct:true,currentStepOrder:2}},
   {...detail,candidateHash:'b'.repeat(64)},{...detail,state:'Ready'},{...detail,approvalEligibility:{canAct:false,currentStepOrder:1}},undefined])
   assert.equal(approvalOperationMatches(opened,changed),false);
 assert.equal(approvalOperationMatches({...opened,stepOrder:0},{...detail,approvalEligibility:{canAct:true,currentStepOrder:0}}),false);
});

test('slow inbox polling completes and an explicit refresh during the read follows it',async()=>{
 const {singleFlightInbox}=await import('../src/approvals/inbox-state.mjs');
 assert.equal(typeof singleFlightInbox,'function');
 const reads=[];const poll=singleFlightInbox(()=>new Promise(resolve=>reads.push(resolve)));
 const first=poll();poll();poll(true);poll();assert.equal(reads.length,1);
 reads[0]();await new Promise(resolve=>setImmediate(resolve));assert.equal(reads.length,2);
 reads[1]();await first;
});
