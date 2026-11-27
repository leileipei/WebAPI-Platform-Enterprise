import assert from 'node:assert/strict';
import {after,before,test} from 'node:test';
import {createRequire} from 'node:module';
import {join} from 'node:path';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const id=n=>'a0000000-0000-4000-8000-'+String(n).padStart(12,'0');
const envs=[{id:id(1),projectId:id(3),name:'开发'},{id:id(2),projectId:id(3),name:'验收'}];
const tree={organizations:[{id:id(4),name:'组织'}],projects:[{id:id(3),organizationId:id(4),name:'项目'}],environments:envs};
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

test('keeps a selected cluster across searches and saves a cluster beyond the first thousand',async t=>{
  const page=await browser.newPage(),seen=[],writes=[];
  t.after(()=>page.close());
  const routeDef={key:'work-order',apiId:id(5),path:'/work-orders',methods:['GET'],policies:[],authenticationMode:'Anonymous'};
  const clusters=Array.from({length:1001},(_,i)=>({id:id(10000+i),name:'后端 '+i}));
  const mapping={routes:[{artifactRouteKey:routeDef.key,targetRouteId:null,clusterId:clusters[0].id,timeoutMs:9000,policies:[]}],applications:[]};
  const promotion={id:id(6),artifactId:id(7),status:'Draft',revision:1,sourceEnvironmentId:envs[0].id,targetEnvironmentId:envs[1].id,mapping,eligibility:{canEditMapping:true}};
  await page.route('**/*',async route=>{
    const req=route.request(),url=new URL(req.url()),path=url.pathname.slice(7),reply=json=>route.fulfill({json});
    if(url.pathname.startsWith('/assets/'))return route.fulfill({path:join(process.env.WEBAPI_CONSOLE_DIST,url.pathname.slice(1))});
    if(!url.pathname.startsWith('/api/v1/'))return route.fulfill({path:join(process.env.WEBAPI_CONSOLE_DIST,'index.html')});
    if(path==='/auth/me')return reply({id:id(8),displayName:'开发员',permissions:['release.read'],scopes:[{scope:{organizationId:id(4)},accessMode:'read_write'}]});
    if(path==='/scope-tree')return reply(tree);
    if(path==='/auth/csrf')return reply({token:'fixture'});
    if(path==='/release-promotions/'+promotion.id)return reply(promotion);
    if(path==='/release-artifacts/'+promotion.artifactId)return reply({id:promotion.artifactId,content:{routes:[routeDef],apis:[]}});
    if(path.endsWith('/mapping-options')){
      const search=url.searchParams.get('search'),n=Number(url.searchParams.get('page')||1);
      seen.push({n,search});
      return reply({routes:[],policies:[],applications:[],clusters:search?clusters.filter(x=>x.name===search):clusters.slice((n-1)*1000,n*1000),truncated:!search&&n===1});
    }
    if(path.endsWith('/mapping')&&req.method()==='PUT'){writes.push(req.postDataJSON());return reply({revision:2});}
    if(path.startsWith('/environments/'))return reply(envs.find(x=>path.endsWith(x.id)));
    return reply({items:[],total:0,page:1,pageSize:50});
  });
  await page.goto('http://127.0.0.1/delivery/promotions/'+promotion.id);
  await page.getByRole('tab',{name:'3. 目标映射'}).click();
  const save=page.getByRole('button',{name:'保存目标映射'}),select=page.getByLabel('目标 Cluster');
  assert.equal(await save.isEnabled(),true);
  await page.getByRole('button',{name:'加载更多目标资源'}).click();
  await select.selectOption(clusters.at(-1).id);
  await page.getByLabel('搜索目标资源').fill(clusters[2].name);
  await Promise.all([page.waitForResponse(r=>new URL(r.url()).searchParams.get('search')===clusters[2].name),page.getByRole('button',{name:'搜索',exact:true}).click()]);
  assert.equal(await select.inputValue(),clusters.at(-1).id);
  await Promise.all([page.waitForResponse(r=>r.request().method()==='PUT'&&r.url().endsWith('/mapping')),save.click()]);
  assert.equal(writes[0].routes[0].clusterId,clusters.at(-1).id);
  assert.ok(seen.some(x=>x.n===2));
});
