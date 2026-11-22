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
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

async function fixture(t, {entry='detail', permissions=allPermissions, mode='read_write',
  canReadSchemas=true, canWriteSchemas=true, canWriteVersions=true, versionChanges={}}={}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(4000);
  const state = {permissions,mode,canReadSchemas,canWriteSchemas,canWriteVersions,version:{...version,...versionChanges},
    parameters:[{id:'filter',apiVersionId:version.id,name:'筛选条件',location:'query',dataType:'string',required:false,schema:'{"type":"string"}'}],
    schemas:[{id:'body',apiVersionId:version.id,name:'响应内容',schemaType:'response',statusCode:200,contentType:'application/json',schemaJson:'{"type":"object"}'}]};
  const requests = [];
  const routePath = entry==='detail'?path:path+'/wizard';
  await page.route('**/*', async route => {
    const req = route.request(), pathname = new URL(req.url()).pathname, method = req.method();
    if (pathname.startsWith('/assets/')) return route.fulfill({path:join(dist,pathname.slice(1))});
    if (pathname===routePath) return route.fulfill({path:join(dist,'index.html')});
    const resource = pathname.slice('/api/v1'.length);
    const body = req.postData()?req.postDataJSON():null;
    requests.push({resource,method,body,headers:req.headers()});
    const reply = (json,status=200) => route.fulfill({json,status});
    if (resource==='/auth/me') return reply({id:'actor',username:'user',displayName:'维护人员',authSource:'local',
      permissions:state.permissions,scopes:[{scope:{organizationId:api.organizationId,projectId:api.projectId},accessMode:state.mode},
        {scope:{organizationId:'other'},accessMode:'read_write'}]});
    if (resource==='/auth/csrf') return reply({token:'fixture-csrf'});
    if (resource==='/scope-tree') return reply({organizations:[{id:'org',name:'订单组织'}],
      projects:[{id:'project',organizationId:'org',name:'订单项目'}],environments:[]});
    if (resource===path && method==='GET') {
      if (state.detailWait) await state.detailWait;
      if (state.detailStatus) return reply({detail:'API 不可用'},state.detailStatus);
      return reply({api,workingRevision:1,versions:[state.version],runningConfigVersion:{},pendingReleaseId:null,
        canReadSchemas:state.canReadSchemas,canWriteSchemas:state.canWriteSchemas,canWriteVersions:state.canWriteVersions});
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
  await page.getByRole('cell',{name:'1.0',exact:true}).waitFor();
  async function tab(kind) {
    if (entry==='wizard') await page.locator('.wizard-steps button').filter({hasText:kind==='parameters'?'参数定义':'请求与响应'}).click();
    else await button(page,kind==='parameters'?'参数':'Schema').click();
  }
  const dialog=page.getByRole('dialog');
  const writes=()=>requests.filter(r=>r.method==='PUT'||r.method==='POST'&&!r.resource.endsWith('/schema-validation'));
  return {page,state,requests,tab,dialog,writes};
}

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
