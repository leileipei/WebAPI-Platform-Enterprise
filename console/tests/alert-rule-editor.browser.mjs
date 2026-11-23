// Run against the production bundle with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after,before,test} from 'node:test';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist=process.env.WEBAPI_CONSOLE_DIST,base='/observability/alert-rules';
const first='aaaaaaaa-1000-4000-8000-000000000001',second='bbbbbbbb-1000-4000-8000-000000000002';
const button=(root,name)=>root.getByRole('button',{name,exact:true});
const ruleName=dialog=>dialog.getByRole('textbox',{name:/规则名称/});
const list=items=>({items,total:items.length,page:1,pageSize:50});
function rule(id,name,expression) {
  return {id,revision:1,logicRevision:1,evaluationState:'Known',lastSuccessAt:null,definition:{
    organizationId:'org',projectId:'project',environmentId:'dev',name,expression,metric:'latency_p95_ms',
    severity:'Warning',enabled:true,forSeconds:300,targetType:'Environment',targetId:null,windowSeconds:60,
    notification:{inConsole:true,requestedChannels:[],externalEnabled:false,emailRecipients:[],notifyRecovery:true,
      retryPolicy:{maxAttempts:5,baseDelaySeconds:30,maxDelaySeconds:900,expiresAfterMinutes:1440}}}};
}
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

async function fixture(t) {
  const page=await browser.newPage(),errors=[],writes=[],reads=[],pending=new Map();
  const rules=new Map([[first,rule(first,'规则甲','latency_p95_ms > 100')],[second,rule(second,'规则乙','latency_p95_ms > 900')]]);
  const releases=[];
  t.after(async()=>{for(const release of releases)release();await page.close();assert.deepEqual(errors,[]);});
  page.setDefaultTimeout(4500);
  page.on('pageerror',error=>errors.push(error.message));
  async function respond(key,reply,value) {
    const held=pending.get(key);
    if(!held)return reply(value);
    pending.delete(key);held.started.resolve();
    await held.release.promise;
    return reply(held.value,held.status);
  }
  await page.route('**/*',async route=>{
    const req=route.request(),method=req.method(),{pathname}=new URL(req.url());
    const reply=(json,status=200)=>route.fulfill({json,status});
    if(pathname.startsWith('/assets/'))return route.fulfill({path:join(dist,pathname.slice(1))});
    if(!pathname.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
    const path=pathname.slice('/api/v1'.length);
    if(path==='/auth/me')return reply({id:'operator',username:'operator',displayName:'值班员',
      permissions:['alert.rule.manage','alert.read'],scopes:[{scope:{organizationId:'org'},accessMode:'read_write'}]});
    if(path==='/scope-tree')return reply({organizations:[{id:'org',name:'组织'}],
      projects:[{id:'project',organizationId:'org',name:'项目'}],
      environments:[{id:'dev',projectId:'project',name:'开发'},{id:'qa',projectId:'project',name:'验收'}]});
    if(path==='/auth/csrf')return respond('csrf',reply,{token:'test-csrf'});
    if(path==='/notification-limits')return reply({maxRecipients:10,emailConfigured:false,emailEnabled:false,webhookConfigured:false,webhookEnabled:false});
    if(path==='/observability/alerts')return reply(list([]));
    if(path===base && method==='GET')return reply(list([...rules.values()]));
    const id=path.slice(base.length+1).toLowerCase();
    if(path.startsWith(base+'/') && method==='GET' && rules.has(id)) {
      reads.push(id);
      return respond(id,reply,rules.get(id));
    }
    if((path===base && method==='POST') || (path.startsWith(base+'/') && rules.has(id) && method==='PUT')){
      const body=req.postDataJSON(),etag=req.headers()['if-match'];
      writes.push({method,id,etag,name:body.name,expression:body.expression});
      if(method==='PUT' && etag!==`"${rules.get(id).revision}"`)return reply({detail:'规则修订已经变化。'},412);
      const saved={...rules.get(id||first),definition:body,revision:(rules.get(id)?.revision||0)+1};
      if(method==='PUT')rules.set(id,saved);
      return respond('write:'+id,reply,saved);
    }
    throw Error('Unexpected alert rule request '+method+' '+path);
  });
  function hold(id,value=rules.get(id),status=200) {
    const started=Promise.withResolvers(),release=Promise.withResolvers();
    releases.push(release.resolve);pending.set(id,{started,release,value,status});
    return {started:started.promise,finish:async()=>{
      release.resolve();
      await page.waitForLoadState('networkidle');
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    }};
  }
  const dialog=page.getByRole('dialog',{name:'编辑告警规则'}),create=page.getByRole('dialog',{name:'新建告警规则'});
  const visit=(query='')=>page.goto('http://127.0.0.1'+base+query);
  const openSecond=async()=>{
    await button(page.getByRole('row').filter({hasText:'规则乙'}),'编辑').click();
    await ruleName(dialog).waitFor();
  };
  const save=async target=>{
    const [response]=await Promise.all([page.waitForResponse(r=>['POST','PUT'].includes(r.request().method()) &&
      new URL(r.url()).pathname.startsWith('/api/v1'+base)),button(target,'保存规则').click()]);
    await response.finished();
    return response.status();
  };
  return {page,rules,reads,writes,hold,dialog,create,visit,openSecond,save};
}

for(const entry of ['list','bookmark']) {
  test('the latest editor keeps its own write target, draft and revision after a slow '+entry+' read',async t=>{
    const {page,rules,writes,hold,dialog,visit,openSecond,save}=await fixture(t);
    if(entry==='bookmark')rules.get(second).revision=7;
    const slow=hold(first);
    await visit(entry==='bookmark'?'?edit='+first.toUpperCase():'');
    if(entry==='list')await button(page,'规则甲').click();
    await slow.started;
    await openSecond();
    assert.equal(await ruleName(dialog).inputValue(),'规则乙');
    await ruleName(dialog).fill('规则乙夜班');
    await slow.finish();
    assert.equal(await ruleName(dialog).inputValue(),'规则乙夜班');
    const status=await save(dialog);
    t.diagnostic(JSON.stringify({writes,status}));
    assert.deepEqual(writes,[{method:'PUT',id:second,etag:entry==='list'?'"1"':'"7"',name:'规则乙夜班',expression:'latency_p95_ms > 900'}]);
    assert.equal(status,200);
  });
}

test('reopening the same rule retries the detail read without resetting the new draft on a late failure',async t=>{
  const {page,rules,writes,hold,dialog,visit,save}=await fixture(t);
  const slow=hold(first,{detail:'过时的规则详情'},503);
  await visit();await button(page,'规则甲').click();await slow.started;
  rules.get(first).revision=4;
  await button(page,'规则甲').click();
  await ruleName(dialog).fill('甲的当前草稿');
  await slow.finish();
  assert.equal(await page.getByRole('alert').count(),0);
  assert.equal(await save(dialog),200);
  assert.deepEqual(writes,[{method:'PUT',id:first,etag:'"4"',name:'甲的当前草稿',expression:'latency_p95_ms > 100'}]);
});

test('a late detail error leaves the new editor and its status alone',async t=>{
  const {page,hold,dialog,visit,openSecond}=await fixture(t);
  const slow=hold(first,{detail:'过期的详情错误'},503);
  await visit();await button(page,'规则甲').click();await slow.started;
  await openSecond();await ruleName(dialog).fill('继续编辑乙');
  await slow.finish();
  assert.equal(await page.getByRole('alert').count(),0);
  assert.equal(await ruleName(dialog).inputValue(),'继续编辑乙');
});

test('closing the chosen editor retires previous detail reads',async t=>{
  const {page,writes,hold,dialog,visit,openSecond}=await fixture(t);
  const slow=hold(first);
  await visit();await button(page,'规则甲').click();await slow.started;
  await openSecond();await button(dialog,'关闭').click();await dialog.waitFor({state:'hidden'});
  await slow.finish();
  assert.equal(await page.getByRole('dialog').count(),0);
  assert.deepEqual(writes,[]);
});

test('new rule supersedes a pending edit and submits a creation without a revision',async t=>{
  const {page,writes,hold,create,visit,save}=await fixture(t);
  const slow=hold(first);
  await visit();await button(page,'规则甲').click();await slow.started;
  await button(page,'新建规则').click();await ruleName(create).fill('值班新规则');
  await slow.finish();
  assert.equal(await create.count(),1);
  assert.equal(await save(create),200);
  assert.deepEqual(writes,[{method:'POST',id:'',etag:undefined,name:'值班新规则',expression:'latency_p95_ms > 800'}]);
});

for(const context of ['list scope','workspace','page']) {
  test('leaving the '+context+' retires a pending rule detail',async t=>{
    const {page,writes,hold,visit}=await fixture(t);
    const slow=hold(first);
    await visit();await button(page,'规则甲').click();await slow.started;
    if(context==='list scope')await page.getByRole('combobox',{name:'规则显示范围'}).selectOption('all');
    if(context==='workspace')await page.getByRole('combobox',{name:'当前环境'}).selectOption('qa');
    if(context==='page'){
      await page.getByRole('link',{name:/告警中心/}).last().click();
      await page.getByRole('link',{name:/管理规则/}).click();
    }
    await slow.finish();
    assert.equal(await page.getByRole('dialog').count(),0);
    assert.deepEqual(writes,[]);
  });
}

test('a pending edit cannot open on top of another rule action',async t=>{
  const {page,hold,visit}=await fixture(t);
  const slow=hold(first);
  await visit();await button(page,'规则甲').click();await slow.started;
  await button(page.getByRole('row').filter({hasText:'规则乙'}),'禁用').click();
  await page.getByRole('dialog',{name:'禁用告警规则'}).waitFor();
  await slow.finish();
  assert.equal(await page.getByRole('dialog').count(),1);
  assert.ok(await page.getByRole('dialog',{name:'禁用告警规则'}).isVisible());
});

test('a dirty editor keeps its draft through navigation and other opening attempts',async t=>{
  const {page,reads,writes,dialog,visit,openSecond,save}=await fixture(t);
  await visit();await openSecond();await ruleName(dialog).fill('保留乙的修改');
  await page.getByRole('combobox',{name:'规则显示范围'}).selectOption('all');
  assert.equal(new URL(page.url()).search,'');
  // The overlay intercepts pointer clicks. Exercise the underlying actions as keyboard/script activation can.
  await button(page,'新建规则').evaluate(control=>control.click());
  await button(page,'规则甲').evaluate(control=>control.click());
  await page.waitForLoadState('networkidle');
  assert.deepEqual(reads,[second]);
  assert.equal(await ruleName(dialog).inputValue(),'保留乙的修改');
  assert.equal(await save(dialog),200);
  assert.deepEqual(writes,[{method:'PUT',id:second,etag:'"1"',name:'保留乙的修改',expression:'latency_p95_ms > 900'}]);
});

test('a list refresh preserves the open draft and its original revision',async t=>{
  const {page,rules,writes,dialog,visit,openSecond,save}=await fixture(t);
  await visit();await openSecond();
  await ruleName(dialog).fill('乙的在编辑草稿');
  rules.get(second).revision=6;
  await button(page,'刷新').evaluate(control=>control.click());
  await page.getByRole('row').filter({hasText:'规则乙'}).getByText(/修订 6/).waitFor();
  assert.equal(await ruleName(dialog).inputValue(),'乙的在编辑草稿');
  assert.equal(await save(dialog),412);
  assert.deepEqual(writes,[{method:'PUT',id:second,etag:'"1"',name:'乙的在编辑草稿',expression:'latency_p95_ms > 900'}]);
});

for(const mode of ['edit','create']){
  for(const action of ['save','discard']){
    test('a dirty '+mode+' bookmark clears its editor parameters after '+action,async t=>{
      const {page,writes,dialog,create,visit,save}=await fixture(t);
      const query=mode==='edit'?'?edit='+second:'?all=true&create=true&api='+first;
      const target=mode==='edit'?dialog:create;
      await visit(query);await ruleName(target).fill('深链草稿');
      if(action==='save'){
        assert.equal(await save(target),200);
        assert.deepEqual(writes.map(w=>[w.method,w.id,w.name]),[[mode==='edit'?'PUT':'POST',mode==='edit'?second:'','深链草稿']]);
      }else{
        await button(target,'关闭').click();
        const confirm=target.getByRole('alertdialog',{name:'放弃未保存的修改'});
        await button(confirm,'继续编辑').click();
        assert.equal(new URL(page.url()).search,query);
        assert.equal(await ruleName(target).inputValue(),'深链草稿');
        await button(target,'关闭').click();await button(confirm,'确认放弃').click();
        assert.deepEqual(writes,[]);
      }
      await page.waitForURL('http://127.0.0.1'+base+(mode==='create'?'?all=true':''));
      await target.waitFor({state:'hidden'});
      await page.reload();await button(page,'规则甲').waitFor();
      assert.equal(await page.getByRole('dialog').count(),0);
    });
  }
}

for(const stage of ['awaiting CSRF','already received']) {
  test('leaving a clean editor while its write is '+stage+' keeps the next editor independent',async t=>{
    const {page,rules,writes,hold,dialog,openSecond}=await fixture(t);
    await page.goto('http://127.0.0.1/observability/alerts');
    await page.getByRole('link',{name:/管理规则/}).click();await openSecond();
    const held=stage==='awaiting CSRF'?hold('csrf',{token:'test-csrf'}):hold('write:'+second,{...rules.get(second),revision:2});
    await button(dialog,'保存规则').click();await held.started;
    await page.goBack();
    await page.getByRole('link',{name:/管理规则/}).click();
    await button(page,'规则甲').click();await ruleName(dialog).fill('重新进入后的甲草稿');
    await held.finish();
    assert.equal(await ruleName(dialog).inputValue(),'重新进入后的甲草稿');
    assert.equal(await page.getByText(/规则“规则乙”已保存/).count(),0);
    assert.deepEqual(writes,stage==='awaiting CSRF'?[]:[
      {method:'PUT',id:second,etag:'"1"',name:'规则乙',expression:'latency_p95_ms > 900'}
    ]);
    assert.equal(rules.get(second).revision,stage==='awaiting CSRF'?1:2);
  });
}

for(const invalid of ['wrong identity','missing revision']){
  test('a detail with '+invalid+' cannot open a writable rule',async t=>{
    const {page,rules,writes,hold,visit}=await fixture(t);
    const response=invalid==='wrong identity'?rules.get(second):{...rules.get(first),revision:null};
    const slow=hold(first,response);
    await visit();await button(page,'规则甲').click();await slow.started;await slow.finish();
    assert.equal(await page.getByRole('dialog').count(),0);
    assert.ok(await page.getByRole('alert').isVisible());
    assert.deepEqual(writes,[]);
  });
}

test('conflict recovery only adopts a verified revision of the edited rule and preserves its draft',async t=>{
  const {page,rules,writes,hold,dialog,visit,openSecond,save}=await fixture(t);
  await visit();await openSecond();await ruleName(dialog).fill('合并后的乙');
  rules.get(second).revision=8;
  assert.equal(await save(dialog),412);
  const read=button(dialog,'查看服务器最新版本'),adopt=button(dialog,'保持我的输入，采用最新修订');
  const wrong=hold(second,{...rules.get(first),revision:9});
  await read.click();await wrong.started;await wrong.finish();
  assert.equal(await adopt.count(),0);
  const slow=hold(second,{...rules.get(second),revision:3});
  await read.click();await slow.started;await read.click();
  await dialog.getByText(/服务器修订 8/).waitFor();
  await slow.finish();
  await adopt.click();
  assert.equal(await ruleName(dialog).inputValue(),'合并后的乙');
  assert.equal(await save(dialog),200);
  assert.deepEqual(writes.map(w=>[w.id,w.etag,w.name]),[[second,'"1"','合并后的乙'],[second,'"8"','合并后的乙']]);
});
