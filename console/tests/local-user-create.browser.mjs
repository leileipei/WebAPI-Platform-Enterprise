import {after, before, test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

const organization = {id:'org', code:'ORG', name:'示例组织', status:'Active', revision:1};
const operator = {id:'operator', username:'operator', displayName:'操作员', email:null, status:'Active',
  authSource:'local', revision:1, roleIds:[], mustChangePassword:false};

async function openUsers(t, {mode, permissions=['user.manage'], platform=true, directoryDelay}={}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(4000);
  const f = {permissions, platform, directoryDelay, meStatus:200, writes:[], users:[operator]};
  const scopes = mode ? [{scope:{organizationId:'org',projectId:null,environmentId:null},accessMode:mode}] : [];
  await page.route('**/*', async route => {
    const req = route.request(), path = new URL(req.url()).pathname;
    const reply = (body,status=200) => route.fulfill({status,json:body});
    if (path.startsWith('/assets/')) return route.fulfill({path:join(dist,path.slice(1))});
    if (!path.startsWith('/api/v1/')) return route.fulfill({path:join(dist,'index.html')});
    if (path === '/api/v1/auth/me') return f.meStatus === 200
      ? reply({id:operator.id, username:operator.username, displayName:operator.displayName, authSource:'local',
        permissions:f.permissions, scopes, mustChangePassword:false}) : reply({detail:'会话已失效'}, f.meStatus);
    if (path === '/api/v1/auth/sso/providers') return reply([]);
    if (path === '/api/v1/auth/deployment') return reply({environment:'Test'});
    if (path === '/api/v1/auth/csrf') return reply({token:'local-fixture-csrf'});
    if (path === '/api/v1/scope-tree') return reply({organizations:mode ? [organization] : [],projects:[],environments:[]});
    if (path === '/api/v1/users') {
      if (req.method() === 'GET') await f.directoryDelay;
      else {
        assert.equal(req.method(),'POST');
        f.writes.push({body:req.postDataJSON(), headers:req.headers()});
      }
      if (!f.platform || !f.permissions.includes('user.manage')) return reply({code:'platform_permission_required',detail:'需要平台用户管理权限'},403);
      if (req.method() === 'GET') return reply({items:f.users,total:f.users.length,page:1,pageSize:50});
      const {username,displayName,email} = req.postDataJSON();
      const created = {...operator,id:'created',username,displayName,email,mustChangePassword:true};
      f.users.push(created);
      return reply(created);
    }
    if (path === '/api/v1/organizations/org/projects') return reply({items:[],total:0,page:1,pageSize:50});
    throw Error('Unexpected request '+req.method()+' '+path);
  });
  await page.goto('http://127.0.0.1/users');
  await page.getByRole('heading',{name:'用户管理'}).waitFor();
  return {page,f,create:page.getByRole('button',{name:'＋ 新建',exact:true}),form:page.getByRole('dialog',{name:'新建用户管理'})};
}

async function fillUser(form) {
  await form.getByLabel('用户名').fill('new.user');
  await form.getByLabel('初始密码').fill('Fixture-Change123!');
  await form.getByLabel('显示名称').fill('新增用户');
}

for (const [scenario, mode] of [['no business scope',undefined],['only read access in the selected organization','read']]) {
  test('platform user creation succeeds with '+scenario, async t => {
    const {page,f,create,form} = await openUsers(t,{mode});
    if (mode) await page.getByRole('option',{name:organization.name}).waitFor({state:'attached'});
    await page.getByRole('cell',{name:'operator',exact:true}).waitFor();
    assert.equal(await page.getByRole('combobox',{name:'当前组织'}).inputValue(),mode ? 'org' : '');
    assert.equal(await create.isEnabled(),true);
    await create.click();
    await fillUser(form);
    await form.getByRole('button',{name:'保存',exact:true}).click();
    await form.waitFor({state:'hidden'});
    await page.getByRole('row').filter({hasText:'new.user'}).waitFor();
    assert.equal(f.writes.length,1);
    const {body,headers} = f.writes[0];
    assert.equal(body.username,'new.user');
    assert.equal(body.displayName,'新增用户');
    assert.equal(body.password,'Fixture-Change123!');
    assert.equal(body.email,null);
    assert.ok(!('organizationId' in body) && !('projectId' in body) && !('environmentId' in body));
    assert.equal(headers['x-csrf-token'],'local-fixture-csrf');
    assert.ok(headers['idempotency-key']);
  });
}

for (const [scenario, permissions] of [['no user.manage',[]],['an organization role with user.manage',['user.manage']]]) {
  test('a writable organization cannot authorize platform user creation with '+scenario, async t => {
    const {page,f,create} = await openUsers(t,{mode:'read_write',permissions,platform:false});
    await page.getByRole('alert').filter({hasText:'需要平台用户管理权限'}).waitFor();
    assert.equal(await create.isDisabled(),true);
    assert.deepEqual(f.writes,[]);
  });
}

test('the platform user directory must finish checking access before a writable scope can open the form', async t => {
  let release;
  const directoryDelay = new Promise(resolve => { release = resolve; });
  const {page,create} = await openUsers(t,{mode:'read_write',directoryDelay});
  try {
    await page.getByRole('option',{name:organization.name}).waitFor({state:'attached'});
    assert.equal(await create.isDisabled(),true);
  } finally { release(); }
  await page.getByRole('cell',{name:'operator',exact:true}).waitFor();
  assert.equal(await create.isEnabled(),true);
});

test('revoking the platform role invalidates an open form even when the permission union stays the same', async t => {
  const {page,f,create,form} = await openUsers(t,{mode:'read_write'});
  await create.click();
  await fillUser(form);
  f.platform = false;
  let release;
  f.directoryDelay = new Promise(resolve => { release = resolve; });
  try {
    await Promise.all([
      page.waitForRequest(req => new URL(req.url()).pathname === '/api/v1/users' && req.method() === 'GET'),
      page.evaluate(() => window.dispatchEvent(new Event('permission-refresh')))
    ]);
    const save = form.getByRole('button',{name:'保存',exact:true});
    assert.ok(await save.count() === 0 || await save.isDisabled());
  } finally { release(); }
  await page.getByRole('alert').filter({hasText:'需要平台用户管理权限'}).waitFor();
  await form.waitFor({state:'hidden'});
  assert.equal(await create.isDisabled(),true);
  assert.deepEqual(f.writes,[]);
  f.platform = true;
  await page.getByRole('button',{name:'刷新会话与权限'}).click();
  await page.getByRole('cell',{name:'operator',exact:true}).waitFor();
  assert.equal(await form.count(),0);
  await create.click();
  assert.equal(await form.getByLabel('用户名').inputValue(),'');
});

test('session expiration closes a local user draft without sending its password', async t => {
  const {page,f,create,form} = await openUsers(t,{mode:'read_write'});
  await create.click();
  await fillUser(form);
  f.meStatus = 401;
  await page.evaluate(() => window.dispatchEvent(new Event('permission-refresh')));
  await page.getByRole('heading',{name:'登录管理控制台'}).waitFor();
  assert.equal(await form.count(),0);
  assert.deepEqual(f.writes,[]);
});

test('the platform user grant leaves scoped project creation subject to organization write access', async t => {
  const {page,create} = await openUsers(t,{mode:'read',permissions:['user.manage','project.write']});
  await page.goto('http://127.0.0.1/projects');
  await page.getByRole('heading',{name:'项目管理'}).waitFor();
  await page.getByRole('option',{name:organization.name}).waitFor({state:'attached'});
  assert.equal(await create.isDisabled(),true);
});
