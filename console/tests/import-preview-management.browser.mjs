import assert from 'node:assert/strict';
import {createRequire} from 'node:module';

const base = new URL(process.argv[2]);
if (base.protocol !== 'http:' || base.hostname !== '127.0.0.1' ||
    !process.env.WEBAPI_PLAYWRIGHT_PACKAGE || !process.env.WEBAPI_CHROMIUM_EXECUTABLE)
  throw new Error('An explicit loopback preview and installed Playwright/Chromium paths are required.');
const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});
const id = n => '98000000-0000-0000-0000-' + String(n).padStart(12,'0');
const org = id(1), project = id(2), other = id(3), env = id(4), secondEnv = id(5), cluster = id(6);
const endpoint = '/openapi/import-previews';
const button = (area,name) => area.getByRole('button',{name,exact:true});
const row = (page,previewId) => page.getByRole('row').filter({hasText:previewId});
const results = [];

try {
  for (const scenario of ['delayed-workspace-with-default-policy','release-one-and-retry','recover-lost-response-after-scope-change','switch-during-revocation']) {
    const context = await browser.newContext();
    try {
      const page = await context.newPage(), records = [], requests = [], errors = [];
      page.setDefaultTimeout(6000);
      page.on('pageerror',e => errors.push(e.message));
      let dropped = false, failDelete = true, releaseDelete, sawDelete;
      const held = new Promise(resolve => { releaseDelete=resolve; });
      const started = new Promise(resolve => { sawDelete=resolve; });
      let releaseScope;
      const scopeResponse = new Promise(resolve => { releaseScope=resolve; });
      await page.route('**/api/v1/**',async route => {
        const req = route.request(), url = new URL(req.url()), path = url.pathname.slice('/api/v1'.length);
        requests.push({path,method:req.method(),body:req.postDataJSON()});
        const reply = (body,status=200) => route.fulfill({status,contentType:'application/json',body:JSON.stringify(body)});
        if (path === '/auth/me') return reply({id:id(7),displayName:'开发员',permissions:['api.create','route.write','cluster.read'],
          scopes:[{scope:{organizationId:org},accessMode:'read_write'}]});
        if (path === '/auth/csrf') return reply({token:'fixture'});
        if (path === '/scope-tree') {
          if (scenario==='delayed-workspace-with-default-policy') await scopeResponse;
          return reply({organizations:[{id:org,name:'组织'}],
          projects:[{id:project,organizationId:org,name:'第一项目'},{id:other,organizationId:org,name:'第二项目'}],
          environments:[{id:env,projectId:project,name:'开发环境'},{id:secondEnv,projectId:other,name:'第二环境'}]});
        }
        if (path.endsWith('/clusters')) return reply({items:[{id:cluster,status:'Active',name:'集群'}],total:1,page:1,pageSize:50});
        if (path === endpoint && req.method() === 'GET')
          return reply(records.filter(x => x.projectId === url.searchParams.get('projectId') && x.active));
        if (path === endpoint && req.method() === 'POST') {
          const {projectId} = req.postDataJSON();
          if (records.filter(x => x.active && x.projectId===projectId).length >= 5)
            return reply({detail:'预览配额已满',code:'import_preview_quota'},429);
          const record = {projectId,previewId:id(100+records.length),bundleHash:'a'.repeat(64),createdAt:new Date().toISOString(),
            expiresAt:new Date(Date.now()+1200000).toISOString(),environmentName:'开发环境',clusterName:'集群',active:true};
          records.push(record);
          if (scenario.startsWith('recover') && !dropped) {dropped=true;return route.abort('failed');}
          return reply({...record,dialect:'Oas30',sourceHash:'b'.repeat(64),operations:[],issues:[]});
        }
        if (req.method() === 'DELETE' && path.startsWith(endpoint+'/')) {
          if (scenario==='switch-during-revocation') {
            sawDelete();await held;
            return reply({detail:'旧访问已失效'},403);
          }
          if (failDelete) {failDelete=false;return reply({detail:'撤销服务暂时不可用'},503);}
          const record = records.find(x => x.previewId === path.split('/').pop() && x.active);
          if (!record) return reply({detail:'预览已失效'},409);
          record.active=false;
          return route.fulfill({status:204});
        }
        return reply({},404);
      });
      await page.goto(new URL('/imports',base).href);
      const input=page.getByLabel('OpenAPI JSON / YAML 原文'), inspect=button(page,'解析并固定预览');
      async function create(title) {
        await input.fill(JSON.stringify({openapi:'3.0.3',info:{title,version:'1'},paths:{}}));
        await inspect.click();
      }
      if (scenario==='delayed-workspace-with-default-policy') {
        try {
          await input.waitFor({state:'visible'});
          assert.equal(await input.isEditable(),false,'创建草稿前须取得目标工作区');
        } finally { releaseScope(); }
        await create('initial scope');
        await page.locator('.import-preview-meta').getByText(id(100),{exact:true}).waitFor();
        const posted=requests.filter(r=>r.path===endpoint&&r.method==='POST');
        assert.equal(posted.length,1);
        assert.equal(JSON.parse(posted[0].body.sourceText).info.title,'initial scope');
        assert.equal(posted[0].body.projectId,project);
        assert.equal(posted[0].body.environmentId,env);
        assert.equal(requests.some(r=>r.path.endsWith('/import-source-policy')),false);
      } else if (scenario.startsWith('release')) {
        for (const [index,title] of ['A','A','B','B','C'].entries()) {
          await create(title);
          await page.locator('.import-preview-meta').getByText(id(100+index),{exact:true}).waitFor();
        }
        await create('D');
        await page.getByRole('alert').filter({hasText:'预览配额已满'}).waitFor();
        const first=row(page,id(100));
        await button(first,'撤销').click();
        await page.getByRole('alert').filter({hasText:'撤销服务暂时不可用'}).waitFor();
        await button(first,'撤销').click();await first.waitFor({state:'hidden'});
        await create('D');
        await page.locator('.import-preview-meta').getByText(id(105),{exact:true}).waitFor();
        await button(row(page,id(101)),'撤销').click();
        await row(page,id(101)).waitFor({state:'hidden'});
        await page.locator('.import-preview-meta').getByText(id(105),{exact:true}).waitFor();
        assert.deepEqual(records.filter(x=>x.active).map(x=>x.previewId),[id(102),id(103),id(104),id(105)]);
      } else if (scenario.startsWith('recover')) {
        await create('lost');await page.getByRole('alert').filter({hasText:'输入原文已保留'}).waitFor();
        assert.equal(await page.locator('.import-preview-meta').count(),0);
        await button(page,'清空导入草稿').click();
        await page.getByLabel('当前项目').selectOption(other);
        await page.getByRole('heading',{name:'本项目中我的有效预览'}).waitFor();
        assert.equal(await row(page,id(100)).count(),0);
        await page.getByLabel('当前项目').selectOption(project);
        await row(page,id(100)).waitFor();
        await page.reload();await row(page,id(100)).waitFor();
        assert.equal(await page.locator('.import-preview-meta').count(),0);
      } else {
        await create('first');await page.locator('.import-preview-meta').getByText(id(100),{exact:true}).waitFor();
        await button(page,'清空导入草稿').click();
        await button(row(page,id(100)),'撤销').click();await started;
        await page.getByLabel('当前项目').selectOption(other);
        await create('second');await page.locator('.import-preview-meta').getByText(id(101),{exact:true}).waitFor();
        await button(page,'清空导入草稿').click();
        await page.getByLabel('当前项目').selectOption(project);await row(page,id(100)).waitFor();
        await create('third');await page.locator('.import-preview-meta').getByText(id(102),{exact:true}).waitFor();
        await page.evaluate(() => {
          window.stalePermissionRefreshes=0;
          window.addEventListener('permission-refresh',()=>window.stalePermissionRefreshes++);
        });
        const late=page.waitForResponse(r=>r.request().method()==='DELETE');
        releaseDelete();await (await late).finished();
        await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
        assert.equal(await page.evaluate(()=>window.stalePermissionRefreshes),0);
        assert.equal(await page.getByText('旧访问已失效').count(),0);
        await page.locator('.import-preview-meta').getByText(id(102),{exact:true}).waitFor();
      }
      assert.deepEqual(errors,[]);
      results.push(scenario);
    } finally { await context.close(); }
  }
} finally { await browser.close(); }
console.log(JSON.stringify({passed:results.length,results}));
