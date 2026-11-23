// Run node --test with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {randomInt} from 'node:crypto';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const origin = 'http://127.0.0.1:' + randomInt(49152, 65536);
const providers = '/api/v1/settings/sso/providers', providerId = 'e109626d-d74c-42f0-b2be-d5d7d02778af';
const detail = providers + '/' + providerId;
const pageOf = items => ({items, total:items.length, page:1, pageSize:50});
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

async function openProvider(t, directoryStatus = 200) {
  const page = await browser.newPage();
  page.setDefaultTimeout(5000);
  t.after(() => page.close());
  const fixture = {
    provider:{id:providerId, organizationId:null, name:'企业身份源', providerType:'Oidc', issuer:'https://id.example.test',
      clientId:'console', secretRef:'file://sso/enterprise', scopes:['openid','profile','email'],
      claimMapping:{displayName:'name',email:'email'}, enabled:false, isDefault:false, revision:1, authRevision:1,
      lastTest:null, callbackUrl:'https://platform.example.test/auth/oidc/callback/' + providerId},
    permissions:['system.sso.manage'], detailStatus:200, detailReads:0, nextDetail:null, writes:[], rotations:[]
  };
  await page.route('**/*', async route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    const reply = (json, status = 200) => route.fulfill({json,status});
    if (path.startsWith('/assets/')) return route.fulfill({path:join(dist,path.slice(1))});
    if (path === '/settings/sso') return route.fulfill({path:join(dist,'index.html')});
    if (path === '/api/v1/auth/me') return reply({id:'manager',username:'manager',displayName:'管理员',
      authSource:'local',permissions:fixture.permissions,scopes:[]});
    if (path === '/api/v1/auth/csrf') return reply({token:'fixture-csrf'});
    if (path === '/api/v1/scope-tree') return reply({organizations:[],projects:[],environments:[]});
    if (path === '/api/v1/organizations') {
      const status = await directoryStatus;
      return status === 200 ? reply(pageOf([])) : reply({detail:'无组织目录权限'},status);
    }
    if (path === providers) return reply(pageOf([fixture.provider]));
    if (path === detail && request.method() === 'GET') {
      fixture.detailReads++;
      const pending = fixture.nextDetail;
      fixture.nextDetail = null;
      if (pending) await pending;
      if (fixture.detailStatus !== 200) return reply({detail:'身份源详情暂不可读'},fixture.detailStatus);
      return reply(fixture.provider);
    }
    if (path === detail && request.method() === 'PUT') {
      const headers = request.headers(), body = request.postDataJSON(), etag = headers['if-match'];
      fixture.writes.push({etag,key:headers['idempotency-key'],body});
      if (etag !== '"' + fixture.provider.revision + '"') return reply({code:'stale_revision',detail:'身份源修订已变化'},412);
      fixture.provider = {...fixture.provider,...body,revision:fixture.provider.revision+1};
      return reply(fixture.provider);
    }
    if (path === detail + '/rotate' && request.method() === 'POST') {
      const etag = request.headers()['if-match'];
      fixture.rotations.push(etag);
      if (etag !== '"' + fixture.provider.revision + '"') return reply({code:'stale_revision',detail:'身份源修订已变化'},412);
      fixture.provider = {...fixture.provider,revision:fixture.provider.revision+1,authRevision:fixture.provider.authRevision+1};
      return reply(fixture.provider);
    }
    throw new Error('Unexpected SSO request ' + request.method() + ' ' + path);
  });
  await page.goto(origin + '/settings/sso', {waitUntil:'domcontentloaded'});
  await page.getByRole('button',{name:'配置与测试'}).click();
  const dialog = page.getByRole('dialog',{name:'身份源配置'});
  await dialog.getByText('配置 r1',{exact:true}).waitFor();
  const name = dialog.getByLabel('显示名称'), claim = dialog.getByLabel('Display Name Claim');
  const button = label => dialog.getByRole('button',{name:label,exact:true});
  const save = button('保存配置'), review = button('读取最新配置'), adopt = button('保留输入，采用最新修订');
  const permissionEvent = () => page.evaluate(() => window.dispatchEvent(new Event('permission-refresh')));
  async function readAfter(action, expectedStatus = 200) {
    const [response] = await Promise.all([
      page.waitForResponse(r => new URL(r.url()).pathname === detail && r.request().method() === 'GET'), action()
    ]);
    assert.equal(response.status(),expectedStatus);
    await response.finished();
    await dialog.locator('fieldset:enabled').waitFor();
  }
  async function submit(expectedStatus) {
    const [response] = await Promise.all([page.waitForResponse(r => r.request().method() === 'PUT'),save.click()]);
    assert.equal(response.status(),expectedStatus);
    if (expectedStatus === 412) await review.waitFor();
    else await dialog.getByText('操作已保存。',{exact:true}).waitFor();
  }
  function changeClaims(patch) {
    const previous = fixture.provider;
    fixture.provider = {...previous,revision:previous.revision+1,authRevision:previous.authRevision+1,
      claimMapping:{...previous.claimMapping,...patch}};
  }
  return {fixture,page,dialog,name,claim,save,review,adopt,permissionEvent,readAfter,submit,changeClaims};
}

test('a directory 403 during editing exposes a revision conflict and cannot save stale claim mapping', async t => {
  let denyDirectory;
  const directoryStatus = new Promise(resolve => { denyDirectory = () => resolve(403); });
  const {fixture,dialog,name,claim,save,review,adopt,readAfter,submit,changeClaims} = await openProvider(t,directoryStatus);
  await name.fill('本地新名称');
  changeClaims({displayName:'preferred_username'});
  await readAfter(denyDirectory);
  // Exercise an enabled save against the revision-checking receiver so an accidental overwrite is visible.
  if (await save.isEnabled()) await submit(200);
  assert.deepEqual(fixture.writes,[],'an automatic read must not authorize a PUT with the new revision');
  assert.equal(await name.inputValue(),'本地新名称');
  assert.equal(await claim.inputValue(),'name');
  assert.equal(await adopt.count(),0);
  await readAfter(() => review.click());
  const latest = JSON.parse(await dialog.locator('pre').innerText());
  assert.equal(latest.revision,2);
  assert.equal(latest.claimMapping.displayName,'preferred_username');
  assert.equal(await save.isDisabled(),true);
  await adopt.click();
  assert.equal(await name.inputValue(),'本地新名称');
  assert.equal(await claim.inputValue(),'preferred_username');
  await submit(200);
  assert.deepEqual(fixture.writes.map(w => [w.etag,w.body.name,w.body.claimMapping.displayName]),
    [['"2"','本地新名称','preferred_username']]);
});

test('rechecking the same revision repeatedly keeps unsaved input writable on its original revision', async t => {
  const {fixture,name,review,permissionEvent,readAfter,submit} = await openProvider(t);
  await name.fill('未保存的草稿');
  await readAfter(permissionEvent);
  await readAfter(permissionEvent);
  assert.equal(await name.inputValue(),'未保存的草稿');
  assert.equal(await review.count(),0);
  await submit(200);
  assert.deepEqual(fixture.writes.map(w => [w.etag,w.body.name]),[['"1"','未保存的草稿']]);
});

test('a clean editor follows a background revision and uses its new configuration when editing begins', async t => {
  const {fixture,dialog,name,claim,review,changeClaims,readAfter,permissionEvent,submit} = await openProvider(t);
  changeClaims({displayName:'department_name'});
  await readAfter(permissionEvent);
  await dialog.getByText('配置 r2',{exact:true}).waitFor();
  assert.equal(await claim.inputValue(),'department_name');
  assert.equal(await review.count(),0);
  await name.fill('使用新配置');
  await submit(200);
  assert.deepEqual(fixture.writes.map(w => [w.etag,w.body.name,w.body.claimMapping.displayName]),
    [['"2"','使用新配置','department_name']]);
});

test('a 412 needs an explicit review, and a newer background revision invalidates only the outdated review', async t => {
  const {fixture,dialog,name,save,review,adopt,changeClaims,readAfter,permissionEvent,submit} = await openProvider(t);
  await name.fill('我的修改');
  changeClaims({displayName:'remote_display'});
  await submit(412);
  assert.equal(fixture.writes[0].etag,'"1"');
  await readAfter(permissionEvent);
  assert.equal(await adopt.count(),0,'a permission check cannot review a 412 for the user');
  await readAfter(() => review.click());
  await readAfter(permissionEvent);
  assert.equal(await adopt.isEnabled(),true,'a repeated read of the reviewed revision keeps the review usable');
  changeClaims({email:'remote_email'});
  fixture.provider.name = '服务器修改了同一字段';
  await readAfter(permissionEvent);
  assert.equal(await adopt.count(),0);
  assert.equal(await save.isDisabled(),true);
  assert.equal(await name.inputValue(),'我的修改');
  await readAfter(() => review.click());
  assert.equal(JSON.parse(await dialog.locator('pre').innerText()).name,'服务器修改了同一字段');
  await adopt.click();
  await submit(200);
  assert.deepEqual(fixture.writes.map(w => w.etag),['"1"','"3"']);
  assert.notEqual(fixture.writes[0].key,fixture.writes[1].key);
  assert.equal(fixture.provider.name,'我的修改');
  assert.deepEqual(fixture.provider.claimMapping,{displayName:'remote_display',email:'remote_email'});
});

test('a failed second review keeps the draft, requires a successful read and allows a retry', async t => {
  const {fixture,name,save,review,adopt,changeClaims,readAfter,submit} = await openProvider(t);
  await name.fill('保留的输入');
  changeClaims({email:'upn'});
  await submit(412);
  await readAfter(() => review.click());
  fixture.detailStatus = 503;
  await readAfter(() => review.click(),503);
  assert.equal(await adopt.count(),0);
  assert.equal(await save.isDisabled(),true);
  assert.equal(await name.inputValue(),'保留的输入');
  fixture.detailStatus = 200;
  await readAfter(() => review.click());
  await adopt.click();
  await submit(200);
  assert.equal(fixture.provider.claimMapping.email,'upn');
});

test('a concurrent permission recheck lets a pending user review finish', async t => {
  const {fixture,page,name,review,adopt,save,changeClaims,permissionEvent,submit} = await openProvider(t);
  await name.fill('等待核对');
  changeClaims({email:'external_email'});
  await submit(412);
  let release;
  fixture.nextDetail = new Promise(resolve => { release = resolve; });
  try {
    await Promise.all([page.waitForRequest(r => new URL(r.url()).pathname === detail),review.click()]);
    const [list] = await Promise.all([page.waitForResponse(r => {
      const url = new URL(r.url());
      return url.pathname === providers && url.searchParams.get('page') === '1';
    }),permissionEvent()]);
    await list.finished();
    assert.equal(fixture.detailReads,2,'the recheck must not supersede the pending review with a background detail read');
    assert.equal(await save.isDisabled(),true);
  } finally { release(); }
  await adopt.click();
  await submit(200);
  assert.deepEqual(fixture.writes.map(w => w.etag),['"1"','"2"']);
  assert.equal(fixture.provider.name,'等待核对');
  assert.equal(fixture.provider.claimMapping.email,'external_email');
});

test('a 412 during a confirmed operation retires that confirmation and requires a new one for the reviewed revision', async t => {
  const {fixture,dialog,review,adopt,claim,changeClaims,readAfter} = await openProvider(t);
  const rotate = dialog.getByRole('button',{name:'秘密轮换后更新认证修订'});
  const confirmation = dialog.getByRole('alertdialog',{name:'确认身份源操作'});
  const confirm = confirmation.getByRole('button',{name:'确认更新认证修订'});
  await rotate.click();
  changeClaims({displayName:'r2_name'});
  await confirm.click();
  await review.waitFor();
  assert.equal(await confirmation.count(),0,'the stale confirmation cannot be retried on a different revision');
  await readAfter(() => review.click());
  await adopt.click();
  assert.equal(await claim.inputValue(),'r2_name');
  assert.deepEqual(fixture.rotations,['"1"']);
  await rotate.click();
  await confirm.click();
  await dialog.getByText('认证修订已更新，旧 SSO 会话将在下一次请求失效。',{exact:true}).waitFor();
  assert.deepEqual(fixture.rotations,['"1"','"2"']);
});

test('losing SSO management permission clears a reviewed draft before it can be adopted or written', async t => {
  const {fixture,page,dialog,name,review,adopt,changeClaims,readAfter,permissionEvent,submit} = await openProvider(t);
  await name.fill('已撤权的草稿');
  changeClaims({displayName:'latest_name'});
  await submit(412);
  await readAfter(() => review.click());
  assert.equal(await adopt.isEnabled(),true);
  fixture.permissions = [];
  await permissionEvent();
  await dialog.waitFor({state:'hidden'});
  assert.equal(await page.getByRole('button',{name:'保留输入，采用最新修订'}).count(),0);
  assert.deepEqual(fixture.writes.map(w => w.etag),['"1"']);
  assert.equal(fixture.provider.name,'企业身份源');
});
