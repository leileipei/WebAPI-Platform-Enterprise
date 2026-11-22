import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const project = {id:'orders', organizationId:'company', name:'订单项目'};
const environment = {id:'orders-qa', projectId:project.id, name:'订单 QA'};
const apiId = '00000000-0000-4000-8000-000000000001';
const metricsPath = '/observability/metrics';
const range = {start:'2026-11-20T03:00:00Z', end:'2026-11-20T04:00:00Z'};
const policyKeys = ['circuit_rejected_count', 'rate_limit_store_unavailable_count'];
const projectScope = {organizationId:project.organizationId, projectId:project.id};
const configPermissions = ['policy.read','gateway.read','gateway.config.read'];
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

function metrics(values, kpiState='Available', sourceState='Available') {
  return {sourceState, range, observedAt:range.end, sampling:null,
    coverage:{complete:!['Partial','Stale','Unavailable'].includes(sourceState), missingNodes:[], truncated:false},
    data:{kpis:policyKeys.map((metric,i)=>({metric,value:values[i],unit:'requests',sampleCount:kpiState==='NoData'?0:12,state:kpiState})),
      trends:{}, groups:[], totalGroups:0, page:1, pageSize:50, nodeHealth:[]}};
}

async function fixture(t, {data, status=200, permissions=[], scope=projectScope, entry=metricsPath, query=''} = {}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(3500);
  await page.addInitScript(({project,environment}) => {
    localStorage.setItem('core-organization',project.organizationId);
    localStorage.setItem('core-project',project.id);
    localStorage.setItem('core-environment',environment.id);
  },{project,environment});
  const requests = [];
  const pageOf = items => ({items,total:items.length,page:1,pageSize:50});
  await page.route('**/*', route => {
    const url = new URL(route.request().url());
    if (url.pathname.startsWith('/assets/')) return route.fulfill({path:join(dist,url.pathname.slice(1))});
    if (!url.pathname.startsWith('/api/v1/')) return route.fulfill({path:join(dist,'index.html')});
    const path = url.pathname.slice('/api/v1'.length);
    requests.push({path,params:url.searchParams});
    const reply = (json,status=200) => route.fulfill({json,status});
    if (path==='/auth/me') return reply({id:'reader',username:'reader',displayName:'值班人员',
      permissions:['metrics.read',...permissions],scopes:[{scope,accessMode:'read_only'}]});
    if (path==='/scope-tree') return reply({organizations:[{id:'company',name:'公司'}],
      projects:[{id:'inventory',organizationId:'company',name:'库存项目'},project],
      environments:[{id:'inventory-qa',projectId:'inventory',name:'库存 QA'},{id:'orders-dev',projectId:'orders',name:'订单 DEV'},environment]});
    if (path===metricsPath || path==='/observability/apis/'+apiId+'/metrics')
      return reply(status===200?data:{detail:status===403?'当前范围无权读取指标':'采集源不可用'},status);
    if (path==='/projects/orders/policies') return reply(pageOf([{id:'breaker',organizationId:'company',projectId:'orders',
      name:'订单熔断',type:'circuit_breaker',enabled:true,versionNo:5,config:'{}'}]));
    if (path==='/policies/breaker/references') return reply(pageOf([{referenceKind:'Runtime',
      environmentId:environment.id,configVersion:17,sourceRevision:4,name:'配置 v17'}]));
    if (path==='/environments/orders-qa/gateway-nodes') return reply(pageOf([{id:'gateway',nodeName:'qa-node',
      currentConfigVersion:17,currentDeploymentSequence:21,desiredConfigVersion:18,desiredDeploymentSequence:22}]));
    if (path==='/environments/orders-qa/snapshots') return reply(pageOf([{configVersion:17,latestDeploymentSequence:21}]));
    return reply({detail:'未提供此资源'},404);
  });
  await page.goto('http://127.0.0.1'+entry+'?end='+range.end+query);
  // Both old and new pages render this notice only after the metrics request completes.
  if (status===200) await page.getByText(/采集观察/).waitFor();
  else await page.locator('.ob-source[role="alert"]').waitFor();
  return {page,requests,section:page.getByRole('region',{name:'策略观测'})};
}

function configRequests(requests) {
  return requests.filter(({path})=>/\/(policies|gateway-nodes|snapshots)(\/|$)/.test(path));
}

async function expectMetrics(section, values, state) {
  for (const [index,label] of ['熔断拒绝请求','限流存储故障请求'].entries()) {
    const row = section.getByRole('row').filter({hasText:label});
    assert.deepEqual(await row.getByRole('cell').allTextContents(),[label,values[index],state]);
  }
}

test('monitor-only users see policy events from the selected environment without a configuration claim or read', async t => {
  const {page,requests,section} = await fixture(t,{data:metrics([3,7])});
  assert.doesNotMatch(await page.locator('main').innerText(),/熔断\s*\/\s*限流尚未启用/);
  await expectMetrics(section,['3','7'],'采集正常');
  assert.match(await section.innerText(),/拒绝.*旁路放行/);
  assert.equal(await section.getByRole('link').count(),0);
  const query = requests.find(r=>r.path===metricsPath).params;
  assert.deepEqual([query.get('projectId'),query.get('environmentId')],[project.id,environment.id]);
  assert.deepEqual(configRequests(requests),[]);
});

test('a measured zero coexists with an enabled working policy and an older applied gateway configuration', async t => {
  const {page,requests,section} = await fixture(t,{data:metrics([0,0]),permissions:configPermissions});
  await expectMetrics(section,['0','0'],'采集正常');
  assert.deepEqual(configRequests(requests),[]);
  await section.getByRole('link',{name:'查看项目策略工作配置'}).click();
  const policy = page.getByRole('row').filter({hasText:'订单熔断'});
  await policy.getByText('v17 / r4').waitFor();
  assert.equal(await policy.getByRole('cell',{name:'启用',exact:true}).count(),1);
  assert.match(await policy.innerText(),/r5/);
  await page.goBack();
  await section.getByRole('link',{name:'查看当前环境节点版本'}).click();
  const node = page.getByRole('row').filter({hasText:'qa-node'});
  await node.getByText('v17 / seq 21').waitFor();
  assert.equal(await node.getByText('v18 / seq 22').count(),1);
  await page.goBack();
  await section.getByRole('link',{name:'查看当前环境配置快照'}).click();
  await page.getByRole('button',{name:'查看管理视图'}).waitFor();
  assert.deepEqual(new Set(configRequests(requests).map(r=>r.path)),new Set([
    '/projects/orders/policies','/policies/breaker/references',
    '/environments/orders-qa/gateway-nodes','/environments/orders-qa/snapshots'
  ]));
});

for (const [description, kpiState, sourceState, label] of [
  ['complete traffic coverage with no policy series','NoData','Available','暂无策略样本'],
  ['partial collection with observed samples','Partial','Partial','数据覆盖不完整'],
  ['stale collection','Stale','Stale','采集已过期']
]) {
  test(description+' keeps policy counts unknown', async t => {
    const {section} = await fixture(t,{data:metrics([null,null],kpiState,sourceState)});
    await expectMetrics(section,['—','—'],label);
  });
}

test('an unavailable metrics service leaves both event counts unknown', async t => {
  const {section,requests} = await fixture(t,{status:503});
  await expectMetrics(section,['—','—'],'采集源不可用');
  assert.deepEqual(configRequests(requests),[]);
});

test('a denied metrics scope clears both values and labels the permission loss', async t => {
  const {page,section,requests} = await fixture(t,{status:403});
  await expectMetrics(section,['—','—'],'范围或权限不可用');
  assert.match(await page.locator('.ob-source[role="alert"]').innerText(),/权限已失效/);
  assert.deepEqual(configRequests(requests),[]);
});

test('API monitoring reports an unsupported dimension without substituting environment-wide counts', async t => {
  const {section,requests} = await fixture(t,{entry:'/observability/apis/'+apiId,data:metrics([null,null],'NotApplicable')});
  await expectMetrics(section,['—','—'],'该维度暂不支持');
  const metricReads = requests.filter(r=>r.path.endsWith('/metrics'));
  assert.ok(metricReads.length);
  assert.ok(metricReads.every(r=>r.path==='/observability/apis/'+apiId+'/metrics'&&r.params.get('apiId')===apiId));
});

test('an environment grant and an all-environments query keep configuration links within their own scope', async t => {
  const {page,requests,section} = await fixture(t,{data:metrics([3,7]),permissions:configPermissions,
    scope:{...projectScope,environmentId:environment.id}});
  assert.equal(await section.getByRole('link',{name:'查看项目策略工作配置'}).count(),0);
  assert.equal(await section.getByRole('link',{name:'查看当前环境节点版本'}).count(),1);
  assert.equal(await section.getByRole('link',{name:'查看当前环境配置快照'}).count(),1);
  await page.getByLabel('监控数据范围').selectOption('all');
  await page.getByText(/采集观察/).waitFor();
  assert.equal(await section.getByRole('link').count(),0);
  const aggregate = requests.find(r=>r.params.get('allAccessibleEnvironments')==='true');
  assert.equal(aggregate.params.get('projectId'),project.id);
  assert.equal(aggregate.params.get('environmentId'),null);
  assert.deepEqual(configRequests(requests),[]);
});

for (const [permission, target] of [['policy.read','/policies'],['gateway.read','/nodes'],['gateway.config.read','/snapshots']]) {
  test(permission+' exposes only its matching read-only monitoring shortcut', async t => {
    const {section,requests} = await fixture(t,{data:metrics([0,0]),permissions:[permission]});
    const links = section.getByRole('link');
    assert.equal(await links.count(),1);
    assert.equal(await links.getAttribute('href'),target);
    assert.deepEqual(configRequests(requests),[]);
  });
}

test('a project-wide aggregate offers project configuration without linking a single environment', async t => {
  const {section} = await fixture(t,{data:metrics([3,7]),permissions:configPermissions,query:'&all=true'});
  const links = section.getByRole('link');
  assert.equal(await links.count(),1);
  assert.equal(await links.getAttribute('href'),'/policies');
});
