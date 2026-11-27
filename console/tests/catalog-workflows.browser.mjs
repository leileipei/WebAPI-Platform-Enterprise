// Run node --test with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after,before,test} from 'node:test';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist=process.env.WEBAPI_CONSOLE_DIST;
const origin='http://127.0.0.1';
const org='org',project='project',env='env',apiId='orders';
const versions=[
  {id:'old',version:'1.0',status:'Published',createdAt:'2026-10-01T12:00:00Z',sealedAt:'2026-11-01T09:00:00Z'},
  {id:'new',version:'2.0',status:'Draft',createdAt:'2026-11-01T10:00:00+08:00',sealedAt:null},
  {id:'legacy',version:'0.9',status:'Retired',sealedAt:null}
].map(v=>({...v,apiId,changeType:'compatible',revision:2}));
const button=(where,name)=>where.getByRole('button',{name,exact:true});
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

async function fixture(t,permissions,handle,detail={}){
  const page=await browser.newPage(),errors=[],unexpected=[];
  page.setDefaultTimeout(5000);
  page.on('pageerror',error=>errors.push(error.message));
  t.after(async()=>{await page.close();assert.deepEqual(errors,[]);assert.deepEqual(unexpected,[]);});
  await page.route('**/*',async route=>{
    const url=new URL(route.request().url());
    assert.equal(url.origin,origin);
    if(url.pathname.startsWith('/assets/'))return route.fulfill({path:join(dist,url.pathname.slice(1))});
    if(!url.pathname.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
    const path=url.pathname.slice('/api/v1'.length);
    const reply=(json,status=200)=>route.fulfill({json,status});
    if(path==='/auth/me')return reply({id:'operator',username:'operator',displayName:'工程师',permissions,rolePermissions:[{organizationId:org,permissions}],
      scopes:[{scope:{organizationId:org},accessMode:'read_write'}]});
    if(path==='/auth/csrf')return reply({token:'fixture'});
    if(path==='/scope-tree')return reply({organizations:[{id:org,name:'组织'}],
      projects:[{id:project,organizationId:org,name:'项目'}],environments:[{id:env,projectId:project,name:'测试环境'}]});
    if(path==='/apis/'+apiId)return reply({api:{id:apiId,projectId:project,organizationId:org,code:'orders',name:'订单 API'},
      versions,workingRevision:3,runningConfigVersion:{[env]:2},canReadVersions:true,...detail});
    if(await handle({path,url,req:route.request(),reply,
      file:(body,contentType)=>route.fulfill({body,contentType})})===false){
      unexpected.push(route.request().method()+' '+path);
      return reply({detail:'未配置的测试请求'},500);
    }
  });
  return page;
}

const yamlSource='openapi: 3.1.0\n# 保留原本的注释和换行\ninfo: {title: Orders, version: "1"}\npaths: {}\n';
const normalized='{"openapi":"3.1.0","info":{"title":"Orders","version":"1"},"paths":{},"x-id":9007199254740993}';
const contractVersions=[
  {...versions[0],sourceFormat:'yaml',openapiSource:yamlSource,openapiDocument:normalized},
  {...versions[1],sourceFormat:'json',openapiSource:normalized,openapiDocument:normalized},
  {...versions[2],openapiDocument:normalized},
  {...versions[2],id:'empty',version:'0.8'}
];

async function readDownload(page,control){
  const [download]=await Promise.all([page.waitForEvent('download'),control.click()]);
  return {name:download.suggestedFilename(),content:await readFile(await download.path(),'utf8')};
}

test('a version reader downloads the stored YAML and normalized JSON without an environment entry',async t=>{
  const requests=[];
  const page=await fixture(t,['api.read','api.version.read','environment.read'],async({path,url,reply,file})=>{
    if(path.endsWith('/access-addresses'))return reply({configured:false,routes:[]});
    if(!['/versions/old/openapi','/versions/new/openapi'].includes(path))return false;
    const representation=url.searchParams.get('representation');
    requests.push(path+'?representation='+representation);
    return path==='/versions/old/openapi'&&representation==='source'?file(yamlSource,'application/yaml; charset=utf-8'):file(normalized,'application/json');
  },{versions:contractVersions,canReadSchemas:false});
  await page.goto(origin+'/apis/'+apiId);
  const row=page.getByRole('row').filter({has:page.getByRole('cell',{name:'1.0',exact:true})});
  await button(row,'查看 OpenAPI').click();
  assert.equal(await button(page,'下载环境 OpenAPI 文档').isDisabled(),true);
  const panel=page.getByRole('region',{name:'版本 OpenAPI 文档'});
  const raw=await readDownload(page,button(panel,'下载版本原文'));
  const json=await readDownload(page,button(panel,'下载规范化 JSON'));
  assert.deepEqual([raw,json],[{name:'openapi-old.source.yaml',content:yamlSource},{name:'openapi-old.normalized.json',content:normalized}]);
  await page.getByRole('combobox',{name:/^API 版本/}).selectOption('new');
  const rawJson=await readDownload(page,button(panel,'下载版本原文'));
  assert.deepEqual(rawJson,{name:'openapi-new.source.json',content:normalized});
  assert.deepEqual(requests,['/versions/old/openapi?representation=source','/versions/old/openapi?representation=json','/versions/new/openapi?representation=source']);
  t.diagnostic(JSON.stringify({requests,files:[raw.name,json.name,rawJson.name],originalBytes:Buffer.byteLength(raw.content),normalizedBytes:Buffer.byteLength(json.content)}));
});

test('changing version or revoking contract read aborts old file requests',async t=>{
  const permissions=['api.read','api.schema.read'];
  const waits={old:Promise.withResolvers(),new:Promise.withResolvers()},downloads=[];
  const page=await fixture(t,permissions,async({path,url,file})=>{
    const version=/^\/versions\/(old|new)\/openapi$/.exec(path)?.[1];
    if(!version)return false;
    if(url.searchParams.get('representation')==='source')await waits[version].promise;
    return file(normalized,'application/json');
  },{versions:contractVersions,canReadVersions:false,canReadSchemas:true});
  await page.addInitScript(()=>{
    window.versionDownloadAborts=[];
    const fetcher=window.fetch;
    window.fetch=(input,options)=>{
      if(String(input).includes('/openapi?representation='))options?.signal?.addEventListener('abort',()=>window.versionDownloadAborts.push(String(input)),{once:true});
      return fetcher(input,options);
    };
  });
  page.on('download',download=>downloads.push(download.suggestedFilename()));
  try{
    await page.goto(origin+'/apis/'+apiId);
    await button(page,'OpenAPI').click();
    const panel=page.getByRole('region',{name:'版本 OpenAPI 文档'}),select=page.getByRole('combobox',{name:/^API 版本/});
    async function start(version){
      await Promise.all([page.waitForRequest(req=>req.url().endsWith('/versions/'+version+'/openapi?representation=source')),
        button(panel,'下载版本原文').click()]);
    }
    await start('old');
    await select.selectOption('new');
    await page.waitForFunction(()=>window.versionDownloadAborts.length===1);
    waits.old.resolve();
    const result=await readDownload(page,button(panel,'下载规范化 JSON'));
    assert.deepEqual(result,{name:'openapi-new.normalized.json',content:normalized});
    await start('new');
    permissions.splice(permissions.indexOf('api.schema.read'),1);
    await Promise.all([page.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1/apis/'+apiId),
      page.evaluate(()=>window.dispatchEvent(new Event('permission-refresh')))]);
    await page.waitForFunction(()=>window.versionDownloadAborts.length===2);
    waits.new.resolve();
    const denied=page.getByText('缺少此 API 的契约读取权限。');
    await page.getByRole('cell',{name:'1.0',exact:true}).or(denied).first().waitFor();
    if(!await denied.isVisible())await button(page,'OpenAPI').click();
    await denied.waitFor();
    await page.evaluate(()=>new Promise(done=>requestAnimationFrame(()=>requestAnimationFrame(done))));
    assert.equal(await button(page,'下载版本原文').count(),0);
    assert.deepEqual(downloads,['openapi-new.normalized.json']);
    t.diagnostic(JSON.stringify({aborted:await page.evaluate(()=>window.versionDownloadAborts),downloads}));
  }finally{waits.old.resolve();waits.new.resolve();}
});

test('legacy JSON, empty versions and server-denied contract reads expose only recorded documents',async t=>{
  const detail={versions:contractVersions,canReadSchemas:true,canReadVersions:false},requests=[];
  const page=await fixture(t,['api.read','api.schema.read'],async({path,url,file})=>{
    if(path!=='/versions/legacy/openapi')return false;
    requests.push(url.searchParams.get('representation'));
    return file(normalized,'application/json');
  },detail);
  await page.goto(origin+'/apis/'+apiId);await button(page,'OpenAPI').click();
  const select=page.getByRole('combobox',{name:/^API 版本/});
  await select.selectOption('legacy');
  assert.equal(await button(page,'下载版本原文').count(),0);
  assert.deepEqual(await readDownload(page,button(page,'下载规范化 JSON')),{name:'openapi-legacy.normalized.json',content:normalized});
  await select.selectOption('empty');
  await page.getByText('尚未保存 OpenAPI 文档').waitFor();
  assert.equal(await button(page,'下载规范化 JSON').count(),0);
  await select.selectOption('old');
  detail.canReadSchemas=false;
  await button(page,'刷新会话与权限').click();
  await page.getByText('缺少此 API 的契约读取权限。').waitFor();
  assert.equal(await button(page,'下载版本原文').count(),0);
  assert.equal(await button(page,'下载规范化 JSON').count(),0);
  assert.deepEqual(requests,['json']);
});

test('an environment download belongs to its selected view across late responses',async t=>{
  const waits={running:Promise.withResolvers(),working:Promise.withResolvers()};
  const requests=[],downloads=[];
  const page=await fixture(t,['api.read','api.version.read','api.schema.read','environment.read'],async({path,url,reply})=>{
    const view=url.searchParams.get('view');
    if(path.endsWith('/access-addresses'))return reply({configured:true,routes:[{routeId:'route',method:'GET',
      pathTemplate:'/orders',publicTemplate:'https://api.example.test/orders',runningConfigVersion:view==='running'?2:null}]});
    if(!path.endsWith('/openapi'))return false;
    requests.push(view);
    await waits[view].promise;
    return reply({openapi:'3.1.0',info:{title:view,version:'1'}});
  });
  page.on('download',event=>downloads.push(event.suggestedFilename()));
  try{
    await page.goto(origin+'/apis/'+apiId);
    const panel=page.getByRole('region',{name:'客户端 API 访问地址'});
    const select=panel.getByLabel('访问地址配置视图');
    const download=button(panel,'下载环境 OpenAPI 文档');
    async function start(view){
      const sent=page.waitForRequest(req=>req.url().includes('/openapi?')&&new URL(req.url()).searchParams.get('view')===view);
      await download.click();await sent;
    }
    await start('running');
    await select.selectOption('working');
    await panel.getByText(/工作模板/).waitFor();
    assert.equal(await download.isEnabled(),true,'新视图可发起独立下载');
    await start('working');
    const oldResponse=page.waitForResponse(response=>response.url().includes('/openapi?view=running'));
    waits.running.resolve();await (await oldResponse).finished();
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    assert.equal(await button(panel,'生成中…').isDisabled(),true,'旧响应不能结束新视图的下载');
    assert.deepEqual(downloads,[]);
    const completed=page.waitForEvent('download');waits.working.resolve();await completed;
    await panel.getByRole('status').filter({hasText:'已下载环境文档'}).waitFor();
    await select.selectOption('running');await panel.getByText(/运行配置版本 2/).waitFor();
    assert.equal(await download.isEnabled(),true);
    assert.deepEqual(downloads,['api-orders-working.openapi.json']);
    assert.deepEqual(requests,['running','working']);
    t.diagnostic(JSON.stringify({requests,downloads}));
  }finally{waits.running.resolve();waits.working.resolve();}
});

test('the import form previews one root at a one-resource budget and rejects additional files',async t=>{
  const submitted=[];
  const source=JSON.stringify({openapi:'3.1.0',info:{title:'订单',version:'1'},paths:{}});
  const page=await fixture(t,['api.create','route.write','cluster.read','project.read'],async({path,req,reply})=>{
    if(path.endsWith('/import-source-policy'))return reply({projectId:project,revision:1,allowances:[],
      limits:{maxResources:1,maxDocumentBytes:2048,maxBundleBytes:4096}});
    if(path.endsWith('/clusters'))return reply({items:[{id:'cluster',status:'Active',name:'集群'}],total:1,page:1,pageSize:50});
    if(path!=='/openapi/import-previews')return false;
    if(req.method()==='GET')return reply([]);
    submitted.push(req.postDataJSON());
    return reply({previewId:'fixed-preview',sourceHash:'root',bundleHash:'bundle',sourcePolicyRevision:1,
      expiresAt:new Date(Date.now()+120000).toISOString(),dialect:'oas-3.1',sourceFormat:'json',operations:[],issues:[]});
  });
  await page.goto(origin+'/imports');
  await page.getByText('当前修订 1',{exact:false}).waitFor();
  await page.getByLabel('来源方式').selectOption('file');
  const file=(name,text)=>({name,mimeType:'application/json',buffer:Buffer.from(text)});
  await page.getByLabel('根 OpenAPI 文件').setInputFiles(file('orders.json',source));
  await page.waitForFunction(text=>document.querySelector('.import-source-input textarea')?.value===text,source);
  const attachments=page.getByLabel('附带引用文件');
  assert.match(await attachments.locator('..').innerText(),/最多 0 份附带文件/);
  await attachments.setInputFiles(file('types.json','{}'));
  await page.getByRole('alert').filter({hasText:'数量超过预算'}).waitFor();
  assert.equal(await page.getByLabel('OpenAPI JSON / YAML 原文').inputValue(),source);
  await button(page,'解析并固定预览').click();
  await page.getByText('fixed-preview',{exact:true}).waitFor();
  assert.deepEqual(submitted.map(body=>({sourceText:body.sourceText,files:body.files})),[{sourceText:source,files:[]}]);
  t.diagnostic('maxResources=1, root=1, attachments=0, over-limit selection rejected, preview received');
});

test('the API timeline orders actual creation and sealing events without inventing retirement dates',async t=>{
  const page=await fixture(t,['api.read','api.version.read'],async()=>false);
  await page.goto(origin+'/apis/'+apiId);
  await page.getByRole('cell',{name:'1.0',exact:true}).waitFor();
  const timeline=page.getByRole('region',{name:'版本时间线'});
  await timeline.waitFor();
  const events=await timeline.locator('li').evaluateAll(items=>items.map(li=>({
    title:li.querySelector('strong')?.textContent,time:li.querySelector('time')?.getAttribute('datetime')
  })));
  assert.deepEqual(events,[
    {title:'封存版本 1.0',time:versions[0].sealedAt},
    {title:'创建版本 2.0',time:versions[1].createdAt},
    {title:'创建版本 1.0',time:versions[0].createdAt}
  ]);
  await timeline.getByText(/0\.9.*时间未记录/).waitFor();
  assert.equal(await page.getByRole('row').filter({hasText:'2.0'}).getByRole('button',{name:'定义'}).isEnabled(),true);
  t.diagnostic(JSON.stringify(events));
});

test('role editors describe operation-specific conditions without granting the related permission',async t=>{
  const dictionary=[
    {code:'release.rollback',module:'release',description:'创建回滚草稿'},
    {code:'gateway.config.read',module:'gateway',description:'查看快照'},
    {code:'vendor.rollback',module:'vendor',name:'扩展操作',description:'由扩展服务定义'}
  ];
  const writes=[];
  const page=await fixture(t,['role.manage',...dictionary.map(p=>p.code)],async({path,req,reply})=>{
    if(path==='/role-directories')return reply({canManagePlatformRoles:false,organizations:[{id:org,name:'组织'}]});
    if(path==='/roles')return reply([{id:'role',name:'订单角色',code:'ORD',organizationId:org,isSystem:false,
      revision:1,permissions:[],userCount:0}]);
    if(path==='/permissions')return reply(dictionary);
    if(path==='/roles/role/permissions'&&req.method()==='PUT'){writes.push(req.postDataJSON());return reply({});}
    return false;
  });
  await page.goto(origin+'/roles');
  await button(page,'查看权限').click();
  const preview=page.getByRole('region',{name:'角色权限详情'});
  const rollback=where=>where.getByRole('checkbox',{name:/release\.rollback/});
  const dependency=where=>where.getByRole('checkbox',{name:/gateway\.config\.read/});
  const hint=await rollback(preview).evaluate(el=>document.getElementById(el.getAttribute('aria-describedby'))?.innerText);
  assert.match(hint??'',/敏感操作.*创建回滚.*gateway\.config\.read/s,'只读矩阵应提供条目级可访问说明');
  await button(preview,'关闭权限详情').click();
  await button(page,'配置权限').click();
  const edit=page.getByRole('dialog'),choice=rollback(edit).locator('..');
  await rollback(edit).check();
  assert.equal(await dependency(edit).isChecked(),false);
  assert.match(await choice.innerText(),/创建回滚.*gateway\.config\.read.*本角色未选/s);
  await dependency(edit).check();
  assert.match(await choice.innerText(),/gateway\.config\.read.*本角色已选/s);
  await dependency(edit).uncheck();
  const extension=edit.getByRole('checkbox',{name:/vendor\.rollback/}).locator('..');
  assert.doesNotMatch(await extension.innerText(),/敏感操作|另需/);
  await button(edit,'保存').click();
  await edit.waitFor({state:'hidden'});
  assert.deepEqual(writes,[{permissions:['release.rollback']}]);
  t.diagnostic(JSON.stringify(writes));
});
