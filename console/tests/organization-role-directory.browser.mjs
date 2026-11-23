// Exercise the production console with separate organization and platform directory responses.
import {after,before,test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});
const a='a', b='b';
const organizations=[{id:a,name:'甲公司',status:'Active'},{id:b,name:'乙公司',status:'Active'}];
const permission=(code)=>({code,name:code,module:code.split('.')[0]});
const permissions=['role.manage','api.read','route.read','system.manage'];
const role=(id,name,organizationId,isSystem=false,grants=['api.read'])=>({id,name,code:id,organizationId,isSystem,revision:1,permissions:grants,userCount:0});

async function openDirectory(t,{path='/roles',platform=false,tree=organizations,managed=[a],mode='read_write',denyRoleReads=false}={}) {
  const page=await browser.newPage();
  t.after(()=>page.close());
  page.setDefaultTimeout(4500);
  const f={managed,platform,tree,mode,denyRoleReads,identityReads:0,reads:[],writes:[],conflict:false,
    roles:[role('alpha','甲方角色',a),role('kept','保留既有授权',a,false,['api.read','system.manage']),
      role('beta','乙方角色',b),role('builtin','系统角色',null,true),role('global','平台自定义角色',null)]};
  await page.route('**/*',async route=>{
    const req=route.request(),url=new URL(req.url());
    const asset=url.pathname;
    const reply=(body,status=200)=>route.fulfill({status,json:body});
    if(asset.startsWith('/assets/')) return route.fulfill({path:join(process.env.WEBAPI_CONSOLE_DIST,asset.slice(1))});
    if(!asset.startsWith('/api/v1/')) return route.fulfill({path:join(process.env.WEBAPI_CONSOLE_DIST,'index.html')});
    const endpoint=asset.slice(7);
    if(endpoint==='/auth/me'){f.identityReads++;return reply({id:'manager',username:'manager',displayName:'角色管理员',permissions:['role.manage','api.read','route.read'],
      isPlatformAdministrator:f.platform,rolePermissions:[...(f.platform?[{organizationId:null,permissions:['role.manage']}]:[]),
        ...f.managed.map(organizationId=>({organizationId,permissions:['role.manage','api.read','route.read']}))],
      scopes:organizations.map(o=>({scope:{organizationId:o.id},accessMode:f.mode}))});}
    if(endpoint==='/auth/csrf') return reply({token:'test-csrf'});
    if(endpoint==='/scope-tree') return reply({organizations:f.tree,projects:[],environments:[]});
    if(endpoint==='/role-directories') return reply({canManagePlatformRoles:f.platform,organizations:organizations.filter(o=>f.managed.includes(o.id))});
    const org=url.searchParams.get('organizationId');
    if(req.method()==='GET' && (endpoint==='/roles'||endpoint==='/permissions')) {
      f.reads.push({endpoint,org});
      if(endpoint==='/roles'&&f.denyRoleReads)return reply({detail:'角色读取已拒绝'},403);
      if(org ? !f.managed.includes(org) : !f.platform) return reply({detail:'无权读取该目录'},403);
      if(endpoint==='/roles') return reply(f.roles.filter(r=>!org||r.organizationId===org));
      return reply((org ? permissions.slice(0,3) : permissions).map(permission));
    }
    if(req.method()==='POST'&&endpoint==='/roles') {
      const body=req.postDataJSON(),item=role('created',body.name,body.organizationId,false,[]);
      item.code=body.code;
      f.writes.push({endpoint,body});f.roles.push(item);return reply(item);
    }
    if(req.method()==='POST'&&endpoint==='/roles/alpha/copy'){
      const body=req.postDataJSON(),source=f.roles.find(r=>r.id==='alpha'),copy={...source,id:'copy',code:body.code,name:body.name,userCount:0};
      f.writes.push({endpoint,body,tag:req.headers()['if-match']});f.roles.push(copy);return reply(copy);
    }
    if(req.method()==='DELETE'&&endpoint==='/roles/copy'){
      f.writes.push({endpoint,tag:req.headers()['if-match']});f.roles=f.roles.filter(r=>r.id!=='copy');return route.fulfill({status:204});
    }
    if(req.method()==='PUT'&&endpoint.startsWith('/roles/')) {
      const [, , id, action]=endpoint.split('/'),r=f.roles.find(x=>x.id===id);
      const body=req.postDataJSON(),headers=req.headers();
      f.writes.push({endpoint,body,revision:headers['if-match'],csrf:headers['x-csrf-token'],key:headers['idempotency-key']});
      if(f.conflict){f.conflict=false;r.revision++;r.permissions=['route.read'];return reply({detail:'角色修订已更新'},412);}
      if(headers['if-match']!=='"'+r.revision+'"')return reply({detail:'修订已更新'},412);
      if(action==='permissions')r.permissions=body.permissions;
      else if(body.organizationId!==r.organizationId)return reply({detail:'归属不可修改'},422);
      else {r.name=body.name;r.code=body.code;}
      r.revision++;return reply(r);
    }
    throw new Error('Unexpected request '+req.method()+' '+asset);
  });
  await page.goto('http://127.0.0.1'+path);
  await page.getByRole('heading',{name:path==='/roles'?'角色管理':'权限字典',exact:true}).waitFor();
  return {page,f,dialog:page.getByRole('dialog'),row:name=>page.getByRole('row').filter({has:page.getByRole('cell',{name,exact:true})})};
}

test('organization manager with only role-directory visibility can create, rename and configure an organization role',async t=>{
  const {page,f,row,dialog}=await openDirectory(t,{tree:[]});
  await row('甲方角色').waitFor();
  assert.equal(await row('乙方角色').count(),0);
  assert.equal(await row('系统角色').count(),0);
  await page.getByRole('button',{name:'＋ 新建',exact:true}).click();
  await dialog.getByLabel('编码').fill('NEW');
  await dialog.getByLabel('名称').fill('新建角色');
  await dialog.getByRole('button',{name:'保存',exact:true}).click();
  await row('新建角色').getByRole('button',{name:'编辑',exact:true}).click();
  await dialog.getByLabel('名称').fill('新名称');
  await dialog.getByRole('button',{name:'保存',exact:true}).click();
  await row('新名称').getByRole('button',{name:'配置权限',exact:true}).click();
  await dialog.getByRole('checkbox',{name:/api\.read/}).check();
  await dialog.getByRole('button',{name:'保存',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await row('新名称').getByRole('button',{name:'查看权限',exact:true}).click();
  assert.equal(await page.getByRole('region',{name:'角色权限详情'}).getByRole('checkbox',{name:/api\.read/}).isChecked(),true);
  assert.deepEqual(f.writes.map(w=>w.body),[{code:'NEW',name:'新建角色',organizationId:a},
    {code:'NEW',name:'新名称',organizationId:a},{permissions:['api.read']}]);
  assert.deepEqual(f.writes.slice(1).map(w=>w.revision),['"1"','"2"']);
  assert.ok(f.writes[2].key&&f.writes[2].csrf==='test-csrf');
  assert.ok(f.reads.length>0&&f.reads.every(v=>v.org===a),'organization-only operator must stay in the scoped directory');
});

test('undisplayed existing grants can be viewed and cannot be lost in a replacement',async t=>{
  const {page,f,row}=await openDirectory(t);
  const r=row('保留既有授权');
  await r.waitFor();
  assert.equal(await r.getByRole('button',{name:'配置权限'}).isDisabled(),true);
  await r.getByRole('button',{name:'查看权限'}).click();
  assert.match(await page.getByRole('region',{name:'角色权限详情'}).innerText(),/system\.manage/);
  assert.deepEqual(f.writes,[]);
});

test('switching organizations replaces the role directory and permission dictionary',async t=>{
  const {page,f,row}=await openDirectory(t,{managed:[a,b]});
  await row('甲方角色').waitFor();
  await page.getByRole('combobox',{name:'当前组织'}).selectOption(b);
  await row('乙方角色').waitFor();
  assert.equal(await row('甲方角色').count(),0);
  await row('乙方角色').getByRole('button',{name:'查看权限'}).click();
  await page.getByRole('region',{name:'角色权限详情'}).waitFor();
  assert.ok(f.reads.some(x=>x.org===b&&x.endpoint==='/permissions'));
  assert.ok(f.reads.every(x=>x.org!==null));
});

test('platform catalog retains system roles and preserves a global custom role owner when an organization is selected',async t=>{
  const {page,f,row,dialog}=await openDirectory(t,{platform:true,managed:[a,b]});
  await page.getByRole('combobox',{name:'角色管理范围'}).selectOption('platform');
  await row('系统角色').waitFor();
  assert.equal(await row('系统角色').getByRole('button',{name:'编辑',exact:true}).isDisabled(),true);
  assert.equal(await row('系统角色').getByRole('button',{name:'配置权限'}).isDisabled(),true);
  await row('平台自定义角色').getByRole('button',{name:'编辑',exact:true}).click();
  await dialog.getByLabel('名称').fill('平台角色新名称');
  await dialog.getByRole('button',{name:'保存',exact:true}).click();
  await row('平台角色新名称').waitFor();
  assert.equal(f.writes[0].body.organizationId,null);
  assert.ok(f.reads.some(x=>x.endpoint==='/roles'&&x.org===null));
});

test('permission dictionary itself uses the selected authorized organization',async t=>{
  const {page,f}=await openDirectory(t,{path:'/permissions'});
  await page.getByRole('cell',{name:'api.read',exact:true}).waitFor();
  assert.equal(await page.getByRole('cell',{name:'system.manage',exact:true}).count(),0);
  assert.ok(f.reads.every(x=>x.endpoint==='/permissions'&&x.org===a));
});

test('a stale role permission replacement requires reviewing the newly assigned grants',async t=>{
  const {f,row,dialog}=await openDirectory(t);
  const configure=row('甲方角色').getByRole('button',{name:'配置权限'});
  await configure.click();
  await dialog.getByRole('checkbox',{name:/api\.read/}).uncheck();
  f.conflict=true;
  await dialog.getByRole('button',{name:'保存',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  await configure.click();
  assert.equal(await dialog.getByRole('checkbox',{name:/route\.read/}).isChecked(),true);
  assert.equal(await dialog.getByRole('checkbox',{name:/api\.read/}).isChecked(),false);
  await dialog.getByRole('checkbox',{name:/api\.read/}).check();
  await dialog.getByRole('button',{name:'保存',exact:true}).click();
  await dialog.waitFor({state:'hidden'});
  assert.deepEqual([...f.writes[1].body.permissions].sort(),['api.read','route.read']);
  assert.equal(f.writes[1].revision,'"2"');
});

test('a role grant removed in this organization invalidates an open editor while the session permission union is unchanged',async t=>{
  const {page,f,row,dialog}=await openDirectory(t);
  await row('甲方角色').getByRole('button',{name:'配置权限'}).click();
  await dialog.getByRole('checkbox',{name:/route\.read/}).check();
  f.managed=[];
  await page.getByRole('button',{name:'刷新会话与权限'}).evaluate(button=>button.click());
  await dialog.waitFor({state:'hidden'});
  await page.getByText('当前没有可管理的角色目录。').waitFor();
  assert.equal(await row('甲方角色').count(),0);
  assert.deepEqual(f.writes,[]);
});

test('read-only scope cannot create or edit and does not issue platform directory requests',async t=>{
  const {page,f,row}=await openDirectory(t,{managed:[],mode:'read'});
  await page.getByText('当前没有可管理的角色目录。').waitFor();
  assert.equal(await row('甲方角色').count(),0);
  assert.equal(await page.getByRole('button',{name:'＋ 新建',exact:true}).isEnabled(),false);
  assert.deepEqual(f.reads,[]);
});

test('rejected directory reads wait for a deliberate refresh and expose no stale write controls',async t=>{
  const {page,f,row}=await openDirectory(t,{denyRoleReads:true});
  await page.getByRole('alert').filter({hasText:'角色读取已拒绝'}).waitFor();
  assert.equal(f.identityReads,1,'an authorization read must not trigger another session and directory read');
  assert.equal(await page.getByRole('button',{name:'＋ 新建',exact:true}).isDisabled(),true);
  assert.equal(await row('甲方角色').count(),0);
  f.denyRoleReads=false;
  await page.getByRole('button',{name:'刷新',exact:true}).click();
  await row('甲方角色').waitFor();
  assert.equal(await page.getByRole('button',{name:'＋ 新建',exact:true}).isEnabled(),true);
});

test('editing an organization role from the platform catalog checks its organization grantable codes',async t=>{
  const {page,f,row,dialog}=await openDirectory(t,{platform:true});
  await page.getByRole('combobox',{name:'角色管理范围'}).selectOption('platform');
  await row('保留既有授权').getByRole('button',{name:'配置权限'}).click();
  await page.getByText('当前目录无法配置该角色的完整权限。').waitFor();
  assert.equal(await dialog.count(),0);
  assert.deepEqual(f.writes,[]);
  assert.ok(f.reads.at(-1).endpoint==='/permissions' && f.reads.at(-1).org===a);
});


test('custom role copy and confirmed deletion preserve the source and use the displayed revision',async t=>{
 const {page,f,dialog,row}=await openDirectory(t);
 const source=row('甲方角色');await source.getByRole('button',{name:'复制角色',exact:true}).click();
 await dialog.getByLabel('编码').fill('copied');await dialog.getByLabel('名称').fill('角色副本');
 await dialog.getByRole('button',{name:'保存',exact:true}).click();
 const copied=row('角色副本');await copied.getByRole('button',{name:'删除角色'}).click();
 assert.equal(f.writes.length,1);
 await page.getByRole('alertdialog').getByRole('button',{name:'确认删除'}).click();
 await copied.waitFor({state:'hidden'});assert.equal(await source.count(),1);
 assert.deepEqual(f.writes.map(w=>[w.endpoint,w.tag]),[['/roles/alpha/copy','"1"'],['/roles/copy','"1"']]);
});

test('platform role copy checks the row organization and every source permission there',async t=>{
  const {page,f,row}=await openDirectory(t,{platform:true,managed:[a]});
  await page.getByRole('combobox',{name:'角色管理范围'}).selectOption('platform');
  await row('乙方角色').waitFor();
  const copy=name=>row(name).getByRole('button',{name:'复制角色',exact:true});
  assert.equal(await copy('乙方角色').count(),0);
  assert.equal(await copy('保留既有授权').count(),0);
  assert.equal(await copy('甲方角色').isEnabled(),true);
  assert.deepEqual(f.writes,[]);
});
