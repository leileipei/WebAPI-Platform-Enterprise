// Run node --test with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const api = {id:'orders', organizationId:'org', projectId:'project', code:'ORDERS', name:'订单 API'};
const version = {id:'orders-v1', apiId:api.id, version:'1.0', status:'Draft', revision:7, sealedAt:null};
const path = '/apis/'+api.id, versionPath = '/versions/'+version.id;
const read = ['api.read','api.schema.read'], schemaWrite = 'api.schema.write', versionWrite = 'api.version.write';
const allPermissions = [...read,schemaWrite,versionWrite];
const button = (element,name) => element.getByRole('button',{name,exact:true});
const workspaceTree = {
  organizations:[{id:'org',name:'订单组织'},{id:'other',name:'其他组织'}],
  projects:[{id:'project',organizationId:'org',name:'订单项目'},{id:'shipping',organizationId:'org',name:'物流项目'},
    {id:'finance',organizationId:'other',name:'财务项目'}],
  environments:[{id:'dev',projectId:'project',name:'开发'},{id:'qa',projectId:'project',name:'验收'},
    {id:'shipping-env',projectId:'shipping',name:'物流环境'},{id:'finance-env',projectId:'finance',name:'财务环境'}]
};
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

async function fixture(t, {entry='detail', permissions=allPermissions, mode='read_write',
  canReadSchemas=true, canReadVersions=false, canWriteSchemas=true, canWriteVersions=true, versionChanges={},
  workspaces=false, selectedProject, selectedOrganization}={}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(4000);
  const state = {permissions,mode,canReadSchemas,canReadVersions,canWriteSchemas,canWriteVersions,api:{...api},version:{...version,...versionChanges},
    parameters:[{id:'filter',apiVersionId:version.id,name:'筛选条件',location:'query',dataType:'string',required:false,schema:'{"type":"string"}'}],
    schemas:[{id:'body',apiVersionId:version.id,name:'响应内容',schemaType:'response',statusCode:200,contentType:'application/json',schemaJson:'{"type":"object"}'}]};
  const requests = [];
  const routePath = entry==='detail'?path:path+'/wizard';
  if (selectedProject) await page.addInitScript(([org,project])=>{
    localStorage.setItem('core-organization',org);
    localStorage.setItem('core-project',project);
  },[selectedOrganization||'org',selectedProject]);
  await page.route('**/*', async route => {
    const req = route.request(), pathname = new URL(req.url()).pathname, method = req.method();
    if (pathname.startsWith('/assets/')) return route.fulfill({path:join(dist,pathname.slice(1))});
    if (!pathname.startsWith('/api/v1/')) return route.fulfill({path:join(dist,'index.html')});
    const resource = pathname.slice('/api/v1'.length);
    const body = req.postData()?req.postDataJSON():null;
    requests.push({resource,method,body,headers:req.headers()});
    const reply = (json,status=200) => route.fulfill({json,status});
    if (resource==='/auth/me') return reply({id:'actor',username:'user',displayName:'维护人员',authSource:'local',
      permissions:state.permissions,scopes:[{scope:workspaces?{organizationId:api.organizationId}:{organizationId:api.organizationId,projectId:api.projectId},accessMode:state.mode},
        {scope:{organizationId:'other'},accessMode:'read_write'}]});
    if (resource==='/auth/csrf') {
      if (state.csrfWait) await state.csrfWait;
      return reply({token:'fixture-csrf'});
    }
    if (resource==='/scope-tree') return reply(workspaces?workspaceTree:{organizations:[{id:'org',name:'订单组织'}],
      projects:[{id:'project',organizationId:'org',name:'订单项目'}],environments:[]});
    if (workspaces && resource.endsWith('/api-groups')) return reply([]);
    if (workspaces && resource.startsWith('/projects/') && resource.endsWith('/apis')) {
      const items=resource==='/projects/project/apis'?[state.api]:[];
      return reply({items,total:items.length,page:1,pageSize:50});
    }
    if (resource.toLowerCase()==='/apis/'+state.api.id.toLowerCase() && method==='GET') {
      if (state.detailWait) await state.detailWait;
      if (state.detailStatus) return reply({detail:'API 不可用'},state.detailStatus);
      return reply({api:state.api,workingRevision:1,versions:[state.version],runningConfigVersion:{},pendingReleaseId:null,
        canReadSchemas:state.canReadSchemas,canReadVersions:state.canReadVersions,canWriteSchemas:state.canWriteSchemas,canWriteVersions:state.canWriteVersions});
    }
    if (resource===path && method==='PUT') {
      state.api={...state.api,name:body.name};
      return reply(state.api);
    }
    if (resource===versionPath && method==='GET')
      return state.permissions.includes('api.version.read')?reply(state.version):reply({detail:'缺少版本读取权限'},404);
    if (resource===versionPath && method==='PUT') {
      if (!state.canWriteVersions) return reply({detail:'缺少版本权限'},403);
      if (req.headers()['if-match']!=='"'+state.version.revision+'"') return reply({detail:'工作修订已变化'},412);
      state.version={...state.version,version:body.version,revision:state.version.revision+1};
      return reply(state.version);
    }
    if (resource===path+'/versions' && method==='POST') return reply({...version,id:'new-version',version:body.version});
    if (resource===versionPath+'/schema-validation')
      return reply({evaluatedVersionRevision:state.version.revision,result:{status:'Valid',formatMode:body.formatMode,issues:[],coverageIssues:[]}});
    for (const kind of ['parameters','schemas']) {
      if (resource!==versionPath+'/'+kind) continue;
      if (method==='GET') return state.canReadSchemas&&!state.hideDefinitions?reply(state[kind]):reply({detail:'缺少定义读取权限'},404);
      if (!state.canWriteSchemas) return reply({detail:'缺少定义写权限'},403);
      if (req.headers()['if-match']!=='"'+state.version.revision+'"') return reply({detail:'工作修订已变化'},412);
      state[kind]=body.map(row=>({...row,id:row.id??'created',apiVersionId:version.id}));
      state.version={...state.version,revision:state.version.revision+1};
      return reply(state[kind]);
    }
    throw Error('Unexpected browser request: '+method+' '+pathname);
  });
  await page.goto('http://127.0.0.1'+routePath);
  if (selectedProject) await page.waitForLoadState('networkidle');
  else await page.getByRole('cell',{name:'1.0',exact:true}).waitFor();
  async function tab(kind) {
    if (entry==='wizard') await page.locator('.wizard-steps button').filter({hasText:kind==='parameters'?'参数定义':'请求与响应'}).click();
    else await button(page,kind==='parameters'?'参数':'Schema').click();
  }
  const dialog=page.getByRole('dialog');
  const writes=()=>requests.filter(r=>r.method==='PUT'||r.method==='POST'&&!r.resource.endsWith('/schema-validation'));
  return {page,state,requests,tab,dialog,writes};
}

const contract = {openapiSource:'openapi: 3.1.0\n# fixture source',openapiDocument:'{"openapi":"3.1.0","info":{"title":"fixture document"}}',sourceFormat:'yaml'};

for (const entry of ['detail','wizard']) {
  test(entry+' restricts both OpenAPI representations to readers of the current project',async t=>{
    const {page,state}=await fixture(t,{entry,permissions:[...read,'api.version.read'],canReadSchemas:false,versionChanges:contract});
    await button(page,'OpenAPI').click();
    await page.getByText(/缺少.*契约.*读取权限/).waitFor();
    assert.equal(await page.locator('pre').filter({hasText:'fixture'}).count(),0);

    async function refresh() {
      const loaded=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1'+path);
      await button(page,'刷新会话与权限').click();
      await loaded;
    }
    state.canReadVersions=true;
    await refresh();
    await page.locator('pre').filter({hasText:'fixture source'}).waitFor();
    state.canReadVersions=false;
    state.canReadSchemas=true;
    state.version={...state.version,openapiSource:null};
    await refresh();
    await page.locator('pre').filter({hasText:'fixture document'}).waitFor();
    state.canReadSchemas=false;
    await refresh();
    await page.getByText(/缺少.*契约.*读取权限/).waitFor();
    assert.equal(await page.locator('pre').filter({hasText:'fixture'}).count(),0);
  });
}

test('version editor clears stored source when read access changes and preserves it on a metadata-only save',async t=>{
  const {page,state,dialog,writes}=await fixture(t,{versionChanges:contract});
  await button(page,'编辑').click();
  assert.equal(await dialog.getByLabel('原始文档').inputValue(),contract.openapiSource);
  state.canReadSchemas=false;
  const loaded=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1'+path);
  await page.evaluate(()=>window.dispatchEvent(new Event('permission-refresh')));
  await loaded;
  await page.waitForFunction(()=>[...document.querySelectorAll('[role=dialog] textarea')].every(input=>!input.value));
  if (!await dialog.count()) await button(page,'编辑').click();
  await dialog.getByLabel('版本号').fill('1.1');
  await button(dialog,'保存').click();
  await page.getByRole('cell',{name:'1.1'}).waitFor();
  const [write]=writes();
  for (const key of ['openapiSource','openapiDocument','sourceFormat']) assert.equal(Object.hasOwn(write.body,key),false,key);

  await button(page,'编辑').click();
  const replacement='openapi: 3.1.0\n# replacement fixture';
  await dialog.getByLabel('原始文档').fill(replacement);
  await dialog.getByLabel('原文格式').selectOption('yaml');
  await button(dialog,'保存').click();
  await dialog.waitFor({state:'hidden'});
  assert.equal(writes()[1].body.openapiSource,replacement);
  assert.equal(writes()[1].body.sourceFormat,'yaml');
  assert.equal(Object.hasOwn(writes()[1].body,'openapiDocument'),false);
});

for (const entry of ['detail','wizard']) {
  for (const kind of ['parameters','schemas']) {
    test(entry+' '+kind+' saves a definition with schema write alone', async t=>{
      const {page,tab,dialog,writes}=await fixture(t,{entry,permissions:[...read,schemaWrite],canWriteVersions:false});
      assert.equal(await button(page,'＋ 新建版本').isDisabled(),true);
      assert.equal(await button(page,'编辑').isDisabled(),true);
      await tab(kind);
      await page.getByRole('cell',{name:kind==='parameters'?'筛选条件':'响应内容'}).waitFor();
      assert.equal(await button(page,'＋ 新增').isEnabled(),true,'draft definitions use schema write');
      await button(page,'结构 / 原文 / 验证').click();
      await dialog.getByLabel('定义名称').fill('已更新');
      await button(dialog,'保存工作定义').click();
      await page.getByRole('cell',{name:'已更新'}).waitFor();
      const [write]=writes();
      assert.equal(writes().length,1);
      assert.equal(write.resource,versionPath+'/'+kind);
      assert.equal(write.body[0].name,'已更新');
      assert.equal(write.headers['if-match'],'"7"');
      assert.ok(write.headers['idempotency-key']);
    });
  }
  for (const scenario of [
    {name:'version write only', permissions:[...read,versionWrite], canWriteSchemas:false},
    {name:'read only scope', mode:'read_only', canWriteSchemas:false,canWriteVersions:false},
    {name:'schema write from another organization', canWriteSchemas:false},
    {name:'published version without a seal timestamp', versionChanges:{status:'Published'}},
    {name:'sealed draft', versionChanges:{sealedAt:'2026-11-22T00:00:00Z'}}
  ]) {
    test(entry+' keeps definitions readable and immutable for '+scenario.name, async t=>{
      const {page,tab,dialog,writes}=await fixture(t,{entry,...scenario});
      await tab('parameters');
      await page.getByRole('cell',{name:'筛选条件'}).waitFor();
      assert.equal(await button(page,'＋ 新增').isDisabled(),true,scenario.name);
      assert.equal(await button(page,'移除工作定义').isDisabled(),true);
      await button(page,'查看 / 验证').click();
      assert.equal(await dialog.getByLabel('Schema 原文').getAttribute('readonly'),'');
      assert.equal(await button(dialog,'保存工作定义').count(),0);
      await button(dialog,'验证示例').click();
      await dialog.getByText('验证通过',{exact:true}).waitFor();
      assert.deepEqual(writes(),[]);
    });
  }
  test(entry+' skips definition reads without resource schema read access', async t=>{
    const {page,tab,requests,writes}=await fixture(t,{entry,canReadSchemas:false});
    await tab('parameters');
    await page.getByText(/缺少.*读取权限/).waitFor();
    assert.equal(requests.some(r=>r.resource===versionPath+'/parameters'),false);
    assert.equal(await page.getByRole('button',{name:'＋ 新增',exact:true,disabled:false}).count(),0);
    assert.deepEqual(writes(),[]);
  });
}

test('independent version permission allows creating and editing a draft version',async t=>{
  const {page,dialog,writes}=await fixture(t,{permissions:[...read,versionWrite],canWriteSchemas:false});
  await button(page,'编辑').click();
  await dialog.getByLabel('版本号').fill('1.1');
  await button(dialog,'保存').click();
  await page.getByRole('cell',{name:'1.1'}).waitFor();
  await button(page,'＋ 新建版本').click();
  await dialog.getByLabel('版本号').fill('2.0');
  await button(dialog,'保存').click();
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual(writes().map(r=>[r.resource,r.method,r.body.version]),[[versionPath,'PUT','1.1'],[path+'/versions','POST','2.0']]);
});

for (const edit of ['parameters','schemas','version','new-version']) {
  test('an open '+edit+' editor rechecks organization grants even with unchanged session summaries',async t=>{
    const {page,tab,state,dialog,writes}=await fixture(t);
    const definition=['parameters','schemas'].includes(edit);
    if (definition) {
      await tab(edit);
      await button(page,'结构 / 原文 / 验证').click();
    } else await button(page,edit==='version'?'编辑':'＋ 新建版本').click();
    await dialog.getByLabel(definition?'定义名称':'版本号').fill(definition?'原草稿':'3.0');
    const save=button(dialog,definition?'保存工作定义':'保存');
    assert.equal(await save.isEnabled(),true);
    state.canWriteSchemas=false;
    state.canWriteVersions=false;
    let release;
    state.detailWait=new Promise(resolve=>{release=resolve;});
    try {
      const pending=page.waitForRequest(r=>new URL(r.url()).pathname==='/api/v1'+path,{timeout:1800});
      await page.evaluate(()=>window.dispatchEvent(new Event('permission-refresh')));
      await pending;
      await page.waitForFunction(()=>![...document.querySelectorAll('[role=dialog] button')].some(b=>
        ['保存','保存工作定义'].includes(b.textContent)&&!b.disabled));
      if (definition) assert.equal(await dialog.getByRole('button',{name:'验证示例',disabled:false}).count(),0);
    } finally { release(); }
    await page.waitForLoadState('networkidle');
    assert.equal(await save.isVisible()&&await save.isEnabled(),false);
    if (!definition && await dialog.count()) await dialog.evaluate(form=>form.requestSubmit());
    assert.deepEqual(writes(),[]);
  });
}

test('schema conflict can recover using schema read alone and becomes readonly when the latest version is published',async t=>{
  const {page,tab,state,dialog,requests,writes}=await fixture(t,{permissions:[...read,schemaWrite],canWriteVersions:false});
  await tab('schemas');
  await button(page,'结构 / 原文 / 验证').click();
  await dialog.getByLabel('定义名称').fill('未保存内容');
  state.version={...state.version,revision:8,status:'Published'};
  await button(dialog,'保存工作定义').click();
  await button(dialog,'查看最新内容').click();
  await dialog.getByText('当前为只读或封存版本，不提供保存入口。').waitFor();
  assert.equal(await button(dialog,'保存工作定义').count(),0);
  assert.equal(await dialog.getByLabel('定义名称').inputValue(),'未保存内容');
  assert.equal(requests.some(r=>r.method==='GET'&&r.resource===versionPath),false);
  assert.equal(writes().length,1);
});

test('a denied resource refresh stays idle and a manual session refresh restores accessible data',async t=>{
  const {page,state,requests}=await fixture(t);
  state.detailStatus=404;
  await button(page,'刷新会话与权限').click();
  await page.getByText('API 不可用',{exact:true}).waitFor();
  await page.waitForLoadState('networkidle');
  const deniedCount=requests.filter(r=>r.resource===path).length;
  state.detailStatus=undefined;
  await button(page,'刷新会话与权限').click();
  await page.getByRole('cell',{name:'1.0'}).waitFor();
  assert.equal(requests.filter(r=>r.resource===path).length,deniedCount+1);
});

test('a hidden collection closes its open definition and grant recovery starts with a fresh list',async t=>{
  const {page,tab,state,dialog,writes}=await fixture(t);
  await tab('parameters');
  await button(page,'结构 / 原文 / 验证').click();
  state.hideDefinitions=true;
  await page.evaluate(()=>window.dispatchEvent(new Event('permission-refresh')));
  await page.getByText(/缺少.*读取权限/).first().waitFor();
  assert.equal(await dialog.count(),0);
  state.hideDefinitions=false;
  await button(page,'刷新会话与权限').click();
  await page.getByRole('cell',{name:'筛选条件'}).waitFor();
  assert.equal(await dialog.count(),0);
  assert.deepEqual(writes(),[]);
});

test('updated version metadata never allows a stale parameter collection to replace a newer row',async t=>{
  const {page,tab,state,writes}=await fixture(t,{entry:'wizard'});
  await tab('parameters');
  const old=page.getByRole('row').filter({has:page.getByRole('cell',{name:'筛选条件'})});
  await old.waitFor();
  state.parameters.push({...state.parameters[0],id:'added-elsewhere',name:'其他人的新参数'});
  state.version={...state.version,revision:8};
  const reloaded=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1'+path);
  await button(page,'刷新工作数据').click();
  await reloaded;
  const saved=page.waitForResponse(r=>r.request().method()==='PUT');
  await button(old,'移除工作定义').click();
  assert.equal((await saved).status(),200);
  assert.deepEqual(writes().map(write=>[write.headers['if-match'],write.body.map(row=>row.id)]),[['"8"',['added-elsewhere']]]);
});

test('an open version edit retains the revision the user viewed when access is refreshed',async t=>{
  const {page,state,dialog,writes}=await fixture(t);
  await button(page,'编辑').click();
  await dialog.getByLabel('版本号').fill('2.0');
  state.version={...state.version,revision:8,version:'1.1'};
  await page.evaluate(()=>window.dispatchEvent(new Event('permission-refresh')));
  await page.getByRole('cell',{name:'1.1'}).waitFor();
  await button(dialog,'保存').click();
  await dialog.getByText('工作修订已变化',{exact:true}).waitFor();
  assert.equal(writes()[0].headers['if-match'],'"7"');
  assert.equal(await dialog.getByLabel('版本号').inputValue(),'2.0');
});

for (const entry of ['detail','wizard']) {
  test('workspace project switch leaves the '+entry+' editor and opens the new catalog',async t=>{
    const {page,dialog,writes,requests}=await fixture(t,{entry,workspaces:true});
    const project=page.getByRole('combobox',{name:'当前项目'});
    const create=button(page,'＋ 新建版本');
    await create.click();
    await dialog.waitFor();
    await project.selectOption('shipping',{force:true});
    await dialog.waitFor({state:'hidden'});
    await page.waitForLoadState('networkidle');
    assert.equal(await project.inputValue(),'shipping');
    if (await create.isVisible() && await create.isEnabled()) {
      await create.click();
      await dialog.getByLabel('版本号').fill('2.0');
      await button(dialog,'保存').click();
      await dialog.waitFor({state:'hidden'});
    }
    assert.deepEqual(writes().map(r=>[r.method,r.resource]),[]);
    assert.equal(new URL(page.url()).pathname,'/apis');
    assert.ok(requests.some(r=>r.resource==='/projects/shipping/apis'));

    await project.selectOption('project');
    await page.getByRole('link',{name:'查看详情',exact:true}).click();
    await create.click();
    await dialog.getByLabel('版本号').fill('2.0');
    await button(dialog,'保存').click();
    await dialog.waitFor({state:'hidden'});
    assert.deepEqual(writes().map(r=>[r.method,r.resource,r.body.version]),[['POST',path+'/versions','2.0']]);
  });
}

const foreignEntries=[
  {entry:'detail',action:'编辑 API',field:'API 名称',value:'错误组织的改名',org:'other',project:'finance'},
  {entry:'wizard',step:'基本信息',action:'编辑 API',field:'API 名称',value:'错误项目的改名'},
  {entry:'wizard',step:'API 版本',action:'编辑',field:'版本号',value:'2.1'},
  {entry:'wizard',step:'参数定义',action:'结构 / 原文 / 验证',field:'定义名称',value:'错误项目的参数'},
  {entry:'wizard',step:'请求与响应',action:'结构 / 原文 / 验证',field:'定义名称',value:'错误项目的 Schema'}
];
for (const {entry,step,action,field,value,org,project='shipping'} of foreignEntries) {
  test('workspace direct link blocks '+entry+' '+(step||'metadata')+' outside its owning project',async t=>{
    const {page,dialog,requests,writes}=await fixture(t,{entry,workspaces:true,
      selectedProject:project,selectedOrganization:org,permissions:[...allPermissions,'api.edit']});
    assert.equal(await page.getByRole('combobox',{name:'当前项目'}).inputValue(),project);
    const notice=page.getByRole('status').filter({hasText:'订单项目'});
    if (step) {
      const openStep=page.locator('.wizard-steps button').filter({hasText:step});
      if (await openStep.isVisible() && await openStep.isEnabled()) {
        await openStep.click();
        if (field==='定义名称') await Promise.race([notice.waitFor(),page.getByRole('cell',{
          name:step==='参数定义'?'筛选条件':'响应内容',exact:true
        }).waitFor()]);
      }
    }
    const open=button(page,action);
    if (await open.isVisible() && await open.isEnabled()) {
      await open.click();
      await dialog.getByLabel(field).fill(value);
      await button(dialog,field==='定义名称'?'保存工作定义':'保存').click();
      await dialog.waitFor({state:'hidden'});
    }
    assert.deepEqual(writes().map(r=>[r.method,r.resource]),[]);
    assert.equal(requests.some(r=>r.resource===versionPath+'/parameters'||r.resource===versionPath+'/schemas'),false);
    const create=button(page,'＋ 新建版本');
    assert.equal(await create.isVisible() && await create.isEnabled(),false);
    await notice.getByRole('link',{name:'API 目录',exact:true}).click();
    await page.getByRole('heading',{name:'API 目录',exact:true}).waitFor();
  });
}

test('workspace environment switch within the owning project keeps project-level editing available',async t=>{
  const {page,dialog,writes}=await fixture(t,{workspaces:true});
  await page.getByRole('combobox',{name:'当前环境'}).selectOption('qa');
  await button(page,'编辑').click();
  await dialog.getByLabel('版本号').fill('1.1');
  await button(dialog,'保存').click();
  await page.getByRole('cell',{name:'1.1'}).waitFor();
  assert.equal(new URL(page.url()).pathname,path);
  assert.deepEqual(writes().map(r=>[r.method,r.resource,r.body.version]),[['PUT',versionPath,'1.1']]);
});

for (const entry of ['detail','wizard']) {
  test('workspace accepts an uppercase API GUID bookmark for the owning '+entry,async t=>{
    const {page,state,dialog,writes}=await fixture(t,{entry,workspaces:true});
    const guid='35e38fcc-364c-45e2-973b-26aade165df0';
    state.api={...state.api,id:guid};
    state.version={...state.version,apiId:guid};
    await page.goto('http://127.0.0.1/apis/'+guid.toUpperCase()+(entry==='wizard'?'/wizard':''));
    await page.waitForLoadState('networkidle');
    assert.equal(await page.getByRole('cell',{name:'1.0',exact:true}).count(),1);
    await button(page,'编辑').click();
    await dialog.getByLabel('版本号').fill('1.1');
    await button(dialog,'保存').click();
    await page.getByRole('cell',{name:'1.1',exact:true}).waitFor();
    assert.deepEqual(writes().map(r=>[r.method,r.resource]),[['PUT',versionPath]]);
  });
}

test('workspace switch discards a late detail response from the previous project',async t=>{
  const {page,state,writes}=await fixture(t,{workspaces:true});
  const detail=Promise.withResolvers();
  state.detailWait=detail.promise;
  const pending=page.waitForRequest(req=>new URL(req.url()).pathname==='/api/v1'+path);
  try {
    await button(page,'刷新会话与权限').click();
    await pending;
    await page.getByRole('combobox',{name:'当前项目'}).selectOption('shipping');
  } finally { detail.resolve(); }
  await page.waitForLoadState('networkidle');
  assert.equal(await page.getByRole('heading',{name:'订单 API',exact:true}).count(),0);
  assert.equal(new URL(page.url()).pathname,'/apis');
  assert.deepEqual(writes(),[]);
});

for (const target of ['api','parameters']) {
  test('workspace switch cancels a pending '+target+' write before its security token arrives',async t=>{
    const {page,state,tab,dialog,writes}=await fixture(t,{workspaces:true,permissions:[...allPermissions,'api.edit']});
    if (target==='api') await button(page,'编辑 API').click();
    else await tab('parameters');
    const token=Promise.withResolvers();
    state.csrfWait=token.promise;
    const pending=page.waitForRequest(req=>new URL(req.url()).pathname==='/api/v1/auth/csrf');
    try {
      await (target==='api'?button(dialog,'保存'):button(page,'移除工作定义')).click();
      await pending;
      await page.getByRole('combobox',{name:'当前项目'}).selectOption('shipping',{force:true});
      await dialog.waitFor({state:'hidden'});
    } finally { token.resolve(); }
    await page.waitForLoadState('networkidle');
    assert.deepEqual(writes().map(r=>[r.method,r.resource]),[]);
  });
}

test('workspace conflict recovery rechecks the organization before reading schema rows',async t=>{
  const {page,tab,state,dialog,requests,writes}=await fixture(t,{workspaces:true});
  await tab('schemas');
  await button(page,'结构 / 原文 / 验证').click();
  await dialog.getByLabel('定义名称').fill('保留草稿');
  state.version={...state.version,revision:8};
  await button(dialog,'保存工作定义').click();
  await button(dialog,'查看最新内容').waitFor();
  state.api={...state.api,organizationId:'other'};
  const reads=()=>requests.filter(r=>r.resource===versionPath+'/schemas'&&r.method==='GET').length;
  const previous=reads();
  await button(dialog,'查看最新内容').click();
  await page.waitForLoadState('networkidle');
  assert.equal(reads(),previous);
  assert.equal(await button(dialog,'保持草稿，采用最新修订').count(),0);
  assert.equal(await dialog.getByLabel('定义名称').inputValue(),'保留草稿');
  assert.equal(writes().length,1);
});
