// Run with a loopback console build and the same explicit browser paths as the other browser fixtures.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {test,after} from 'node:test';

const base = new URL(process.argv[2]);
assert.equal(base.protocol,'http:');
assert.equal(base.hostname,'127.0.0.1');
const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});
after(() => browser.close());

const apiA='api-a', apiB='api-b', env='staging', alt='qa', project='project', foreign='foreign-project';
const apis=[{id:apiA,name:'甲 API'},{id:apiB,name:'乙 API'}].map(a=>({...a,organizationId:'org',projectId:project}));
const versions=api=>['1.0','2.0'].map((version,i)=>({apiId:api,id:api+'-'+(i+1),version,status:'Draft',revision:1}));
const v1=apiB+'-1', v2=apiB+'-2';
const row=(id,apiVersionId,environmentId=env)=>({id,environmentId,apiVersionId,routeName:id,path:'/'+id,methods:['GET'],clusterId:environmentId+'-cluster',priority:100,enabled:true,timeoutMs:2500,revision:7,requireApiKey:true});
const button=(scope,name)=>scope.getByRole('button',{name,exact:true});
const step=(page,n)=>page.locator('.wizard-steps button').nth(n-1);
const selection=page=>page.getByRole('combobox',{name:'向导 API 版本'});
const dialog=page=>page.getByRole('dialog');
const routeRow=(page,id)=>page.getByRole('row').filter({has:page.getByRole('cell',{name:id,exact:true})});

async function fixture(t) {
  const context=await browser.newContext(),page=await context.newPage(),requests=[],errors=[];
  t.after(()=>context.close());
  page.setDefaultTimeout(4500);
  page.on('pageerror',e=>errors.push(e.message));
  const state={versions:versions(apiB),frozen:null,rows:[row('other-api',apiA+'-1'),row('other-version',v1),row('selected-first',v2),row('selected-second',v2),row('other-environment',v2,alt)],delay:null};
  await page.route('**/api/v1/**',async r=>{
    const req=r.request(), url=new URL(req.url()), path=url.pathname.slice('/api/v1'.length);
    const method=req.method(), body=req.postData()?req.postDataJSON():undefined, etag=req.headers()['if-match'];
    requests.push({path,method,body,etag,query:url.searchParams});
    const reply=(value,status=200)=>r.fulfill({status,json:value});
    const paged=(items,size=50)=>{const n=Number(url.searchParams.get('page')||1);return reply({items:items.slice((n-1)*size,n*size),total:items.length,page:n,pageSize:size});};
    if(path==='/auth/me')return reply({id:'tester',username:'fixture',displayName:'向导测试',authSource:'local',permissions:state.permissions||['api.read','api.version.read','environment.read','route.read','route.write','policy.write','release.read','release.create'],scopes:[{scope:{organizationId:'org'},accessMode:'read_write'}]});
    if(path==='/auth/csrf')return reply({token:'synthetic'});
    if(path==='/scope-tree')return reply({organizations:[{id:'org',name:'组织'}],projects:[{id:project,organizationId:'org',name:'选定项目'},{id:foreign,organizationId:'org',name:'其他项目'}],environments:[{id:env,projectId:project,name:'预发'},{id:alt,projectId:project,name:'联调'},{id:'foreign-env',projectId:foreign,name:'其他环境'}]});
    for(const api of apis){
      const vv=api.id===apiB?state.versions:versions(api.id);
      if(path==='/apis/'+api.id)return reply({api,versions:vv,runningConfigVersion:{}});
      if(path==='/apis/'+api.id+'/versions')return reply(vv);
    }
    if(path==='/projects/'+project+'/apis')return paged(apis,1);
    if(path==='/projects/'+project+'/policies')return paged([{id:'selected-policy',type:'timeout',projectId:project,name:'超时策略',enabled:true}]);
    if(path.endsWith('/access-addresses'))return reply({configured:false,routes:[]});
    for(const e of [env,alt,'foreign-env']){
      const prefix='/environments/'+e;
      if(path===prefix)return reply({id:e,desiredConfigVersion:9});
      if(path===prefix+'/clusters')return paged([{id:e+'-cluster',name:e}]);
      if(path===prefix+'/route-defaults')return reply({timeoutMs:2500,source:'Saved'});
      if(path===prefix+'/routes'&&method==='GET'){
        const target=url.searchParams.get('apiVersionId');
        const items=state.rows.filter(item=>item.environmentId===e&&(!target||item.apiVersionId===target));
        if(state.delay?.version===target){const delay=state.delay;state.delay=null;await delay.wait;}
        return paged(items,1);
      }
      if(path===prefix+'/routes'&&method==='POST'){const saved={...row('new-route',body.apiVersionId,e),...body,id:'new-route'};state.rows.push(saved);return reply(saved);}
      if(path===prefix+'/releases/preview'||path===prefix+'/releases'){
        const selected=apis.flatMap(api=>versions(api.id).filter(v=>body.versionIds.includes(v.id)).map(version=>({api,version})));
        const routes=state.rows.filter(row=>row.environmentId===e&&body.versionIds.includes(row.apiVersionId));
        const candidate={versions:selected,routes,resourceRevisions:routes.map(({id,revision})=>({id,revision}))};
        if(path.endsWith('/preview'))return reply(candidate);
        state.frozen=structuredClone(candidate);
        return reply({id:'draft',environmentId:e});
      }
    }
    if(path==='/routes/selected-second'||path==='/routes/selected-second/policies'){
      const selected=state.rows.find(r=>r.id==='selected-second');
      if(method==='PUT'){
        if(etag!=='"'+selected.revision+'"')return reply({detail:'目标修订已变化'},412);
        if(path.endsWith('/policies'))selected.revision++;
        else Object.assign(selected,body,{revision:selected.revision+1});
      }
      return reply(path.endsWith('/policies')?{routeId:selected.id,bindings:[],revision:selected.revision}:selected);
    }
    if(path==='/releases/draft/preview')return reply(state.previewOverride||state.frozen);
    if(path==='/releases/draft/submit')return reply({id:'draft'});
    return reply({detail:'Unexpected fixture request '+path},404);
  });
  async function openWizard(){
    await page.goto(new URL('/apis/'+apiB+'/wizard?step=review',base).href);
    await selection(page).selectOption(v2);
    await step(page,5).click();
  }
  const writes=()=>requests.filter(r=>['PUT','POST'].includes(r.method));
  return {page,requests,errors,state,writes,openWizard};
}

test('the selected API version owns new routes, paged edits, bindings and the submitted release',async t=>{
  const {page,requests,errors,state,writes,openWizard}=await fixture(t);
  await openWizard();
  await button(page,'＋ 新建路由').click();
  const editor=dialog(page);
  await editor.getByLabel('路由名称',{exact:false}).fill('created');
  await editor.getByLabel('请求路径',{exact:false}).fill('/created');
  await button(editor,'保存').click();
  await editor.waitFor({state:'hidden'});
  const create=writes().find(r=>r.path==='/environments/staging/routes');
  assert.equal(create.body.apiVersionId,v2);
  assert.equal(create.body.clusterId,'staging-cluster');
  assert.deepEqual(create.body.methods,['GET']);
  assert.equal(create.body.timeoutMs,2500);
  assert.equal(await selection(page).inputValue(),v2);
  await routeRow(page,'selected-first').waitFor();
  assert.equal(await routeRow(page,'other-api').count(),0);
  assert.equal(await routeRow(page,'other-version').count(),0);
  await button(page,'下一页').click();
  const second=routeRow(page,'selected-second');
  await button(second,'编辑').click();
  await editor.getByLabel('路由名称',{exact:false}).fill('edited');
  await button(editor,'保存').click();
  await editor.waitFor({state:'hidden'});
  const update=writes().find(r=>r.path==='/routes/selected-second');
  assert.equal(update.body.apiVersionId,v2);
  assert.equal(update.body.routeName,'edited');
  assert.equal(update.etag,'"7"');
  await button(routeRow(page,'edited'),'策略绑定').click();
  await editor.getByRole('combobox').filter({has:page.getByRole('option',{name:'超时策略',exact:true})}).selectOption('selected-policy');
  await button(editor,'保存').click();
  await editor.waitFor({state:'hidden'});
  const bind=writes().find(r=>r.path==='/routes/selected-second/policies');
  assert.equal(bind.etag,'"8"');
  assert.deepEqual(bind.body,{bindings:[{policyId:'selected-policy',priority:100}]});
  await step(page,6).click();
  await button(page,'创建发布草稿并预览').click();
  await page.getByRole('cell',{name:v2,exact:true}).waitFor();
  await button(page,'提交审批').click();
  await page.waitForURL('**/releases/draft');
  const releaseWrites=writes().slice(-3);
  assert.deepEqual(releaseWrites.map(r=>r.path),['/environments/staging/releases/preview','/environments/staging/releases','/releases/draft/submit']);
  assert.deepEqual(releaseWrites[0].body,{baseConfigVersion:9,versionIds:[v2]});
  assert.deepEqual(releaseWrites[1].body,{baseConfigVersion:9,versionIds:[v2],resourceRevisions:[{id:'selected-first',revision:7},{id:'selected-second',revision:9},{id:'new-route',revision:7}]});
  assert.ok(requests.some(r=>r.path==='/environments/staging/apis/api-b/access-addresses'&&r.query.get('view')==='working'&&r.query.get('versionId')===v2));
  assert.equal(requests.some(r=>r.path==='/projects/project/apis'),false);
  assert.deepEqual(state.frozen.versions.map(v=>[v.api.id,v.version.id]),[[apiB,v2]]);
  assert.deepEqual(state.frozen.routes.map(r=>r.routeName),['selected-first','edited','created']);
  assert.deepEqual(errors,[]);
});

test('an empty or vanished version and an unrelated project cannot create routes or submit an old draft',async t=>{
  const {page,state,writes,openWizard}=await fixture(t);
  await openWizard();
  await selection(page).selectOption('');
  assert.equal(await button(page,'＋ 新建路由').count(),0);
  await step(page,6).click();
  assert.equal(await button(page,'创建发布草稿并预览').isDisabled(),true);
  await selection(page).selectOption(v2);
  await button(page,'创建发布草稿并预览').click();
  await button(page,'提交审批').waitFor();
  state.versions=versions(apiB).slice(0,1);
  await button(page,'刷新工作数据').click();
  await button(page,'提交审批').waitFor({state:'hidden'});
  assert.equal(await selection(page).inputValue(),'');
  assert.equal(await button(page,'创建发布草稿并预览').isDisabled(),true);
  await step(page,5).click();
  await page.getByRole('combobox',{name:'当前项目'}).selectOption(foreign);
  await step(page,5).click();
  assert.equal(await button(page,'＋ 新建路由').count(),0);
  assert.equal(writes().some(r=>r.path.endsWith('/submit')||r.path.endsWith('/routes')),false);
});

test('late version reads cannot restore old route actions after changing selection or environment',async t=>{
  const {page,state,openWizard}=await fixture(t);
  let finish;
  state.delay={version:v2,wait:new Promise(resolve=>finish=resolve)};
  t.after(()=>finish());
  const entered=page.waitForRequest(r=>r.url().includes('/routes?')&&new URL(r.url()).searchParams.get('apiVersionId')===v2);
  await Promise.all([entered,openWizard()]);
  await selection(page).selectOption(v1);
  await routeRow(page,'other-version').waitFor();
  finish();
  await page.waitForLoadState('networkidle');
  assert.equal(await routeRow(page,'selected-first').count(),0);
  await button(routeRow(page,'other-version'),'编辑').click();
  await page.getByRole('combobox',{name:'当前环境'}).selectOption(alt,{force:true});
  await dialog(page).waitFor({state:'hidden'});
  await step(page,5).click();
  await selection(page).selectOption(v2);
  await routeRow(page,'other-environment').waitFor();
  assert.equal(await routeRow(page,'other-version').count(),0);
});

test('standalone routes retain the API and version selectors and environment pagination',async t=>{
  const {page}=await fixture(t);
  await page.goto(new URL('/routes',base).href);
  await routeRow(page,'other-api').waitFor();
  await button(page,'下一页').click();
  await routeRow(page,'other-version').waitFor();
  await page.getByRole('combobox',{name:'路由所属 API'}).selectOption(apiB);
  await button(page,'＋ 新建路由').click();
  const version=dialog(page).getByLabel('API 版本',{exact:false});
  assert.equal(await version.inputValue(),v1);
  await version.selectOption(v2);
  assert.equal(await version.inputValue(),v2);
});

test('a route moved after opening the editor cannot be rebound or restored with a newer ETag',async t=>{
  const {page,state,writes,openWizard}=await fixture(t);
  await openWizard();
  await routeRow(page,'selected-first').waitFor();
  await button(page,'下一页').click();
  const second=routeRow(page,'selected-second'), editor=dialog(page), selected=state.rows.find(r=>r.id==='selected-second');
  await button(second,'策略绑定').click();
  await editor.waitFor();
  selected.apiVersionId=v1;
  selected.revision++;
  await button(editor,'保存').click();
  await editor.getByRole('alert').waitFor();
  assert.equal(writes().filter(r=>r.path.endsWith('/policies')).length,0);
  await button(editor,'关闭').click();
  await button(second,'编辑').click();
  await editor.getByLabel('路由名称',{exact:false}).fill('stale');
  await button(editor,'保存').click();
  await button(editor,'查看最新内容').click();
  await editor.getByRole('alert').filter({hasText:'路由所属环境或 API 版本已变化'}).waitFor();
  assert.equal(await button(editor,'保持我的输入，采用最新修订').count(),0);
  assert.deepEqual(writes().filter(r=>r.path==='/routes/selected-second').map(r=>r.etag),['"7"']);
  assert.equal(selected.apiVersionId,v1);
});

test('the wizard blocks submission when the draft preview identifies another version or stale revisions',async t=>{
  const {page,state,writes,openWizard}=await fixture(t);
  await openWizard();
  await step(page,6).click();
  state.previewOverride={versions:[{api:apis[1],version:versions(apiB)[0]}],routes:[]};
  await button(page,'创建发布草稿并预览').click();
  await page.getByRole('alert').filter({hasText:'候选与当前 API、版本或环境不一致'}).waitFor();
  assert.equal(await button(page,'提交审批').isDisabled(),true);
  await selection(page).selectOption(v1);
  state.previewOverride={versions:[{api:apis[1],version:versions(apiB)[0]}],routes:[],preconditionsCurrent:false};
  await button(page,'创建发布草稿并预览').click();
  await page.getByRole('alert').filter({hasText:'工作修订已变化'}).waitFor();
  assert.equal(await button(page,'提交审批').isDisabled(),true);
  assert.equal(writes().some(r=>r.path.endsWith('/submit')),false);
});

test('saving a route after returning from review invalidates the previous draft',async t=>{
  const {page,writes,openWizard}=await fixture(t);
  await openWizard();
  await step(page,6).click();
  await button(page,'创建发布草稿并预览').click();
  await page.getByRole('cell',{name:v2,exact:true}).waitFor();
  await step(page,5).click();
  await button(page,'＋ 新建路由').click();
  const editor=dialog(page);
  await editor.getByLabel('路由名称',{exact:false}).fill('after-preview');
  await editor.getByLabel('请求路径',{exact:false}).fill('/after-preview');
  await button(editor,'保存').click();
  await editor.waitFor({state:'hidden'});
  await step(page,6).click();
  assert.equal(await button(page,'提交审批').count(),0);
  assert.equal(await button(page,'创建发布草稿并预览').isEnabled(),true);
  assert.equal(writes().some(r=>r.path.endsWith('/submit')),false);
});

test('a read-only user cannot open route or policy editors or create a release',async t=>{
  const {page,state,writes,openWizard}=await fixture(t);
  state.permissions=['api.read','api.version.read','environment.read','route.read','release.read'];
  await openWizard();
  const first=routeRow(page,'selected-first');
  await first.waitFor();
  for(const control of [button(page,'＋ 新建路由'),button(first,'编辑'),button(first,'策略绑定')])assert.equal(await control.isDisabled(),true);
  await step(page,6).click();
  assert.equal(await button(page,'创建发布草稿并预览').isDisabled(),true);
  assert.deepEqual(writes(),[]);
});
