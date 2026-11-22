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

async function openMetrics(t, projectId, environmentId) {
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
