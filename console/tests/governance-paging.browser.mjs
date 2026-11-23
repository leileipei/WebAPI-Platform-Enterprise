import {after,before,test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist=process.env.WEBAPI_CONSOLE_DIST;
const id=n=>'b1d36ce4-90eb-4052-8ed1-'+String(n).padStart(12,'0');
const org={id:id(1),name:'甲组织'},project={id:id(2),organizationId:org.id,name:'甲项目'},environment={id:id(3),projectId:project.id,name:'开发环境'};
const tree={organizations:[org],projects:[project],environments:[environment]};
const user={id:id(4),username:'operator',displayName:'当前人员',permissions:['user.manage','scope.manage','release.read','organization.read','project.read','project.write','organization.write'],
  isPlatformAdministrator:true,rolePermissions:[{organizationId:null,permissions:['user.manage','scope.manage','project.write','organization.write']}],
  scopes:[{scope:{organizationId:org.id},accessMode:'read_write'}]};
const target={id:id(5),displayName:'甲成员',username:'member',status:'Active',authSource:'local',revision:7,roleIds:[]};
const pageOf=(items,total=items.length)=>({items,total,page:1,pageSize:50});
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>browser?.close());
async function fixture(t,handler,identity=user){
 const page=await browser.newPage();page.setDefaultTimeout(5000);t.after(()=>page.close());
 await page.route('**/*',async route=>{
  const url=new URL(route.request().url());
  if(!url.pathname.startsWith('/api/v1/'))return route.fulfill({path:join(dist,url.pathname.startsWith('/assets/')?url.pathname.slice(1):'index.html')});
  const path=url.pathname.slice(7);const reply=json=>route.fulfill({json});
  if(path==='/auth/me')return reply(identity);
  if(path==='/scope-tree')return reply(tree);
  if(path==='/auth/csrf')return reply({token:'fixture'});
  return handler(path,url,route,reply);
 });
 return page;
}

test('a scope editor reads only its target in a directory of 10000 users',async t=>{
 let lists=0;const targetReads=[];
 const page=await fixture(t,(path,url,route,reply)=>{
  if(path==='/users'){lists++;return reply(pageOf([target],10000));}
  targetReads.push(path);
  if(path==='/users/'+target.id)return reply(target);
  if(path==='/users/'+target.id+'/scopes')return reply([{scope:{organizationId:org.id},accessMode:'read'}]);
  throw Error('Unexpected '+path);
 });
 await page.goto('http://127.0.0.1/scopes');
 const configure=page.getByRole('button',{name:'配置 Scope'});await configure.waitFor();
 await page.waitForLoadState('networkidle');
 const beforeOpening=lists;assert.ok(beforeOpening>0);
 await configure.click();
 await page.getByRole('dialog').getByText(/修订 7/).waitFor();
 assert.equal(lists,beforeOpening);
 assert.deepEqual(targetReads,['/users/'+target.id,'/users/'+target.id+'/scopes','/users/'+target.id]);
});

test('a poll lasting more than two intervals displays its result and a changed filter starts a fresh read',async t=>{
 const rows=[]; let open;
 const gated=new Promise(resolve=>open=resolve);t.after(()=>open());
 const item={id:id(7),releaseNo:'审批慢请求',releaseType:'publish',state:'WaitingApproval',
  organization:org,project,environment,requestedBy:target.id,applicantDisplayName:target.displayName,createdAt:'2026-11-01T00:00:00Z',
  approvalEligibility:{canAct:true,currentStepOrder:1},approvedCount:0,requiredCount:2,risk:{visibility:'Restricted'}};
 const page=await fixture(t,async(path,url,route,reply)=>{
  if(path!=='/approvals')throw Error('Unexpected '+path);
  const view=url.searchParams.get('view');rows.push(view);
  if(view==='PendingMine')await gated;
  return reply({page:pageOf(view==='PendingMine'?[item]:[]),counts:{pendingMine:1,handledMine:0,allVisible:1}});
 });
 await page.clock.install();
 await page.goto('http://127.0.0.1/approvals');
 await page.getByText('尚未读取').waitFor();
 await page.waitForFunction(()=>document.readyState==='complete');
 await new Promise(resolve=>setTimeout(resolve,100));
 const initial=rows.length;assert.ok(initial>0);
 await page.clock.runFor(11000);
 assert.equal(rows.length,initial,'interval ticks cannot supersede an in-flight read');
 open();await page.getByRole('button',{name:'审批慢请求'}).waitFor();
 await page.getByRole('tab',{name:/我已处理/}).click();
 await page.getByText('当前筛选下暂无审批申请').waitFor();
 assert.ok(rows.includes('HandledMine'));assert.equal(await page.getByRole('button',{name:'审批慢请求'}).count(),0);
});

test('a project row opens the project workspace and hands ownership over on the current project revision',async t=>{
 const second={id:id(20),organizationId:org.id,name:'第二项目',code:'P2',status:'Active',revision:4};
 let owner=target.id,revision=4;const writes=[];
 const page=await fixture(t,(path,url,route,reply)=>{
  const method=route.request().method();
  if(path==='/organizations/'+org.id+'/projects')return reply(pageOf([{...project,revision,ownerUserId:owner,ownerDisplayName:target.displayName},second]));
  if(path==='/projects/'+project.id)return reply({...project,revision,ownerUserId:owner});
  if(path==='/projects/'+project.id+'/owner-candidates')return reply(pageOf([{...target,id:user.id,displayName:'接任者'}]));
  if(method==='PUT'&&path==='/projects/'+project.id+'/owner'){
   writes.push({tag:route.request().headers()['if-match'],body:route.request().postDataJSON()});owner=user.id;revision++;return reply({...project,ownerUserId:owner,revision});
  }
  if(path==='/approvals')return reply({page:pageOf([]),counts:{pendingMine:0}});
  return reply(pageOf([]));
 });
 await page.goto('http://127.0.0.1/projects');
 const row=page.getByRole('row').filter({has:page.getByRole('cell',{name:project.name,exact:true})});
 await row.getByRole('button',{name:'设置负责人'}).click();
 await page.getByRole('button',{name:'选为负责人'}).click();await page.getByRole('button',{name:'确认变更'}).click();
 await page.getByRole('button',{name:'确认变更'}).waitFor({state:'hidden'});
 assert.deepEqual(writes,[{tag:'"4"',body:{userId:user.id}}]);
 await row.getByRole('button',{name:'进入项目'}).click();
 await page.getByRole('heading',{name:'企业工作台',exact:true}).waitFor();
 assert.equal(await page.getByRole('combobox',{name:'当前项目'}).inputValue(),project.id);
});

test('organization details and administrator settings use the selected organization and target user revision',async t=>{
 const writes=[];let assigned=false;
 const page=await fixture(t,(path,url,route,reply)=>{
  const organization={...org,code:'ORG',status:'Active',revision:3};
  if(path==='/organizations')return reply(pageOf([organization]));
  if(path==='/organizations/'+org.id)return reply(organization);
  const admin='/organizations/'+org.id+'/administrators';
  if(path===admin)return reply(pageOf(url.searchParams.get('candidates')==='true'&&!assigned||url.searchParams.get('candidates')!=='true'&&assigned?[{...target,administrator:assigned}]:[]));
  if(path===admin+'/'+target.id&&route.request().method()==='PUT'){
   assigned=route.request().postDataJSON().enabled;writes.push({tag:route.request().headers()['if-match'],assigned});return reply({userId:target.id,enabled:assigned,revision:8});
  }
  throw Error('Unexpected '+path);
 });
 await page.goto('http://127.0.0.1/organizations');
 await page.getByRole('button',{name:'组织详情'}).click();
 await page.getByRole('region',{name:'组织详情'}).getByText(org.id).waitFor();
 await page.getByRole('button',{name:'管理员设置'}).click();
 await page.getByRole('button',{name:'添加管理员'}).click();
 await page.getByRole('button',{name:'指定为管理员'}).click();
 await page.getByRole('button',{name:'确认变更'}).click();
 await page.getByRole('button',{name:'确认变更'}).waitFor({state:'hidden'});
 assert.deepEqual(writes,[{tag:'"7"',assigned:true}]);
 await page.getByRole('button',{name:'管理员设置'}).click();await page.getByRole('button',{name:'移除管理员'}).waitFor();
});

test('project handover and administrator settings check the organization that supplied the function',async t=>{
 const identity={...user,rolePermissions:[{organizationId:null,permissions:['user.manage','scope.manage']},
   {organizationId:id(40),permissions:['project.write','organization.write']}]};
 const page=await fixture(t,(path,url,route,reply)=>reply(pageOf(path==='/organizations'?[org]:[project])),identity);
 await page.goto('http://127.0.0.1/projects');
 await page.getByRole('cell',{name:project.name,exact:true}).waitFor();
 assert.equal(await page.getByRole('button',{name:'设置负责人'}).count(),0);
 await page.goto('http://127.0.0.1/organizations');
 await page.getByRole('cell',{name:org.name,exact:true}).waitFor();
 assert.equal(await page.getByRole('button',{name:'管理员设置'}).count(),0);
});

test('administrator settings require the system platform administrator role',async t=>{
 const page=await fixture(t,(path,url,route,reply)=>reply(pageOf([org])),{...user,isPlatformAdministrator:false});
 await page.goto('http://127.0.0.1/organizations');
 await page.getByRole('cell',{name:org.name,exact:true}).waitFor();
 assert.equal(await page.getByRole('button',{name:'管理员设置'}).count(),0);
});
