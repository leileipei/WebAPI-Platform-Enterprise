import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after,before,test} from 'node:test';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist=process.env.WEBAPI_CONSOLE_DIST,origin='http://127.0.0.1';
const read=['api.read','cluster.read'], write=['api.create','api.edit','cluster.write','route.write','alert.rule.manage'];
const orgs=['甲','乙'].map((label,i)=>{
  const id=n=>'e2100000-0000-4000-8000-'+String(i*10+n).padStart(12,'0');
  return {id:id(1),project:id(2),env:id(3),api:id(4),group:id(5),cluster:id(6),destination:id(7),label};
});
const [first,second]=orgs;
const tree={
  organizations:orgs.map(o=>({id:o.id,name:o.label+'组织'})),
  projects:orgs.map(o=>({id:o.project,organizationId:o.id,name:o.label+'项目'})),
  environments:orgs.map(o=>({id:o.env,projectId:o.project,name:o.label+'环境'}))
};
const api=o=>({id:o.api,organizationId:o.id,projectId:o.project,name:o.label+' API',code:o.label,ownerUserId:'user',lifecycleStatus:'Draft',workingRevision:3});
const group=o=>({id:o.group,projectId:o.project,name:o.label+'分组',sortOrder:0,revision:2});
const cluster=o=>({id:o.cluster,projectId:o.project,environmentId:o.env,name:o.label+'集群',status:'Active',revision:1,
  destinations:[{id:o.destination,clusterId:o.cluster,name:o.label+'实例',enabled:true,weight:1,revision:1}]});
const rule=o=>({id:'rule-'+o.label,revision:3,logicRevision:1,evaluationState:'Known',lastSuccessAt:null,
  definition:{organizationId:o.id,projectId:o.project,environmentId:o.env,name:o.label+'规则',expression:'latency_p95_ms > 100',
    metric:'latency_p95_ms',severity:'Warning',enabled:true,forSeconds:300,windowSeconds:60,targetType:'Environment',targetId:null,
    notification:{inConsole:true,requestedChannels:[],externalEnabled:false,emailRecipients:[],notifyRecovery:true,
      retryPolicy:{maxAttempts:5,baseDelaySeconds:30,maxDelaySeconds:900,expiresAfterMinutes:1440}}}});
const button=(page,name)=>page.getByRole('button',{name,exact:true});
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

async function fixture(t,{roles=[],secondMode='read_write',secondGrants,secondRole=[],mixed=false,legacy=false}={}){
  const page=await browser.newPage(),writes=[],errors=[];
  page.setDefaultTimeout(5000);
  page.on('pageerror',error=>errors.push(error.message));
  t.after(async()=>{await page.close();assert.deepEqual(errors,[]);});
  const state={roles:[{organizationId:first.id,permissions:[...read,...write]},
    {organizationId:second.id,permissions:[...read,'app.write',...secondRole]},...roles]};
  await page.route('**/*',async route=>{
    const req=route.request(),url=new URL(req.url()),path=url.pathname;
    assert.equal(url.origin,origin);
    if(path.startsWith('/assets/'))return route.fulfill({path:join(dist,path.slice(1))});
    if(!path.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
    const resource=path.slice('/api/v1'.length),reply=json=>route.fulfill({json});
    if(resource==='/auth/me')return reply({id:'user',displayName:'目录用户',username:'catalog',authSource:'local',
      permissions:[...new Set(state.roles.flatMap(r=>r.permissions))].sort(),...(!legacy&&{rolePermissions:state.roles}),isPlatformAdministrator:false,
      scopes:[{scope:{organizationId:first.id},accessMode:'read_write'},
        ...(secondGrants??[{scope:{organizationId:second.id},accessMode:secondMode}])]});
    if(resource==='/scope-tree')return reply(tree);
    if(resource==='/auth/csrf'){if(state.csrfWait)await state.csrfWait;return reply({token:'fixture'});}
    const collection=items=>({items,page:1,pageSize:50,total:items.length});
    if(req.method()!=='GET'){writes.push({method:req.method(),path:resource,body:req.postDataJSON(),etag:req.headers()['if-match']});return reply({});}
    if(resource==='/openapi/import-previews')return reply([]);
    if(resource==='/observability/alert-rules')return reply(collection(orgs.filter(o=>o.project===url.searchParams.get('projectId')).map(rule)));
    for(const o of orgs){
      const list=mixed?orgs:[o];
      if(resource==='/projects/'+o.project+'/apis')return reply(collection(list.map(api)));
      if(resource==='/projects/'+o.project+'/api-groups')return reply(list.map(group));
      if(resource==='/apis/'+o.api)return reply({api:api(o),workingRevision:3,runningConfigVersion:{},versions:[],
        canReadVersions:false,canWriteVersions:false,canReadSchemas:false,canWriteSchemas:false});
      if(resource==='/environments/'+o.env+'/clusters')return reply(collection(list.map(cluster)));
      if(resource==='/clusters/'+o.cluster)return reply(cluster(o));
    }
    errors.push(req.method()+' '+resource);
    return route.fulfill({status:404,json:{detail:'未设置夹具'}});
  });
  const go=path=>page.goto(origin+path);
  const select=async o=>{await page.getByRole('combobox',{name:'当前组织'}).selectOption(o.id);};
  const refresh=async(idle=true)=>{
    const loaded=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1/auth/me');
    await page.evaluate(()=>window.dispatchEvent(new Event('permission-refresh')));await loaded;
    if(idle)await page.waitForLoadState('networkidle');
    else await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve()))));
  };
  return {page,go,select,refresh,state,writes};
}

async function available(f,o){
  const {page,go,select}=f;
  await go('/apis');await select(o);await page.getByRole('cell',{name:o.label+'分组'}).waitFor();
  const newApi=await button(page,'＋ 新建 API').isEnabled(),groups=await button(page,'管理分组').isEnabled(),
    editGroup=await button(page,'编辑').isEnabled(),importLink=!!await page.getByRole('link',{name:'导入 OpenAPI'}).count();
  await page.getByRole('link',{name:'查看详情'}).click();await page.getByRole('heading',{name:o.label+' API'}).waitFor();
  const editApi=await button(page,'编辑 API').isEnabled();
  await go('/clusters');await page.getByRole('cell',{name:o.label+'集群'}).waitFor();
  const newCluster=await button(page,'＋ 新建集群').isEnabled(),editRow=await button(page,'编辑').isEnabled(),copy=await button(page,'复制').isEnabled();
  await page.getByRole('link',{name:'查看实例'}).click();await page.getByRole('cell',{name:o.label+'实例'}).waitFor();
  const editCluster=await button(page,'编辑集群').isEnabled(),addInstance=await button(page,'＋ 新增实例').isEnabled(),
    editInstance=await button(page,'编辑').isEnabled(),probe=await button(page,'立即探测').isEnabled();
  return {newApi,groups,editGroup,importLink,editApi,newCluster,editRow,copy,editCluster,addInstance,editInstance,probe};
}

for(const [name,options,secondWrites] of [
  ['role in another organization',{},false],
  ['platform role within writable resource scope',{roles:[{organizationId:null,permissions:write}]},true],
  ['platform role with a read-only resource scope',{roles:[{organizationId:null,permissions:write}],secondMode:'read'},false]
]){
  test(name+' controls catalog, group, API, cluster and destination actions after switching organizations',async t=>{
    const f=await fixture(t,options);
    for(const [org,expected] of [[first,true],[second,secondWrites],[first,true]]){
      const observed=await available(f,org);
      t.diagnostic(JSON.stringify({organization:org.label,observed}));
      assert.deepEqual(Object.entries(observed).filter(([,enabled])=>enabled!==expected).map(([action])=>action),[]);
    }
    assert.deepEqual(f.writes,[]);
  });
}

test('directory exposes only the selected project groups and APIs when a collection includes both organizations',async t=>{
  const f=await fixture(t,{mixed:true});await f.go('/apis');
  for(const o of orgs){
    await f.select(o);await f.page.getByRole('cell',{name:o.label+'分组'}).waitFor();
    const other=orgs.find(org=>org!==o);
    assert.equal(await f.page.getByRole('cell',{name:other.label+'分组'}).count(),0);
    assert.equal(await f.page.getByRole('cell',{name:other.label+' API'}).count(),0);
  }
  assert.deepEqual(f.writes,[]);
});

test('an open group editor rechecks a role change even when aggregate permissions and resource scopes stay the same',async t=>{
  const f=await fixture(t);await f.go('/apis');await button(f.page,'管理分组').click();
  const dialog=f.page.getByRole('dialog');await dialog.getByLabel('名称').fill('授权待核对');
  f.state.roles=[{organizationId:first.id,permissions:[...read,'app.write']},{organizationId:second.id,permissions:[...read,...write]}];
  await f.refresh();
  assert.equal(await button(f.page,'管理分组').isEnabled(),false);
  assert.ok(!await dialog.count()||await button(dialog,'保存').isDisabled());
  assert.deepEqual(f.writes,[]);
});

test('project actions and environment actions respect different grants owned by the same role',async t=>{
  const f=await fixture(t,{secondRole:write,secondGrants:[
    {scope:{organizationId:second.id,projectId:second.project},accessMode:'read'},
    {scope:{organizationId:second.id,projectId:second.project,environmentId:second.env},accessMode:'read_write'}
  ]});
  assert.deepEqual(await available(f,second),{newApi:false,groups:false,editGroup:false,importLink:false,editApi:false,
    newCluster:true,editRow:true,copy:true,editCluster:true,addInstance:true,editInstance:true,probe:true});
  assert.deepEqual(f.writes,[]);
});

test('each action uses its own permission and an authorized group edit sends the selected revision',async t=>{
  const f=await fixture(t,{secondRole:['api.create']});
  assert.deepEqual(await available(f,second),{newApi:true,groups:false,editGroup:false,importLink:false,editApi:false,
    newCluster:false,editRow:false,copy:false,editCluster:false,addInstance:false,editInstance:false,probe:false});
  await f.go('/apis');await f.select(first);
  const row=f.page.getByRole('row').filter({has:f.page.getByRole('cell',{name:'甲分组'})});
  await button(row,'编辑').click();
  const dialog=f.page.getByRole('dialog');await dialog.getByLabel('名称').fill('甲分组已复核');
  await button(dialog,'保存').click();await dialog.waitFor({state:'hidden'});
  assert.deepEqual(f.writes.map(r=>[r.method,r.path,r.etag,r.body.name]),[['PUT','/api-groups/'+first.group,'"2"','甲分组已复核']]);
});

test('direct OpenAPI import checks the selected organization for preview creation and management',async t=>{
  const f=await fixture(t);await f.go('/imports');
  const inspect=button(f.page,'解析并固定预览');
  for(const [o,expected] of [[first,true],[second,false],[first,true]]){
    await f.select(o);
    await f.page.waitForFunction(id=>{
      const target=document.querySelector('select[aria-label="导入目标集群"]');
      return target?.value===id&&!target.disabled;
    },o.cluster);
    assert.equal(await inspect.isEnabled(),expected,o.label);
    assert.equal(await f.page.getByRole('heading',{name:'本项目中我的有效预览'}).count(),Number(expected));
  }
  assert.deepEqual(f.writes,[]);
});

test('shared scoped write checks apply to the alert rule page after each organization switch',async t=>{
  const f=await fixture(t);await f.go('/observability/alert-rules');
  for(const [o,expected] of [[first,true],[second,false],[first,true]]){
    await f.select(o);await button(f.page,o.label+'规则').waitFor();
    assert.equal(await button(f.page,'新建规则').isEnabled(),expected,o.label);
    assert.equal(await button(f.page,'禁用').isEnabled(),expected,o.label);
  }
  assert.deepEqual(f.writes,[]);
});

test('legacy aggregate permissions retain server-authorized reads and cannot establish organization write authority',async t=>{
  const f=await fixture(t,{legacy:true});await f.go('/apis');await f.page.getByRole('cell',{name:first.label+' API'}).waitFor();
  assert.equal(await button(f.page,'＋ 新建 API').isEnabled(),false);
  await f.go('/observability/alert-rules');await f.page.getByRole('combobox',{name:'当前环境'}).getByRole('option',{name:'甲环境'}).waitFor({state:'attached'});
  assert.equal(await button(f.page,'新建规则').isEnabled(),false);
  assert.deepEqual(f.writes,[]);
});

for(const target of ['group','api','cluster','alert']){
  test('revoking the organization role cancels a pending '+target+' save before its token arrives',async t=>{
    const f=await fixture(t),token=Promise.withResolvers();
    const [path,action]={group:['/apis','管理分组'],api:['/apis/'+first.api,'编辑 API'],cluster:['/clusters','＋ 新建集群'],
      alert:['/observability/alert-rules','禁用']}[target];
    await f.go(path);await button(f.page,action).click();
    const dialog=f.page.getByRole('dialog');
    if(target!=='alert')await dialog.getByLabel(target==='group'?'名称':target==='api'?'API 名称':'集群名称').fill('待确认的变更');
    f.state.csrfWait=token.promise;
    try{
      const pending=f.page.waitForRequest(r=>new URL(r.url()).pathname==='/api/v1/auth/csrf');
      await button(dialog,'保存').click();await pending;
      f.state.roles=[{organizationId:first.id,permissions:read},{organizationId:second.id,permissions:[...read,...write,'app.write']}];
      await f.refresh(false);
    }finally{token.resolve();}
    await f.page.waitForLoadState('networkidle');
    assert.deepEqual(f.writes.map(r=>r.path),[]);
  });
}
