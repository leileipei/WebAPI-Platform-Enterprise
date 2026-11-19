// Run against a local Vite preview with a controlled API contract.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';

const base = new URL(process.argv[2]);
if (base.protocol !== 'http:' || base.hostname !== '127.0.0.1' ||
    !process.env.WEBAPI_PLAYWRIGHT_PACKAGE || !process.env.WEBAPI_CHROMIUM_EXECUTABLE)
  throw new Error('An explicit loopback preview and installed Playwright/Chromium paths are required.');
const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const browser = await chromium.launch({headless:true, executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});

const id = n => '10000000-0000-0000-0000-' + String(n).padStart(12,'0');
const organizationId = id(1), projectId = id(2), appId = id(3), environmentId = id(4), otherEnvironmentId = id(5);
const oldId = id(6), replacementId = id(7), rotationId = id(8), operationId = id(9);
const accessKey = 'synthetic-access-old1234';
const replacementAccessKey = 'synthetic-access-new5678';
const usage = {state:'Unknown', coverageStart:null, completeThrough:null, freshness:null, missingNodes:[], gaps:[], reason:'BrowserFixture'};
const key = {id:oldId, applicationId:appId, environmentId, bindingMode:'EnvironmentBound', status:'Active', accessKey,
  validFrom:'2026-11-20T09:00:00.123456+00:00', expiresAt:'2027-01-20T09:00:00.654321+00:00', revision:7};
const replacement = {...key, id:replacementId, accessKey:replacementAccessKey, revision:2};
const keys = [
  key, replacement, {...replacement, id:id(15), environmentId:otherEnvironmentId},
  {...replacement, id:id(16), bindingMode:'LegacyGlobal'}, {...replacement, id:id(17), status:'Revoked'}
];
const targets = [{nodeId:id(11), instanceId:id(12)}, {nodeId:id(13), instanceId:id(14)}];
const hardDeadline = '2026-12-22T10:00:00.000Z';
const schedule = {rotationId, credentialId:oldId, replacementCredentialId:replacementId, environmentId, hardDeadline,
  recordedAt:'2026-11-19T12:00:00Z', status:'Scheduled', revision:1, replayed:false, revocationOperationId:null, abortRevocationOperationId:null};
const appPath = `/applications/${appId}`;
const eligibilityPath = appPath + '/formal-rotation-eligibility';
const resumePath = appPath + '?' + new URLSearchParams({formalRotationOld:oldId, formalRotationId:rotationId});
const rotationPath = `/credential-rotations/${rotationId}`;
const oldFormalPath = `/credentials/${oldId}/formal-rotation`;
const results = [];

try {
  for (const scenario of ['manage-only','read-only','other-environment','cross-organization-aggregate','schedule-and-complete',
    'resume-and-abort','wrong-old-key','deadline-closed','permission-lost-on-decision','eligibility-refresh-denied']) {
    const context = await browser.newContext({viewport:{width:1400,height:1000}});
    try {
      const page = await context.newPage(), errors = [], writes = [];
      let receipt = scenario === 'wrong-old-key' ? {...schedule, credentialId:replacementId} : schedule;
      let verified = false, verificationReads = 0, eligibilityReads = 0, eligible = true;
      let releaseRefresh;
      const allowRefreshToFinish = new Promise(resolve => { releaseRefresh = resolve; });
      page.on('pageerror', error => errors.push(error.message));
      await page.route('**/api/v1/**', async route => {
        const request = route.request(), url = new URL(request.url()), endpoint = url.pathname.slice('/api/v1'.length);
        const reply = (body,status=200) => route.fulfill({status, contentType:'application/json',
          headers:{'Cache-Control':'no-store'}, body:JSON.stringify(body)});
        if (endpoint === '/auth/me') {
          const permissions = ['app.read','credential.manage',...(scenario === 'manage-only' ? [] : ['credential.revoke'])];
          const scopes = scenario === 'manage-only' ? [{scope:{organizationId,projectId},accessMode:'read_write'}] :
            [{scope:{organizationId,projectId},accessMode:'read_only'},
             {scope:{organizationId,projectId,environmentId:scenario === 'other-environment' ? otherEnvironmentId : environmentId},
                accessMode:scenario === 'read-only' ? 'read_only' : 'read_write'}];
          return reply({id:id(10), username:'rotation-review', displayName:'轮换检查', authSource:'local', permissions, scopes});
        }
        if (endpoint === '/auth/csrf') return reply({token:'rotation-fixture-csrf'});
        if (endpoint === '/scope-tree') return reply({
          organizations:[{id:organizationId, name:'测试组织', status:'Active'}],
          projects:[{id:projectId, organizationId, name:'测试项目', status:'Active'}],
          environments:[{id:environmentId, projectId, name:'测试环境', status:'Active'},
            {id:otherEnvironmentId, projectId, name:'其他环境', status:'Active'}]
        });
        if (endpoint === appPath) return reply({application:{id:appId, organizationId, projectId, name:'轮换测试', code:'ROTATION',
          owner:'负责人', status:'Active', revision:1}, credentials:keys, permissions:[], runningConfigVersions:{}});
        if (endpoint === eligibilityPath) {
          assert.equal(request.method(),'GET');
          eligibilityReads++;
          if (scenario === 'eligibility-refresh-denied' && eligibilityReads > 1) {
            await allowRefreshToFinish;
            return reply({code:'scope_denied',detail:'当前授权不可用。'},403);
          }
          return reply({credentialIds:eligible && scenario !== 'cross-organization-aggregate' ? [oldId] : []});
        }
        if (endpoint === appPath + '/credential-usage') return reply({window:{from:hardDeadline,to:hardDeadline},
          visibleEnvironmentIds:[environmentId], page:{items:keys.map(item => ({credentialId:item.id, count:null, coverage:usage,
            lastObservedAt:null, visibleLastUsedAt:null})), total:keys.length, page:1, pageSize:50}, coverage:usage});
        if (endpoint === rotationPath) return reply(receipt);
        if (endpoint === rotationPath + '/verification') {
          verificationReads++;
          if (scenario === 'deadline-closed')
            return reply({rotationId,state:'Closed',configVersion:null,deploymentSequence:null,targets:[]});
          return reply({rotationId, state:verified?'Verified':'AwaitingProof', configVersion:5, deploymentSequence:7,
            targets:targets.map((target,index) => ({...target, state:verified||index===0?'Verified':'Missing'}))});
        }
        if (endpoint.startsWith(oldFormalPath) || endpoint.startsWith(rotationPath + '/')) {
          const body = request.postDataJSON(), etag = request.headers()['if-match'], commandKey = request.headers()['idempotency-key'];
          writes.push({endpoint, body, etag, commandKey});
          assert.ok(commandKey);
          assert.equal(request.method(), 'POST');
          assert.equal(url.search, '');
          if (endpoint === oldFormalPath + '/preview') {
            assert.equal(etag, '"7"');
            assert.deepEqual(body, {environmentId, replacementCredentialId:replacementId, newValidFrom:replacement.validFrom,
              newExpiresAt:replacement.expiresAt, overlapUntil:'2026-12-21T10:00:00.000Z', hardDeadline});
            return reply({...body, credentialId:oldId, applicationId:appId, maskedAccessKey:'••••1234', credentialRevision:7,
              previewHash:'a'.repeat(64), configVersion:5, deploymentSequence:7, lastWorkingCredentialAtDeadline:true,
              lastPublishedCredentialAtDeadline:true, targets});
          }
          if (endpoint === oldFormalPath + '/confirm') {
            assert.equal(etag, '"7"');
            assert.deepEqual(body,{window:writes.find(item=>item.endpoint===oldFormalPath+'/preview').body,
              previewHash:'a'.repeat(64), confirmAutomaticRevocation:true, reason:'credential_retired'});
            if (writes.filter(item=>item.endpoint===endpoint).length===1) return route.abort('failed');
            return reply(receipt,202);
          }
          assert.equal(etag, '"1"');
          if (endpoint === rotationPath + '/complete') {
            assert.equal(verified,true);
            assert.deepEqual(body,{confirm:true,reason:'credential_retired'});
            receipt = {...receipt, status:'ManualRevocationRecorded', revision:2, revocationOperationId:operationId};
          } else if (endpoint === rotationPath + '/abort') {
            assert.equal(verified,false);
            assert.deepEqual(body,{confirm:true,reason:'owner_request'});
            if (scenario === 'permission-lost-on-decision') {
              eligible = false;
              return reply({code:'permission_denied',detail:'目标组织的撤销权限已撤回。'},403);
            }
            receipt = {...receipt, status:'AbortPending', revision:2, abortRevocationOperationId:operationId};
          } else throw Error('Unexpected rotation action ' + endpoint);
          return reply(receipt,202);
        }
        return reply({detail:'Unexpected synthetic API request'},404);
      });

      const resume = ['resume-and-abort','wrong-old-key','deadline-closed','permission-lost-on-decision','cross-organization-aggregate'].includes(scenario);
      const expectedEligibility = ['cross-organization-aggregate','schedule-and-complete','resume-and-abort','wrong-old-key',
        'deadline-closed','permission-lost-on-decision','eligibility-refresh-denied'].includes(scenario);
      const firstEligibility = expectedEligibility ?
        page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1'+eligibilityPath,{timeout:7000}) : undefined;
      await page.goto(new URL(resume ? resumePath : appPath,base).href);
      const row = page.getByRole('row').filter({hasText:'••••1234'});
      await row.waitFor();
      if (firstEligibility) await firstEligibility;
      const formal = row.getByRole('button',{name:'正式轮换',exact:true});
      const ordinary = row.getByRole('button',{name:'轮换',exact:true});
      if (['manage-only','read-only','other-environment','cross-organization-aggregate'].includes(scenario)) {
        assert.equal(await formal.count(),0);
        if (scenario === 'manage-only') {
          await ordinary.click();
          await page.getByRole('region',{name:'凭证轮换向导'}).waitFor();
        }
        assert.equal(writes.length,0);
        if (scenario === 'cross-organization-aggregate') {
          assert.equal(eligibilityReads,1);
          assert.equal(verificationReads,0);
          assert.equal(await page.getByRole('region',{name:'正式轮换',exact:true}).count(),0);
        }
      } else if (scenario === 'eligibility-refresh-denied') {
        await formal.waitFor();
        await formal.click();
        const panel = page.getByRole('region',{name:'正式轮换',exact:true});
        await panel.waitFor();
        const refreshed = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1'+eligibilityPath);
        await page.getByRole('button',{name:'刷新',exact:true}).click();
        await formal.waitFor({state:'hidden'});
        await panel.waitFor({state:'hidden'});
        releaseRefresh();
        assert.equal((await refreshed).status(),403);
        assert.equal(await formal.count(),0);
        assert.equal(writes.length,0);
      } else {
        if (!resume) await formal.click();
        const panel = page.getByRole('region',{name:'正式轮换',exact:true});
        await panel.waitFor();
        const complete = panel.getByRole('button',{name:'人工完成并撤销旧钥'});
        if (scenario === 'wrong-old-key') {
          await panel.getByRole('alert').filter({hasText:'回执与当前旧凭证、环境或已知修订不匹配。'}).waitFor();
          assert.equal(await complete.count(),0);
          assert.equal(verificationReads,0);
        } else if (scenario === 'deadline-closed') {
          await panel.getByText('验证窗口已关闭').waitFor();
          assert.equal(await complete.isDisabled(),true);
          assert.equal(await panel.getByRole('button',{name:'请求中止轮换'}).isDisabled(),true);
          assert.equal(writes.length,0);
        } else if (scenario === 'schedule-and-complete') {
          const select = panel.getByLabel('替换凭证',{exact:true});
          await select.waitFor();
          assert.deepEqual(await select.locator('option').evaluateAll(options=>options.map(option=>option.value)), ['',replacementId]);
          await select.selectOption(replacementId);
          await panel.getByLabel('重叠窗口结束（UTC）').fill('2026-12-21T10:00');
          await panel.getByLabel('硬截止（UTC）').fill('2026-12-22T10:00');
          await panel.getByRole('button',{name:'预览正式轮换'}).click();
          const review = panel.getByRole('region',{name:'正式轮换影响'});
          await review.getByText('截止时已发布配置中缺少其他有效凭证。').waitFor();
          await review.getByText('安全租约无法续签时，同环境依赖安全状态的其他 ApiKey 或 JWT 请求也可能暂时收到 503。').waitFor();
          for (const target of targets) {
            await review.getByRole('cell',{name:target.nodeId,exact:true}).waitFor();
            await review.getByRole('cell',{name:target.instanceId,exact:true}).waitFor();
          }
          const confirm = review.getByRole('button',{name:'确认登记正式轮换'});
          assert.equal(await confirm.isDisabled(),true);
          await review.getByLabel('操作理由').selectOption('credential_retired');
          assert.equal(await confirm.isDisabled(),true);
          await review.getByRole('checkbox').check();
          await confirm.click();
          await panel.getByRole('alert').waitFor();
          assert.equal(await complete.count(),0);
          await confirm.click();
          await panel.getByText('已登记硬截止 · 修订 1').waitFor();
          assert.deepEqual(writes.filter(item=>item.endpoint===oldFormalPath+'/confirm')[0],
            writes.filter(item=>item.endpoint===oldFormalPath+'/confirm')[1]);
          assert.equal(new URL(page.url()).searchParams.get('formalRotationId'),rotationId);
          assert.equal(eligibilityReads,1);
          await page.reload();
          await panel.getByText('等待完整的替换钥请求证明').waitFor();
          assert.equal(await complete.isDisabled(),true);
          verified = true;
          await panel.getByRole('button',{name:'刷新回执与验证'}).click();
          await panel.getByText('全部当前网关实例已核验替换钥请求').waitFor();
          await complete.click();
          const manual = panel.getByRole('region',{name:'确认人工撤旧'});
          const submit = manual.getByRole('button',{name:'提交人工撤旧'});
          assert.equal(await submit.isDisabled(),true);
          await manual.getByLabel('操作理由').selectOption('credential_retired');
          await manual.getByRole('checkbox').check();
          await submit.click();
          await panel.getByText('人工撤旧已记录，待核对网关执行 · 修订 2').waitFor();
          await panel.getByText(operationId,{exact:true}).waitFor();
        } else {
          await panel.getByText('等待完整的替换钥请求证明').waitFor();
          assert.equal(await complete.isDisabled(),true);
          await panel.getByRole('button',{name:'请求中止轮换'}).click();
          const abort = panel.getByRole('region',{name:'确认中止请求'});
          await abort.getByLabel('操作理由').selectOption('owner_request');
          await abort.getByRole('checkbox').check();
          const deniedRecheck = scenario === 'permission-lost-on-decision'
            ? page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1'+eligibilityPath)
            : undefined;
          await abort.getByRole('button',{name:'提交中止请求'}).click();
          if (scenario === 'permission-lost-on-decision') {
            await panel.waitFor({state:'hidden'});
            await formal.waitFor({state:'hidden'});
            await deniedRecheck;
            assert.equal(writes.length,1);
            assert.equal(eligibilityReads,2);
          } else {
            await panel.getByText('替换钥撤销已记录，等待中止确认 · 修订 2').waitFor();
            await panel.getByText('等待撤销确认期间，原硬截止继续生效。').waitFor();
            await panel.getByText(operationId,{exact:true}).waitFor();
            assert.equal(await complete.count(),0);
            receipt = {...receipt,status:'Aborted',revision:3};
            await panel.getByRole('button',{name:'刷新回执与验证'}).click();
            await panel.getByText('已确认中止正式轮换 · 修订 3').waitFor();
          }
        }
      }
      const url = new URL(page.url());
      assert.equal(url.href.includes(accessKey),false);
      assert.equal(url.href.includes(replacementAccessKey),false);
      assert.equal((await page.locator('body').innerText()).includes(replacementAccessKey),false);
      assert.deepEqual(errors,[]);
      results.push({scenario,writes:writes.length,verificationReads,eligibilityReads});
    } finally { await context.close(); }
  }
  console.log(JSON.stringify({passed:results.length,results},null,2));
} finally { await browser.close(); }
