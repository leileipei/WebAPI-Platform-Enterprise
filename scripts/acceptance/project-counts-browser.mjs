// Real browser acceptance against WebApi.ProjectCounts.BrowserHost. No mocked HTTP.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash, randomUUID} from 'node:crypto';
import {createRequire} from 'node:module';

import {assertFixtureBinding,assertServedIdentity,claimEvidenceDirectory,publicFailure} from './project-counts-guards.mjs';

async function main(){
const [baseArgument, directoryArgument, outputArgument] = process.argv.slice(2);
const base = new URL(baseArgument), directory = path.resolve(directoryArgument), output = path.resolve(outputArgument);
assert.ok(process.env.WEBAPI_PLAYWRIGHT_PACKAGE && process.env.WEBAPI_CHROMIUM_EXECUTABLE);
const fixture = JSON.parse(await fs.readFile(path.join(directory, 'fixture.json')));
const contextInfo = JSON.parse(await fs.readFile(path.join(directory, 'context.json')));
const credentials = JSON.parse(await fs.readFile(path.join(directory, 'browser-private.json')));
assertFixtureBinding(base.href,directory,fixture,credentials,contextInfo);
const identityResponse=await fetch(new URL('/acceptance-identity.json',base),{redirect:'error'});
assert.equal(identityResponse.status,200);assertServedIdentity(fixture,await identityResponse.json());
for(const [file,digest] of Object.entries(fixture.staticFiles)) {
  const url=new URL('/'+file,base);assert.equal(url.origin,base.origin);
  const response=await fetch(url,{redirect:'error'});assert.equal(response.status,200);
  assert.equal(createHash('sha256').update(Buffer.from(await response.arrayBuffer())).digest('hex'),digest,'Served static asset mismatch.');
}
Object.assign(fixture,contextInfo);
await claimEvidenceDirectory(output);
const resultFile = path.join(output,'results.json');
const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const browser = await chromium.launch({headless:true, executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});
const results = {productRevision:fixture.productRevision, owner:fixture.owner, classification:fixture.classification,
  startedAt:new Date().toISOString(), viewport:{width:1440,height:1000}, cases:[], passed:false};
let phase='initialization',scenario='none';
async function persist(){const temporary=resultFile+'.'+randomUUID()+'.tmp';await fs.writeFile(temporary,JSON.stringify(results,null,2)+'\n',{flag:'wx',mode:0o600});await fs.rename(temporary,resultFile);}
const fields = ['environmentCount','apiCount','applicationCount'];
const counts = row => fields.map(field => row[field]);
const projectPath = `/api/v1/organizations/${fixture.organizationId}/projects`;
const scenarios = ['all','no-environment','no-api','no-app','no-count-reads','project-only','environment-only','no-project-read','no-governance-read','revoke'];
const scenarioNames = {
  all:'多资源、空项目、停用资源、版本与重叠范围不重复、共享应用排除',
  'no-environment':'缺 environment.read', 'no-api':'缺 api.read', 'no-app':'缺 app.read',
  'no-count-reads':'三类读取权限均缺失', 'project-only':'仅项目范围与其他项目隔离',
  'environment-only':'仅环境范围', 'no-project-read':'环境读取可见性继承',
  'no-governance-read':'无项目和环境读取权限', revoke:'已有会话逐项撤销读取权限'
};
async function checkpoint(page, name, data) {
  const file = name + '.png';
  await page.screenshot({path:path.join(output,file),fullPage:true});
  results.cases.push({id:name, ...data, screenshot:file,
    screenshotSha256:createHash('sha256').update(await fs.readFile(path.join(output,file))).digest('hex'),
    visuallyInspected:false, observedAt:new Date().toISOString(),passed:true});
  await persist();
}
async function rendered(page, rows) {
  await page.getByRole('heading',{name:'项目管理',exact:true}).waitFor();
  await page.getByRole('columnheader',{name:'项目应用数',exact:true}).waitFor();
  await page.waitForFunction(total => document.querySelectorAll('tbody tr').length === total, rows.length);
  for (const row of rows) {
    const tr = page.getByRole('row').filter({has:page.getByRole('cell',{name:row.code,exact:true})});
    await tr.waitFor();
    for (let i=0;i<fields.length;i++) {
      const expected = String(row[fields[i]] ?? '—');
      await assertEventually(async()=>assert.equal((await tr.getByRole('cell').nth(i+3).innerText()).trim(),expected));
    }
    assert.equal(await tr.getByRole('button',{name:'编辑',exact:true}).isDisabled(),true);
  }
  assert.equal(await page.getByRole('button',{name:'＋ 新建',exact:true}).isDisabled(),true);
}
async function assertEventually(action) {
  const end=Date.now()+10000;
  while(true){try{return await action();}catch(error){if(Date.now()>=end)throw error;await new Promise(r=>setTimeout(r,100));}}
}
try {
  for (const name of scenarios) {
    scenario=name;phase='login';
    const context = await browser.newContext({viewport:results.viewport, timezoneId:'Asia/Shanghai',locale:'zh-CN'});
    await context.route('**/*',route=>new URL(route.request().url()).origin===base.origin?route.continue():route.abort('blockedbyclient'));
    const page=await context.newPage(), errors=[];
    page.on('pageerror',error=>errors.push(error.message));
    const account=credentials.accounts.find(account=>account.name===name);
    assert.ok(account);
    try {
      // Login through the actual UI. Password fields are never photographed.
      await page.goto(new URL('/projects',base).href);
      assert.equal(new URL(page.url()).origin,base.origin);
      await page.getByRole('textbox',{name:'用户名',exact:true}).fill(account.username);
      await page.getByLabel('密码',{exact:true}).fill(account.password);
      const login=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1/auth/login'&&r.request().method()==='POST');
      await page.getByRole('button',{name:'登录',exact:true}).click();
      assert.equal((await login).status(),200);
      phase='read-counts';
      const response=await context.request.get(new URL(projectPath+'?pageSize=100',base).href);
      assert.equal(response.status(),200);
      const data=await response.json(), row=data.items.find(row=>row.id===fixture.projectId);
      const expectedTotal=name==='no-governance-read'?0:['project-only','environment-only','revoke'].includes(name)?1:name==='no-project-read'?3:4;
      assert.equal(data.total,expectedTotal);
      if(row) {
        const expected=name==='no-environment'?[null,2,2]:name==='no-api'?[2,null,2]:name==='no-app'?[2,2,null]:
          name==='no-count-reads'?[null,null,null]:name==='environment-only'?[1,null,null]:[2,2,2];
        assert.deepEqual(counts(row),expected);
      } else assert.equal(name,'no-governance-read');
      if(name==='all') {
        assert.deepEqual(counts(data.items.find(row=>row.id===fixture.emptyProjectId)),[0,0,0]);
        assert.deepEqual(counts(data.items.find(row=>row.id===fixture.disabledProjectId)),[1,null,null]);
        // A second page contains the main project; out-of-range pages retain totals.
        const second=await (await context.request.get(new URL(projectPath+'?page=2&pageSize=1',base).href)).json();
        assert.equal(second.items[0].id,fixture.projectId);assert.deepEqual(counts(second.items[0]),[2,2,2]);
        const beyond=await (await context.request.get(new URL(projectPath+'?page=9&pageSize=1',base).href)).json();
        assert.equal(beyond.total,4);assert.deepEqual(beyond.items,[]);
      }
      if(['project-only','environment-only','revoke'].includes(name)) {
        assert.deepEqual(data.items.map(row=>row.id),[fixture.projectId]);
        const hidden=await context.request.get(new URL(`/api/v1/projects/${fixture.otherProjectId}`,base).href);
        assert.equal(hidden.status(),404);
      }
      if(name==='no-governance-read') {
        const hidden=await context.request.get(new URL(`/api/v1/projects/${fixture.projectId}`,base).href);
        assert.equal(hidden.status(),404);
      }
      phase='render-counts';
      await rendered(page,data.items);
      await checkpoint(page,'PC-'+name,{scenario:scenarioNames[name],httpStatus:response.status(),total:data.total,
        rows:data.items.map(row=>({id:row.id,code:row.code,counts:counts(row)})),requirements:['P04-C03','P04-C04','P04-C05']});
      if(name==='revoke') {
        phase='revoke-existing-session';
        for(const [index,permission] of ['environment.read','api.read','app.read'].entries()) {
          const commandId=randomUUID();
          // This file control is private to the owned test host, never an HTTP product endpoint.
          const final=path.join(directory,'commands',commandId+'.json'),temporary=final+'.tmp';
          await fs.writeFile(temporary,JSON.stringify({action:'revoke',permission}),{mode:0o600});
          await fs.rename(temporary,final);
          await assertEventually(async()=>assert.equal(JSON.parse(await fs.readFile(path.join(directory,'done-'+commandId+'.json'))).changed,1));
          const next=await context.request.get(new URL(projectPath,base).href);
          assert.equal(next.status(),200);
          const nextData=await next.json();
          assert.deepEqual(counts(nextData.items[0]),[index>=0?null:2,index>=1?null:2,index>=2?null:2]);
          const refreshed=page.waitForResponse(r=>new URL(r.url()).pathname===projectPath&&r.status()===200);
          await page.getByRole('button',{name:'刷新',exact:true}).click();
          await refreshed;
          await rendered(page,nextData.items);
          await checkpoint(page,'PC-revoke-'+permission.split('.')[0],{scenario:'同一会话撤销 '+permission,
            reauthenticated:false,httpStatus:next.status(),rows:nextData.items.map(row=>({id:row.id,counts:counts(row)})),
            requirements:['P04-C03','P04-C04','P04-C05']});
        }
      }
      assert.deepEqual(errors,[]);
      phase='logout';
      await page.getByRole('button',{name:'退出登录',exact:true}).click();
      await page.getByRole('heading',{name:'登录管理控制台'}).waitFor();
      const afterLogout=await context.request.get(new URL('/api/v1/auth/me',base).href);
      assert.equal(afterLogout.status(),401);
    } finally {await context.close();}
  }
  results.passed=true;
} catch(error) {
  results.failure=publicFailure(error,phase,scenario);
  process.exitCode=1;
} finally {
  results.finishedAt=new Date().toISOString();
  await persist();
  await browser.close();
}
console.log(JSON.stringify({passed:results.passed,observations:results.cases.length,resultFile,failure:results.failure},null,2));

}
main().catch(error=>{console.log(JSON.stringify({passed:false,failure:publicFailure(error,'preflight','none')}));process.exitCode=1;});
