import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const id = n => 'b5000000-0000-4000-8000-' + String(n).padStart(12,'0');
const [org,project,environment,cluster,version,routeId,apiId] = Array.from({length:7},(_,i)=>id(i+1));
const endpoint = '/openapi/import-previews';
const operations = ['orders','stock','customers'].map(name => ({operationId:name,method:'GET',
  path:'/'+name,summary:name,suggestedCode:name,supported:true,warnings:[],parameterCount:0,schemaCount:0}));
const doc = title => JSON.stringify({openapi:'3.0.3',info:{title,version:'1'},paths:
  Object.fromEntries(operations.map(o=>[o.path,{get:{operationId:o.operationId,responses:{200:{description:'ok'}}}}]))});
const button = (root,name) => root.getByRole('button',{name,exact:true});
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

async function fixture(t,firstFailure) {
  const page=await browser.newPage();
  t.after(()=>page.close());
  page.setDefaultTimeout(6000);
  if(firstFailure==='lost')await page.clock.install({time:new Date()});
  const commits=[], previews=[], errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  t.after(()=>assert.deepEqual(errors,[]));
  const remote={bundleHash:'a'.repeat(64),revision:1};
  await page.route('**/*',async route=>{
    const request=route.request(), url=new URL(request.url()), method=request.method();
    if(url.pathname.startsWith('/assets/')) return route.fulfill({path:join(dist,url.pathname.slice(1))});
    if(!url.pathname.startsWith('/api/v1/')) return route.fulfill({path:join(dist,'index.html')});
    const path=url.pathname.slice('/api/v1'.length), reply=json=>route.fulfill({json});
    if(path==='/auth/me') return reply({id:id(8),displayName:'导入员',username:'importer',authSource:'local',
      permissions:['api.create','route.write','cluster.read','project.read'],
      scopes:[{scope:{organizationId:org},accessMode:'read_write'}]});
    if(path==='/auth/csrf') return reply({token:'fixture'});
    if(path==='/scope-tree') return reply({organizations:[{id:org,name:'组织'}],projects:[{id:project,organizationId:org,name:'项目'}],
      environments:[{id:environment,projectId:project,name:'开发环境'}]});
    if(path==='/projects/'+project+'/import-source-policy') return reply({projectId:project,revision:remote.revision,allowances:[]});
    if(path==='/environments/'+environment+'/clusters') return reply({items:[{id:cluster,status:'Active',name:'集群'}],total:1,page:1,pageSize:50});
    if(path===endpoint && method==='GET') return reply([]);
    if(path===endpoint && method==='POST') {
      const index=previews.push(request.postDataJSON());
      return reply({previewId:id(100+index),sourceHash:'f'.repeat(64),bundleHash:remote.bundleHash,sourcePolicyRevision:remote.revision,
        expiresAt:new Date(Date.now()+1200000).toISOString(),operations:index%2?operations:[...operations].reverse(),issues:[],sourceFormat:'json',dialect:'Oas30'});
    }
    if(path.startsWith(endpoint+'/') && path.endsWith('/commit') && method==='POST') {
      commits.push({path,body:request.postDataJSON(),key:request.headers()['idempotency-key']});
      if(commits.length===1&&firstFailure==='lost')return route.abort('failed');
      if(commits.length===1 && firstFailure) {
        if(firstFailure==='import_preview_changed') remote.revision++;
        return route.fulfill({status:firstFailure==='stale_revision'?412:409,json:{code:firstFailure,detail:firstFailure}});
      }
      return reply({importId:id(200),operations:[],warnings:[]});
    }
    throw Error('Unexpected fixture request '+method+' '+path);
  });
  await page.goto('http://127.0.0.1/imports');
  const checkbox = name => page.getByRole('checkbox',{name:'选择 Operation '+name});
  const row = name => page.getByRole('row').filter({has:checkbox(name)});
  const inspect = async()=>{
    const nextId=id(101+previews.length);
    await button(page,'解析并固定预览').click();
    await page.locator('.import-preview-meta').getByText(nextId,{exact:true}).waitFor();
  };
  async function edit(name) {
    await button(row(name),'映射目标').click();
    return page.getByRole('dialog',{name:'导入映射 · '+name});
  }
  async function save(editor) {
    await button(editor,'保存').click();
    await editor.waitFor({state:'hidden'});
  }
  return {page,checkbox,row,inspect,edit,save,remote,commits,previews,source:page.getByLabel('OpenAPI JSON / YAML 原文'),submit:button(page,'确认整批导入')};
}

for(const conflict of ['route_conflict','import_preview_changed','stale_revision']) {
  test('import keeps selected manual targets and requires fresh overwrite revisions after '+conflict,async t=>{
    const {page,checkbox,row,inspect,edit,save,commits,previews,source,submit}=await fixture(t,conflict);
    await source.fill(doc('Orders'));await inspect();
    await checkbox('stock').uncheck();
    let editor=await edit('orders');
    await editor.getByLabel('目标模式').selectOption('version');
    await editor.getByLabel('覆盖草稿版本 ID').fill(version);
    await editor.getByLabel('覆盖路由 ID').fill(routeId);
    await editor.getByLabel('覆盖版本最新修订').fill('4');
    await editor.getByLabel('覆盖路由最新修订').fill('7');
    await save(editor);
    editor=await edit('customers');
    await editor.getByLabel('目标模式').selectOption('api');
    await editor.getByLabel('已有 API ID').fill(apiId);
    await editor.getByLabel('新版本号').fill('2.3.0');
    await save(editor);
    await submit.click();await page.getByRole('alert').filter({hasText:conflict}).waitFor();
    if(conflict!=='stale_revision') {
      await inspect();
      assert.equal(previews.length,2);
    }
    assert.equal(await checkbox('stock').isChecked(),false,'cancelled operation must stay unselected');
    assert.equal(await checkbox('orders').isChecked(),true);
    assert.match(await row('orders').innerText(),new RegExp(version));
    assert.match(await row('customers').innerText(),new RegExp(apiId));
    assert.equal(await submit.isDisabled(),true,'previous overwrite revisions cannot authorize this submission');
    assert.match(await row('orders').innerText(),/核对.*修订|修订.*核对/);
    editor=await edit('orders');
    assert.equal(await editor.getByLabel('覆盖草稿版本 ID').inputValue(),version);
    assert.equal(await editor.getByLabel('覆盖路由 ID').inputValue(),routeId);
    assert.equal(await editor.getByLabel('覆盖版本最新修订').inputValue(),'');
    assert.equal(await editor.getByLabel('覆盖路由最新修订').inputValue(),'');
    await editor.getByLabel('覆盖版本最新修订').fill('5');
    await editor.getByLabel('覆盖路由最新修订').fill('8');
    await save(editor);await submit.click();
    await page.getByRole('heading',{name:/已写入工作配置/}).waitFor();
    assert.equal(commits.length,2);
    const [before,after]=commits, mapped=after.body.targets.find(t=>t.operationId==='orders');
    assert.deepEqual(after.body.targets.map(t=>t.operationId).sort(),['customers','orders']);
    assert.deepEqual(mapped,{...before.body.targets.find(t=>t.operationId==='orders'),expectedRevision:5,expectedRouteRevision:8});
    assert.deepEqual(after.body.targets.find(t=>t.operationId==='customers'),before.body.targets.find(t=>t.operationId==='customers'));
    assert.equal(after.path,endpoint+'/'+id(100+previews.length)+'/commit');
    assert.equal(after.body.expectedBundleHash,'a'.repeat(64));
  });
}

test('manual re-preview retains an empty selection and a different text starts with new mappings',async t=>{
  const {checkbox,source,inspect,submit}=await fixture(t);
  await source.fill(doc('First'));await inspect();
  for(const op of operations) await checkbox(op.operationId).uncheck();
  await inspect();
  for(const op of operations) assert.equal(await checkbox(op.operationId).isChecked(),false);
  assert.equal(await submit.isDisabled(),true);
  await source.fill(doc('Second'));await inspect();
  for(const op of operations) assert.equal(await checkbox(op.operationId).isChecked(),true);
});

test('manual re-preview adopts its current source policy and can submit the original selection',async t=>{
  const {page,checkbox,source,inspect,submit,remote,previews,commits}=await fixture(t);
  await source.fill(doc('Policy'));await inspect();
  await checkbox('stock').uncheck();
  remote.revision++;
  await inspect();
  await page.getByText(/当前修订 2/).waitFor();
  assert.equal(await checkbox('stock').isChecked(),false);
  await submit.click();await page.getByRole('heading',{name:/已写入工作配置/}).waitFor();
  assert.equal(previews.length,2);
  assert.deepEqual(commits[0].body.targets.map(t=>t.operationId).sort(),['customers','orders']);
  assert.equal(commits[0].path,endpoint+'/'+id(102)+'/commit');
});

test('a different frozen bundle at the same URL starts with new targets',async t=>{
  const {page,checkbox,inspect,remote,row}=await fixture(t);
  await page.getByLabel('来源方式').selectOption('url');
  await page.getByLabel('来源 URL').fill('https://source.invalid/orders');
  await inspect();await checkbox('stock').uncheck();
  remote.bundleHash='b'.repeat(64);
  await inspect();
  assert.equal(await checkbox('stock').isChecked(),true);
  assert.match(await row('orders').innerText(),/orders · 1.0.0/);
});


test('a lost import response can replay the original key and body after expiry',async t=>{
  const f=await fixture(t,'lost');
  await f.source.fill(doc('receipt'));
  await f.inspect();
  await f.submit.click();
  await f.page.getByText(/请求未完成，输入已保留/).waitFor();
  await f.page.clock.fastForward(21*60*1000);
  await button(f.page,'用原请求查询导入回执').click();
  await f.page.getByText(/已写入工作配置/).waitFor();
  assert.equal(f.commits.length,2);
  assert.ok(f.commits[0].key);
  assert.deepEqual(f.commits[0],f.commits[1]);
});
