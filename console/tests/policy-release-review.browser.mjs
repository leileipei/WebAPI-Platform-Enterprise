// Run against a built console with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after,before,test} from 'node:test';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const origin='http://127.0.0.1', dist=process.env.WEBAPI_CONSOLE_DIST;
const id=n=>'61000000-0000-0000-0000-'+String(n).padStart(12,'0');
const org=id(1), project=id(2), env=id(3), policy='6abc0000-0000-0000-0000-000000000004', api=id(5), otherApi=id(6);
const v1=id(11), v2=id(12), retired=id(13), stock=id(14);
const r1=id(21), r2=id(22), retiredRoute=id(23), stockRoute=id(24), extraRoute=id(25);
const versions=[[v1,api,'1.0','Published'],[v2,api,'2.0','Draft'],[retired,api,'0.1','Retired'],[stock,otherApi,'1.0','Draft']]
  .map(([id,apiId,version,status])=>({id,apiId,version,status,changeType:'compatible',revision:1}));
const routes=[[r1,v1],[r2,v2],[retiredRoute,retired],[stockRoute,stock],[extraRoute,v1]]
  .map(([id,apiVersionId])=>({id,environmentId:env,apiVersionId,routeName:'测试路由'}));
const ref=(routeId,apiId=api,referenceKind='Working',environmentId=env)=>({
  routeId,apiId,referenceKind,environmentId,name:'路由引用',sourceRevision:1,configVersion:null
});
const pageOf=(items,page,size=2)=>({items:items.slice((page-1)*size,page*size),page,pageSize:size,total:items.length});
const button=(root,name)=>root.getByRole('button',{name,exact:true});
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

async function openReview(t,references=[ref(r1)],availableRoutes=routes,entryId=policy) {
  const page=await browser.newPage();
  t.after(()=>page.close());
  page.setDefaultTimeout(4000);
  const state={
    references, boundRoutes:references.filter(r=>r.referenceKind==='Working'&&r.environmentId===env).map(r=>r.routeId),
    previewRequests:[], creates:[], revision:1, reverseRevisions:false, base:0
  };
  await page.route('**/*',route=>{
    const request=route.request(), url=new URL(request.url()), path=url.pathname;
    if(path.startsWith('/assets/'))return route.fulfill({path:join(dist,path.slice(1))});
    if(!path.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
    const sub=path.slice('/api/v1'.length), pageNumber=Number(url.searchParams.get('page')||1);
    const reply=(json,status=200)=>route.fulfill({json,status});
    if(sub==='/auth/me')return reply({id:id(8),username:'editor',displayName:'发布员',permissions:['policy.read','route.read','api.read','api.version.read','release.create'],
      scopes:[{scope:{organizationId:org},accessMode:'read_write'}]});
    if(sub==='/scope-tree')return reply({organizations:[{id:org,name:'组织'}],projects:[{id:project,name:'项目',organizationId:org}],
      environments:[{id:env,name:'测试',projectId:project}]});
    if(sub==='/auth/csrf')return reply({token:'fixture'});
    if([policy,entryId].some(id=>sub==='/policies/'+id))return reply({id:policy,organizationId:org,projectId:project,name:'超时策略',type:'timeout',config:'{"timeoutMs":1000}',enabled:true,versionNo:1});
    if(sub==='/policies/limits')return reply({});
    if([policy,entryId].some(id=>sub==='/policies/'+id+'/references'))return reply(pageOf(state.references,pageNumber));
    if(sub==='/environments/'+env+'/routes')return reply(pageOf(availableRoutes,pageNumber));
    if(sub==='/apis/'+api+'/versions'||sub==='/apis/'+otherApi+'/versions')return reply(versions.filter(v=>v.apiId===sub.split('/')[2]));
    if(sub==='/environments/'+env)return reply({id:env,desiredConfigVersion:state.base});
    if(sub==='/environments/'+env+'/releases/preview'){
      const body=request.postDataJSON(), selected=versions.filter(v=>body.versionIds.includes(v.id));
      state.previewRequests.push(body);
      if(new Set(selected.map(v=>v.apiId)).size!==body.versionIds.length||selected.some(v=>v.status==='Retired'))
        return reply({code:'foreign_version',detail:'版本跨范围、重复选择同一API或已停用。'},422);
      const selectedRoutes=availableRoutes.filter(r=>body.versionIds.includes(r.apiVersionId));
      const resourceRevisions=[...selected.map(v=>({type:'version',id:v.id,revision:1})),{type:'policy',id:policy,revision:state.revision}];
      return reply({versions:selected.map(version=>({version,api:{id:version.apiId}})),routes:selectedRoutes,clusters:[],applications:[],
        bindings:selectedRoutes.filter(r=>state.boundRoutes.includes(r.id)).map(r=>({routeId:r.id,policyId:policy,priority:0})),
        policies:[{id:policy,type:'timeout',enabled:true,config:'{"timeoutMs":1000}',revision:state.revision}],
        resourceRevisions:state.reverseRevisions?resourceRevisions.reverse():resourceRevisions});
    }
    if(sub==='/environments/'+env+'/releases'){
      state.creates.push({key:request.headers()['idempotency-key'],body:request.postDataJSON()});
      return route.abort('failed');
    }
    throw new Error('Unexpected policy release request '+request.method()+' '+sub);
  });
  await page.goto(origin+'/policies/'+entryId);
  await button(page,'提交发布').click();
  const panel=page.getByRole('heading',{name:'提交发布 · 目标环境'}).locator('..');
  const load=button(panel,'加载受影响 API 版本'), preview=button(panel,'读取完整发布 Review'), create=button(panel,'确认 Review，创建发布草稿');
  await load.click();
  await preview.waitFor();
  async function read(){await preview.click();await create.waitFor();}
  async function attempt(){
    const failed=page.waitForEvent('requestfailed',{predicate:r=>r.method()==='POST'&&new URL(r.url()).pathname.endsWith('/releases')});
    await create.click();
    await failed;
    await panel.getByRole('alert').waitFor();
  }
  return {state,panel,load,preview,create,read,attempt};
}

test('only working route bindings in the target environment offer a non-retired version, across pages',async t=>{
  const {state,panel,read,create}=await openReview(t,[ref(r2,api,'FrozenRelease'),ref(r2,api,'Runtime'),ref(r2,api,'Working',id(90)),
    ref(retiredRoute),ref(extraRoute),ref(r1)]);
  const offered=await panel.locator('.form-grid').innerText();
  assert.ok(!offered.includes(v2),'an unbound sibling version cannot be offered for release');
  assert.ok(!offered.includes(retired),'a retired version cannot be offered for release');
  assert.equal(offered.split(v1).length-1,1,'two bound routes of one version offer it once');
  await read();
  assert.deepEqual(state.previewRequests.map(r=>r.versionIds),[[v1]]);
  assert.equal(await create.isEnabled(),true);
});

test('two bound versions of one API require an explicit choice and switching versions invalidates the review',async t=>{
  const {state,panel,read,preview,create}=await openReview(t,[ref(r1),ref(r2)]);
  assert.equal(await preview.isDisabled(),true,'two bound versions must not be submitted together by default');
  const choice=panel.getByRole('combobox',{name:new RegExp(api)});
  await choice.selectOption(v2);
  await read();
  await choice.selectOption(v1);
  assert.equal(await create.count(),0);
  await read();
  assert.deepEqual(state.previewRequests.map(r=>r.versionIds),[[v2],[v1]]);
  await choice.selectOption('');
  assert.equal(await preview.isDisabled(),true);
});

test('a deep link with an alternate GUID spelling can review the canonical policy returned by the server',async t=>{
  for(const entryId of [policy.toUpperCase(),policy.replaceAll('-','')]){
    const {panel,preview,create}=await openReview(t,[ref(r1)],[routes[0]],entryId);
    await preview.click();
    await panel.locator('[role=alert]').or(create).waitFor();
    assert.equal(await create.count(),1,'the binding belongs to the current policy regardless of its URL spelling');
  }
});

test('losing a binding for one selected API during review prevents creating a partial policy draft',async t=>{
  const {state,panel,preview,create,load,read}=await openReview(t,[ref(r1),ref(stockRoute,otherApi)],routes.filter(r=>[r1,stockRoute].includes(r.id)));
  state.boundRoutes=[r1];
  state.references=[ref(r1)];
  await preview.click();
  await panel.locator('[role=alert]').or(create).waitFor();
  assert.equal(await create.count(),0,'a different API still referencing the policy cannot mask the removed binding');
  assert.deepEqual(state.creates,[]);
  await load.click();
  await preview.waitFor();
  await read();
  assert.deepEqual(state.previewRequests.at(-1).versionIds,[v1]);
});

test('retrying an unknown draft result sends the same reviewed body and idempotency key',async t=>{
  const {state,read,attempt}=await openReview(t,[ref(r1)],[routes[0]]);
  await read();
  await attempt();
  await attempt();
  assert.equal(state.creates.length,2);
  assert.ok(state.creates[0].key);
  assert.deepEqual(state.creates[1],state.creates[0]);
});

test('re-reading identical revisions preserves the attempt while changed revisions and baseline get new keys',async t=>{
  const {state,read,attempt}=await openReview(t,[ref(r1)],[routes[0]]);
  await read();await attempt();
  state.reverseRevisions=true;
  await read();await attempt();
  assert.deepEqual(state.creates[1],state.creates[0],'ordering the same revisions differently must preserve the retried command');
  state.revision++;
  await read();await attempt();
  assert.notEqual(state.creates[2].key,state.creates[1].key);
  assert.equal(state.creates[2].body.resourceRevisions.find(r=>r.type==='policy').revision,2);
  state.base++;
  await read();await attempt();
  assert.notEqual(state.creates[3].key,state.creates[2].key);
  assert.equal(state.creates[3].body.baseConfigVersion,1);
});

test('changing the selected versions after an unknown result sends a new draft key',async t=>{
  const {state,panel,read,attempt}=await openReview(t,[ref(r1),ref(stockRoute,otherApi)],routes.filter(r=>[r1,stockRoute].includes(r.id)));
  await read();await attempt();
  const stockChoice=panel.getByRole('combobox',{name:new RegExp(otherApi)});
  if(await stockChoice.count())await stockChoice.selectOption('');
  else await panel.getByRole('checkbox',{name:new RegExp(stock)}).uncheck();
  await read();await attempt();
  assert.notEqual(state.creates[1].key,state.creates[0].key);
  assert.deepEqual(state.creates.map(r=>r.body.versionIds),[[v1,stock],[v1]]);
});
