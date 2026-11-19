// Run against an explicitly started loopback preview and synthetic API responses.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';

const base = new URL(process.argv[2]);
if (base.protocol !== 'http:' || base.hostname !== '127.0.0.1' ||
    !process.env.WEBAPI_PLAYWRIGHT_PACKAGE || !process.env.WEBAPI_CHROMIUM_EXECUTABLE)
  throw new Error('An explicit loopback preview and installed Playwright/Chromium paths are required.');
const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const browser = await chromium.launch({headless:true, executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});
const id = n => '20000000-0000-0000-0000-' + String(n).padStart(12,'0');
const org = id(1), project = id(2), environment = id(3), api = id(4), appId = id(5), permissionId = id(6), routeId = id(7);
const appPath = `/applications/${appId}`, permissionPath = appPath + '/permissions';
const usage = {state:'Unknown',coverageStart:null,completeThrough:null,missingNodes:[],gaps:[],reason:'BrowserFixture'};
const legacy = {id:permissionId,apiId:api,environmentId:environment,revision:3,validFrom:'2026-11-01T00:00:59.123456Z',
  expiresAt:'2027-10-01T00:00:59.654321Z'};
const existing = {...legacy,mode:'RouteMethod',routeMethods:[{routeId,authorizationRevision:5,methods:['GET']}]};
const results = [];

try {
  for (const scenario of ['legacy-create','explicit-create','convert-legacy','stale-route','stale-permission','option-read-lost']) {
    const context = await browser.newContext({timezoneId:'UTC'});
    try {
      const page = await context.newPage(), errors = [], writes = [];
      let routeReads = 0, authorizationRevision = scenario === 'stale-route' ? 6 : 5;
      let row = scenario.endsWith('create') ? undefined : scenario === 'convert-legacy' ? legacy : existing;
      page.on('pageerror',error => errors.push(error.message));
      await page.route('**/api/v1/**',async route => {
        const request = route.request(), url = new URL(request.url()), endpoint = url.pathname.slice('/api/v1'.length);
        const reply = (body,status=200) => route.fulfill({status,contentType:'application/json',body:JSON.stringify(body)});
        if (endpoint === '/auth/me') return reply({id:id(8),username:'permissions-review',displayName:'授权检查',
          authSource:'local', permissions:['app.read','app.permission.manage'],scopes:[{scope:{organizationId:org,projectId:project},accessMode:'read_write'}]});
        if (endpoint === '/auth/csrf') return reply({token:'permission-csrf'});
        if (endpoint === '/scope-tree') return reply({organizations:[{id:org,name:'组织'}],
          projects:[{id:project,organizationId:org,name:'项目'}],environments:[{id:environment,projectId:project,name:'目标环境'}]});
        if (endpoint === appPath) return reply({application:{id:appId,organizationId:org,projectId:project,name:'测试应用',
          status:'Active',revision:1},credentials:[],permissions:row?[row]:[],runningConfigVersions:{}});
        if (endpoint === appPath + '/credential-usage') return reply({coverage:usage,page:{items:[],total:0,page:1,pageSize:50}});
        if (endpoint === `/projects/${project}/apis`) return reply({items:[{id:api,name:'订单 API'}],total:1,page:1,pageSize:50});
        if (endpoint === appPath + '/route-method-options') {
          assert.equal(url.searchParams.get('environmentId'),environment);
          assert.equal(url.searchParams.get('apiId'),api);
          routeReads++;
          if (scenario === 'option-read-lost' && routeReads === 2) return route.abort('failed');
          return reply({environmentId:environment,apiId:api,routes:[{routeId,path:'/orders',authorizationRevision,methods:['GET','POST']}]});
        }
        if (endpoint === permissionPath && request.method() === 'GET') return reply(row?[row]:[]);
        if (endpoint === permissionPath || endpoint === permissionPath + '/' + permissionId) {
          const body = request.postDataJSON(), etag = request.headers()['if-match'];
          assert.ok(request.headers()['idempotency-key']);
          assert.equal(request.headers()['x-csrf-token'],'permission-csrf');
          writes.push({body,etag,method:request.method()});
          if (writes.length === 1 && scenario === 'stale-route') {
            authorizationRevision++;
            return reply({code:'route_method_revision_changed',detail:'目标路由修订已变化。'},409);
          }
          if (writes.length === 1 && scenario === 'stale-permission') {
            row = {...row,revision:4};
            return reply({code:'stale_revision',detail:'数据已更新。'},412);
          }
          row = {...body,id:permissionId,revision:(row?.revision || 0) + 1};
          return reply(row);
        }
        return reply({detail:'Unexpected fixture API request'},404);
      });
      await page.goto(new URL(appPath,base).href);
      await page.getByRole('button',{name:'API 授权',exact:true}).click();
      const create = scenario.endsWith('create');
      const action = scenario === 'legacy-create' ? '＋ 新增授权' : scenario === 'explicit-create' ? '＋ 新增路由方法授权'
        : scenario === 'convert-legacy' ? '改为路由方法授权' : '编辑授权';
      await page.getByRole('button',{name:action,exact:true}).click();
      const dialog = page.getByRole('dialog'), save = dialog.getByRole('button',{name:'保存',exact:true});
      const selectedApi = dialog.getByRole('combobox').nth(0), selectedEnvironment = dialog.getByRole('combobox').nth(1);
      if (create) {
        await selectedApi.selectOption(api);
        await selectedEnvironment.selectOption(environment);
      } else {
        assert.equal(await selectedApi.isDisabled(),true);
        assert.equal(await selectedEnvironment.isDisabled(),true);
      }
      if (scenario !== 'legacy-create') {
        const get = dialog.getByRole('checkbox',{name:'GET /orders'});
        const post = dialog.getByRole('checkbox',{name:'POST /orders'});
        const refresh = dialog.getByRole('button',{name:'重新读取目标路由'});
        await post.waitFor();
        assert.equal(await post.isChecked(),false);
        assert.equal(await get.isChecked(),['stale-permission','option-read-lost'].includes(scenario));
        if (scenario === 'option-read-lost') {
          await refresh.click();
          await dialog.getByRole('alert').waitFor();
          assert.equal(await save.isDisabled(),true);
          assert.equal(await get.count(),0);
          await refresh.click();
          await get.waitFor();
          assert.equal(await get.isChecked(),false);
          assert.equal(await save.isDisabled(),true);
        }
        if (await get.isChecked()) await get.uncheck();
        assert.equal(await save.isDisabled(),true);
        await post.check();
      }
      let response = page.waitForResponse(result => result.request().method() === (create?'POST':'PUT'));
      await save.click();
      let status = (await response).status();
      if (scenario === 'stale-route' || scenario === 'stale-permission') {
        const stale = scenario === 'stale-permission';
        assert.equal(status,stale?412:409);
        await dialog.getByRole('alert').filter({hasText:stale?'授权修订已变化':'目标路由或方法授权已变化'}).waitFor();
        assert.equal(await save.isDisabled(),true);
        if (stale) {
          await dialog.getByRole('button',{name:'查看最新内容'}).click();
          await dialog.getByRole('button',{name:'保持我的输入，采用最新修订'}).click();
        }
        await dialog.getByRole('button',{name:'重新读取目标路由'}).click();
        const post = dialog.getByRole('checkbox',{name:'POST /orders'});
        await post.waitFor();
        assert.equal(await post.isChecked(),false);
        assert.equal(await save.isDisabled(),true);
        await post.check();
        response = page.waitForResponse(result => result.request().method() === 'PUT');
        await save.click();
        status = (await response).status();
      }
      assert.equal(status,200);
      const latest = writes.at(-1);
      assert.equal(latest.etag,create?undefined:scenario === 'stale-permission'?'"4"':'"3"');
      assert.equal(latest.body.environmentId,environment);
      assert.equal(latest.body.apiId,api);
      if (scenario === 'legacy-create') {
        assert.equal(routeReads,0);
        assert.equal(Object.hasOwn(latest.body,'mode'),false);
        assert.equal(Object.hasOwn(latest.body,'routeMethods'),false);
      } else {
        assert.equal(latest.body.mode,'RouteMethod');
        assert.deepEqual(latest.body.routeMethods,[{routeId,authorizationRevision,methods:['POST']}]);
        if (!create) {
          assert.equal(latest.body.validFrom,legacy.validFrom);
          assert.equal(latest.body.expiresAt,legacy.expiresAt);
        }
      }
      assert.deepEqual(errors,[]);
      results.push({scenario,writes:writes.length,routeReads});
    } finally { await context.close(); }
  }
  console.log(JSON.stringify({passed:results.length,results},null,2));
} finally { await browser.close(); }
