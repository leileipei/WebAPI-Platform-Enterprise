// The built console handles login and navigation. The identity provider and API use local fixtures.
import {after,before,test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist=process.env.WEBAPI_CONSOLE_DIST, origin='http://127.0.0.1';
const providerId='8877c055-6114-45ed-a09d-c8efbcaa6c19';
const policyId='19aea78b-3259-4fa5-b1a4-667e7a57e1be';
const policy={id:policyId,organizationId:'org',projectId:'project',name:'订单接口超时',type:'timeout',config:'{"timeoutMs":5000}',enabled:true,versionNo:1};
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

async function loginPage(t,entry,{mustChangePassword=false}={}){
 const page=await browser.newPage();
 t.after(()=>page.close());
 const user={id:'reader',username:'reader',displayName:'测试读者',authSource:'local',mustChangePassword,
  permissions:['policy.read'],scopes:[{scope:{organizationId:'org',projectId:'project'},accessMode:'read'}]};
 const emptyPage={items:[],total:0,page:1,pageSize:50};
 let signedIn=false;
 await page.route('**/*',route=>{
  const request=route.request(),path=new URL(request.url()).pathname;
  if(path.startsWith('/assets/'))return route.fulfill({path:join(dist,path.slice(1))});
  if(!path.startsWith('/api/v1/'))return route.fulfill({path:join(dist,'index.html')});
  const reply=(json,status=200)=>route.fulfill({status,json});
  const endpoint=path.slice('/api/v1'.length);
  if(endpoint==='/auth/me')return signedIn?reply(user):reply({detail:'请登录'},401);
  if(endpoint==='/auth/csrf')return reply({token:'fixture-csrf'});
  if(endpoint==='/auth/sso/providers')return reply([{id:providerId,name:'企业测试登录',isDefault:true}]);
  if(endpoint==='/auth/login'&&request.method()==='POST'){signedIn=true;return reply(user);}
  if(endpoint==='/auth/change-password'&&request.method()==='POST'){user.mustChangePassword=false;return reply(user);}
  if(endpoint==='/auth/sso/'+providerId+'/start'&&request.method()==='POST'){
   signedIn=true;
   user.authSource='sso';
   const form=new URLSearchParams(request.postData());
   const target='/auth/sso/complete?returnPath='+encodeURIComponent(form.get('returnPath'));
   return route.fulfill({contentType:'text/html; charset=utf-8',body:'<a href="'+target+'">完成企业登录</a>'});
  }
  if(endpoint==='/scope-tree')return reply({organizations:[{id:'org',name:'组织',status:'Active'}],
   projects:[{id:'project',organizationId:'org',name:'项目',status:'Active'}],environments:[]});
  if(endpoint==='/policies/'+policyId)return reply(policy);
  if(endpoint==='/policies/limits')return reply({maxEntryBytes:262144,allowedVaryHeaders:['Accept'],transformMaxBodyBytes:65536});
  if(endpoint==='/projects/project/policies')return reply({...emptyPage,items:[policy],total:1});
  if(endpoint==='/policies/'+policyId+'/references'||endpoint==='/organizations')return reply(emptyPage);
  throw Error('Unexpected login fixture request '+request.method()+' '+endpoint);
 });
 await page.goto(origin+entry);
 await page.getByRole('heading',{name:'登录管理控制台'}).waitFor();
 return page;
}

async function localLogin(page){
 await page.getByLabel('用户名',{exact:true}).fill('reader');
 await page.getByLabel('密码',{exact:true}).fill('temporary-fixture-password');
 await page.getByRole('button',{name:'登录',exact:true}).click();
}

async function assertDestination(page,path,heading){
 await page.getByRole('main').waitFor();
 assert.equal(page.url(),origin+path);
 await page.getByRole('heading',{name:heading,exact:true}).waitFor();
}

for(const [path,heading] of [['/dashboard','企业工作台'],['/dashboard/','企业工作台'],['/policies','策略中心'],['/policies/','策略中心']]){
 test('local login returns to '+path,async t=>{
  const page=await loginPage(t,path);
  await localLogin(page);
  await assertDestination(page,path,heading);
 });
}

test('first password change preserves the policy editor and its query from the login link',async t=>{
 const path='/policies/new?type=timeout&scope=project';
 const page=await loginPage(t,'/login?returnPath='+encodeURIComponent(path),{mustChangePassword:true});
 await localLogin(page);
 await page.getByRole('heading',{name:'首次登录 · 修改密码'}).waitFor();
 assert.equal(await page.getByRole('main').count(),0);
 await page.getByLabel('当前密码',{exact:true}).fill('temporary-fixture-password');
 await page.getByLabel(/^新密码/).fill('new-fixture-password');
 await page.getByLabel('确认新密码',{exact:true}).fill('new-fixture-password');
 await page.getByRole('button',{name:'确认修改并继续'}).click();
 await assertDestination(page,path,'新建超时策略');
 assert.equal(await page.getByRole('button',{name:'保存工作配置'}).count(),0);
});

for(const path of ['/policies/'+policyId,'/policies/'+policyId+'/']){
 test('enterprise login resumes the requested existing policy at '+path,async t=>{
  const page=await loginPage(t,path);
  await page.getByRole('button',{name:/企业测试登录/}).click();
  await page.getByRole('link',{name:'完成企业登录'}).click();
  await assertDestination(page,path,policy.name);
  assert.equal(await page.getByRole('button',{name:'保存工作配置'}).count(),0);
 });
}
