// Run against a local build preview; API responses are controlled browser fixtures.
// Real PostgreSQL/HTTP concurrency and authorization are covered by CredentialCreateReceiptTests.
import {organizationPermissions} from './identity-fixture.mjs';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import fs from 'node:fs/promises';
import path from 'node:path';

const base=new URL(process.argv[2]);
if(base.protocol!=='http:'||base.hostname!=='127.0.0.1'||!process.env.WEBAPI_PLAYWRIGHT_PACKAGE||!process.env.WEBAPI_CHROMIUM_EXECUTABLE)
 throw new Error('An explicit loopback preview and installed Playwright/Chromium paths are required.');
const output=process.argv[3];
if(output)await fs.mkdir(output,{recursive:false});
const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const browser=await chromium.launch({headless:true,executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});
const org='10000000-0000-0000-0000-000000000001',project='20000000-0000-0000-0000-000000000001',appId='30000000-0000-0000-0000-000000000001';
const keyId='40000000-0000-0000-0000-000000000001',apiKey='fixture-access.fixture-secret';
const coverage={state:'Unknown',coverageStart:null,completeThrough:null,freshness:null,missingNodes:[],gaps:[],reason:'BrowserFixture'};
const results=[];
try{
 for(const scenario of ['first-response','lost-response-retry','retry-after-permission-loss']){
  const context=await browser.newContext({viewport:{width:1400,height:1000}});
  try{
   const page=await context.newPage(),errors=[],requests=[];let credential,authorized=true;
   page.on('pageerror',error=>errors.push(error.message));
   await page.route('**/api/v1/**',async route=>{
    const request=route.request(),url=new URL(request.url()),endpoint=url.pathname.slice('/api/v1'.length);
    const reply=(body,status=200)=>route.fulfill({status,contentType:'application/json',headers:{'Cache-Control':'no-store'},body:JSON.stringify(body)});
    if(endpoint==='/auth/me')return reply({id:'50000000-0000-0000-0000-000000000001',username:'receipt-review',displayName:'回执检查',authSource:'local',...organizationPermissions(org,['app.read',...(authorized?['credential.manage']:[])]),scopes:[{scope:{organizationId:org,projectId:project},accessMode:'read_write'}]});
    if(endpoint==='/auth/csrf')return reply({token:'fixture-csrf'});
    if(endpoint==='/scope-tree')return reply({organizations:[{id:org,name:'测试组织',status:'Active'}],projects:[{id:project,organizationId:org,name:'测试项目',status:'Active'}],environments:[]});
    if(endpoint===`/applications/${appId}`)return reply({application:{id:appId,organizationId:org,projectId:project,name:'回执测试应用',code:'RECEIPT',owner:'测试负责人',status:'Active',revision:credential?2:1},credentials:credential?[credential]:[],permissions:[],runningConfigVersions:{}});
    if(endpoint===`/applications/${appId}/credential-usage`)return reply({window:{from:'2026-11-17T00:00:00Z',to:'2026-11-18T00:00:00Z'},visibleEnvironmentIds:[],page:{items:credential?[{credentialId:keyId,count:null,coverage,lastObservedAt:null,visibleLastUsedAt:null}]:[],total:credential?1:0,page:1,pageSize:50},coverage});
    if(endpoint===`/applications/${appId}/credentials`&&request.method()==='POST'){
     requests.push({key:request.headers()['idempotency-key'],body:request.postDataJSON()});
     if(!authorized)return reply({code:'permission_denied',detail:'缺少操作权限。'},403);
     if(credential)return reply({credential,secret:null,apiKey:null,secretUnavailable:true});
     credential={id:keyId,applicationId:appId,accessKey:'fixture-access',secretLast4:'cret',status:'Active',...request.postDataJSON(),lastUsedAt:null,revokedAt:null,revision:1};
     if(scenario!=='first-response')return route.abort('failed');
     return reply({credential,secret:'fixture-secret',apiKey,secretUnavailable:false});
    }
    throw new Error(`Unexpected browser fixture API: ${request.method()} ${endpoint}`);
   });
   await page.goto(new URL(`/applications/${appId}`,base).href);
   await page.getByRole('button',{name:'＋ 创建凭证',exact:true}).click();
   let editor=page.getByRole('dialog',{name:'创建凭证',exact:true});
   await editor.getByRole('button',{name:'保存',exact:true}).click();
   if(scenario==='first-response'){
    const dialog=page.getByRole('dialog',{name:'一次性凭证',exact:true});
    await dialog.waitFor({state:'visible'});
    assert.equal(await dialog.getByRole('textbox',{name:'一次性 API Key'}).inputValue(),apiKey);
    await dialog.getByRole('button',{name:'我已保存，关闭'}).click();
    await dialog.waitFor({state:'hidden'});
    assert.equal((await page.locator('body').innerText()).includes('fixture-secret'),false);
    assert.equal(requests.length,1);
   }else{
    await editor.getByRole('alert').waitFor({state:'visible'});
    if(scenario==='retry-after-permission-loss')authorized=false;
    const retried=page.waitForResponse(response=>new URL(response.url()).pathname===`/api/v1/applications/${appId}/credentials`&&response.request().method()==='POST');
    await editor.getByRole('button',{name:'保存',exact:true}).click();
    assert.equal((await retried).status(),authorized?200:403);
    if(authorized){
     await page.getByRole('status').filter({hasText:'本次重试只返回记录'}).waitFor({state:'visible'});
     await editor.waitFor({state:'hidden'});
     assert.equal(await page.getByRole('dialog',{name:'一次性凭证'}).count(),0);
     assert.equal((await page.locator('body').innerText()).includes('fixture-secret'),false);
    }else{
     // A refreshed permission set remounts the shell and clears the old editor.
     await editor.waitFor({state:'hidden'});
     const create=page.getByRole('button',{name:'＋ 创建凭证',exact:true});
     await create.waitFor({state:'visible'});
     assert.equal(await create.isDisabled(),true);
     assert.equal(await page.getByRole('dialog',{name:'一次性凭证'}).count(),0);
    }
    assert.equal(requests.length,2);
    assert.ok(requests[0].key);
    assert.deepEqual(requests[1],requests[0]);
    if(output)await page.screenshot({path:path.join(output,scenario+'.png'),fullPage:true});
   }
   assert.deepEqual(errors,[]);
   results.push({scenario,passed:true,createRequests:requests.length,retryKeptKey:requests.length===2});
  }finally{await context.close();}
 }
 if(output)await fs.writeFile(path.join(output,'results.json'),JSON.stringify({kind:'controlled-browser-api-fixture',results},null,2),{flag:'wx'});
 console.log(JSON.stringify({passed:results.length,results},null,2));
}finally{await browser.close();}
