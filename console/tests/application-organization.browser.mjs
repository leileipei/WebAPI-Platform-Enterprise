// Run with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const id = n => 'adcde610-6a8e-4143-9b2f-' + String(n).padStart(12,'0');
const [orgA,orgB,projectA,projectB,envA,envB,appA,appB,apiA,apiB] = Array.from({length:10},(_,i)=>id(i+1));
const detail = app => '/applications/' + app;
const button = (root,name) => root.getByRole('button',{name,exact:true});
const organization = page => page.getByRole('combobox',{name:'当前组织'});
const pageOf = items => ({items,total:items.length,page:1,pageSize:50});
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

async function fixture(t,{selected=orgA,sharedWithoutProjects=false,detailWait,scopeUnavailable=false,writableOrganizations=[orgA,orgB]}={}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(5000);
  await page.addInitScript(org => localStorage.setItem('core-organization',org),selected);
  const organizations = [{id:orgA,name:'甲组织',status:'Active'},{id:orgB,name:'乙组织',status:'Active'}];
  const projects = [{id:projectA,organizationId:orgA,name:'甲项目',status:'Active'},{id:projectB,organizationId:orgB,name:'乙项目',status:'Active'}];
  const environments = [{id:envA,projectId:projectA,name:'甲环境'},{id:envB,projectId:projectB,name:'乙环境'}];
  const apps = organizations.map((org,i)=>({id:[appA,appB][i],organizationId:org.id,
    projectId:sharedWithoutProjects && i===0 ? null : projects[i].id, name:'订单应用',code:'ORDERS',
    owner:org.name+'负责人',status:'Active',revision:3}));
  const reads=[], writes=[], state={scopeUnavailable,listReads:[]};
  await page.route('**/*', async route => {
    const request=route.request(), url=new URL(request.url()), method=request.method();
    if (url.pathname.startsWith('/assets/')) return route.fulfill({path:join(dist,url.pathname.slice(1))});
    if (!url.pathname.startsWith('/api/v1/')) return route.fulfill({path:join(dist,'index.html')});
    const path=url.pathname.slice('/api/v1'.length), reply=json=>route.fulfill({json});
    if (method==='GET') reads.push(path);
    else writes.push({method,path,body:request.postDataJSON(),etag:request.headers()['if-match']});
    if (path==='/auth/me') return reply({id:id(11),username:'operator',displayName:'业务管理员',authSource:'local',
      permissions:['app.read','app.write','credential.manage','api.read','app.permission.manage'],
      rolePermissions:writableOrganizations.map(organizationId=>({organizationId,permissions:['app.write']})),
      scopes:organizations.map(o=>({scope:{organizationId:o.id},accessMode:'read_write'}))});
    if (path==='/auth/csrf') return reply({token:'test-csrf'});
    if (path==='/scope-tree') {
      if (state.scopeWait) await state.scopeWait.promise;
      return state.scopeUnavailable ? route.fulfill({status:503,json:{detail:'范围树暂不可用'}})
        : reply({organizations,projects:sharedWithoutProjects?projects.slice(1):projects,
            environments:sharedWithoutProjects?environments.slice(1):environments});
    }
    if(path==='/applications'&&method==='GET'){
      const organizationId=url.searchParams.get('organizationId');state.listReads.push({organizationId,page:url.searchParams.get('page')});
      return reply(pageOf(organizationId?apps.filter(a=>a.organizationId===organizationId):apps));
    }
    if(path==='/applications'&&method==='POST')return reply({...request.postDataJSON(),id:id(55),revision:1});
    const [,resource,appId,child,operation] = path.split('/');
    const i=apps.findIndex(app=>app.id===appId?.toLowerCase());
    if (resource!=='applications' || i < 0) throw Error('Unexpected fixture request '+method+' '+path);
    const app=apps[i], environment=environments[i], api={id:[apiA,apiB][i],name:'订单 API',code:'ORDERS',grants:[]};
    if (!child && method==='GET') {
      if (detailWait && app.id===appA) await detailWait.promise;
      return reply({application:state.detailOverride??app,credentials:[],permissions:[],runningConfigVersions:{}});
    }
    if (!child && method==='PUT') {
      if (request.headers()['if-match']!=='"'+app.revision+'"')
        return route.fulfill({status:412,json:{code:'stale_revision',detail:'修订已变更'}});
      Object.assign(app,request.postDataJSON(),{revision:app.revision+1});
      return reply(app);
    }
    if (child==='credential-usage') return reply({coverage:{state:'Unknown',missingNodes:[],gaps:[]},page:pageOf([])});
    if (child==='credentials' && method==='POST') return reply({credential:{id:id(12)},apiKey:'fixture-key'});
    if (child==='permission-matrix') {
      if (!operation) return reply({...pageOf([api]),applicationId:app.id,projectId:app.projectId,
        evaluatedAt:new Date().toISOString(),environments:[{...environment,code:'DEV',isProduction:false,
          canManage:true,canGrantLegacy:true,canGrantRouteMethods:false}]});
      if (operation==='preview') return reply({previewToken:'reviewed-'+app.id,expiresAt:new Date(Date.now()+60000).toISOString(),
        requiresProductionConfirmation:false,items:[{apiId:api.id,environmentId:environment.id,isProduction:false,
          before:null,after:null,running:{status:'Unconfirmed',configVersion:null,deploymentSequence:null,grant:null}}]});
      if (operation==='commit') return reply({appliedCount:1,activation:'WorkingConfiguration'});
    }
    throw Error('Unexpected fixture request '+method+' '+path);
  });
  const open = path => page.goto('http://127.0.0.1'+path);
  return {page,open,reads,writes,apps,state};
}

async function openListedApplication(page,appId) {
  const link=page.getByRole('link',{name:'详情与接入'});
  await link.waitFor();
  assert.equal(await link.getAttribute('href'),detail(appId));
  await link.click();
  await page.getByRole('heading',{name:'订单应用',exact:true}).waitFor();
}

async function switchToSecondList(page) {
  await organization(page).selectOption(orgB);
  assert.equal(await organization(page).inputValue(),orgB);
  assert.equal(new URL(page.url()).pathname,'/applications');
  await page.getByRole('cell',{name:'乙组织负责人'}).waitFor();
  assert.equal(await page.getByRole('dialog').count(),0);
  assert.equal(await button(page,'编辑应用').count(),0);
  assert.equal(await button(page,'＋ 创建凭证').count(),0);
  assert.equal(await button(page,'API 授权').count(),0);
}

test('an organization switch returns to the selected list and disables only its explicitly opened application',async t=>{
  const {page,open,writes,apps}=await fixture(t);
  await open('/applications');
  await openListedApplication(page,appA);
  await switchToSecondList(page);
  await page.goBack();
  await page.getByRole('button',{name:/切换到甲组织/}).waitFor();
  assert.equal(await organization(page).inputValue(),orgB);
  assert.equal(await button(page,'编辑应用').isEnabled(),false);
  assert.equal(await button(page,'＋ 创建凭证').count(),0);
  await page.goForward();
  await openListedApplication(page,appB);
  await button(page,'编辑应用').click();
  const editor=page.getByRole('dialog');
  await editor.getByLabel('状态').selectOption('Disabled');
  await button(editor,'保存').click();
  await editor.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(w=>[w.method,w.path,w.body.organizationId,w.body.projectId,w.etag]),
    [['PUT',detail(appB),orgB,projectB,'"3"']]);
  assert.deepEqual(apps.map(app=>app.status),['Active','Disabled']);
});

test('a dirty application editor keeps the original organization until the user discards the draft',async t=>{
  const {page,open,writes}=await fixture(t);
  await open(detail(appA));
  await button(page,'编辑应用').click();
  const editor=page.getByRole('dialog');
  await editor.getByLabel('应用名称').fill('甲的草稿');
  await organization(page).selectOption(orgB);
  await editor.getByRole('alert').waitFor();
  assert.equal(await organization(page).inputValue(),orgA);
  assert.equal(await editor.getByLabel('应用名称').inputValue(),'甲的草稿');
  await button(editor,'关闭').click();
  await button(editor,'确认放弃').click();
  await button(page,'编辑应用').click();
  await switchToSecondList(page);
  assert.deepEqual(writes,[]);
});

for (const kind of ['credential','permission']) {
  test(`switching with an open ${kind} dialog closes it and binds subsequent writes to the newly opened application`,async t=>{
    const {page,open,writes,reads}=await fixture(t);
    const dialog=page.getByRole('dialog');
    async function openDialog() {
      if (kind==='credential') await button(page,'＋ 创建凭证').click();
      else {
        await button(page,'API 授权').click();
        await page.getByRole('button',{name:/新增授权 订单 API/}).click();
      }
      await dialog.waitFor();
    }
    await open('/applications');
    await openListedApplication(page,appA);
    await openDialog();
    const previousReads=reads.filter(path=>path.startsWith(detail(appA))).length;
    await switchToSecondList(page);
    await openListedApplication(page,appB);
    await openDialog();
    if (kind==='credential') {
      await button(dialog,'保存').click();
      await page.getByRole('dialog',{name:'一次性凭证'}).waitFor();
      assert.deepEqual(writes.map(w=>[w.method,w.path]),[['POST',detail(appB)+'/credentials']]);
    } else {
      await button(dialog,'预览影响').click();
      await button(dialog,'提交工作配置').click();
      await dialog.waitFor({state:'hidden'});
      assert.deepEqual(writes.map(w=>[w.path,w.body.projectId,w.body.changes[0].apiId,w.body.changes[0].environmentId]),
        ['preview','commit'].map(operation=>[detail(appB)+'/permission-matrix/'+operation,projectB,apiB,envB]));
    }
    assert.equal(reads.filter(path=>path.startsWith(detail(appA))).length,previousReads);
  });
}

test('a late detail response cannot reopen the former organization’s application or child editors',async t=>{
  const gate=Promise.withResolvers();
  const {page,open,writes,reads}=await fixture(t,{detailWait:gate});
  try {
    await open('/applications');
    const request=page.waitForRequest(r=>new URL(r.url()).pathname==='/api/v1'+detail(appA));
    await page.getByRole('link',{name:'详情与接入'}).click();
    await request;
    await switchToSecondList(page);
    await openListedApplication(page,appB);
  } finally { gate.resolve(); }
  await page.waitForLoadState('networkidle');
  assert.equal(new URL(page.url()).pathname,detail(appB));
  assert.equal(await organization(page).inputValue(),orgB);
  assert.equal(reads.some(path=>path.startsWith(detail(appA)+'/')),false);
  await button(page,'编辑应用').click();
  assert.equal(await page.getByRole('dialog').getByLabel('负责人').inputValue(),'乙组织负责人');
  assert.deepEqual(writes,[]);
});

test('a cross-organization bookmark exposes its shared application only after choosing the owner organization',async t=>{
  const {page,open,reads,writes}=await fixture(t,{selected:orgB,sharedWithoutProjects:true});
  const bookmark=detail(appA.toUpperCase())+'?from=review#credentials';
  await open(bookmark);
  await page.waitForLoadState('networkidle');
  assert.equal(await organization(page).inputValue(),orgB);
  assert.equal(await button(page,'编辑应用').isEnabled(),false);
  assert.equal(await button(page,'＋ 创建凭证').count(),0);
  assert.equal(await button(page,'API 授权').count(),0);
  assert.equal(reads.some(path=>path.startsWith(detail(appA)+'/')),false);
  const historyLength=await page.evaluate(()=>history.length);
  await page.getByRole('button',{name:/切换到甲组织/}).click();
  await page.getByRole('heading',{name:'订单应用',exact:true}).waitFor();
  assert.equal(await organization(page).inputValue(),orgA);
  assert.equal(await page.getByRole('combobox',{name:'当前项目'}).inputValue(),'');
  assert.equal(page.url(),'http://127.0.0.1'+bookmark);
  assert.equal(await page.evaluate(()=>history.length),historyLength);
  await button(page,'编辑应用').click();
  const editor=page.getByRole('dialog');
  await editor.getByLabel('应用名称').fill('甲共享应用');
  await button(editor,'保存').click();
  await editor.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(w=>[w.path,w.body.organizationId,w.body.projectId]),[[detail(appA),orgA,null]]);
});

test('a direct application link waits for a verified host organization before exposing the child editors',async t=>{
  const {page,open,reads,writes,state}=await fixture(t,{scopeUnavailable:true});
  await open(detail(appA));
  await page.getByRole('alert').filter({hasText:'范围树暂不可用'}).waitFor();
  await page.waitForLoadState('networkidle');
  assert.equal(await organization(page).inputValue(),'');
  assert.equal(await button(page,'编辑应用').isEnabled(),false);
  assert.equal(await button(page,'＋ 创建凭证').count(),0);
  assert.equal(await button(page,'API 授权').count(),0);
  assert.equal(reads.some(path=>path.startsWith(detail(appA)+'/')),false);
  state.scopeUnavailable=false;
  await button(page,'刷新会话与权限').click();
  await button(page,'＋ 创建凭证').waitFor();
  assert.equal(await organization(page).inputValue(),orgA);
  assert.equal(await button(page,'编辑应用').isEnabled(),true);
  assert.deepEqual(writes,[]);
});

for (const kind of ['application','credential','permission preview','permission commit']) {
  test(`an interrupted scope refresh pauses ${kind} submissions and preserves the open draft until recovery`,async t=>{
    const {page,open,writes,state}=await fixture(t);
    const dialog=page.getByRole('dialog');
    await open(detail(appA));
    let field,action,expected;
    if (kind==='application') {
      await button(page,'编辑应用').click();
      field=dialog.getByLabel('应用名称'); expected='待恢复草稿'; action=button(dialog,'保存');
    } else if (kind==='credential') {
      await button(page,'＋ 创建凭证').click();
      field=dialog.getByLabel('失效时间'); expected='2035-12-05T14:23'; action=button(dialog,'保存');
    } else {
      await button(page,'API 授权').click();
      await page.getByRole('button',{name:/新增授权 订单 API/}).click();
      field=dialog.getByLabel('生效时间'); expected='2030-11-25T08:41'; action=button(dialog,'预览影响');
    }
    await field.fill(expected);
    if (kind==='permission commit') {
      await action.click();
      action=button(dialog,'提交工作配置');
      await action.waitFor();
    }
    const writesBeforeRefresh=writes.length;
    const gate=Promise.withResolvers();
    state.scopeWait=gate;
    state.scopeUnavailable=true;
    try {
      const reading=page.waitForRequest(r=>new URL(r.url()).pathname==='/api/v1/scope-tree');
      // Refresh the workspace while the modal is open, as an existing read can finish after the editor opens.
      await button(page,'刷新会话与权限').evaluate(control=>control.click());
      await reading;
      await page.getByText('正在读取服务端数据…',{exact:true}).first().waitFor();
      assert.equal(!!await action.count() && await action.isVisible() && await action.isEnabled(),false);
    } finally { gate.resolve(); }
    await page.getByRole('alert').filter({hasText:'范围树暂不可用'}).waitFor();
    assert.equal(await organization(page).inputValue(),orgA);
    assert.equal(!!await action.count() && await action.isVisible() && await action.isEnabled(),false);
    assert.equal(writes.length,writesBeforeRefresh);
    state.scopeUnavailable=false;
    await page.getByRole('button',{name:'重新核对组织范围'}).click();
    await action.waitFor();
    if(kind==='permission commit') await dialog.getByRole('heading',{name:'授权影响预览'}).waitFor();
    else assert.equal(await field.inputValue(),expected);
    await action.click();
    if(kind==='permission preview') await button(dialog,'提交工作配置').click();
    if(kind==='credential') await page.getByRole('dialog',{name:'一次性凭证'}).waitFor();
    else await dialog.waitFor({state:'hidden'});
    const finalPath=kind==='application'?detail(appA):detail(appA)+(kind==='credential'?'/credentials':'/permission-matrix/commit');
    assert.equal(writes.at(-1).path,finalPath);
  });
}

test('a conflict cannot adopt a revision read from a different organization’s application',async t=>{
  const {page,open,apps,state,writes}=await fixture(t);
  await open(detail(appA));
  await button(page,'编辑应用').click();
  const editor=page.getByRole('dialog');
  await editor.getByLabel('负责人').fill('已复核负责人');
  apps[0].revision=4;
  await button(editor,'保存').click();
  await editor.getByRole('alert').filter({hasText:'修订已变更'}).waitFor();
  state.detailOverride=apps[1];
  await button(editor,'查看最新内容').click();
  await page.waitForLoadState('networkidle');
  assert.equal(await button(editor,'保持我的输入，采用最新修订').count(),0);
  assert.equal(await button(editor,'保存').isEnabled(),false);
  state.detailOverride=undefined;
  await button(editor,'查看最新内容').click();
  await button(editor,'保持我的输入，采用最新修订').click();
  await button(editor,'保存').click();
  await editor.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(w=>[w.path,w.etag]),[[detail(appA),'"3"'],[detail(appA),'"4"']]);
  assert.equal(apps[0].owner,'已复核负责人');
  assert.equal(apps[1].owner,'乙组织负责人');
});


test('a selected organization requests only its first page and can create a shared application alongside projects',async t=>{
  const {page,open,writes,state}=await fixture(t);
  await open('/applications');
  await page.getByRole('cell',{name:'甲组织负责人'}).waitFor();
  assert.deepEqual(state.listReads,[{organizationId:orgA,page:'1'}]);
  await button(page,'＋ 新建应用').click();
  const editor=page.getByRole('dialog',{name:'新建应用'});
  await editor.getByLabel('应用归属').selectOption({label:'组织共享应用'});
  await editor.getByLabel('应用编码').fill('SHARED');await editor.getByLabel('应用名称').fill('共享接入');await editor.getByLabel('负责人').fill('负责人');
  await button(editor,'保存').click();await editor.waitFor({state:'hidden'});
  assert.equal(writes.length,1);assert.equal(writes[0].body.organizationId,orgA);assert.equal(writes[0].body.projectId,null);
});

test('application creation never combines another organization’s function with local write scope',async t=>{
  const {page,open,writes}=await fixture(t,{writableOrganizations:[orgA]});
  await open('/applications');
  await button(page,'＋ 新建应用').waitFor();
  await organization(page).selectOption(orgB);
  await page.getByRole('cell',{name:'乙组织负责人'}).waitFor();
  assert.equal(await button(page,'＋ 新建应用').count(),0);
  assert.deepEqual(writes,[]);
});

test('the credential creation form can bind a replacement credential to the actual application environment',async t=>{
  const {page,open,writes}=await fixture(t);
  await open(detail(appA));await button(page,'＋ 创建凭证').click();
  const editor=page.getByRole('dialog',{name:'创建凭证'});
  const binding=editor.getByLabel('凭证使用范围');assert.equal(await binding.locator('option[value="'+envB+'"]').count(),0);
  await binding.selectOption(envA);await button(editor,'保存').click();
  await page.getByRole('dialog',{name:'一次性凭证'}).waitFor();
  assert.equal(writes.length,1);assert.equal(writes[0].body.environmentId,envA);
});
