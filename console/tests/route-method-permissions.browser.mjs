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
const appPath = `/applications/${appId}`, matrixPath = appPath + '/permission-matrix';
const usage = {state:'Unknown',coverageStart:null,completeThrough:null,missingNodes:[],gaps:[],reason:'BrowserFixture'};
const legacy = {id:permissionId,apiId:api,environmentId:environment,revision:3,mode:'LegacyApi',state:'Active',routeMethods:null,validFrom:'2026-11-01T00:00:59.123456Z',
  expiresAt:'2027-10-01T00:00:59.654321Z'};
const existing = {...legacy,mode:'RouteMethod',routeMethods:[{routeId,authorizationRevision:5,methods:['GET']}]};
const target = '订单 API · 目标环境';
const results = [];

function authority(grant) {
  if (!grant) return null;
  const methods = grant.routeMethods?.map(item => ({...item,path:'/orders',matchesRoute:true})) ?? null;
  return {mode:grant.mode,state:'Active',validFrom:grant.validFrom,expiresAt:grant.expiresAt,methods,
    methodCount:methods?.reduce((sum,item) => sum + item.methods.length,0) ?? 0};
}

try {
  for (const scenario of ['legacy-create','explicit-create','convert-legacy','stale-route','stale-permission','option-read-lost']) {
    const context = await browser.newContext({timezoneId:'UTC'});
    try {
      const page = await context.newPage(), errors = [], previews = [], commits = [], matrixRevisions = [];
      page.setDefaultTimeout(10_000);
      let routeReads = 0, authorizationRevision = 5;
      let row = scenario.endsWith('create') ? undefined : scenario === 'convert-legacy' ? legacy : existing;
      page.on('pageerror',error => errors.push(error.message));
      await page.route('**/api/v1/**',async route => {
        const request = route.request(), url = new URL(request.url()), endpoint = url.pathname.slice('/api/v1'.length);
        const reply = (body,status=200) => route.fulfill({status,contentType:'application/json',body:JSON.stringify(body)});
        if (endpoint === '/auth/me') return reply({id:id(8),username:'permissions-review',displayName:'授权检查',
          authSource:'local', permissions:['app.read','api.read','environment.read','route.read','app.permission.manage'],scopes:[{scope:{organizationId:org,projectId:project},accessMode:'read_write'}]});
        if (endpoint === '/auth/csrf') return reply({token:'permission-csrf'});
        if (endpoint === '/scope-tree') return reply({organizations:[{id:org,name:'组织'}],
          projects:[{id:project,organizationId:org,name:'项目'}],environments:[{id:environment,projectId:project,name:'目标环境'}]});
        if (endpoint === appPath) return reply({application:{id:appId,organizationId:org,projectId:project,name:'测试应用',
          status:'Active',revision:1},credentials:[],permissions:row?[row]:[],runningConfigVersions:{}});
        if (endpoint === appPath + '/credential-usage') return reply({coverage:usage,page:{items:[],total:0,page:1,pageSize:50}});
        if (endpoint === matrixPath && request.method() === 'GET') {
          assert.equal(url.searchParams.get('projectId'),project);
          assert.equal(url.searchParams.get('page'),'1');
          matrixRevisions.push(row?.revision);
          return reply({applicationId:appId,projectId:project,evaluatedAt:new Date().toISOString(),page:1,pageSize:50,total:1,
            environments:[{id:environment,code:'TARGET',name:'目标环境',isProduction:false,canManage:true,canGrantLegacy:true,canGrantRouteMethods:true}],
            items:[{id:api,code:'ORDERS',name:'订单 API',grants:row ? [row] : []}]});
        }
        if (endpoint === appPath + '/route-method-options') {
          assert.equal(url.searchParams.get('environmentId'),environment);
          assert.equal(url.searchParams.get('apiId'),api);
          routeReads++;
          if (scenario === 'option-read-lost' && routeReads === 2) return reply({detail:'目标路由暂时不可读取。'},503);
          return reply({environmentId:environment,apiId:api,routes:[{routeId,path:'/orders',authorizationRevision,methods:['GET','POST']}]});
        }
        if (endpoint === matrixPath + '/preview' || endpoint === matrixPath + '/commit') {
          assert.equal(request.method(),'POST');
          assert.ok(request.headers()['idempotency-key']);
          assert.equal(request.headers()['x-csrf-token'],'permission-csrf');
          const body = request.postDataJSON();
          assert.equal(body.projectId,project);
          assert.equal(body.changes.length,1);
          const [change] = body.changes;
          assert.equal(change.apiId,api);
          assert.equal(change.environmentId,environment);
          assert.equal(change.action,'Save');
          if (endpoint.endsWith('/preview')) {
            previews.push(body);
            return reply({previewToken:'preview-' + previews.length,expiresAt:new Date(Date.now()+60_000).toISOString(),
              requiresProductionConfirmation:false,items:[{apiId:api,environmentId:environment,isProduction:false,
                before:authority(row),after:authority(change),running:{status:'Unconfirmed',configVersion:null,deploymentSequence:null,grant:null}}]});
          }
          commits.push(body);
          assert.equal(body.previewToken,'preview-' + previews.length);
          assert.deepEqual(body.changes,previews.at(-1).changes);
          assert.equal(body.confirmProduction,false);
          if (commits.length === 1 && scenario === 'stale-route') {
            authorizationRevision++;
            return reply({code:'route_method_revision_changed',detail:'目标路由修订已变化。'},409);
          }
          if (commits.length === 1 && scenario === 'stale-permission') {
            row = {...row,revision:4};
            return reply({code:'stale_revision',detail:'数据已更新。'},412);
          }
          row = {...change,id:permissionId,state:'Active',revision:(row?.revision ?? 0)+1};
          return reply({appliedCount:1,activation:'WorkingConfiguration'});
        }
        return reply({detail:'Unexpected fixture API request'},404);
      });
      await page.goto(new URL(appPath,base).href);
      await page.getByRole('button',{name:'API 授权',exact:true}).click();
      const create = scenario.endsWith('create');
      await page.getByRole('button',{name:(create ? '新增授权 ' : '编辑授权 ') + target,exact:true}).click();
      const dialog = page.getByRole('dialog'), preview = dialog.getByRole('button',{name:'预览影响',exact:true});
      const mode = dialog.getByRole('combobox',{name:'授权模式 ' + target});
      assert.equal(await dialog.getByRole('combobox').count(),1);
      if (scenario === 'explicit-create' || scenario === 'convert-legacy') await mode.selectOption('RouteMethod');
      assert.equal(await mode.inputValue(),scenario === 'legacy-create' ? 'LegacyApi' : 'RouteMethod');
      const get = dialog.getByRole('checkbox',{name:'GET /orders'});
      const post = dialog.getByRole('checkbox',{name:'POST /orders'});
      const refresh = dialog.getByRole('button',{name:'重新读取目标路由'});
      if (scenario !== 'legacy-create') {
        await post.waitFor();
        assert.equal(await post.isChecked(),false);
        assert.equal(await get.isChecked(),!create && scenario !== 'convert-legacy');
        if (scenario === 'option-read-lost') {
          await refresh.click();
          await dialog.getByRole('alert').waitFor();
          assert.equal(await preview.isDisabled(),true);
          assert.equal(await get.count(),0);
          await refresh.click();
          await get.waitFor();
          assert.equal(await get.isChecked(),false);
          assert.equal(await preview.isDisabled(),true);
        }
        if (await get.isChecked()) await get.uncheck();
        assert.equal(await preview.isDisabled(),true);
        await post.check();
      }
      async function previewAndCommit() {
        await preview.click();
        const impact = dialog.getByRole('region',{name:'变更后工作授权'});
        await impact.waitFor();
        if (scenario !== 'legacy-create') assert.match(await impact.innerText(),/POST/);
        const response = page.waitForResponse(result => new URL(result.url()).pathname.endsWith(matrixPath + '/commit'));
        await dialog.getByRole('button',{name:'提交工作配置'}).click();
        return (await response).status();
      }
      let status = await previewAndCommit();
      if (scenario === 'stale-route' || scenario === 'stale-permission') {
        const stalePermission = scenario === 'stale-permission';
        assert.equal(status,stalePermission ? 412 : 409);
        assert.equal(commits[0].changes[0].revision,3);
        assert.deepEqual(commits[0].changes[0].routeMethods,[{routeId,authorizationRevision:5,methods:['POST']}]);
        await dialog.getByRole('alert').filter({hasText:stalePermission ? '授权修订已变化' : '预览需要更新'}).waitFor();
        assert.equal(await preview.isDisabled(),true);
        if (stalePermission) {
          await dialog.getByRole('button',{name:'读取最新授权修订'}).click();
          await dialog.getByText(/已读取最新授权修订/).waitFor();
          assert.equal(matrixRevisions.at(-1),4);
          await refresh.click();
        }
        await post.waitFor();
        assert.equal(await post.isChecked(),false);
        assert.equal(await preview.isDisabled(),true);
        await post.check();
        status = await previewAndCommit();
      }
      assert.equal(status,200);
      await dialog.waitFor({state:'detached'});
      await page.getByRole('button',{name:'编辑授权 ' + target,exact:true}).waitFor();
      const latest = commits.at(-1).changes[0];
      assert.equal(latest.permissionId,create ? undefined : permissionId);
      assert.equal(latest.revision,create ? undefined : scenario === 'stale-permission' ? 4 : 3);
      assert.equal(matrixRevisions.at(-1),row.revision);
      assert.equal(previews.length,commits.length);
      if (scenario === 'legacy-create') {
        assert.equal(routeReads,0);
        assert.equal(latest.mode,'LegacyApi');
        assert.equal(Object.hasOwn(latest,'routeMethods'),false);
      } else {
        assert.equal(latest.mode,'RouteMethod');
        assert.deepEqual(latest.routeMethods,[{routeId,authorizationRevision,methods:['POST']}]);
        if (!create) {
          assert.equal(latest.validFrom,legacy.validFrom);
          assert.equal(latest.expiresAt,legacy.expiresAt);
        }
      }
      assert.deepEqual(errors,[]);
      results.push({scenario,previews:previews.length,commits:commits.length,routeReads});
    } finally { await context.close(); }
  }
  console.log(JSON.stringify({passed:results.length,results},null,2));
} finally { await browser.close(); }
