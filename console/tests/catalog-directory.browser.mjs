import {organizationPermissions} from './identity-fixture.mjs';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {readFile} from 'node:fs/promises';
import {after,before,test} from 'node:test';
const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist=process.env.WEBAPI_CONSOLE_DIST;
const id=n=>'b7100000-0000-4000-8000-'+String(n).padStart(12,'0');
const [org,project,env,other,api,version,cluster,routeId,release,owner]=Array.from({length:10},(_,i)=>id(i+1));
const permissions=['api.read','api.version.read','api.version.write','api.schema.read','api.schema.write','route.read','route.write','metrics.read','policy.read','cluster.read','release.read'];
const item={id:api,organizationId:org,projectId:project,code:'Z999',name:'冷库校正',lifecycleStatus:'Draft',workingRevision:1,
 ownerUserId:owner,ownerName:'仓储负责人',methods:['GET'],versions:['2.1'],tags:['inventory'],health:'Healthy'};
const prior=Array.from({length:50},(_,i)=>({...item,id:id(100+i),name:'部件'+i,code:'A'+i}));
const releases=[{id:release,releaseNo:'REL-123',status:'Succeeded',createdAt:'2026-11-01T00:00:00Z'}];
const basicRoute={id:routeId,apiVersionId:version,environmentId:env,path:'/inventory',methods:['GET'],clusterId:cluster,routeName:'盘点',priority:350,timeoutMs:8500,enabled:true,revision:3,requireApiKey:true};
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>browser?.close());
const button=(p,name)=>p.getByRole('button',{name,exact:true});
async function fixture(t,perms=permissions){
 const page=await browser.newPage();t.after(()=>page.close());page.setDefaultTimeout(8000);
 const seen=[],errors=[];t.after(()=>assert.deepEqual(errors,[]));page.on('pageerror',e=>errors.push(e.message));
 let retired=false,parameters=[];
 await page.route('**/*',async route=>{
  const req=route.request(),url=new URL(req.url());
  if(url.pathname.startsWith('/assets/'))return route.fulfill({path:join(dist,url.pathname.slice(1))});
  if(!url.pathname.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
  const path=url.pathname.slice(7),method=req.method(),reply=json=>route.fulfill({json});
  seen.push({path,method,query:Object.fromEntries(url.searchParams),body:req.postDataJSON(),tag:req.headers()['if-match']});
  const data=(items,total=items.length,page=1)=>({items,total,page,pageSize:50});
  if(path==='/auth/me')return reply({id:owner,displayName:'仓储负责人',...organizationPermissions(org,perms),scopes:[{scope:{organizationId:org},accessMode:'read_write'}]});
  if(path==='/scope-tree')return reply({organizations:[{id:org,name:'测试组织'}],projects:[{id:project,organizationId:org,name:'库存项目'}],environments:[{id:env,projectId:project,name:'开发环境'},{id:other,projectId:project,name:'目标环境'}]});
  if(path==='/auth/csrf')return reply({token:'test'});
  if(path.endsWith('/api-groups'))return reply([]);
  if(path==='/projects/'+project+'/apis'){
   if(url.searchParams.has('q')||url.searchParams.has('method'))return reply(data([item]));
   const page=Number(url.searchParams.get('page')||1);return reply(data(page===1?prior:[item],51,page));
  }
  if(path.endsWith('/apis/export'))return reply(req.postDataJSON().ids.map(k=>[...prior,item].find(a=>a.id===k)));
  const v={id:version,apiId:api,version:'2.1',status:retired?'Retired':'Draft',changeType:'compatible',revision:retired?3:2};
  if(path==='/apis/'+api)return reply({api:item,workingRevision:1,runningConfigVersion:{[env]:7},versions:[v],canReadSchemas:perms.includes('api.schema.read'),canWriteSchemas:true,canReadVersions:true,canWriteVersions:true});
  if(path.endsWith('/versions'))return reply(path==='/apis/'+api+'/versions'?[v]:[]);
  if(path.endsWith('/retire')){retired=true;return reply({...v,status:'Retired'});}
  if(path.endsWith('/parameters')){if(method==='PUT')parameters=req.postDataJSON().map(r=>({...r,id:id(88)}));return reply(parameters);}
  if(path==='/apis/'+api+'/overview')return reply({apiId:api,environmentId:env,runningState:'Confirmed',configVersion:7,routePolicies:[{routeId,path:'/inventory',authentication:'ApiKey',timeoutMs:8500}],policies:[{policyId:id(55),type:'timeout',revision:3}],clusters:[{clusterId:cluster,health:'Healthy'}],releases});
  if(path==='/observability/apis/'+api+'/metrics')return reply({sourceState:'Available',range:{start:url.searchParams.get('start'),end:url.searchParams.get('end')},coverage:{complete:true},data:{kpis:[{metric:'request_count',value:123,unit:'count',sampleCount:100,state:'Available'}]}});
  if(path.endsWith('/clusters'))return reply(data([{id:cluster,name:'目标库存后端',status:'Active',environmentId:path.includes(other)?other:env,projectId:project}]));
  if(path.endsWith('/routes'))return reply(data([basicRoute]));
  if(path.endsWith('/route-defaults'))return reply({timeoutMs:30000,source:'Saved'});
  if(path.endsWith('/copy'))return reply({...basicRoute,id:id(90),environmentId:other});
  return reply({});
 });
 return {page,seen};
}
test('directory searches beyond page one and exports authorized selections from two pages',async t=>{
 const {page,seen}=await fixture(t);await page.goto('http://127.0.0.1/apis');
 await page.getByRole('checkbox',{name:'选择 A0',exact:true}).check();await button(page,'下一页').click();
 await page.getByRole('checkbox',{name:'选择 Z999',exact:true}).check();
 const download=page.waitForEvent('download');await page.getByRole('button',{name:'批量导出（2）'}).click();
 const data=JSON.parse(await readFile(await(await download).path(),'utf8'));assert.deepEqual(data.map(r=>r.id),[prior[0].id,api]);
 await page.getByRole('textbox',{name:'搜索 API'}).fill('冷库校正');await page.getByText('共 1 条',{exact:false}).waitFor();
 assert.ok(seen.some(r=>r.path.endsWith('/apis')&&r.query.q==='冷库校正'&&r.query.page==='1'));
 await page.getByLabel('方法',{exact:true}).selectOption('GET');await page.getByText('冷库校正',{exact:true}).waitFor();
 assert.ok(seen.some(r=>r.query.method==='GET'&&r.query.q==='冷库校正'&&r.query.environmentId===env));
});
test('API overview uses the current environment and the saved parameter schema follows the selected type',async t=>{
 const {page,seen}=await fixture(t);await page.goto('http://127.0.0.1/apis/'+api);
 const overview=page.getByRole('region',{name:'API 运行总览'});
 await overview.getByText('123',{exact:true}).waitFor();await overview.getByText('REL-123',{exact:true}).waitFor();
 await overview.getByText(/8500 ms/).waitFor();await overview.getByText(/健康/).first().waitFor();
 const query=seen.find(r=>r.path.endsWith('/metrics')).query;assert.equal(query.environmentId,env);assert.equal(query.projectId,project);assert.equal(query.allAccessibleEnvironments,'false');
 await button(page,'参数').click();await button(page,'＋ 新增').click();const d=page.getByRole('dialog');
 await d.getByLabel('定义名称',{exact:true}).fill('quantity');await d.getByLabel('数据类型',{exact:true}).selectOption('integer');
 assert.deepEqual(JSON.parse(await d.getByLabel('Schema 原文').inputValue()),{type:'integer'});
 await button(d,'保存工作定义').click();await page.getByText('quantity',{exact:true}).waitFor();
 const body=seen.find(r=>r.path.endsWith('/parameters')&&r.method==='PUT').body[0];
 assert.deepEqual([body.dataType,JSON.parse(body.schema).type],['integer','integer']);
 await button(page,'版本').click();await button(page,'废弃版本').click();await button(page.getByRole('dialog'),'保存').click();
 await page.getByText('Retired',{exact:true}).waitFor();assert.equal(seen.find(r=>r.path.endsWith('/retire')).tag,'"2"');
});
test('a catalog reader makes no privileged overview reads',async t=>{
 const {page,seen}=await fixture(t,['api.read']);await page.goto('http://127.0.0.1/apis/'+api);await page.getByRole('heading',{name:'冷库校正'}).waitFor();
 assert.equal(await page.getByRole('region',{name:'API 运行总览'}).count(),0);
 assert.equal(seen.some(r=>r.path.endsWith('/overview')||r.path.endsWith('/metrics')),false);
});
test('single route copy maps an explicit destination environment and cluster with a guarded source revision',async t=>{
 const {page,seen}=await fixture(t);await page.goto('http://127.0.0.1/routes');await button(page,'复制到环境').click();
 const dialog=page.getByRole('dialog');await dialog.getByLabel('目标集群',{exact:true}).selectOption(cluster);await button(dialog,'保存').click();
 await page.getByText('已在目标环境创建工作路由。').waitFor();const copy=seen.find(r=>r.path===`/routes/${routeId}/copy`);
 assert.equal(copy.tag,'"3"');assert.deepEqual(copy.body,{clusterId:cluster,environmentId:other});
});
