// Browser acceptance against the fixed, locally owned test host. HTTP uses the actual control plane.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash, randomUUID} from 'node:crypto';
import {createRequire} from 'node:module';
import {assertFixtureBinding, assertServedIdentity, claimEvidenceDirectory, publicFailure} from './project-counts-guards.mjs';

async function main() {
  const [baseArgument, directoryArgument, outputArgument] = process.argv.slice(2);
  const base = new URL(baseArgument);
  const directory = path.resolve(directoryArgument);
  const output = path.resolve(outputArgument);
  const read = async name => JSON.parse(await fs.readFile(path.join(directory, name), 'utf8'));
  const fixtureIdentity = await read('fixture.json');
  const fixture = await read('context.json');
  const credentials = await read('browser-private.json');
  assertFixtureBinding(base.href, directory, fixtureIdentity, credentials, fixture);
  assert.equal(fixtureIdentity.scenario, 'permission-matrix');
  assert.equal(fixture.scenario, fixtureIdentity.scenario);
  assert.equal(credentials.scenario, fixtureIdentity.scenario);
  const identityResponse = await fetch(new URL('/acceptance-identity.json', base), {redirect:'error'});
  assert.equal(identityResponse.status, 200);
  const servedIdentity = await identityResponse.json();
  assertServedIdentity(fixtureIdentity, servedIdentity);
  assert.equal(servedIdentity.scenario, fixtureIdentity.scenario);
  for (const [file, digest] of Object.entries(fixtureIdentity.staticFiles)) {
    const url = new URL('/' + file, base);
    assert.equal(url.origin, base.origin);
    const response = await fetch(url, {redirect:'error'});
    assert.equal(response.status, 200);
    assert.equal(createHash('sha256').update(Buffer.from(await response.arrayBuffer())).digest('hex'), digest);
  }

  await claimEvidenceDirectory(output);
  assert.ok(process.env.WEBAPI_PLAYWRIGHT_PACKAGE && process.env.WEBAPI_CHROMIUM_EXECUTABLE);
  const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
  const browser = await chromium.launch({headless:true, executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});
  const results = {
    productRevision:fixture.productRevision, owner:fixture.owner, classification:fixture.classification,
    beforeLoginIdentity:{sourceManifestSha256:fixtureIdentity.sourceManifestSha256, staticCount:Object.keys(fixtureIdentity.staticFiles).length},
    startedAt:new Date().toISOString(), cases:[], passed:false
  };
  const resultFile = path.join(output, 'results.json');
  let phase = 'initialization';
  let scenario = 'none';

  async function persist() {
    const temporary = resultFile + '.' + randomUUID() + '.tmp';
    await fs.writeFile(temporary, JSON.stringify(results, null, 2) + '\n', {flag:'wx', mode:0o600});
    await fs.rename(temporary, resultFile);
  }

  async function checkpoint(page, id, observed) {
    const file = id + '.png';
    const target = path.join(output, file);
    await page.screenshot({path:target, animations:'disabled'});
    results.cases.push({id, viewport:page.viewportSize(), ...observed, screenshot:file,
      screenshotSha256:createHash('sha256').update(await fs.readFile(target)).digest('hex'),
      visuallyInspected:false, observedAt:new Date().toISOString(), passed:true});
    await persist();
  }

  const matrixPath = applicationId => `/api/v1/applications/${applicationId}/permission-matrix`;
  const table = page => page.getByRole('table', {name:'API 授权矩阵', exact:true});
  const button = (page, name) => page.getByRole('button', {name, exact:true});
  const check = (page, name) => page.getByRole('checkbox', {name, exact:true});
  const choice = (page, api, env) => check(page, `选择 ${api} · ${env}`);
  const risk = page => check(page, '我确认本次变更涉及生产环境');
  const save = page => button(page, '提交工作配置');

  async function eventually(readCondition) {
    const expires = Date.now() + 12000;
    while (true) {
      try { return await readCondition(); }
      catch (error) {
        if (Date.now() >= expires) throw error;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
    }
  }

  async function jsonGet(session, pathname) {
    const response = await session.request.get(new URL(pathname, base).href);
    assert.equal(response.status(), 200);
    return response.json();
  }

  async function readMatrix(session, applicationId = fixture.applicationId, projectId = fixture.projectId) {
    const q = new URLSearchParams({projectId, pageSize:'100'});
    return jsonGet(session, matrixPath(applicationId) + '?' + q);
  }

  function grant(matrix, apiId, environmentId) {
    const row = matrix.items.find(row => row.id === apiId);
    assert.ok(row, 'Expected API on the fixture readback page.');
    return row.grants.find(grant => grant.environmentId === environmentId);
  }

  async function open(page, applicationId) {
    await page.goto(new URL(`/applications/${applicationId}`, base).href);
    await button(page, 'API 授权').click();
    await page.getByRole('heading', {name:'API 授权矩阵', exact:true}).waitFor();
  }

  async function withAccount(name, applicationId, action) {
    scenario = name;
    phase = 'login';
    const session = await browser.newContext({viewport:{width:1440,height:1000}, timezoneId:'Asia/Shanghai', locale:'zh-CN'});
    await session.route('**/*', route => new URL(route.request().url()).origin === base.origin ? route.continue() : route.abort('blockedbyclient'));
    const page = await session.newPage();
    page.setDefaultTimeout(14000);
    const errors = [];
    page.on('pageerror', () => errors.push('pageerror'));
    const account = credentials.accounts.find(account => account.name === name);
    assert.ok(account);
    try {
      await page.goto(new URL(`/applications/${applicationId}`, base).href);
      assert.equal(new URL(page.url()).origin, base.origin);
      await page.getByRole('textbox', {name:'用户名', exact:true}).fill(account.username);
      await page.getByLabel('密码', {exact:true}).fill(account.password);
      const login = page.waitForResponse(r => new URL(r.url()).pathname === '/api/v1/auth/login' && r.request().method() === 'POST');
      await button(page, '登录').click();
      assert.equal((await login).status(), 200);
      phase = 'open-matrix';
      await open(page, applicationId);
      await action(page, session);
      phase = 'logout';
      await button(page, '退出登录').click();
      await eventually(async () => assert.equal((await session.request.get(new URL('/api/v1/auth/me', base).href)).status(), 401));
      assert.deepEqual(errors, []);
    } finally {
      await session.close();
    }
  }

  async function command(page, action, clicked, expectedStatus = 200) {
    const actual = page.waitForResponse(r => new URL(r.url()).pathname === matrixPath(fixture.applicationId) + '/' + action &&
      r.request().method() === 'POST');
    await clicked.click();
    const response = await actual;
    assert.equal(response.status(), expectedStatus);
    return response.json();
  }

  async function preview(page) {
    const response = await command(page, 'preview', button(page, '预览影响'));
    await page.getByRole('heading', {name:'授权影响预览', exact:true}).waitFor();
    assert.equal(typeof response.previewToken, 'string');
    return response;
  }

  async function commit(page, count) {
    const receipt = await command(page, 'commit', save(page));
    assert.equal(receipt.appliedCount, count);
    assert.equal(receipt.activation, 'WorkingConfiguration');
    await page.getByRole('status').filter({hasText:`已提交 ${count} 项授权变更至工作配置。`}).waitFor();
    return receipt.appliedCount;
  }

  async function revokeManager() {
    const id = randomUUID();
    const pending = path.join(directory, 'commands', id + '.tmp');
    await fs.writeFile(pending, JSON.stringify({owner:fixture.owner, action:'revoke-matrix-management'}), {flag:'wx', mode:0o600});
    await fs.rename(pending, path.join(directory, 'commands', id + '.json'));
    return eventually(async () => {
      const record = await read('done-' + id + '.json');
      assert.equal(record.owner, fixture.owner);
      assert.equal(record.commandId, id);
      assert.equal(record.removed, 1);
      assert.equal(record.userId, fixture.accounts.find(a => a.name === 'revoked').userId);
      return record.removed;
    });
  }

  try {
    await withAccount('manager', fixture.applicationId, async (page, session) => {
      phase = 'matrix-scope-and-windows';
      const initial = await readMatrix(session);
      assert.equal(initial.total, 55);
      assert.deepEqual(initial.environments.map(env => env.id), [fixture.environmentId, fixture.prodId]);
      assert.equal(grant(initial, fixture.timeWindowApiId, fixture.environmentId).state, 'Expired');
      assert.equal(grant(initial, fixture.timeWindowApiId, fixture.prodId).state, 'Pending');
      await table(page).getByRole('columnheader', {name:/测试环境/}).waitFor();
      await table(page).getByRole('columnheader', {name:/生产环境/}).waitFor();
      assert.equal(await table(page).getByRole('columnheader', {name:/停用环境|联调环境/}).count(), 0);
      const history = table(page).getByRole('row').filter({hasText:'历史授权'});
      await history.getByText('已过期', {exact:true}).waitFor();
      await history.getByText('待生效', {exact:true}).waitFor();
      await checkpoint(page, 'B02-matrix-and-window-states', {apiCount:initial.total, environments:2, windowStates:['Expired','Pending']});

      const environmentPath = id => `/api/v1/environments/${id}`;
      const environmentsBefore = await Promise.all([fixture.environmentId, fixture.prodId].map(id => jsonGet(session, environmentPath(id))));
      phase = 'cross-page-selection';
      await choice(page, '订单', '测试环境').check();
      await button(page, '下一页').click();
      await choice(page, '末页 API', '测试环境').check();
      await page.getByText('已选 2 项 / 最多 20 项', {exact:true}).waitFor();
      await checkpoint(page, 'B02-cross-page-selection', {selected:2, page:2});
      await button(page, '批量授权').click();
      await page.getByRole('combobox', {name:'授权模式 订单 · 测试环境', exact:true}).selectOption('LegacyApi');
      await page.getByRole('combobox', {name:'授权模式 末页 API · 测试环境', exact:true}).selectOption('LegacyApi');
      phase = 'batch-grant-preview';
      const batch = await preview(page);
      assert.equal(batch.requiresProductionConfirmation, false);
      assert.deepEqual(new Set(batch.items.map(i => i.apiId)), new Set([fixture.apiId, fixture.lastApiId]));
      assert.ok(batch.items.every(i => i.before === null && i.after?.mode === 'LegacyApi' && i.running.status === 'Unconfirmed'));
      await checkpoint(page, 'B02-batch-grant-preview', {changeCount:batch.items.length, running:'Unconfirmed'});
      phase = 'batch-grant-readback';
      assert.equal(await commit(page, 2), 2);
      let readback = await readMatrix(session);
      assert.equal(grant(readback, fixture.apiId, fixture.environmentId).mode, 'LegacyApi');
      assert.equal(grant(readback, fixture.lastApiId, fixture.environmentId).mode, 'LegacyApi');
      await checkpoint(page, 'B02-batch-grant-readback', {changeCount:2, actualModes:['LegacyApi','LegacyApi']});

      phase = 'production-confirmation';
      if (await button(page, '上一页').isEnabled()) await button(page, '上一页').click();
      await button(page, '新增授权 订单 · 生产环境').click();
      const modes = page.getByRole('combobox', {name:'授权模式 订单 · 生产环境', exact:true});
      if (await modes.isEnabled()) await modes.selectOption('RouteMethod');
      await check(page, 'POST /orders-production').check();
      const prod = await preview(page);
      assert.equal(prod.requiresProductionConfirmation, true);
      assert.equal(prod.items[0].environmentId, fixture.prodId);
      assert.deepEqual(prod.items[0].after.methods[0].methods, ['POST']);
      assert.equal(await save(page).isDisabled(), true);
      await page.setViewportSize({width:1280,height:1000});
      await checkpoint(page, 'B02-production-confirmation', {requiresProductionConfirmation:true, buttonDisabled:true, method:'POST'});
      await risk(page).focus();
      await page.keyboard.press('Space');
      assert.equal(await risk(page).isChecked(), true);
      await commit(page, 1);
      await page.setViewportSize({width:1440,height:1000});
      readback = await readMatrix(session);
      const productionGrant = grant(readback, fixture.apiId, fixture.prodId);
      assert.equal(productionGrant.mode, 'RouteMethod');
      assert.equal(productionGrant.routeMethods[0].routeId, fixture.productionRouteId);
      assert.deepEqual(productionGrant.routeMethods[0].methods, ['POST']);

      phase = 'batch-window';
      await choice(page, '订单', '测试环境').check();
      await choice(page, '订单', '生产环境').check();
      await button(page, '修改有效期').click();
      await check(page, '修改失效时间').check();
      await page.getByLabel('授权失效时间', {exact:true}).fill('2028-04-18T14:25:37');
      const window = await preview(page);
      const expiry = Date.parse('2028-04-18T06:25:37Z');
      assert.equal(window.requiresProductionConfirmation, true);
      assert.equal(window.items.length, 2);
      assert.ok(window.items.every(i => i.before?.mode === i.after?.mode &&
        i.before.validFrom === i.after.validFrom && Date.parse(i.after.expiresAt) === expiry));
      await risk(page).check();
      await checkpoint(page, 'B02-window-preview', {changes:2, expiryUtc:new Date(expiry).toISOString(), modePreserved:true});
      await commit(page, 2);
      readback = await readMatrix(session);
      assert.equal(Date.parse(grant(readback, fixture.apiId, fixture.environmentId).expiresAt), expiry);
      const afterWindow = grant(readback, fixture.apiId, fixture.prodId);
      assert.equal(Date.parse(afterWindow.expiresAt), expiry);
      assert.deepEqual(afterWindow.routeMethods, productionGrant.routeMethods);

      phase = 'batch-revoke';
      await choice(page, '订单', '测试环境').check();
      await choice(page, '订单', '生产环境').check();
      await button(page, '撤销授权').click();
      const revoke = await preview(page);
      assert.equal(revoke.requiresProductionConfirmation, true);
      assert.equal(revoke.items.length, 2);
      assert.ok(revoke.items.every(i => i.before && i.after === null));
      await risk(page).check();
      await checkpoint(page, 'B02-revoke-preview', {changes:2, production:true});
      await commit(page, 2);
      readback = await readMatrix(session);
      assert.equal(grant(readback, fixture.apiId, fixture.environmentId), undefined);
      assert.equal(grant(readback, fixture.apiId, fixture.prodId), undefined);
      await button(page, '新增授权 订单 · 测试环境').waitFor();
      await button(page, '新增授权 订单 · 生产环境').waitFor();
      assert.ok(grant(readback, fixture.lastApiId, fixture.environmentId));
      const environmentsAfter = await Promise.all([fixture.environmentId, fixture.prodId].map(id => jsonGet(session, environmentPath(id))));
      const releaseState = rows => rows.map(row => [row.desiredConfigVersion, row.deploymentSequence]);
      assert.deepEqual(releaseState(environmentsAfter), releaseState(environmentsBefore));
      await checkpoint(page, 'B02-revoke-readback', {revoked:2, unrelatedGrantPreserved:true, deploymentStateUnchanged:true});

      phase = 'move-legacy-authorization';
      const legacy = grant(readback, fixture.lastApiId, fixture.environmentId);
      await choice(page, '订单', '生产环境').check();
      await button(page, '下一页').click();
      await choice(page, '末页 API', '测试环境').check();
      await button(page, '移动旧版授权').click();
      const moved = await preview(page);
      assert.equal(moved.requiresProductionConfirmation, true);
      assert.equal(moved.items.length, 2);
      const removed = moved.items.find(i => i.apiId === fixture.lastApiId && i.environmentId === fixture.environmentId);
      const added = moved.items.find(i => i.apiId === fixture.apiId && i.environmentId === fixture.prodId);
      assert.equal(removed.before.mode, 'LegacyApi');
      assert.equal(removed.after, null);
      assert.equal(added.before, null);
      assert.equal(added.after.validFrom, legacy.validFrom);
      assert.equal(await save(page).isDisabled(), true);
      await page.getByRole('heading', {name:/移出 末页 API · 测试环境/}).waitFor();
      await page.getByRole('heading', {name:/移入 订单 · 生产环境/}).waitFor();
      await checkpoint(page, 'B02-legacy-move-preview', {changes:2, production:true});
      await risk(page).check();
      await commit(page, 1);
      const movedMatrix = await readMatrix(session);
      assert.equal(grant(movedMatrix, fixture.lastApiId, fixture.environmentId), undefined);
      const newCell = grant(movedMatrix, fixture.apiId, fixture.prodId);
      assert.equal(newCell.id, legacy.id);
      assert.equal(newCell.revision, legacy.revision + 1);
      await checkpoint(page, 'B02-legacy-move-readback', {sourceGone:true, sameId:true, advancedRevision:true});

      phase = 'organization-shared-application';
      await open(page, fixture.sharedApplicationId);
      await page.getByRole('combobox', {name:'授权项目', exact:true}).selectOption(fixture.secondProjectId);
      await table(page).getByRole('columnheader', {name:/联调环境/}).waitFor();
      assert.equal(await table(page).getByRole('columnheader', {name:/生产环境|测试环境/}).count(), 0);
      const shared = await readMatrix(session, fixture.sharedApplicationId, fixture.secondProjectId);
      assert.deepEqual(shared.environments.map(e => e.id), [fixture.secondEnvironmentId]);
      assert.deepEqual(shared.items.map(i => i.id), [fixture.secondApiId]);
      const foreign = await session.request.get(new URL(matrixPath(fixture.applicationId) + `?projectId=${fixture.secondProjectId}`, base).href);
      assert.equal(foreign.status(), 404);
      await checkpoint(page, 'B02-shared-project-scope', {environments:1, apis:1, foreignProjectStatus:404});
    });

    await withAccount('viewer', fixture.applicationId, async (page, session) => {
      phase = 'read-only-scope';
      const matrix = await readMatrix(session);
      assert.ok(matrix.environments.every(e => !e.canManage));
      await table(page).getByRole('columnheader', {name:/测试环境/}).waitFor();
      const selection = choice(page, '订单', '测试环境');
      if (await selection.count()) assert.equal(await selection.isDisabled(), true);
      const action = button(page, '新增授权 订单 · 测试环境');
      if (await action.count()) assert.equal(await action.isDisabled(), true);
      await checkpoint(page, 'B02-read-only-scope', {readable:true, mutable:false});
    });

    await withAccount('no-environment', fixture.applicationId, async (page, session) => {
      phase = 'absent-environment-read';
      const matrix = await readMatrix(session);
      assert.equal(matrix.environments.length, 0);
      const basePath = `/api/v1/applications/${fixture.applicationId}`;
      const detail = await jsonGet(session, basePath);
      assert.deepEqual(detail.permissions, []);
      assert.deepEqual(detail.runningConfigVersions, {});
      assert.deepEqual(await jsonGet(session, basePath + '/permissions'), []);
      await eventually(async () => assert.equal(await page.getByRole('columnheader', {name:/生产环境|测试环境/}).count(), 0));
      await checkpoint(page, 'B02-no-environment-read', {visibleEnvironments:0, detailAndPermissionsMasked:true});
    });

    await withAccount('revoked', fixture.applicationId, async (page, session) => {
      phase = 'revoke-management-after-preview';
      await button(page, '新增授权 订单 · 测试环境').click();
      await page.getByRole('combobox', {name:'授权模式 订单 · 测试环境', exact:true}).selectOption('LegacyApi');
      await preview(page);
      assert.equal(await revokeManager(), 1);
      const denial = await command(page, 'commit', save(page), 403);
      assert.ok(['permission_denied','scope_denied'].includes(denial.code));
      await eventually(async () => assert.equal((await jsonGet(session, '/api/v1/auth/me')).permissions.includes('app.permission.manage'), false));
      await eventually(async () => assert.equal(await save(page).count(), 0));
      assert.equal(grant(await readMatrix(session), fixture.apiId, fixture.environmentId), undefined);
      await checkpoint(page, 'B02-revoked-current-session', {status:403, editClosed:true, absentAfter:true});
    });

    results.passed = true;
    results.finishedAt = new Date().toISOString();
    await persist();
    console.log(JSON.stringify({passed:results.cases.length, productRevision:results.productRevision, owner:results.owner}));
  } catch (error) {
    results.failure = publicFailure(error, phase, scenario);
    results.finishedAt = new Date().toISOString();
    await persist();
    console.error(JSON.stringify(results.failure));
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(JSON.stringify(publicFailure(error, 'fixture-binding', 'pre-login')));
  process.exitCode = 1;
});
