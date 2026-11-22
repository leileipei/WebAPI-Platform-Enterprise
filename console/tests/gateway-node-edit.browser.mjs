// Run against the built console with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after,before,test} from 'node:test';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const origin='http://127.0.0.1',dist=process.env.WEBAPI_CONSOLE_DIST;
const first='ab000001-0000-4000-8000-000000000001',second='ab000002-0000-4000-8000-000000000002';
const button=(scope,name)=>scope.getByRole('button',{name,exact:true});
const usable=async control=>await control.count()>0 && await control.isVisible() && await control.isEnabled();
const editButton=page=>button(page,'编辑身份状态');
const save=dialog=>button(dialog,'保存');
const readLatest=dialog=>button(dialog,'查看最新内容');
const adopt=dialog=>button(dialog,'保持我的输入，采用最新修订');
const detailPath=id=>'/gateway-nodes/'+id;
const grants=[{scope:{organizationId:'org'},accessMode:'read_write'}];
const tree={organizations:[{id:'org',name:'工厂'}],projects:[{id:'project',organizationId:'org',name:'项目'}],
  environments:[{id:'dev',projectId:'project',name:'开发'},{id:'qa',projectId:'project',name:'验收'}]};
function node(id,instanceId,enabled){
  return {id,environmentId:'dev',nodeName:'节点 '+id,instanceId,appVersion:'2.4',enabled,status:'Ready',offline:false,
    currentConfigVersion:4,currentDeploymentSequence:7,targetConfigVersion:4,lastHeartbeatAt:'2026-11-01T00:00:00Z',
    desiredConfigVersion:4,desiredDeploymentSequence:7,supportedSnapshotSchemas:['2.4'],
    eTag:'"'+instanceId+':'+(enabled?'True':'False')+'"'};
}
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

async function fixture(t){
  const page=await browser.newPage(),errors=[],writes=[];
  t.after(()=>page.close());
  t.after(()=>assert.deepEqual(errors,[]));
  page.setDefaultTimeout(5000);
  page.on('pageerror',error=>errors.push(error.message));
  await page.clock.install();
  const nodes={[first]:node(first,'worker-a',true),[second]:node(second,'worker-b',false)};
  const state={permissions:['gateway.read','gateway.operate'],grants,tree,detailStatus:200,nextRead:null};
  await page.route('**/*',async route=>{
    const req=route.request(),{pathname}=new URL(req.url());
    if(pathname.startsWith('/assets/'))return route.fulfill({path:join(dist,pathname.slice(1))});
    if(!pathname.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
    const path=pathname.slice('/api/v1'.length),reply=(json,status=200)=>route.fulfill({json,status});
    if(path==='/auth/me')return reply({id:'operator',username:'operator',displayName:'测试运维',
      permissions:state.permissions,scopes:state.grants});
    if(path==='/scope-tree')return reply(state.tree);
    if(path==='/auth/csrf')return reply({token:'fixture-csrf'});
    const [resource,id,action]=path.slice(1).toLowerCase().split('/');
    if(resource==='environments' && action==='gateway-nodes'){
      const items=Object.values(nodes).filter(row=>row.environmentId===id);
      return reply({items,total:items.length,page:1,pageSize:50});
    }
    if(resource==='gateway-nodes' && nodes[id]){
      if(req.method()==='GET' && action==='events')return reply({items:[],total:0,page:1,pageSize:50});
      if(req.method()==='GET' && !action){
        const read=state.nextRead;
        state.nextRead=null;
        if(read)return read(reply);
        if(state.detailStatus!==200)return reply({detail:'无法读取节点。'},state.detailStatus);
        return reply(nodes[id]);
      }
      if(req.method()==='PUT' && action==='enabled'){
        const body=req.postDataJSON(),etag=req.headers()['if-match'],key=req.headers()['idempotency-key'];
        const status=etag===nodes[id].eTag?200:412;
        writes.push({path,body,etag,key,status});
        if(status===412)return reply({code:'stale_revision',detail:'节点实例或启用状态已变化。'},status);
        nodes[id]=node(id,nodes[id].instanceId,body.enabled);
        return reply(nodes[id]);
      }
    }
    throw Error('Unexpected gateway node fixture request '+req.method()+' '+path);
  });
  const open=(id=first)=>page.goto(origin+'/nodes/'+id);
  const editor=async()=>{
    await editButton(page).click();
    const dialog=page.getByRole('dialog',{name:'节点身份状态'});
    await dialog.waitFor();
    return dialog;
  };
  return {page,nodes,state,writes,open,editor};
}

test('polling a newly disabled node cannot re-enable it with the old draft and a new ETag',async t=>{
  const {page,nodes,writes,open,editor}=await fixture(t);
  await open();
  const dialog=await editor(),enabled=dialog.getByRole('checkbox');
  assert.equal(await enabled.isChecked(),true);
  nodes[first]=node(first,'worker-a',false);
  await page.clock.fastForward(5000);
  await page.locator('.details pre').filter({hasText:'"enabled": false'}).waitFor();
  const [response]=await Promise.all([
    page.waitForResponse(r=>r.request().method()==='PUT'),save(dialog).click()
  ]);
  const observed={etag:writes[0].etag,body:writes[0].body,response:response.status(),enabled:nodes[first].enabled};
  t.diagnostic('after the poll and first save '+JSON.stringify(observed));
  assert.deepEqual(observed,{etag:'"worker-a:True"',body:{enabled:true},response:412,enabled:false});
  await dialog.getByRole('alert').waitFor();
  assert.equal(await enabled.isChecked(),true);
  assert.equal(await usable(save(dialog)),false);
  assert.equal(await usable(adopt(dialog)),false);
  await readLatest(dialog).click();
  await dialog.locator('pre').filter({hasText:'"enabled": false'}).waitFor();
  assert.equal(await usable(save(dialog)),false);
  await adopt(dialog).click();
  assert.equal(writes.length,1);
  await save(dialog).click();
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(w=>[w.path,w.etag,w.body,w.status]),[
    [detailPath(first)+'/enabled','"worker-a:True"',{enabled:true},412],
    [detailPath(first)+'/enabled','"worker-a:False"',{enabled:true},200]
  ]);
  assert.ok(writes.every(w=>w.key));
  assert.notEqual(writes[0].key,writes[1].key);
});

test('an ordinary edit from an uppercase node bookmark changes only that node',async t=>{
  const {nodes,writes,open,editor}=await fixture(t);
  await open(first.toUpperCase());
  const dialog=await editor();
  await dialog.getByRole('checkbox').uncheck();
  await save(dialog).click();
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(w=>[w.path.toLowerCase(),w.etag,w.body,w.status]),[
    [detailPath(first)+'/enabled','"worker-a:True"',{enabled:false},200]
  ]);
  assert.equal(nodes[first].enabled,false);
});

test('each new 412 requires a new read, including after a failed second read',async t=>{
  const {nodes,state,writes,open,editor}=await fixture(t);
  await open();
  const dialog=await editor();
  nodes[first]=node(first,'worker-a',false);
  await save(dialog).click();
  await readLatest(dialog).click();
  await adopt(dialog).click();
  nodes[first]=node(first,'replacement',false);
  await save(dialog).click();
  await dialog.getByRole('alert').waitFor();
  assert.equal(await usable(adopt(dialog)),false,'a second 412 must retire the previous preview');
  state.nextRead=reply=>reply({detail:'重读失败'},503);
  await readLatest(dialog).click();
  await dialog.getByRole('alert').filter({hasText:'重读失败'}).waitFor();
  assert.equal(await usable(adopt(dialog)),false);
  await readLatest(dialog).click();
  await adopt(dialog).click();
  await save(dialog).click();
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(w=>[w.etag,w.status]),[['"worker-a:True"',412],['"worker-a:False"',412],['"replacement:False"',200]]);
});

test('a delayed conflict read cannot replace a newer displayed revision',async t=>{
  const {page,nodes,state,writes,open,editor}=await fixture(t);
  await open();
  const dialog=await editor();
  const earlier=node(first,'older-instance',false);
  nodes[first]=earlier;
  await save(dialog).click();
  const started=Promise.withResolvers(),release=Promise.withResolvers();
  state.nextRead=async reply=>{started.resolve();await release.promise;return reply(earlier);};
  try{
    await readLatest(dialog).click();
    await started.promise;
    nodes[first]=node(first,'newer-instance',false);
    await readLatest(dialog).click();
    await dialog.locator('pre').filter({hasText:'newer-instance'}).waitFor();
  }finally{release.resolve();}
  await page.waitForLoadState('networkidle');
  await adopt(dialog).click();
  const [response]=await Promise.all([page.waitForResponse(r=>r.request().method()==='PUT'),save(dialog).click()]);
  assert.deepEqual([writes[1].etag,response.status()],['"newer-instance:False"',200]);
});

test('a failed new read removes a previously displayed revision from the same conflict',async t=>{
  const {nodes,state,writes,open,editor}=await fixture(t);
  await open();
  const dialog=await editor();
  nodes[first]=node(first,'worker-a',false);
  await save(dialog).click();
  await readLatest(dialog).click();
  await adopt(dialog).waitFor();
  state.nextRead=reply=>reply({detail:'重读失败'},503);
  await readLatest(dialog).click();
  await dialog.getByRole('alert').filter({hasText:'重读失败'}).waitFor();
  assert.equal(await usable(adopt(dialog)),false);
  assert.equal(await usable(save(dialog)),false);
  assert.equal(writes.length,1);
});

for(const [description,override] of [
  ['another node',{id:second}],['another environment',{environmentId:'qa'}],['missing ETag',{eTag:''}],['missing enabled state',{enabled:null}]
]){
  test('a conflict read with '+description+' cannot supply the next revision',async t=>{
    const {page,nodes,state,writes,open,editor}=await fixture(t);
    await open();
    const dialog=await editor();
    nodes[first]=node(first,'worker-a',false);
    await save(dialog).click();
    await readLatest(dialog).waitFor();
    state.nextRead=reply=>reply({...nodes[first],...override});
    await readLatest(dialog).click();
    await page.waitForLoadState('networkidle');
    assert.equal(await usable(adopt(dialog)),false);
    assert.equal(await usable(save(dialog)),false);
    assert.equal(writes.length,1);
  });
}

for(const status of [404,503]){
  test('a polling detail read returning '+status+' leaves the open form unable to write',async t=>{
    const {page,state,writes,open,editor}=await fixture(t);
    await open();
    const dialog=await editor();
    await dialog.getByRole('checkbox').uncheck();
    state.detailStatus=status;
    await page.clock.fastForward(5000);
    await page.waitForLoadState('networkidle');
    assert.equal(await usable(editButton(page)),false);
    assert.equal(await usable(save(dialog)),false);
    if(await dialog.count())await dialog.evaluate(form=>form.requestSubmit());
    await page.waitForLoadState('networkidle');
    assert.deepEqual(writes,[]);
  });
}

test('permission revocation closes the original editor and blocks a new edit',async t=>{
  const {page,state,writes,open,editor}=await fixture(t);
  await open();
  const dialog=await editor();
  state.permissions=['gateway.read'];
  await button(page,'刷新会话与权限').evaluate(element=>element.click());
  await dialog.waitFor({state:'hidden'});
  await page.getByRole('heading',{name:'节点 '+first}).waitFor();
  assert.equal(await usable(editButton(page)),false);
  assert.deepEqual(writes,[]);
});

test('the same session must revalidate the node before its open form can submit',async t=>{
  const {page,state,nodes,writes,open,editor}=await fixture(t);
  await open();
  const dialog=await editor();
  await page.waitForLoadState('networkidle');
  const release=Promise.withResolvers();
  state.nextRead=async reply=>{await release.promise;return reply(nodes[first]);};
  try{
    await Promise.all([
      page.waitForRequest(req=>new URL(req.url()).pathname==='/api/v1'+detailPath(first),{timeout:2000}),
      button(page,'刷新会话与权限').evaluate(element=>element.click())
    ]);
    assert.equal(await usable(save(dialog)),false);
    await dialog.evaluate(form=>form.requestSubmit());
    assert.deepEqual(writes,[]);
    nodes[first]=node(first,'worker-a',false);
  }finally{release.resolve();}
  await page.locator('.details pre').filter({hasText:'"enabled": false'}).waitFor();
  await save(dialog).click();
  await readLatest(dialog).waitFor();
  assert.deepEqual(writes.map(w=>[w.etag,w.status]),[['"worker-a:True"',412]]);
});

for(const problem of ['missing ETag','wrong ID','missing owner','read-only owner']){
  test('a node with '+problem+' cannot open an identity editor',async t=>{
    const {page,nodes,state,writes,open}=await fixture(t);
    if(problem==='missing ETag')nodes[first].eTag='';
    if(problem==='wrong ID')nodes[first].id=second;
    if(problem==='missing owner')nodes[first].environmentId='unknown';
    if(problem==='read-only owner')state.grants=[
      {scope:{organizationId:'org',projectId:'project',environmentId:'dev'},accessMode:'read_only'},
      {scope:{organizationId:'org',projectId:'project',environmentId:'qa'},accessMode:'read_write'}
    ];
    await open();
    await page.waitForLoadState('networkidle');
    assert.equal(await usable(editButton(page)),false);
    assert.deepEqual(writes,[]);
  });
}

test('returning to the list while a conflict read is pending keeps the next node editor independent',async t=>{
  const {page,nodes,state,writes,editor}=await fixture(t);
  await page.goto(origin+'/nodes');
  const nodeLink=id=>page.getByRole('row').filter({hasText:'节点 '+id}).getByRole('link',{name:'查看详情'});
  await nodeLink(first).click();
  const firstDialog=await editor();
  nodes[first]=node(first,'worker-a',false);
  await save(firstDialog).click();
  const started=Promise.withResolvers(),release=Promise.withResolvers();
  state.nextRead=async reply=>{started.resolve();await release.promise;return reply(nodes[first]);};
  let nextDialog;
  try{
    await readLatest(firstDialog).click();
    await started.promise;
    await page.goBack();
    await nodeLink(second).click();
    nextDialog=await editor();
  }finally{release.resolve();}
  await page.waitForLoadState('networkidle');
  assert.equal(await nextDialog.getByRole('checkbox').isChecked(),false);
  await nextDialog.getByRole('checkbox').check();
  await save(nextDialog).click();
  await nextDialog.waitFor({state:'hidden'});
  assert.deepEqual(writes.map(w=>[w.path,w.etag,w.status]),[
    [detailPath(first)+'/enabled','"worker-a:True"',412],[detailPath(second)+'/enabled','"worker-b:False"',200]
  ]);
});
