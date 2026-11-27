import {after,before,test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';

const {chromium}=createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
let browser;
before(async()=>{browser=await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});});
after(async()=>{await browser?.close();});

async function open(t,path,timeout) {
  const page=await browser.newPage();
  t.after(()=>page.close());
  const errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  t.after(()=>assert.deepEqual(errors,[]));
  const permissions=['pipeline.read','pipeline.manage','project.read','project.write','environment.read'];
  const envs=['dev','prod'].map((id,i)=>({id,projectId:'project',name:id,status:'Active',isProduction:i===1,releasePolicyId:i===1?'flow':null}));
  const definition={name:'标准流水线',description:'',stages:envs.map((e,i)=>({order:i+1,environmentId:e.id,requiredTestTypes:[],evidenceValidityMinutes:60,waitTimeoutMinutes:80,approvalFlowId:e.releasePolicyId}))};
  const detail={id:'pipeline',projectId:'project',name:definition.name,status:'Active',revision:2,draft:definition,gatewayAckTimeoutSeconds:timeout};
  await page.route('**/*',async route=>{
    const path=new URL(route.request().url()).pathname;
    if(path.startsWith('/assets/'))return route.fulfill({path:join(process.env.WEBAPI_CONSOLE_DIST,path.slice(1))});
    if(!path.startsWith('/api/v1/'))return route.fulfill({path:join(process.env.WEBAPI_CONSOLE_DIST,'index.html')});
    const reply=body=>route.fulfill({json:body});
    switch(path.slice(7)) {
      case '/auth/me':return reply({id:'u',username:'manager',displayName:'管理员',permissions,scopes:[{scope:{organizationId:'org',projectId:'project'},accessMode:'read_write'}]});
      case '/auth/csrf':return reply({token:'test-csrf'});
      case '/scope-tree':return reply({organizations:[{id:'org',name:'组织'}],projects:[{id:'project',organizationId:'org',name:'项目'}],environments:envs});
      case '/projects/project/release-pipelines':return reply({items:[],page:1,size:50,total:0,coverage:'Full',gatewayAckTimeoutSeconds:timeout});
      case '/release-pipelines/pipeline':return reply(detail);
      case '/release-pipelines/pipeline/versions':return reply([{id:'fixed',versionNo:1,createdAt:'2026-11-01',content:{definition,profiles:[]}}]);
      case '/projects/project/delivery-policy':return reply({mode:'Legacy',revision:1});
      default:return reply({});
    }
  });
  await page.goto('http://127.0.0.1'+path);
  return page;
}

test('draft, published version and edit stages display the configured ACK window',async t=>{
  const page=await open(t,'/delivery/pipelines/pipeline',30);
  await page.getByRole('button',{name:'编辑草稿'}).waitFor();
  const window=page.getByText(/Gateway 节点确认时限/);
  assert.equal(await window.count(),4);
  for(const text of await window.allTextContents())assert.match(text,/30 秒/);
  await page.getByRole('button',{name:'编辑草稿'}).click();
  const edited=page.getByRole('dialog').getByText(/Gateway 节点确认时限/);
  await edited.first().waitFor({timeout:5000});
  assert.equal(await edited.count(),2);
  for(const text of await edited.allTextContents())assert.match(text,/30 秒/);
});

for (const timeout of [47,undefined]) test(`new pipeline reads the ACK window from the directory (${timeout??'older server'})`,async t=>{
  const page=await open(t,'/delivery/pipelines',timeout);
  await page.getByRole('button',{name:'新建流水线'}).click();
  const stages=page.getByRole('dialog').getByText(/Gateway 节点确认时限/);
  await stages.first().waitFor({timeout:5000});
  assert.equal(await stages.count(),2);
  for(const text of await stages.allTextContents()){
    if(timeout)assert.match(text,/47 秒/);
    else{assert.match(text,/未提供/);assert.doesNotMatch(text,/\d+ 秒/);}
  }
});
