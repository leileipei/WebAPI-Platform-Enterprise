// Run node --test with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
// Only the built UI runs. All resources and API responses are fulfilled in this browser context.
import {test, after, before} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

const catalog = ['read','write'].map(id => ({id, code:id, name:id, organizationId:null, revision:1, isSystem:false, permissions:[]}));
async function openUsers(t, options = {}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(5000);
  const fixture = {
    roles:catalog, roleIds:['read'], revision:7, catalogStatus:200, permissions:['user.manage','role.manage'],
    organizationStatus:'Active', puts:[], ...options
  };
  const user = () => ({id:'target',username:'target',displayName:'目标用户',email:null,status:'Active',
    authSource:'local',mustChangePassword:false,roleIds:fixture.roleIds,revision:fixture.revision});
  await page.route('**/*',async route => {
    const path = new URL(route.request().url()).pathname;
    const reply = (json,status=200) => route.fulfill({status,json});
    if (path.startsWith('/assets/')) return route.fulfill({path:join(dist,path.slice(1))});
    if (path === '/users') return route.fulfill({path:join(dist,'index.html')});
    if (path === '/api/v1/auth/me') return reply({id:'admin',username:'admin',displayName:'管理员',authSource:'local',permissions:fixture.permissions,scopes:[]});
    if (path === '/api/v1/auth/csrf') return reply({token:'local-test-token'});
    if (path === '/api/v1/scope-tree') return reply({organizations:[{id:'org',name:'组织',status:fixture.organizationStatus}],projects:[],environments:[]});
    if (path === '/api/v1/users') return reply({items:[user()],total:1,page:1,pageSize:50});
    if (path === '/api/v1/roles') {
      await fixture.delayCatalog;
      return fixture.catalogStatus === 200 ? reply(fixture.roles) : reply({detail:'角色目录读取失败'},fixture.catalogStatus);
    }
    if (path === '/api/v1/users/target/roles' && route.request().method() === 'PUT') {
      const headers = route.request().headers(), body = route.request().postDataJSON();
      fixture.puts.push({body,etag:headers['if-match'],key:headers['idempotency-key'],csrf:headers['x-csrf-token']});
      if (fixture.putStatus) return reply({detail:'角色写入失败'},fixture.putStatus);
      if (headers['if-match'] !== '"' + fixture.revision + '"') return reply({detail:'修订已变更'},412);
      fixture.roleIds = body.roleIds;
      fixture.revision++;
      return reply(user());
    }
    throw new Error('Unexpected request ' + path);
  });
  await page.goto('http://127.0.0.1/users');
  const assign = page.getByRole('button',{name:'分配角色',exact:true});
  await assign.waitFor();
  const dialog = page.getByRole('dialog');
  return {page,fixture,assign,dialog,save:dialog.getByRole('button',{name:'保存',exact:true}),
    refresh:page.getByRole('button',{name:'刷新',exact:true})};
}

test('a delayed or failed catalog cannot cause an empty replacement; refresh recovers on the same page',async t => {
  let release;
  const delayCatalog = new Promise(resolve => { release = resolve; });
  const {page,fixture,assign,dialog,save,refresh} = await openUsers(t,{delayCatalog,catalogStatus:503});
  try {
    if (await assign.isEnabled()) {
      await assign.click();
      if (await save.isEnabled()) {
        await Promise.all([page.waitForResponse(r => r.request().method() === 'PUT'),save.click()]);
      }
    }
    assert.deepEqual(fixture.puts,[], 'loading the directory must never authorize an empty PUT');
  } finally { release(); }
  await page.getByRole('alert').filter({hasText:'角色目录读取失败'}).first().waitFor();
  assert.equal(await assign.isDisabled(),true);
  fixture.catalogStatus = 200;
  await refresh.click();
  await assign.click();
  assert.equal(await dialog.getByRole('checkbox',{name:'read · read'}).isChecked(),true);
  assert.equal(await save.isDisabled(),true);
});

test('real assignment writes preserve undisplayed grants and send the target revision and idempotency key',async t => {
  const {fixture,assign,dialog,save} = await openUsers(t,{roleIds:['read','external']});
  await assign.click();
  await dialog.getByRole('checkbox',{name:'read · read'}).uncheck();
  await dialog.getByRole('checkbox',{name:'write · write'}).check();
  await save.click();
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual([...fixture.puts[0].body.roleIds].sort(),['external','write']);
  assert.equal(fixture.puts[0].etag,'"7"');
  assert.ok(fixture.puts[0].key);
  assert.equal(fixture.puts[0].csrf,'local-test-token');
});

test('an operator may add the first role and explicitly remove the last role',async t => {
  const {fixture,assign,dialog,save} = await openUsers(t,{roleIds:[]});
  await assign.click();
  assert.equal(await save.isDisabled(),true);
  await dialog.getByRole('checkbox',{name:'read · read'}).check();
  await save.click();
  await dialog.waitFor({state:'hidden'});
  await assign.click();
  await dialog.getByRole('checkbox',{name:'read · read'}).uncheck();
  await save.click();
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual(fixture.puts.map(p => [p.etag,p.body]),[['"7"',{roleIds:['read']}],['"8"',{roleIds:[]}]]);
});

test('refresh discards an open replacement and reopens on fresh user and directory data',async t => {
  const {fixture,assign,dialog,save,refresh} = await openUsers(t);
  await assign.click();
  await dialog.getByRole('checkbox',{name:'read · read'}).uncheck();
  fixture.roleIds = ['write'];
  fixture.revision++;
  fixture.roles = [catalog[1]];
  await refresh.evaluate(button => button.click());
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual(fixture.puts,[]);
  await assign.click();
  assert.equal(await dialog.getByRole('checkbox').count(),1);
  assert.equal(await dialog.getByRole('checkbox').isChecked(),true);
  assert.equal(await save.isDisabled(),true);
});

test('a stale revision forces review of the latest grants before another save',async t => {
  const {fixture,assign,dialog,save} = await openUsers(t);
  await assign.click();
  await dialog.getByRole('checkbox',{name:'read · read'}).uncheck();
  fixture.roleIds = ['read','write'];
  fixture.revision++;
  await save.click();
  await dialog.waitFor({state:'hidden'});
  await assign.click();
  assert.equal(await dialog.getByRole('checkbox',{name:'write · write'}).isChecked(),true);
  assert.equal(await save.isDisabled(),true);
  await dialog.getByRole('checkbox',{name:'read · read'}).uncheck();
  await save.click();
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual(fixture.puts.map(p => [p.etag,p.body]),[['"7"',{roleIds:[]}],['"8"',{roleIds:['write']}]]);
});

test('last-admin refusal leaves the assignments intact and allows the operator to correct the form',async t => {
  const {fixture,assign,dialog,save} = await openUsers(t,{putStatus:409});
  await assign.click();
  await dialog.getByRole('checkbox',{name:'read · read'}).uncheck();
  await save.click();
  await dialog.getByRole('alert').waitFor();
  assert.deepEqual(fixture.roleIds,['read']);
  await dialog.getByRole('checkbox',{name:'read · read'}).check();
  assert.equal(await save.isDisabled(),true);
});

test('a change in selected organization status invalidates an open draft even when IDs stay the same',async t => {
  const {page,fixture,assign,dialog} = await openUsers(t);
  await assign.click();
  await dialog.getByRole('checkbox',{name:'read · read'}).uncheck();
  fixture.organizationStatus = 'Disabled';
  await page.getByRole('button',{name:'刷新会话与权限'}).evaluate(button => button.click());
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual(fixture.puts,[]);
});

test('catalog permission loss cannot leave an editable assignment in an existing session',async t => {
  const {page,fixture,assign,dialog} = await openUsers(t);
  await assign.click();
  await dialog.getByRole('checkbox',{name:'read · read'}).uncheck();
  fixture.permissions = ['user.manage'];
  await page.evaluate(() => window.dispatchEvent(new Event('permission-refresh')));
  await dialog.waitFor({state:'hidden'});
  await assign.waitFor();
  assert.equal(await assign.isDisabled(),true);
  assert.deepEqual(fixture.puts,[]);
});

test('retry after a transient write failure reuses the original intent and key',async t => {
  const {fixture,assign,dialog,save} = await openUsers(t,{putStatus:503});
  await assign.click();
  await dialog.getByRole('checkbox',{name:'write · write'}).check();
  await save.click();
  await dialog.getByRole('alert').waitFor();
  fixture.putStatus = 0;
  await save.click();
  await dialog.waitFor({state:'hidden'});
  assert.equal(fixture.puts.length,2);
  assert.deepEqual(fixture.puts[1],fixture.puts[0]);
  assert.deepEqual(fixture.roleIds.sort(),['read','write']);
});

test('a failed reload cannot reuse a cached catalog and an empty catalog cannot erase grants',async t => {
  const {page,fixture,assign,dialog,refresh} = await openUsers(t);
  await assign.click();
  await dialog.getByRole('button',{name:'关闭'}).click();
  let release;
  fixture.delayCatalog = new Promise(resolve => { release = resolve; });
  fixture.catalogStatus = 503;
  try {
    await Promise.all([page.waitForRequest(r => new URL(r.url()).pathname === '/api/v1/roles'),refresh.click()]);
    assert.equal(await assign.isDisabled(),true);
  } finally { release(); }
  await page.getByRole('alert').filter({hasText:'角色目录读取失败'}).first().waitFor();
  assert.equal(await assign.isDisabled(),true);
  fixture.catalogStatus = 200;
  fixture.roles = [];
  await refresh.click();
  await page.getByText('角色目录为空',{exact:false}).waitFor();
  assert.equal(await assign.isDisabled(),true);
  assert.deepEqual(fixture.puts,[]);
  assert.deepEqual(fixture.roleIds,['read']);
});
