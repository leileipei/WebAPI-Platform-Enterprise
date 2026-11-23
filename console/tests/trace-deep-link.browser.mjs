import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const origin = 'http://127.0.0.1';
const dist = process.env.WEBAPI_CONSOLE_DIST;
const first = '11111111111111112222222222222222';
const second = '33333333333333334444444444444444';
const listPath = '/api/v1/observability/traces';
const end = '2026-11-01T02:00:00.000Z';
const log = {id:'log-id', time:end, environmentId:'dev', apiId:null, applicationKey:'orders', method:'GET',
  pathTemplate:'/orders', status:200, durationMs:5, outcome:'Success', requestId:'request-id', traceId:first,
  maskedIp:'', nodeName:'node', configVersion:1, deploymentSequence:1, destinationId:null, policyDecisions:[]};
const button = (page, name) => page.getByRole('button', {name, exact:true});
const heading = (page, trace) => page.getByRole('heading', {name:trace, exact:true});
let browser;
before(async () => { browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE}); });
after(async () => { await browser?.close(); });

async function fixture(t, {available = true, grant = {organizationId:'org'}} = {}) {
  const page = await browser.newPage();
  const state = {available, lists:[], details:[]};
  const errors = [];
  t.after(async () => { await page.close(); assert.deepEqual(errors, []); });
  page.on('pageerror', error => errors.push(error.message));
  page.setDefaultTimeout(6000);
  await page.clock.install();
  await page.route('**/*', route => {
    const {pathname, searchParams} = new URL(route.request().url());
    const reply = json => route.fulfill({json});
    if (pathname.startsWith('/assets/')) return route.fulfill({path:join(dist, pathname.slice(1))});
    if (!pathname.startsWith('/api/v1/')) return route.fulfill({path:join(dist, 'index.html')});
    if (pathname === '/api/v1/auth/me') return reply({id:'reader', username:'reader', displayName:'测试用户',
      permissions:['log.read','trace.read'], scopes:[{scope:grant, accessMode:'read_only'}]});
    if (pathname === '/api/v1/scope-tree') return reply({organizations:[{id:'org', name:'组织'}],
      projects:[{id:'proj', organizationId:'org', name:'项目'}],
      environments:['dev', 'qa'].map(id => ({id, projectId:'proj', name:id}))});
    const range = {start:searchParams.get('start'), end:searchParams.get('end')};
    const envelope = (data, sourceState = 'Available') => ({data, sourceState, range, observedAt:range.end,
      coverage:{complete:true, missingNodes:[], reason:null, truncated:false}, sampling:null});
    if (pathname === '/api/v1/observability/logs') return reply(envelope({items:[log], nextCursor:null, truncated:false}));
    if (pathname === listPath) {
      state.lists.push(searchParams);
      const traceId = searchParams.get('traceId') || (searchParams.has('cursor') ? second : first);
      const items = state.available ? [{traceId, start:range.start, durationMs:state.lists.length, outcome:'Success'}] : [];
      const nextCursor = !searchParams.has('traceId') && !searchParams.has('cursor') ? 'next-page' : null;
      return reply(envelope({items, nextCursor, truncated:false}, items.length ? 'Available' : 'NoData'));
    }
    if (pathname.startsWith(listPath + '/')) {
      const traceId = pathname.split('/').at(-1);
      state.details.push(traceId);
      if (!state.available) return route.fulfill({status:404, json:{code:'observation_trace_not_found', detail:'当前范围未找到链路'}});
      const span = {spanId:'0000000000000001', parentSpanId:null, name:'GET orders', start:range.start,
        durationMs:5, kind:'Server', status:'Ok', tags:{}, policyDecisions:[]};
      return reply(envelope({traceId, partialTrace:false, spans:[span]}));
    }
    throw Error('Unexpected trace fixture request ' + pathname);
  });
  await page.goto(origin + '/observability/logs?range=1h&end=' + end);
  await page.getByRole('link', {name:first.slice(0, 12) + '…', exact:true}).click();
  return {page, state};
}

async function closeFirst(page) {
  await heading(page, first).waitFor();
  await button(page, '关闭链路').click();
  await heading(page, first).waitFor({state:'hidden'});
}

async function assertRefreshedWhileClosed(page, state, read) {
  await page.getByRole('cell', {name:read + '.00 ms', exact:true}).waitFor();
  await page.waitForLoadState('networkidle');
  assert.deepEqual({
    traceFilter:state.lists[read - 1].get('traceId'),
    listReads:state.lists.length, detailReads:state.details, visibleDetails:await page.locator('.trace-detail-head h3').allTextContents()
  }, {traceFilter:first, listReads:read, detailReads:[first], visibleDetails:[]});
}

test('closing a trace opened from a log survives polling and the row can reopen it', async t => {
  const {page, state} = await fixture(t);
  await closeFirst(page);
  await page.clock.fastForward(30_000);
  await assertRefreshedWhileClosed(page, state, 2);
  await button(page, '查看链路').click();
  await heading(page, first).waitFor();
  assert.deepEqual(state.details, [first, first]);
});

test('a dismissed trace stays closed on query, window and data range refreshes', async t => {
  const {page, state} = await fixture(t);
  await closeFirst(page);
  await button(page, '查询链路').click();
  await assertRefreshedWhileClosed(page, state, 2);
  await button(page, '刷新窗口').click();
  await assertRefreshedWhileClosed(page, state, 3);
  assert.notEqual(state.lists[2].get('end'), end);
  await button(page, '近 6 小时').click();
  await assertRefreshedWhileClosed(page, state, 4);
  assert.equal(new URL(page.url()).searchParams.get('range'), '6h');
  await page.getByLabel('查询数据范围').selectOption('all');
  await assertRefreshedWhileClosed(page, state, 5);
  assert.equal(state.lists[4].get('allAccessibleEnvironments'), 'true');
});

test('closing while a list refresh is in flight also applies when its response arrives', async t => {
  const {page, state} = await fixture(t);
  await heading(page, first).waitFor();
  const held = Promise.withResolvers();
  await page.route(url => url.pathname === listPath, route => held.resolve(route), {times:1});
  await page.clock.fastForward(30_000);
  await page.getByText('正在读取链路…', {exact:true}).waitFor();
  await closeFirst(page);
  await (await held.promise).fallback();
  await assertRefreshedWhileClosed(page, state, 2);
});

test('a log deep link still opens when its first trace lookup has no samples', async t => {
  const {page, state} = await fixture(t, {available:false});
  await page.getByText('NoTrace · 未找到当前范围的链路', {exact:true}).waitFor();
  state.available = true;
  await page.clock.fastForward(30_000);
  await heading(page, first).waitFor();
  assert.deepEqual(state.details, [first, first]);
});

test('switching trace filters and navigating Back and Forward honors the current trace despite a late detail', async t => {
  const {page} = await fixture(t);
  await closeFirst(page);
  const held = Promise.withResolvers();
  const secondDetail = url => url.pathname === listPath + '/' + second;
  await page.route(secondDetail, route => held.resolve(route), {times:1});
  await page.getByLabel('筛选 TraceId').fill(second);
  await Promise.all([page.waitForRequest(req => secondDetail(new URL(req.url()))), button(page, '查询链路').click()]);
  const oldRead = await held.promise;
  await page.goBack();
  await heading(page, first).waitFor();
  await oldRead.fallback();
  await page.waitForLoadState('networkidle');
  assert.deepEqual(await page.locator('.trace-detail-head h3').allTextContents(), [first]);
  await page.goForward();
  await heading(page, second).waitFor();
  assert.equal(new URL(page.url()).searchParams.get('trace'), second);
});

test('after clearing the trace filter a cursor page only opens a manually selected row', async t => {
  const {page, state} = await fixture(t);
  await closeFirst(page);
  await button(page, '重置筛选').click();
  await button(page, '下一页').click();
  await button(page, second).waitFor();
  assert.equal(state.lists.at(-1).get('cursor'), 'next-page');
  assert.deepEqual(state.details, [first]);
  await button(page, second).click();
  await heading(page, second).waitFor();
  await button(page, '上一页').click();
  await button(page, first).waitFor();
  assert.deepEqual(await page.locator('.trace-detail-head h3').allTextContents(), []);
});

test('a late detail from the old workspace is discarded after switching to a denied scope', async t => {
  const {page} = await fixture(t, {grant:{organizationId:'org', projectId:'proj', environmentId:'dev'}});
  await heading(page, first).waitFor();
  const held = Promise.withResolvers();
  const firstDetail = url => url.pathname === listPath + '/' + first;
  await page.route(firstDetail, route => held.resolve(route), {times:1});
  await Promise.all([page.waitForRequest(req => firstDetail(new URL(req.url()))), button(page, '查看链路').click()]);
  await page.getByLabel('当前环境').selectOption('qa');
  await page.getByRole('alert').filter({hasText:'当前范围没有 trace.read 权限'}).waitFor();
  await (await held.promise).fallback();
  await page.waitForLoadState('networkidle');
  assert.deepEqual(await page.locator('.trace-detail-head h3').allTextContents(), []);
});
