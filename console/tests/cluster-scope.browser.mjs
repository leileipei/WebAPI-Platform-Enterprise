// Run with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const tree = {
  organizations:[{id:'factory',name:'制造组织'},{id:'office',name:'办公组织'}],
  projects:[{id:'parts',organizationId:'factory',name:'零件项目'},
    {id:'tools',organizationId:'factory',name:'工具项目'},{id:'admin',organizationId:'office',name:'办公项目'}],
  environments:[{id:'dev',projectId:'parts',name:'开发环境'},{id:'qa',projectId:'parts',name:'验收环境'},
    {id:'lab',projectId:'tools',name:'工具环境'},{id:'ops',projectId:'admin',name:'办公环境'}]
};
const button = (element,name) => element.getByRole('button',{name,exact:true});
const environment = page => page.getByRole('combobox',{name:'当前环境'});
const canUse = async control => (await control.count()) > 0 && await control.isVisible() && await control.isEnabled();
const deferred = () => Promise.withResolvers();
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

async function fixture(t, options={}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(4000);
  const clusters = tree.environments.map(({id:env,projectId,name}) => ({
    id:env+'-cluster',projectId,environmentId:env,name:name+'集群',loadBalancingPolicy:'RoundRobin',
    healthCheckEnabled:false,healthCheckPath:'/health',healthCheckIntervalSec:30,status:'Active',revision:7,
    destinations:[{id:env+'-node',clusterId:env+'-cluster',name:name+'实例',address:'http://'+env+'.example.test/',
      weight:1,enabled:true,metadata:null,revision:4}]
  }));
  const state = {...options}, requests = [];
  if (options.selected) await page.addInitScript(env => localStorage.setItem('core-environment',env),options.selected);
  await page.route('**/*', async route => {
    const request = route.request(), {pathname} = new URL(request.url()), method = request.method();
    if (pathname.startsWith('/assets/')) return route.fulfill({path:join(dist,pathname.slice(1))});
    if (!pathname.startsWith('/api/v1/')) return route.fulfill({path:join(dist,'index.html')});
    const path = pathname.slice('/api/v1'.length), body = request.postDataJSON();
    requests.push({method,path,body,etag:request.headers()['if-match']});
    const reply = (json,status=200) => route.fulfill({json,status});
    if (path==='/auth/me') return reply({id:'operator',username:'test',displayName:'测试用户',
      permissions:['cluster.read','cluster.write'],scopes:state.grants??tree.organizations.map(org=>({
        scope:{organizationId:org.id},accessMode:'read_write'}))});
    if (path==='/auth/csrf') return reply({token:'test-csrf'});
    if (path==='/scope-tree') return state.treeFailure?reply({detail:'范围暂不可用'},503):reply(tree);
    const [,resource,id,collection] = path.split('/');
    if (resource==='environments' && collection==='clusters') {
      if (method==='GET') {
        const items = state.listRows??clusters.filter(cluster=>cluster.environmentId===id);
        return reply({items,total:items.length,page:1,pageSize:50});
      }
      if (method==='POST') {
        const projectId=tree.environments.find(env=>env.id===id).projectId;
        const created={...body,id:'created',environmentId:id,projectId,revision:1,destinations:[]};
        clusters.push(created);
        return reply(created,201);
      }
    }
    const cluster = clusters.find(row=>row.id.toLowerCase()===id?.toLowerCase());
    if (resource==='clusters' && cluster) {
      if (method==='GET') {
        if (state.detailWait) await state.detailWait.promise;
        if (state.detailStatus) return reply({detail:'集群暂不可用'},state.detailStatus);
        return reply({...cluster,...state.detailOverride});
      }
      if (method==='PUT') {
        if (request.headers()['if-match']!=='"'+cluster.revision+'"') return reply({detail:'修订已变化'},412);
        Object.assign(cluster,{name:body.name,revision:cluster.revision+1});
        return reply(cluster);
      }
      if (collection==='destinations' && method==='POST') {
        const created={...body,id:'new-node',clusterId:cluster.id,revision:1};
        cluster.destinations.push(created);
        return reply(created,201);
      }
    }
    if (resource==='destinations' && method==='PUT') {
      const row=clusters.flatMap(c=>c.destinations).find(d=>d.id===id);
      if (request.headers()['if-match']!=='"'+row.revision+'"') return reply({detail:'修订已变化'},412);
      Object.assign(row,{name:body.name,revision:row.revision+1});
      return reply(row);
    }
    throw Error('Unexpected fixture request '+method+' '+path);
  });
  const open = path => page.goto('http://127.0.0.1'+path);
  const writes = () => requests.filter(request=>request.method!=='GET');
  return {page,state,open,requests,writes,clusters};
}

test('switching environment from a cluster discards only a confirmed draft and writes to the newly opened cluster',async t=>{
  const {page,open,writes,requests}=await fixture(t);
  await open('/clusters');
  await page.getByRole('link',{name:'查看实例'}).click();
  await page.getByRole('heading',{name:'开发环境集群'}).waitFor();
  const dialog=page.getByRole('dialog');
  await button(page,'＋ 新增实例').click();
  await dialog.getByLabel('实例名称').fill('保留的开发草稿');
  await environment(page).selectOption('qa');
  await dialog.getByRole('alert').waitFor();
  assert.equal(await environment(page).inputValue(),'dev');
  assert.equal(await dialog.getByLabel('实例名称').inputValue(),'保留的开发草稿');
  await button(dialog,'关闭').click();
  await button(dialog,'确认放弃').click();
  const previousDevReads=requests.filter(r=>r.path==='/clusters/dev-cluster').length;
  await environment(page).selectOption('qa');
  assert.equal(new URL(page.url()).pathname,'/clusters');
  await page.getByRole('cell',{name:'验收环境集群'}).waitFor();
  assert.equal(await page.getByText('开发环境实例',{exact:true}).count(),0);
  assert.equal(requests.filter(r=>r.path==='/clusters/dev-cluster').length,previousDevReads);
  await page.getByRole('link',{name:'查看实例'}).click();
  await page.getByRole('heading',{name:'验收环境集群'}).waitFor();
  await button(page,'编辑集群').click();
  await dialog.getByLabel('集群名称').fill('验收集群新名称');
  await button(dialog,'保存').click();
  await page.getByRole('heading',{name:'验收集群新名称'}).waitFor();
  const row=page.getByRole('row').filter({hasText:'验收环境实例'});
  await button(row,'编辑').click();
  await dialog.getByLabel('实例名称').fill('已编辑的验收实例');
  await button(dialog,'保存').click();
  await page.getByRole('cell',{name:'已编辑的验收实例'}).waitFor();
  await button(page,'＋ 新增实例').click();
  await dialog.getByLabel('实例名称').fill('新验收实例');
  await dialog.getByLabel('地址').fill('http://qa-new.example.test/');
  await button(dialog,'保存').click();
  await page.getByRole('cell',{name:'新验收实例'}).waitFor();
  assert.deepEqual(writes().map(r=>[r.method,r.path,r.etag,r.body.name]),[
    ['PUT','/clusters/qa-cluster','"7"','验收集群新名称'],
    ['PUT','/destinations/qa-node','"4"','已编辑的验收实例'],
    ['POST','/clusters/qa-cluster/destinations',undefined,'新验收实例']]);
});

for (const [label,selection,newCluster] of [['当前项目','tools','工具环境集群'],['当前组织','office','办公环境集群']]) {
  test('changing '+label+' on a detail opens the new environment list',async t=>{
    const {page,open,writes}=await fixture(t);
    await open('/clusters/dev-cluster');
    await page.getByRole('heading',{name:'开发环境集群'}).waitFor();
    await page.getByRole('combobox',{name:label}).selectOption(selection);
    assert.equal(new URL(page.url()).pathname,'/clusters');
    await page.getByRole('cell',{name:newCluster}).waitFor();
    assert.equal(await button(page,'编辑集群').count(),0);
    assert.deepEqual(writes(),[]);
  });
}

test('a pending cluster detail cannot add instances and its late response cannot overwrite the next environment',async t=>{
  const wait=deferred(),{page,open,writes}=await fixture(t,{detailWait:wait});
  try {
    await open('/clusters/dev-cluster');
    await page.waitForFunction(()=>document.querySelector('[aria-label=当前环境]')?.value==='dev');
    assert.equal(await canUse(button(page,'＋ 新增实例')),false);
    assert.equal(await canUse(button(page,'编辑集群')),false);
    await environment(page).selectOption('qa');
    await page.getByRole('cell',{name:'验收环境集群'}).waitFor();
  } finally { wait.resolve(); }
  await page.waitForLoadState('networkidle');
  assert.equal(new URL(page.url()).pathname,'/clusters');
  assert.equal(await page.getByText('开发环境实例',{exact:true}).count(),0);
  assert.deepEqual(writes(),[]);
});

test('a direct link or Back to a different environment requires an explicit scope selection before editing',async t=>{
  const {page,open,writes}=await fixture(t);
  const bookmark='/clusters/dev-cluster?from=review#destinations';
  await open(bookmark);
  await page.getByRole('heading',{name:'开发环境集群'}).waitFor();
  await environment(page).selectOption('qa');
  await page.getByRole('cell',{name:'验收环境集群'}).waitFor();
  await page.goBack();
  const switchToOwner=page.getByRole('button',{name:/切换到.*开发环境/});
  await switchToOwner.waitFor();
  assert.equal(await environment(page).inputValue(),'qa');
  assert.equal(await canUse(button(page,'编辑集群')),false);
  assert.equal(await canUse(button(page,'＋ 新增实例')),false);
  assert.equal(await page.getByText('开发环境实例',{exact:true}).count(),0);
  const historyLength=await page.evaluate(()=>history.length);
  await switchToOwner.click();
  await page.getByRole('cell',{name:'开发环境实例'}).waitFor();
  assert.equal(await environment(page).inputValue(),'dev');
  assert.equal(await page.evaluate(()=>history.length),historyLength);
  assert.equal(page.url(),'http://127.0.0.1'+bookmark);
  assert.equal(await canUse(button(page,'编辑集群')),true);
  assert.deepEqual(writes(),[]);
});

test('a bookmarked uppercase cluster GUID can still edit the confirmed object',async t=>{
  const {page,open,clusters,writes}=await fixture(t);
  const clusterId='adcde610-6a8e-4143-9b2f-e712912790dd';
  clusters[0].id=clusterId;
  clusters[0].destinations[0].clusterId=clusterId;
  await open('/clusters/'+clusterId.toUpperCase());
  await page.waitForLoadState('networkidle');
  assert.equal(await canUse(button(page,'编辑集群')),true);
  await button(page,'编辑集群').click();
  const dialog=page.getByRole('dialog');
  await dialog.getByLabel('集群名称').fill('书签进入的开发集群');
  await button(dialog,'保存').click();
  await page.getByRole('heading',{name:'书签进入的开发集群'}).waitFor();
  assert.deepEqual(writes().map(r=>r.path),['/clusters/'+clusterId]);
});

test('write access to the selected environment never unlocks a read-only cluster in another environment',async t=>{
  const grants=[{scope:{organizationId:'factory',projectId:'parts',environmentId:'dev'},accessMode:'read_only'},
    {scope:{organizationId:'factory',projectId:'parts',environmentId:'qa'},accessMode:'read_write'}];
  const {page,open,writes}=await fixture(t,{selected:'qa',grants});
  await open('/clusters/dev-cluster');
  await page.waitForLoadState('networkidle');
  assert.equal(await environment(page).inputValue(),'qa');
  assert.equal(await canUse(button(page,'＋ 新增实例')),false);
  await page.getByRole('button',{name:/切换到.*开发环境/}).click();
  const row=page.getByRole('row').filter({hasText:'开发环境实例'});
  await row.waitFor();
  for (const action of [button(page,'编辑集群'),button(page,'＋ 新增实例'),button(row,'编辑')])
    assert.equal(await canUse(action),false);
  assert.deepEqual(writes(),[]);
});

for (const [description,options] of [
  ['missing detail',{detailStatus:404}],['failed detail',{detailStatus:503}],
  ['wrong cluster ID',{detailOverride:{id:'different-cluster'}}],
  ['wrong parent project',{detailOverride:{projectId:'tools'}}]
]) {
  test(description+' cannot expose another cluster or enable instance creation',async t=>{
    const {page,open,writes}=await fixture(t,options);
    await open('/clusters/dev-cluster');
    await page.waitForLoadState('networkidle');
    assert.equal(await canUse(button(page,'编辑集群')),false);
    assert.equal(await canUse(button(page,'＋ 新增实例')),false);
    assert.equal(await page.getByText('开发环境实例',{exact:true}).count(),0);
    assert.deepEqual(writes(),[]);
  });
}

test('a resource refresh suspends an open editor and a failed refresh keeps its draft from submitting',async t=>{
  const {page,state,open,writes}=await fixture(t);
  await open('/clusters/dev-cluster');
  await page.getByRole('heading',{name:'开发环境集群'}).waitFor();
  await button(page,'＋ 新增实例').click();
  const dialog=page.getByRole('dialog');
  await dialog.getByLabel('实例名称').fill('未保存实例');
  await dialog.getByLabel('地址').fill('http://draft.example.test/');
  const wait=deferred();
  state.detailWait=wait;
  try {
    const refreshing=page.waitForRequest(r=>new URL(r.url()).pathname==='/api/v1/clusters/dev-cluster');
    await button(page,'刷新会话与权限').evaluate(element=>element.click());
    await refreshing;
    assert.equal(await canUse(button(dialog,'保存')),false);
    await dialog.evaluate(form=>form.requestSubmit());
    state.detailStatus=503;
  } finally { wait.resolve(); }
  await page.getByRole('alert').filter({hasText:'集群暂不可用'}).waitFor();
  assert.equal(await canUse(button(dialog,'保存')),false);
  await dialog.evaluate(form=>form.requestSubmit());
  assert.deepEqual(writes(),[]);
});

test('a failed scope-tree refresh disables cluster writes until the workspace can be verified again',async t=>{
  const {page,state,open,writes}=await fixture(t);
  await open('/clusters/dev-cluster');
  await page.getByRole('heading',{name:'开发环境集群'}).waitFor();
  state.treeFailure=true;
  await button(page,'刷新会话与权限').click();
  await page.getByRole('alert').filter({hasText:'范围暂不可用'}).waitFor();
  assert.equal(await canUse(button(page,'编辑集群')),false);
  assert.equal(await canUse(button(page,'＋ 新增实例')),false);
  state.treeFailure=false;
  await button(page,'刷新会话与权限').click();
  await button(page,'编辑集群').click();
  await page.getByRole('dialog',{name:'集群配置'}).waitFor();
  assert.deepEqual(writes(),[]);
});

test('a revision conflict can adopt only a revision from the same cluster and destination',async t=>{
  const {page,state,open,writes,clusters}=await fixture(t);
  await open('/clusters/dev-cluster');
  const row=page.getByRole('row').filter({hasText:'开发环境实例'});
  await button(row,'编辑').click();
  const dialog=page.getByRole('dialog');
  await dialog.getByLabel('实例名称').fill('合并后的开发实例');
  clusters[0].destinations[0].revision=5;
  await button(dialog,'保存').click();
  await dialog.getByText('修订已变化',{exact:true}).waitFor();
  state.detailOverride={environmentId:'qa'};
  await button(dialog,'查看最新内容').click();
  await page.waitForLoadState('networkidle');
  assert.equal(await canUse(button(dialog,'保持我的输入，采用最新修订')),false);
  state.detailOverride=undefined;
  await button(dialog,'查看最新内容').click();
  await button(dialog,'保持我的输入，采用最新修订').click();
  await button(dialog,'保存').click();
  await page.getByRole('cell',{name:'合并后的开发实例'}).waitFor();
  assert.deepEqual(writes().map(r=>[r.path,r.etag]),[['/destinations/dev-node','"4"'],['/destinations/dev-node','"5"']]);
});

test('a list edit and create use the selected environment and ignore foreign rows and instances',async t=>{
  const {page,state,open,clusters,writes}=await fixture(t,{selected:'qa'});
  state.listRows=clusters.slice(0,2);
  await open('/clusters');
  const row=page.getByRole('row').filter({hasText:'验收环境集群'});
  await row.waitFor();
  assert.equal(await page.getByRole('cell',{name:'开发环境集群'}).count(),0);
  const dialog=page.getByRole('dialog');
  await button(row,'编辑').click();
  await dialog.getByLabel('集群名称').fill('列表更新');
  await button(dialog,'保存').click();
  await page.getByRole('cell',{name:'列表更新'}).waitFor();
  state.listRows=undefined;
  await button(page,'＋ 新建集群').click();
  await dialog.getByLabel('集群名称').fill('新验收集群');
  await button(dialog,'保存').click();
  await page.getByRole('cell',{name:'新验收集群'}).waitFor();
  state.detailOverride={destinations:[clusters[0].destinations[0],clusters[1].destinations[0]]};
  await page.getByRole('row').filter({hasText:'列表更新'}).getByRole('link',{name:'查看实例'}).click();
  await page.getByRole('cell',{name:'验收环境实例'}).waitFor();
  assert.equal(await page.getByRole('cell',{name:'开发环境实例'}).count(),0);
  assert.deepEqual(writes().map(r=>[r.method,r.path]),[['PUT','/clusters/qa-cluster'],['POST','/environments/qa/clusters']]);
});
