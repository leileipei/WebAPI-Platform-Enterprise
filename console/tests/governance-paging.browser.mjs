import {after,before,test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist=process.env.WEBAPI_CONSOLE_DIST;
const id=n=>'b1d36ce4-90eb-4052-8ed1-'+String(n).padStart(12,'0');
const org={id:id(1),name:'甲组织',status:'Active'},project={id:id(2),organizationId:org.id,name:'甲项目',status:'Active'},environment={id:id(3),projectId:project.id,name:'开发环境',status:'Active'};
const tree={organizations:[org],projects:[project],environments:[environment]};
const user={id:id(4),username:'operator',displayName:'当前人员',permissions:['user.manage','scope.manage','release.read','organization.read','project.read','project.write','organization.write'],
  isPlatformAdministrator:true,rolePermissions:[{organizationId:null,permissions:['user.manage','scope.manage','project.write','organization.write']}],
  scopes:[{scope:{organizationId:org.id},accessMode:'read_write'}]};
const target={id:id(5),displayName:'甲成员',username:'member',status:'Active',authSource:'local',revision:7,roleIds:[]};
const pageOf=(items,total=items.length)=>({items,total,page:1,pageSize:50});
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>browser?.close());
async function fixture(t,handler,identity=user,scopeTree=tree){
 const page=await browser.newPage();page.setDefaultTimeout(5000);t.after(()=>page.close());
 await page.route('**/*',async route=>{
  const url=new URL(route.request().url());
  if(!url.pathname.startsWith('/api/v1/'))return route.fulfill({path:join(dist,url.pathname.startsWith('/assets/')?url.pathname.slice(1):'index.html')});
  const path=url.pathname.slice(7);const reply=json=>route.fulfill({json});
  if(path==='/auth/me')return reply(identity);
  if(path==='/scope-tree')return reply(scopeTree);
  if(path==='/auth/csrf')return reply({token:'fixture'});
  return handler(path,url,route,reply);
 });
 return page;
}

async function checkDrawer(page,dialog) {
 await dialog.waitFor();
 const box=await dialog.boundingBox(),viewport=page.viewportSize();
 assert.ok(box&&viewport);
 assert.ok(Math.abs(box.x+box.width-viewport.width)<=1,'drawer aligned with right edge');
 assert.ok(box.y<=1&&Math.abs(box.height-viewport.height)<=1,'drawer spans viewport height');
}

test('organization write stays in its granting organization and opens a right drawer',async t=>{
 const second={id:id(51),code:'OTHER',name:'只读组织'},first={...org,code:'OWN',revision:3,status:'Active'};
 const identity={...user,isPlatformAdministrator:false,rolePermissions:[{organizationId:org.id,permissions:user.permissions}],
  scopes:[org,second].map(({id})=>({scope:{organizationId:id},accessMode:'read_write'}))};
 const writes=[];
 const page=await fixture(t,(path,url,route,reply)=>{
  if(path==='/organizations'&&route.request().method()==='GET')return reply(pageOf([first,second]));
  writes.push([route.request().method(),path,route.request().headers()['if-match']]);return reply(first);
 },identity,{...tree,organizations:[org,second]});
 await page.setViewportSize({width:1440,height:900});
 await page.goto('http://127.0.0.1/organizations');
 const row=page.getByRole('row',{name:/甲组织/});
 await row.waitFor();
 assert.equal(await page.getByRole('button',{name:'＋ 新建'}).isDisabled(),true);
 assert.equal(await page.getByRole('row',{name:/只读组织/}).getByRole('button',{name:'编辑'}).isDisabled(),true);
 await row.getByRole('button',{name:'编辑'}).click();
 const drawer=page.getByRole('dialog',{name:'编辑组织管理'});
 await checkDrawer(page,drawer);
 await drawer.getByLabel('名称').fill('修改名称');
 await drawer.getByRole('button',{name:'保存',exact:true}).click();
 await drawer.waitFor({state:'hidden'});
 assert.deepEqual(writes,[['PUT','/organizations/'+org.id,'"3"']]);
});

test('a platform administrator can create an organization and cancel a changed drawer from the keyboard',async t=>{
 const writes=[];
 const page=await fixture(t,(path,url,route,reply)=>{
  assert.equal(path,'/organizations');
  if(route.request().method()==='GET')return reply(pageOf([org]));
  writes.push(route.request().method());return reply({...org,id:id(52)});
 });
 await page.setViewportSize({width:1440,height:900});
 await page.goto('http://127.0.0.1/organizations');
 const create=page.getByRole('button',{name:'＋ 新建'}),drawer=page.getByRole('dialog',{name:'新建组织管理'});
 await create.click();await checkDrawer(page,drawer);
 await drawer.getByLabel('编码').fill('NEW');
 await page.keyboard.press('Escape');
 await drawer.getByRole('button',{name:'确认放弃'}).click();
 await drawer.waitFor({state:'hidden'});
 assert.equal(await create.evaluate(el=>el===document.activeElement),true);
 assert.deepEqual(writes,[]);
 await page.setViewportSize({width:390,height:800});
 await create.click();await checkDrawer(page,drawer);
 await drawer.getByLabel('编码').fill('NEW');
 await drawer.getByLabel('名称').fill('新组织');
 await drawer.getByRole('button',{name:'保存',exact:true}).click();
 await drawer.waitFor({state:'hidden'});
 assert.deepEqual(writes,['POST']);
});

test('environment rows read their own current snapshot and open the selected gateway identity',async t=>{
 const other={...environment,id:id(61),name:'验收环境',desiredConfigVersion:7},empty={...environment,name:'空环境',desiredConfigVersion:null};
 const inactive={...other,id:id(65),name:'已停用环境',status:'Disabled'};
 const node={id:id(62),environmentId:other.id,nodeName:'验收网关',enabled:false,eTag:'"4"'},calls=[];
 const permissions=['gateway.read','gateway.config.read','gateway.operate','environment.read'];
 const actor={...user,permissions,rolePermissions:[{organizationId:org.id,permissions}]};
 const page=await fixture(t,(path,url,route,reply)=>{
  if(path==='/projects/'+project.id+'/environments')return reply(pageOf([empty,other,inactive]));
  calls.push(path);
  if(path==='/environments/'+empty.id+'/snapshots/current')return reply({deploymentSequence:0,snapshot:null});
  if(path==='/environments/'+other.id+'/snapshots/current')return reply({deploymentSequence:9,snapshot:{environmentId:other.id,configVersion:8,schemaVersion:'2.1',routes:[],clusters:[],policies:[],applications:[]}});
  if(path==='/environments/'+other.id+'/gateway-nodes')return reply(pageOf([node]));
  if(path==='/gateway-nodes/'+node.id)return reply(node);
  if(path==='/gateway-nodes/'+node.id+'/events')return reply(pageOf([]));
  throw Error('Unexpected environment read '+path);
 },actor,{...tree,environments:[empty,other,inactive]});
 await page.goto('http://127.0.0.1/environments');
 const emptyRow=page.getByRole('row',{name:/空环境/}),otherRow=page.getByRole('row',{name:/验收环境/});
 const inactiveRow=page.getByRole('row',{name:/已停用环境/});await inactiveRow.waitFor();
 assert.equal(await inactiveRow.getByRole('button',{name:'查看当前快照'}).isDisabled(),true);
 assert.equal(await inactiveRow.getByRole('button',{name:'Gateway 绑定'}).isDisabled(),true);
 await emptyRow.getByRole('button',{name:'查看当前快照'}).click();
 await page.getByText('此环境尚未下发配置快照。').waitFor();
 assert.deepEqual(calls,['/environments/'+empty.id+'/snapshots/current']);
 await otherRow.getByRole('button',{name:'查看当前快照'}).click();
 await page.getByText(/"schemaVersion": "2.1"/).waitFor();
 await page.getByText('目标配置版本 8 · 下发序列 9').waitFor();
 assert.deepEqual(calls,['/environments/'+empty.id+'/snapshots/current','/environments/'+other.id+'/snapshots/current']);
 await otherRow.getByRole('button',{name:'Gateway 绑定'}).click();
 const binding=page.getByRole('region',{name:/验收环境.*Gateway 绑定/});
 await binding.getByText('验收网关').waitFor();
 await binding.getByRole('button',{name:'管理节点身份'}).click();
 await page.getByRole('heading',{name:'验收网关'}).waitFor();
 assert.equal(await page.evaluate(()=>localStorage.getItem('core-environment')),other.id);
 assert.equal(await page.getByRole('button',{name:'编辑身份状态'}).isEnabled(),true);
});

test('project and environment write actions require a role granting the operation in the selected organization',async t=>{
 const permissions=['organization.read','project.read','environment.read','project.write','environment.write'];
 const identity={...user,permissions,isPlatformAdministrator:false,rolePermissions:[
  {organizationId:org.id,permissions:permissions.filter(p=>p.endsWith('.read'))},
  {organizationId:id(65),permissions}]};
 const page=await fixture(t,(path,url,route,reply)=>{
  if(path==='/organizations/'+org.id+'/projects')return reply(pageOf([project]));
  if(path==='/projects/'+project.id+'/environments')return reply(pageOf([environment]));
  throw Error('Unexpected scoped request '+path);
 },identity);
 for(const [kind,name] of [['projects',project.name],['environments',environment.name]]){
  await page.goto('http://127.0.0.1/'+kind);const row=page.getByRole('row',{name:new RegExp(name)});await row.waitFor();
  assert.equal(await page.getByRole('button',{name:'＋ 新建'}).isDisabled(),true);
  assert.equal(await row.getByRole('button',{name:'编辑'}).isDisabled(),true);
 }
});

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
