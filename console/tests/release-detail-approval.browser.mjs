// The built console uses only local assets and synthetic API responses.
import assert from 'node:assert/strict';
import {after,before,test} from 'node:test';
import {createRequire} from 'node:module';
import {join} from 'node:path';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const origin='http://127.0.0.1',dist=process.env.WEBAPI_CONSOLE_DIST;
const hashA='a'.repeat(64),hashB='b'.repeat(64);
const button=(scope,name)=>scope.getByRole('button',{name,exact:true});
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

function release(id,candidateHash=hashA,stepOrder=1){
  return {id,environmentId:'env',releaseNo:'申请 '+id,state:'WaitingApproval',releaseType:'publish',requestedBy:'applicant',
    createdAt:'2026-11-01T00:00:00Z',baselineConfigVersion:0,targetConfigVersion:0,deploymentSequence:null,rollbackOf:null,
    frozenCandidate:{versions:[],routes:[],clusters:[],applications:[],resourceRevisions:[],policies:[],bindings:[]},
    candidateHash,approvalSteps:[1,2].map(step=>({id:'step-'+step,stepOrder:step,roleCode:'reviewer',
      status:step<stepOrder?'Approved':'Pending',actorId:null,comment:null,actedAt:null})),
    targets:[],errors:[],approvalEligibility:{canAct:true,currentStepOrder:stepOrder,reasonCode:null}};
}
async function fixture(t){
  const page=await browser.newPage(),errors=[],writes=[];
  const state={releases:{one:release('one')},writeResponse:null};
  t.after(()=>page.close());
  t.after(()=>assert.deepEqual(errors,[]));
  page.setDefaultTimeout(5500);
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',async route=>{
    const req=route.request(),path=new URL(req.url()).pathname;
    if(path.startsWith('/assets/'))return route.fulfill({path:join(dist,path.slice(1))});
    if(!path.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
    const endpoint=path.slice('/api/v1'.length),reply=(json,status=200)=>route.fulfill({status,json});
    if(endpoint==='/auth/me')return reply({id:'reviewer',username:'reviewer',displayName:'测试审批人',authSource:'local',
      permissions:['release.read','approval.act'],scopes:[{scope:{organizationId:'org'},accessMode:'read_write'}]});
    if(endpoint==='/auth/csrf')return reply({token:'fixture-csrf'});
    if(endpoint==='/scope-tree')return reply({organizations:[{id:'org',name:'组织'}],
      projects:[{id:'project',organizationId:'org',name:'项目'}],environments:[{id:'env',projectId:'project',name:'环境'}]});
    if(endpoint==='/environments/env/snapshots')return reply({items:[],total:0,page:1,pageSize:50});
    if(endpoint==='/approvals'){
      const {id,releaseNo,state:status,releaseType,requestedBy,createdAt,approvalEligibility}=state.releases.one;
      const item={id,releaseNo,state:status,releaseType,requestedBy,createdAt,approvalEligibility,applicantDisplayName:'申请人',
        organization:{id:'org',name:'组织',code:'Org'},project:{id:'project',name:'项目',code:'Project'},
        environment:{id:'env',name:'环境',code:'Env'},approvedCount:0,requiredCount:2,
        risk:{visibility:'Visible',coverage:null,counts:null,reviewCount:0}};
      const items=approvalEligibility.canAct?[item]:[];
      return reply({page:{items,total:items.length,page:1,pageSize:50},counts:{pendingMine:items.length,handledMine:0,allVisible:1}});
    }
    const [,resource,routeId,action]=endpoint.split('/'),id=routeId?.toLowerCase();
    if(resource==='releases'&&state.releases[id]){
      if(req.method()==='GET'&&!action)return reply(state.releases[id]);
      if(req.method()==='POST'&&['approve','reject'].includes(action)){
        writes.push({id,action,body:req.postDataJSON(),key:req.headers()['idempotency-key']});
        if(state.writeResponse)return state.writeResponse(reply);
        const current=state.releases[id],step=current.approvalEligibility.currentStepOrder;
        const next=action==='approve'&&step===1?2:null;
        state.releases[id]={...current,state:next?'WaitingApproval':action==='approve'?'Ready':'Rejected',
          approvalEligibility:{canAct:false,currentStepOrder:next,reasonCode:'independent_approval_required'}};
        return reply(state.releases[id]);
      }
    }
    throw Error('Unexpected approval fixture request '+req.method()+' '+endpoint);
  });
  const openPage=async(id='one')=>{
    await page.goto(origin+'/releases/'+id);
    await button(page,'批准当前步骤').waitFor();
  };
  const open=async(action)=>{
    await button(page,action==='approve'?'批准当前步骤':'拒绝').click();
    const dialog=page.getByRole('dialog');
    await dialog.waitFor();
    return {dialog,comment:dialog.getByRole('textbox',{name:/审批意见/}),confirm:dialog.locator('footer button')};
  };
  return {page,state,writes,openPage,open};
}

for(const action of ['approve','reject'])test('release detail binds '+action+' and sends it only once',async t=>{
  const {state,writes,openPage,open}=await fixture(t);
  await openPage();
  let finish,started;
  const hold=new Promise(resolve=>{finish=resolve;});
  const sent=new Promise(resolve=>{started=resolve;});
  t.after(finish);
  state.writeResponse=async reply=>{started();await hold;return reply(state.releases.one);};
  const {dialog,comment,confirm}=await open(action);
  await comment.fill('已核对第一步候选');
  await confirm.dblclick();
  await sent;
  assert.equal(await confirm.isDisabled(),true);
  assert.equal(writes.length,1);
  assert.deepEqual(writes[0].body,{comment:'已核对第一步候选',expectedStepOrder:1,expectedCandidateHash:hashA});
  assert.ok(writes[0].key);
  finish();
  await dialog.waitFor({state:'hidden'});
});

for(const change of ['step','candidate'])test('release detail keeps its opening '+change+' across a recheck',async t=>{
  const {page,state,writes,openPage,open}=await fixture(t);
  await openPage();
  const changeRelease=()=>{state.releases.one=change==='step'?release('one',hashA,2):release('one',hashB,1);};
  // A candidate may have changed before the dialog's very first re-read.
  if(change==='candidate')changeRelease();
  const {dialog,comment,confirm}=await open(change==='step'?'approve':'reject');
  await comment.fill('保留给原候选的意见');
  if(change==='step'){
    await page.waitForFunction(()=>!document.querySelector('[role=dialog] footer button')?.disabled);
    changeRelease(); // The detail page polls every three seconds.
    await page.getByRole('cell',{name:'Approved',exact:true}).waitFor();
  }
  await dialog.getByText(/批注已保留/).waitFor();
  assert.equal(await confirm.isDisabled(),true);
  assert.equal(await comment.inputValue(),'保留给原候选的意见');
  await dialog.getByText(/步骤 1/).waitFor();
  await dialog.getByText(hashA.slice(0,16),{exact:false}).waitFor();
  assert.deepEqual(writes,[]);
});

test('a 409 retains the draft and requires a fresh release detail decision',async t=>{
  const {page,state,writes,openPage,open}=await fixture(t);
  await openPage();
  state.writeResponse=reply=>{
    state.releases.one=release('one',hashB,2);
    state.writeResponse=null;
    return reply({code:'approval_step_changed',detail:'服务端步骤已变化'},409);
  };
  const {dialog,comment,confirm}=await open('reject');
  await comment.fill('原步骤意见');
  await confirm.click();
  await dialog.getByRole('alert').filter({hasText:'服务端步骤已变化'}).waitFor();
  assert.equal(await comment.inputValue(),'原步骤意见');
  await comment.fill('仍属于原步骤');
  assert.equal(await confirm.isDisabled(),true);
  await button(dialog,'关闭').click();
  await button(dialog,'确认放弃').click();
  await page.getByRole('cell',{name:'Approved',exact:true}).waitFor();
  const next=await open('reject');
  assert.equal(await next.comment.inputValue(),'');
  await next.comment.fill('重新核对第二步');
  await next.confirm.click();
  await next.dialog.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(w=>w.body),[
    {comment:'原步骤意见',expectedStepOrder:1,expectedCandidateHash:hashA},
    {comment:'重新核对第二步',expectedStepOrder:2,expectedCandidateHash:hashB}]);
  assert.notEqual(writes[0].key,writes[1].key);
});

test('switching releases drops the old dialog before an approval can target another release',async t=>{
  const {page,state,writes,openPage,open}=await fixture(t);
  const secondId='aaffffee-0000-4000-8000-000000000002';
  state.releases[secondId]={...release(secondId,hashB,2),rollbackOf:'one'};
  await openPage(secondId.toUpperCase());
  await page.getByRole('link',{name:'原发布'}).click();
  await page.getByText('申请 one',{exact:true}).waitFor();
  const old=await open('approve');
  await page.goBack();
  await page.getByText('申请 '+secondId,{exact:true}).waitFor();
  await old.dialog.waitFor({state:'hidden'});
  const next=await open('approve');
  await next.comment.fill('第二个申请的意见');
  await next.confirm.click();
  await next.dialog.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(({id,body})=>({id,body})),[
    {id:secondId,body:{comment:'第二个申请的意见',expectedStepOrder:2,expectedCandidateHash:hashB}}]);
});

test('an incomplete release detail cannot open either approval action',async t=>{
  const {page,state,writes,openPage}=await fixture(t);
  state.releases.one.candidateHash=null;
  await openPage();
  for(const name of ['批准当前步骤','拒绝'])assert.equal(await button(page,name).isDisabled(),true);
  assert.deepEqual(writes,[]);
});

test('the inbox resolves its candidate from detail and retries the same operation after a transient error',async t=>{
  const {page,state,writes}=await fixture(t);
  state.writeResponse=reply=>{state.writeResponse=null;return reply({detail:'审批服务暂不可用'},503);};
  await page.goto(origin+'/approvals');
  await button(page.getByRole('row').filter({hasText:'申请 one'}),'批准').click();
  const dialog=page.getByRole('dialog');
  await dialog.getByText(hashA.slice(0,16)).waitFor();
  await dialog.getByRole('textbox',{name:/审批意见/}).fill('审批中心复核');
  const confirm=button(dialog,'确认批准');
  await confirm.click();
  await dialog.getByRole('alert').filter({hasText:'审批服务暂不可用'}).waitFor();
  await confirm.click();
  await dialog.waitFor({state:'hidden'});
  assert.equal(writes.length,2);
  assert.deepEqual(writes[0].body,{comment:'审批中心复核',expectedStepOrder:1,expectedCandidateHash:hashA});
  assert.deepEqual(writes[1],writes[0]);
});
