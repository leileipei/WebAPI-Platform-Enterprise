import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const id = n => '10000000-0000-0000-0000-' + String(n).padStart(12, '0');
const org = id(1), project = id(2), applicationId = id(3);
const application = {id:applicationId, organizationId:org, projectId:project, code:'WINDOWS',
  name:'凭证窗口测试应用', owner:'测试负责人', status:'Active', revision:2, activation:'WorkingConfiguration'};
const detailPath = '/applications/' + applicationId;
const coverage = {state:'Unknown', coverageStart:null, completeThrough:null, freshness:null,
  missingNodes:[], gaps:[], reason:'BrowserFixture'};
const now = new Date('2026-11-22T04:00:00Z');
const past = '2026-11-21T04:00:00Z', future = '2026-11-23T04:00:00Z';
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

function credential(n, validFrom, expiresAt, status = 'Active') {
  return {id:id(n), applicationId, accessKey:'fixture-access-' + String(n).padStart(4, '0'), secretLast4:'sX91',
    status, validFrom, expiresAt, lastUsedAt:null, revokedAt:status === 'Revoked' ? past : null,
    revision:1, activation:'WorkingConfiguration'};
}

async function openCredentials(t, credentials, {list = false, usageStatus = 200, readOnly = false} = {}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(5000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.clock.install({time:new Date(now.getTime() - 60000)});
  await page.clock.pauseAt(now);
  await page.route('**/*', route => {
    const path = new URL(route.request().url()).pathname;
    const reply = (json, status = 200) => route.fulfill({json,status});
    if (path.startsWith('/assets/')) return route.fulfill({path:join(dist,path.slice(1))});
    if (path === '/applications' || path === detailPath) return route.fulfill({path:join(dist,'index.html')});
    if (path === '/api/v1/auth/me') return reply({id:id(4), username:'fixture-user', displayName:'测试用户',
      authSource:'local', permissions:readOnly ? ['app.read'] : ['app.read','credential.manage'],
      scopes:[{scope:{organizationId:org,projectId:project}, accessMode:readOnly ? 'read_only' : 'read_write'}]});
    if (path === '/api/v1/scope-tree') return reply({organizations:[{id:org,name:'测试组织',status:'Active'}],
      projects:[{id:project,organizationId:org,name:'测试项目',status:'Active'}], environments:[]});
    if (path === '/api/v1/applications') return reply({items:[application],total:1,page:1,pageSize:50});
    if (path === '/api/v1' + detailPath) return reply({application,credentials,permissions:[],runningConfigVersions:{}});
    if (path === '/api/v1' + detailPath + '/credential-usage') {
      if (usageStatus !== 200) return reply({detail:'统计暂不可用'},usageStatus);
      return reply({window:{from:past,to:now.toISOString()},visibleEnvironmentIds:[],coverage,
        page:{items:credentials.map(row => ({credentialId:row.id,accessKeyLast4:row.accessKey.slice(-4),
          count:null,lastObservedAt:null,visibleLastUsedAt:null,coverage})),total:credentials.length,page:1,pageSize:50}});
    }
    throw new Error('Unexpected fixture request: ' + route.request().method() + ' ' + path);
  });
  await page.goto('http://127.0.0.1' + (list ? '/applications' : detailPath));
  if (list) await page.getByRole('link',{name:'详情与接入'}).click();
  await page.getByRole('heading',{name:application.name}).waitFor();
  if (usageStatus === 200) await page.getByRole('status').filter({hasText:'尚无可用采集'}).waitFor();
  else await page.getByRole('alert').filter({hasText:'统计暂不可用'}).waitFor();
  const row = key => page.getByRole('row').filter({has:page.getByText('••••' + key.accessKey.slice(-4),{exact:true})});
  const status = key => row(key).getByRole('cell').nth(1);
  return {page,row,status,errors};
}

test('application list opens distinguishable credential windows while keeping edit, revoke and key masking semantics', async t => {
  const pending = credential(11,future,'2026-11-24T04:00:00Z');
  const active = credential(12,past,future);
  const expired = credential(13,past,now.toISOString());
  const revoked = credential(14,past,now.toISOString(),'Revoked');
  const credentials = [pending,active,expired,revoked];
  const {page,row,status,errors} = await openCredentials(t,credentials,{list:true});
  assert.deepEqual(await Promise.all(credentials.map(key => status(key).innerText())),['待生效','有效期内','已过期','已撤销']);
  await page.getByRole('columnheader',{name:/工作配置.*浏览器时间/}).waitFor();
  assert.match(await page.locator('.notice').first().innerText(), /网关认证采用已发布/);
  assert.equal(await row(expired).getByRole('button',{name:'轮换',exact:true}).isEnabled(),true);
  assert.equal(await row(revoked).getByRole('button',{name:'轮换',exact:true}).isDisabled(),true);
  const table = await page.getByRole('table').innerText();
  for (const key of credentials) assert.ok(!table.includes(key.accessKey));
  assert.ok(!table.includes('sX91'));
  await row(expired).getByRole('button',{name:'有效期 / 撤销'}).click();
  const editor = page.getByRole('dialog',{name:'编辑凭证窗口'});
  assert.equal(await editor.getByLabel('状态').inputValue(),'Active');
  await editor.getByRole('button',{name:'关闭',exact:true}).click();
  assert.deepEqual(errors,[]);
});

test('an open detail table crosses its exact start and expiry without a refresh or a usage poll', async t => {
  const key = credential(15,'2026-11-22T12:00:01+08:00','2026-11-22T04:00:02.0000001Z');
  const {page,status,errors} = await openCredentials(t,[key]);
  assert.equal(await status(key).innerText(),'待生效');
  await page.clock.runFor(999);
  assert.equal(await status(key).innerText(),'待生效');
  await page.clock.runFor(1);
  await status(key).getByText('有效期内',{exact:true}).waitFor();
  await page.clock.runFor(1000);
  assert.equal(await status(key).innerText(),'有效期内');
  await page.clock.runFor(1);
  await status(key).getByText('已过期',{exact:true}).waitFor();
  assert.deepEqual(errors,[]);
});

test('missing or invalid historic window data stays unknown when usage is unavailable', async t => {
  const missing = credential(16,past,null);
  const malformed = credential(17,'2026-02-30T04:00:00Z',future);
  const revoked = credential(18,'not a date',null,'Revoked');
  const {page,row,status,errors} = await openCredentials(t,[missing,malformed,revoked],{usageStatus:503,readOnly:true});
  assert.deepEqual(await Promise.all([missing,malformed,revoked].map(key => status(key).innerText())),
    ['状态未知','状态未知','已撤销']);
  assert.match(await row(missing).getByRole('cell').nth(2).innerText(), /未知/);
  assert.match(await row(malformed).getByRole('cell').nth(2).innerText(), /未知/);
  assert.doesNotMatch(await page.getByRole('table').innerText(), /1970|Invalid Date/);
  assert.equal(await row(missing).getByRole('button',{name:'有效期 / 撤销'}).isDisabled(),true);
  assert.deepEqual(errors,[]);
});

test('an operator can open a malformed historic window to correct its missing date', async t => {
  const key = credential(19,'not a date',future);
  const {page,row,errors} = await openCredentials(t,[key]);
  await row(key).getByRole('button',{name:'有效期 / 撤销'}).click();
  assert.deepEqual(errors,[]);
  const editor = page.getByRole('dialog',{name:'编辑凭证窗口'});
  assert.equal(await editor.getByLabel('生效时间').inputValue(),'');
  assert.notEqual(await editor.getByLabel('失效时间').inputValue(),'');
  assert.equal(await editor.getByLabel('状态').inputValue(),'Active');
});
