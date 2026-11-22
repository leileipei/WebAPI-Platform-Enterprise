import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const app = '00000000-0000-4000-8000-000000000001';
const organizationId = 'org';
const projects = ['alpha', 'beta'].map(id => ({id, organizationId, name:id, status:'Active'}));
const environments = projects.flatMap(p => ['dev', 'qa'].map(code => ({
  id:p.id + '-' + code, projectId:p.id, name:p.name + ' ' + code, status:'Active'
})));
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

async function metricsPage(t, projectId, environmentId) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(6000);
  await page.addInitScript(({projectId, environmentId}) => {
    localStorage.setItem('core-organization', 'org');
    localStorage.setItem('core-project', projectId);
    localStorage.setItem('core-environment', environmentId);
  }, {projectId, environmentId});
  const envelope = data => ({sourceState:'NoData', observedAt:null, sampling:null,
    coverage:{complete:true, missingNodes:[], reason:null, truncated:false}, data});
  await page.route('**/*', route => {
    const path = new URL(route.request().url()).pathname;
    const reply = json => route.fulfill({json});
    if (path.startsWith('/assets/')) return route.fulfill({path:join(dist, path.slice(1))});
    if (!path.startsWith('/api/v1/')) return route.fulfill({path:join(dist, 'index.html')});
    if (path === '/api/v1/auth/me') return reply({id:'reader', username:'reader', displayName:'测试用户',
      permissions:['app.read','metrics.read','log.read'], scopes:[{scope:{organizationId}, accessMode:'read_only'}]});
    if (path === '/api/v1/scope-tree') return reply({organizations:[{id:organizationId, name:'组织', status:'Active'}], projects, environments});
    if (path === '/api/v1/applications/' + app) return reply({
      application:{id:app, organizationId, projectId:null, code:'SHARED', name:'组织共享应用', status:'Active', revision:1},
      credentials:[], permissions:[], runningConfigVersions:{}
    });
    if (path === '/api/v1/applications/' + app + '/credential-usage') return reply({
      coverage:{state:'Unknown', coverageStart:null, completeThrough:null, missingNodes:[], gaps:[], reason:'Fixture'},
      page:{items:[], total:0, page:1, pageSize:50}
    });
    if (path === '/api/v1/observability/metrics') return reply(envelope({kpis:[], trends:{}, groups:[]}));
    if (path === '/api/v1/observability/logs') return reply(envelope({items:[], nextCursor:null, truncated:false}));
    return reply({items:[], total:0, page:1, pageSize:50});
  });
  return page;
}

async function openMetrics(t, projectId, environmentId) {
  const page = await metricsPage(t, projectId, environmentId);
  await page.goto('http://127.0.0.1/applications/' + app);
  await page.getByLabel('当前项目').getByRole('option', {name:projectId, exact:true}).waitFor({state:'attached'});
  await page.getByRole('button', {name:'Metrics', exact:true}).click();
  return page;
}

test('shared application metrics open in the selected workspace project and environment', async t => {
  const page = await openMetrics(t, 'beta', 'beta-qa');
  assert.equal(await page.getByLabel('应用指标项目').inputValue(), 'beta');
  assert.equal(await page.getByLabel('应用指标环境').inputValue(), 'beta-qa');
});

test('opening access logs carries the project, environment and shared application from the metrics view', async t => {
  const page = await openMetrics(t, 'alpha', 'alpha-dev');
  await page.getByLabel('应用指标项目').selectOption('beta');
  await page.getByLabel('应用指标环境').selectOption('beta-qa');
  const [request] = await Promise.all([
    page.waitForRequest(req => new URL(req.url()).pathname === '/api/v1/observability/logs'),
    page.getByText('查看应用访问日志', {exact:false}).click()
  ]);
  const params = new URL(request.url()).searchParams;
  assert.deepEqual([params.get('projectId'), params.get('environmentId'), params.get('applicationId')],
    ['beta', 'beta-qa', app]);
});

const ordersApi = 'aaaaaaaa-0000-4000-8000-000000000001';
const billingApi = 'bbbbbbbb-0000-4000-8000-000000000002';
const destination = 'dddddddd-0000-4000-8000-000000000003';
const fixedRange = {start:'2026-11-01T00:00:00.000Z', end:'2026-11-01T06:00:00.000Z'};
const detail = '/observability/apis/' + ordersApi;
const overview = '/observability/metrics';

for (const {name, path, query, group, rowId, expectedApi} of [
  {name:'application on API detail', path:detail, group:'Application', rowId:app, expectedApi:ordersApi},
  {name:'destination on API detail with a different API in the URL query', path:detail, query:{api:billingApi},
    group:'Destination', rowId:destination, expectedApi:ordersApi},
  {name:'destination on the API-filtered overview', path:overview, query:{api:ordersApi, all:'true'},
    group:'Destination', rowId:destination, expectedApi:ordersApi},
  {name:'application on the unfiltered overview', path:overview, group:'Application', rowId:app, expectedApi:null},
  {name:'API on the unfiltered overview', path:overview, group:'Api', rowId:billingApi, expectedApi:billingApi}
]) {
  test('ranking row opens the matching access logs for ' + name, async t => {
    const page = await metricsPage(t, 'alpha', 'alpha-qa');
    const envelope = data => ({sourceState:'Available', range:fixedRange, observedAt:fixedRange.end, sampling:null,
      coverage:{complete:true, missingNodes:[], reason:null, truncated:false}, data});
    await page.route(url => url.pathname.startsWith('/api/v1/observability/') && url.pathname.endsWith('/metrics'),
      route => route.fulfill({json:envelope({kpis:[], trends:{}, nodeHealth:[], page:2, pageSize:50, totalGroups:51,
        groups:[{key:rowId, name:'所选资源', values:[]}]})}));

    const logs = [ordersApi, billingApi].map(apiId => ({
      id:apiId, time:fixedRange.end, environmentId:'alpha-qa', apiId, applicationKey:app, destinationId:destination,
      method:'GET', pathTemplate:apiId===ordersApi?'/orders':'/billing', status:200, durationMs:5, outcome:'Success',
      requestId:apiId, traceId:null, maskedIp:'', nodeName:'qa', configVersion:1, deploymentSequence:1, policyDecisions:[]
    }));
    await page.route('**/api/v1/observability/logs?*', route => {
      const params = new URL(route.request().url()).searchParams;
      const items = logs.filter(row => !params.has('apiId') || row.apiId===params.get('apiId'));
      return route.fulfill({json:envelope({items, nextCursor:null, truncated:false})});
    });
    const search = new URLSearchParams({group, range:'6h', end:fixedRange.end, page:'2', cursor:'previous-page', ...query});
    await page.goto('http://127.0.0.1' + path + '?' + search);
    const link = page.getByRole('row').filter({hasText:'所选资源'}).getByRole('link', {name:'访问日志', exact:true});
    const [request] = await Promise.all([
      page.waitForRequest(req => new URL(req.url()).pathname === '/api/v1/observability/logs'), link.click()
    ]);
    const params = new URL(request.url()).searchParams;
    assert.equal(params.get('apiId'), expectedApi);
    assert.equal(params.get('applicationId'), group==='Application'?app:null);
    assert.equal(params.get('destinationId'), group==='Destination'?destination:null);
    assert.equal(params.get('organizationId'), organizationId);
    assert.equal(params.get('projectId'), 'alpha');
    assert.equal(params.get('environmentId'), query?.all?null:'alpha-qa');
    assert.equal(params.get('allAccessibleEnvironments'), query?.all||'false');
    assert.deepEqual([params.get('start'), params.get('end')], [fixedRange.start, fixedRange.end]);
    assert.equal(params.get('cursor'), null);
    const location = new URL(page.url());
    assert.equal(location.searchParams.get('page'), '1');
    assert.equal(location.searchParams.get('api'), expectedApi);
    const rows = page.locator('.table-wrap tbody tr');
    await rows.first().waitFor();
    assert.deepEqual(await rows.locator('td:nth-child(3)').evaluateAll(cells => cells.map(cell => cell.title)),
      expectedApi ? [expectedApi] : [ordersApi, billingApi]);
  });
}

test('project-wide node health maps equal node names to their own environment and health', async t => {
  const page = await metricsPage(t, 'alpha', 'alpha-dev');
  const range = {start:'2026-11-01T01:00:00Z', end:'2026-11-01T02:00:00Z'};
  const missing = {nodeName:'node-0', environmentId:'alpha-qa'};
  const healthy = {nodeName:'node-0', environmentId:'alpha-dev'};
  await page.route('**/api/v1/observability/metrics?*', route => route.fulfill({json:{
    sourceState:'Partial', range, observedAt:range.end, sampling:null,
    coverage:{complete:false, missingNodes:['node-0'], missingNodeIdentities:[missing], reason:'missing_or_stale_nodes', truncated:false},
    data:{kpis:[], trends:{}, groups:[], totalGroups:0, page:1, pageSize:50, nodeHealth:[
      {...missing, health:'Stale', destinations:[]},
      {...healthy, health:'Available', destinations:[{clusterId:'cluster', destinationId:'dest', health:'Healthy'}]}
    ]}
  }}));
  await page.goto('http://127.0.0.1/observability/metrics?all=true&end=' + range.end);
  const rows = page.locator('.ob-nodes tbody tr');
  await rows.first().waitFor();
  assert.equal(await rows.count(), 2);
  assert.deepEqual(await rows.filter({hasText:'alpha dev'}).getByRole('cell').allTextContents(),
    ['node-0', 'alpha dev', '新鲜', '1', '0', '0']);
  assert.deepEqual(await rows.filter({hasText:'alpha qa'}).getByRole('cell').allTextContents(),
    ['node-0', 'alpha qa', '缺失 / 过期', '0', '0', '0']);
  const notice = await page.locator('.ob-source').textContent();
  assert.match(notice, /node-0.*alpha qa/);
  assert.doesNotMatch(notice, /alpha dev/);
});
