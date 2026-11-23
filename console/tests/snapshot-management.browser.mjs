import assert from 'node:assert/strict';
import {after,before,test} from 'node:test';
import {createRequire} from 'node:module';
import {join} from 'node:path';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist=process.env.WEBAPI_CONSOLE_DIST,origin='http://127.0.0.1',snapshotPath='/environments/qa/snapshots';
const tree={organizations:[{id:'org',name:'工厂'}],projects:[{organizationId:'org',id:'project',name:'项目'}],
  environments:[{projectId:'project',id:'qa',name:'测试环境'}]};
const row=v=>({configVersion:v,status:'Published',payloadHash:'hash-'+v,latestReleaseId:'release-'+v,latestDeploymentSequence:v+10,latestDeploymentState:'Succeeded'});
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});
async function fixture(t,write){
  const page=await browser.newPage(),writes=[],reads=[];
  t.after(()=>page.close());
  await page.route('**/*',async route=>{
    const req=route.request(),url=new URL(req.url()),path=url.pathname.slice(7),reply=data=>route.fulfill({json:data});
    if(url.pathname.startsWith('/assets/'))return route.fulfill({path:join(dist,url.pathname.slice(1))});
    if(!url.pathname.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
    reads.push(path);
    if(path==='/auth/me')return reply({id:'operator',displayName:'环境管理员',permissions:['gateway.config.read','release.read',...(write?['release.rollback']:[])],scopes:[{scope:{organizationId:'org'},accessMode:write?'read_write':'read_only'}]});
    if(path==='/scope-tree')return reply(tree);
    if(path==='/auth/csrf')return reply({token:'fixture'});
    if(path===snapshotPath){const n=Number(url.searchParams.get('page')||1);return reply({items:[row(n===1?7:2)],page:n,pageSize:1,total:2});}
    if(path===snapshotPath+'/rollback-source')return reply({source:{releaseId:'current-deployment',configVersion:7,deploymentSequence:24}});
    if(path.startsWith(snapshotPath+'/')){
      const version=Number(path.split('/').at(-1));
      return reply({configVersion:version,environmentId:'qa',schemaVersion:'2.4',routes:[{id:'work-order-route',name:version===2?'旧路由':'新路由'}],clusters:[],applications:[],policies:[]});
    }
    if(path==='/releases/current-deployment/rollback'&&req.method()==='POST'){
      writes.push({body:req.postDataJSON(),key:req.headers()['idempotency-key']});return reply({id:'recovery-draft'});
    }
    if(path.startsWith('/releases/'))return route.fulfill({status:404,json:{detail:'测试发布详情'}});
    return reply({items:[],page:1,pageSize:50,total:0});
  });
  await page.goto(origin+'/snapshots');
  await page.getByRole('button',{name:'设为基准'}).waitFor();
  return {page,writes,reads};
}
test('compares two pages and creates a rollback draft from the current deployment',async t=>{
  const {page,writes}=await fixture(t,true);
  assert.equal(await page.getByRole('link',{name:'关联发布'}).getAttribute('href'),'/releases/release-7');
  await page.getByRole('button',{name:'设为基准'}).click();
  await page.getByRole('button',{name:'下一页'}).click();
  await page.getByRole('button',{name:'与基准比较'}).click();
  await page.getByText('修改 · 路由 · work-order-route').click();
  await page.getByText('旧路由',{exact:false}).waitFor();
  await page.getByText('新路由',{exact:false}).waitFor();
  await page.getByRole('button',{name:'回滚到此版本'}).click();
  await page.getByRole('button',{name:'创建回滚草稿'}).click();
  await page.waitForURL('**/releases/recovery-draft');
  assert.equal(writes.length,1);assert.deepEqual(writes[0].body,{targetConfigVersion:2});assert.ok(writes[0].key);
});
test('read-only users can compare and download versions without loading rollback actions',async t=>{
  const {page,reads}=await fixture(t,false);
  assert.equal(await page.getByRole('link',{name:'下载 JSON'}).getAttribute('href'),'/api/v1'+snapshotPath+'/7/download');
  await page.getByRole('button',{name:'下一页'}).click();
  await page.getByRole('link',{name:'关联发布'}).filter({visible:true}).waitFor();
  assert.equal(await page.getByRole('button',{name:'回滚到此版本'}).count(),0);
  assert.ok(!reads.includes(snapshotPath+'/rollback-source'));
});
