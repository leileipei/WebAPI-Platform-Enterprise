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
    if(path==='/auth/sso/providers')return reply([]);
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

test('an authorized node opens its events read-only when its environment is hidden from the scope tree',async t=>{
  const {page,state,writes,open}=await fixture(t);
  state.tree={...tree,environments:tree.environments.filter(item=>item.id==='qa')};
  const [events]=await Promise.all([
    page.waitForResponse(response=>new URL(response.url()).pathname.endsWith(detailPath(first)+'/events')),
    open(),
  ]);
  assert.equal(events.status(),200);
  await page.getByRole('heading',{name:'节点 '+first}).waitFor();
  assert.equal(await editButton(page).isEnabled(),false);
  assert.deepEqual(writes,[]);
});

async function nodeList(t){
  const {page,nodes}=await fixture(t);
  nodes[second].environmentId='qa';
  const listUrl=origin+'/api/v1/environments/dev/gateway-nodes?page=1';
  const row=page.getByRole('row').filter({hasText:'节点 '+first});
  const status=value=>row.getByRole('cell',{name:value,exact:true});
  let sessionReads=0;
  page.on('request',req=>{if(new URL(req.url()).pathname==='/api/v1/auth/me')sessionReads++;});
  await page.goto(origin+'/nodes');
  await status('Ready').waitFor();

  async function hold(action){
    const intercepted=Promise.withResolvers();
    await page.route(listUrl,route=>intercepted.resolve(route),{times:1});
    await Promise.all([page.waitForRequest(listUrl),action()]);
    return intercepted.promise;
  }
  async function deliver(route,json,code=200){
    await route.fulfill({json,status:code});
    await (await route.request().response()).finished();
    await page.evaluate(()=>new Promise(done=>requestAnimationFrame(()=>requestAnimationFrame(done))));
  }
  const observed=async()=>({
    rows:await row.allTextContents(),alerts:await page.getByRole('alert').allTextContents(),
    lastRead:await page.getByText(/^最后读取：/).allTextContents(),loading:await page.getByRole('status').count(),
    login:await page.getByRole('heading',{name:'登录管理控制台'}).count(),sessionReads
  });
  const payload=offline=>({items:[{...nodes[first],offline}],total:1,page:1,pageSize:50});
  return {page,status,observed,payload,deliver,holdPoll:()=>hold(()=>page.clock.fastForward(5000)),
    holdRefresh:()=>hold(()=>button(page,'刷新').click())};
}

for(const code of [200,503,403,404,401]){
  test('node list polling keeps the newer Offline result after a delayed '+code,async t=>{
    const {page,holdPoll,deliver,payload,status,observed}=await nodeList(t);
    const old=await holdPoll(),current=await holdPoll();
    await deliver(current,payload(true));
    await status('Offline').waitFor();
    const offline=await observed();
    assert.deepEqual(offline.alerts,[]);
    await page.clock.fastForward(1200);
    await deliver(old,code===200?payload(false):{detail:'过期的读取错误'},code);
    assert.deepEqual(await observed(),offline);
  });
}

for(const code of [503,403,404]){
  test('node list polling preserves a current '+code+' through an older success and recovers on the next poll',async t=>{
    const {holdPoll,deliver,payload,status,observed}=await nodeList(t);
    const initial=await observed(),old=await holdPoll(),current=await holdPoll();
    await deliver(current,{detail:'当前读取失败'},code);
    const failure=await observed();
    assert.match(failure.alerts[0],/当前读取失败/);
    if(code===503){
      assert.deepEqual(failure.rows,initial.rows);
      assert.deepEqual(failure.lastRead,initial.lastRead);
      assert.match(failure.alerts[0],/当前数据可能已过时/);
      assert.equal(failure.sessionReads,initial.sessionReads);
    }else{
      assert.deepEqual(failure.rows,[]);
      assert.match(failure.lastRead[0],/—/);
      assert.equal(failure.sessionReads,initial.sessionReads+1);
    }
    await deliver(old,payload(false));
    assert.deepEqual(await observed(),failure);
    await deliver(await holdPoll(),payload(true));
    await status('Offline').waitFor();
    assert.deepEqual((await observed()).alerts,[]);
  });
}

test('node list polling handles a current 401 by returning to login',async t=>{
  const {page,holdPoll,deliver,observed}=await nodeList(t);
  await deliver(await holdPoll(),{detail:'会话已过期'},401);
  await page.getByRole('heading',{name:'登录管理控制台'}).waitFor();
  assert.deepEqual((await observed()).rows,[]);
});

test('node list polling keeps a manual refresh pending until its newest read finishes',async t=>{
  const {page,holdRefresh,holdPoll,deliver,payload,status,observed}=await nodeList(t);
  const old=await holdRefresh();
  await page.getByRole('status').waitFor();
  const refreshing=await observed(),current=await holdPoll();
  await deliver(old,payload(false));
  assert.deepEqual(await observed(),refreshing);
  await deliver(current,payload(true));
  await status('Offline').waitFor();
  assert.equal((await observed()).loading,0);
});

test('node list polling keeps updating when every read takes longer than the polling interval',async t=>{
  const {page,payload,status}=await nodeList(t);
  const path='/api/v1/environments/dev/gateway-nodes';
  let offline=true;
  await page.route(origin+path+'?page=1',route=>route.fulfill({json:payload(offline)}));
  await page.evaluate(path=>{
    const send=window.fetch;
    window.fetch=async(...args)=>{
      if(new URL(args[0],location.href).pathname!==path)return send(...args);
      const delay=new Promise(done=>setTimeout(done,7000));
      const [response]=await Promise.all([send(...args),delay]);
      return response;
    };
  },path);
  await button(page,'刷新').click();
  await page.getByRole('status').waitFor();
  for(const next of [true,false]){
    offline=next;
    const expected=status(next?'Offline':'Ready');
    for(let tick=0;tick<8 && !(await expected.isVisible());tick++){
      await page.clock.fastForward(5000);
      await page.waitForLoadState('networkidle');
    }
    assert.equal(await expected.isVisible(),true,'slow reads must publish '+(next?'Offline':'Ready'));
    assert.equal(await page.getByRole('status').count(),0);
  }
});

test('node list polling allows a manual refresh to replace two outstanding polls',async t=>{
  const {page,holdRefresh,holdPoll,deliver,payload,status,observed}=await nodeList(t);
  const old=await holdPoll(),newer=await holdPoll(),manual=await holdRefresh();
  await deliver(manual,payload(true));
  await status('Offline').waitFor();
  const refreshed=await observed();
  await old.fulfill({json:payload(false)});
  await newer.fulfill({status:401,json:{detail:'过期的会话错误'}});
  await page.waitForLoadState('networkidle');
  assert.deepEqual(await observed(),refreshed);
});

test('node list polling cannot restore the previous environment after the operator switches scope',async t=>{
  const {page,holdPoll,payload,observed}=await nodeList(t);
  const old=await holdPoll();
  await page.getByRole('combobox',{name:'当前环境'}).selectOption('qa');
  const qa=page.getByRole('cell',{name:'节点 '+second});
  await qa.waitFor();
  await old.fulfill({json:payload(false)});
  await page.waitForLoadState('networkidle');
  assert.equal(await qa.isVisible(),true);
  assert.deepEqual((await observed()).rows,[]);
});

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

test('node operations show the reported sample and the selected instance receipt',async t=>{
  const {page,nodes,open}=await fixture(t);
  nodes[first].diagnostics={instanceId:nodes[first].instanceId,observedAt:new Date().toISOString(),cpuPercent:18.25,workingSetBytes:73400320,lastKnownGoodVersion:3};
  const operation={id:'operation-a',nodeId:first,instanceId:nodes[first].instanceId};
  let revision;
  await page.route('**/api/v1/gateway-nodes/'+first+'/retry',route=>{
    revision=route.request().headers()['if-match'];
    return route.fulfill({status:202,json:{...operation,state:'Queued'}});
  });
  await page.route('**/api/v1/gateway-nodes/'+first+'/operations/operation-a',route=>
    route.fulfill({json:{...operation,state:'Completed',outcome:'applied'}}));
  await page.goto(origin+'/nodes');
  await page.getByRole('columnheader',{name:'应用版本'}).waitFor();
  await open();
  await page.getByText('70.0 MiB',{exact:true}).waitFor();
  await page.getByText('v3',{exact:true}).waitFor();
  assert.equal(await page.getByRole('link',{name:'下载诊断包'}).getAttribute('href'),'/api/v1/gateway-nodes/'+first+'/diagnostics');
  await button(page,'重试当前配置').click();
  await page.getByText('配置已应用并确认',{exact:true}).waitFor();
  assert.equal(revision,nodes[first].eTag);
});
