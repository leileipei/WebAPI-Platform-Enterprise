// Run node --test with WEBAPI_CONSOLE_DIST, WEBAPI_PLAYWRIGHT_PACKAGE and WEBAPI_CHROMIUM_EXECUTABLE.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join} from 'node:path';
import {after, before, test} from 'node:test';

const {chromium} = createRequire(process.env.WEBAPI_PLAYWRIGHT_PACKAGE)('playwright');
const dist = process.env.WEBAPI_CONSOLE_DIST;
const id = n => `20000000-0000-0000-0000-${String(n).padStart(12, '0')}`;
const org = id(1), selectedProject = id(2), appProject = id(3), appId = id(4), environmentId = id(5);
const listPath = '/applications', detailPath = `${listPath}/${appId}`;
let browser;

before(async () => {
  browser = await chromium.launch({executablePath:process.env.WEBAPI_CHROMIUM_EXECUTABLE});
});
after(async () => { await browser?.close(); });

async function openApplications(t, {path = detailPath, projectId = null, omitProject = false,
  withProjects = true, grantProjectId = null, currentRevision = 7} = {}) {
  const page = await browser.newPage();
  t.after(() => page.close());
  page.setDefaultTimeout(5000);
  let application = {id:appId, organizationId:org, ...(!omitProject && {projectId}), code:'SAMPLE',
    name:'样例应用', owner:'原负责人', status:'Active', revision:7, activation:'WorkingConfiguration'};
  const writes = [];
  await page.route('**/*', async route => {
    const request = route.request(), pathname = new URL(request.url()).pathname;
    const reply = (json, status = 200) => route.fulfill({status,json});
    if (pathname.startsWith('/assets/')) return route.fulfill({path:join(dist,pathname.slice(1))});
    if ([listPath, detailPath].includes(pathname)) return route.fulfill({path:join(dist,'index.html')});
    if (pathname === '/api/v1/auth/me') return reply({id:id(6), username:'tester', displayName:'测试用户',
      authSource:'local', permissions:['app.read','app.write'],rolePermissions:[{organizationId:org,permissions:['app.write']}],
      scopes:[{scope:{organizationId:org,projectId:grantProjectId},accessMode:'read_write'}]});
    if (pathname === '/api/v1/auth/csrf') return reply({token:'test-csrf-token'});
    if (pathname === '/api/v1/scope-tree') return reply({organizations:[{id:org,name:'组织',status:'Active'}],
      projects:withProjects?[{id:selectedProject,organizationId:org,name:'当前项目',status:'Active'},
        {id:appProject,organizationId:org,name:'应用项目',status:'Active'}]:[],
      environments:withProjects?[{id:environmentId,projectId:selectedProject,name:'测试环境'}]:[]});
    if (pathname === `/api/v1${detailPath}/credential-usage`) return reply({coverage:{state:'Unknown',
      coverageStart:null,completeThrough:null,missingNodes:[],gaps:[],reason:'BrowserFixture'},
      page:{items:[],total:0,page:1,pageSize:50}});
    if (pathname === `/api/v1${listPath}` && request.method() === 'GET')
      return reply({items:[application],total:1,page:1,pageSize:50});
    if (pathname === `/api/v1${detailPath}` && request.method() === 'GET')
      return reply({application,credentials:[],permissions:[],runningConfigVersions:{}});
    if (pathname === `/api/v1${listPath}` || pathname === `/api/v1${detailPath}`) {
      const body = request.postDataJSON(), headers = request.headers(), method = request.method();
      writes.push({body,method,etag:headers['if-match'],key:headers['idempotency-key'],csrf:headers['x-csrf-token']});
      if (method === 'PUT' && headers['if-match'] !== `"${currentRevision}"`) {
        application = {...application,revision:currentRevision};
        return reply({code:'stale_revision',detail:'修订已变更'},412);
      }
      if (method === 'PUT' && (body.organizationId !== org || body.projectId !== projectId))
        return reply({code:'application_scope_immutable',detail:'应用不能直接移动到其他组织或项目。'},422);
      application = {...application,...body,revision:++currentRevision};
      return reply(application);
    }
    throw new Error(`Unexpected fixture request: ${request.method()} ${pathname}`);
  });
  await page.goto(`http://127.0.0.1${path}`);
  if (withProjects) await page.getByRole('combobox',{name:'当前项目'}).getByRole('option',{name:'当前项目'}).waitFor({state:'attached'});
  const action = name => page.getByRole('button',{name,exact:true});
  const dialog = page.getByRole('dialog');
  async function save(expectedStatus, method = 'PUT') {
    const response = page.waitForResponse(item => item.request().method() === method);
    await dialog.getByRole('button',{name:'保存',exact:true}).click();
    const result = await response;
    assert.equal(result.status(),expectedStatus,JSON.stringify(writes.at(-1)?.body));
    if (result.ok()) await dialog.waitFor({state:'hidden'});
  }
  return {page,action,dialog,save,writes};
}

function assertWrite(write, projectId, etag = '"7"', method = 'PUT') {
  assert.equal(write.method,method);
  assert.equal(write.body.organizationId,org);
  assert.equal(write.body.projectId,projectId);
  assert.equal(write.etag,method === 'PUT' ? etag : undefined);
  assert.ok(write.key);
  assert.equal(write.csrf,'test-csrf-token');
}

test('renaming a shared application from the list keeps its organization-level scope', async t => {
  const {page,action,dialog,save,writes} = await openApplications(t,{path:listPath});
  await page.getByRole('link',{name:'详情与接入'}).click();
  await action('编辑应用').click();
  await dialog.getByLabel('应用名称').fill('更名后的应用');
  await save(200);
  assertWrite(writes[0],null);
  assert.equal(writes[0].body.name,'更名后的应用');
  await page.getByRole('heading',{name:'更名后的应用'}).waitFor();
});

for (const {label,value,field} of [{label:'负责人',value:'新负责人',field:'owner'},
  {label:'状态',value:'Disabled',field:'status'}]) {
  test(`changing ${field} through the detail editor keeps a shared application organization-level`, async t => {
    const {action,dialog,save,writes} = await openApplications(t);
    await action('编辑应用').click();
    if (field === 'status') await dialog.getByLabel(label).selectOption(value);
    else await dialog.getByLabel(label).fill(value);
    await save(200);
    assertWrite(writes[0],null);
    assert.equal(writes[0].body[field],value);
  });
}

test('a shared application returned without a projectId saves with explicit null', async t => {
  const {action,dialog,save,writes} = await openApplications(t,{omitProject:true});
  await action('编辑应用').click();
  await dialog.getByLabel('应用名称').fill('历史应用');
  await save(200);
  assertWrite(writes[0],null);
});

test('an existing project application keeps its project even when the workspace selects another', async t => {
  const {action,dialog,save,writes} = await openApplications(t,{projectId:appProject});
  await action('编辑应用').click();
  await dialog.getByLabel('应用名称').fill('项目应用');
  await save(200);
  assertWrite(writes[0],appProject);
});

for (const withProjects of [true,false]) {
  test(`creation uses the workspace default ${withProjects?'project':'organization'}`, async t => {
    const {action,dialog,save,writes} = await openApplications(t,{path:listPath,withProjects});
    await action('＋ 新建应用').click();
    await dialog.getByLabel('应用编码').fill('NEW');
    await dialog.getByLabel('应用名称').fill('新建应用');
    await dialog.getByLabel('负责人').fill('测试负责人');
    await save(200,'POST');
    assertWrite(writes[0],withProjects?selectedProject:null,undefined,'POST');
  });
}

test('a stale revision still requires reviewing the latest revision before saving a shared application', async t => {
  const {action,dialog,save,writes} = await openApplications(t,{currentRevision:8});
  await action('编辑应用').click();
  await dialog.getByLabel('应用名称').fill('修订后应用');
  await save(412);
  await dialog.getByRole('alert').waitFor();
  assert.equal(await dialog.getByRole('button',{name:'保存',exact:true}).isDisabled(),true);
  await dialog.getByRole('button',{name:'查看最新内容'}).click();
  await dialog.getByRole('button',{name:'保持我的输入，采用最新修订'}).click();
  await save(200);
  assertWrite(writes[0],null,'"7"');
  assertWrite(writes[1],null,'"8"');
  assert.notEqual(writes[0].key,writes[1].key);
  assert.equal(writes[1].body.name,'修订后应用');
});

test('a project grant does not allow editing a shared application', async t => {
  const {page,action,writes} = await openApplications(t,{grantProjectId:selectedProject});
  await page.getByRole('heading',{name:'样例应用'}).waitFor();
  assert.equal(await action('编辑应用').isDisabled(),true);
  assert.deepEqual(writes,[]);
});
