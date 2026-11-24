import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {scenario} from '../../scripts/observability-scenario.mjs';

const fixtureActors = ['e2e-admin', 'e2e-approver', 'e2e-security', 'e2e-reader'];

function fixture(t, contextOverrides = {}) {
 const root = fs.mkdtempSync(path.join(os.tmpdir(), 'observability-scenario-test-'));
 const project = 'webapi-enterprise-e2e-observability-test';
 const directory = path.join(root, project);
 fs.mkdirSync(directory);
 t.after(() => fs.rmSync(root, {recursive:true, force:true}));
 const env = {WEBAPI_E2E_DIRECTORY:directory, WEBAPI_E2E_PROJECT:project};
 for (const [name, value] of Object.entries(env)) {
  const original = process.env[name];
  process.env[name] = value;
  t.after(() => { if (original === undefined) delete process.env[name]; else process.env[name] = original; });
 }
 const passwordFiles = Object.fromEntries(fixtureActors.map(name => {
  fs.writeFileSync(path.join(directory, 'password-'+name), name+'-personal-synthetic', {mode:0o600});
  return [name, '/workspace/.test/'+project+'/password-'+name];
 }));
 fs.writeFileSync(path.join(directory, 'password'), 'shared-bootstrap-synthetic', {mode:0o600});
 fs.writeFileSync(path.join(directory, 'credential'), 'synthetic-key', {mode:0o600});
 fs.writeFileSync(path.join(directory, 'context.json'), JSON.stringify({
  environmentId:'env-1', apiId:'api-1', clusterId:'cluster-1', passwordFiles, ...contextOverrides,
 }));
 fs.writeFileSync(path.join(directory, 'observability-context.json'), JSON.stringify({
  endpoints:{control_plane:'http://fixture.invalid'}, settings:{},
 }));
 return directory;
}

function mockServer(t, operation, identity = {mustChangePassword:false}) {
 const logins = [];
 t.mock.method(globalThis, 'fetch', async (address, options = {}) => {
  const {pathname, search} = new URL(address);
  const endpoint = pathname.replace(/^\/api\/v1/, '') + search;
  const body = options.body === undefined ? undefined : JSON.parse(options.body);
  if (endpoint === '/auth/csrf') return Response.json({token:'synthetic-token'});
  if (endpoint === '/auth/login') {
   logins.push({body, previousSession:options.headers.Cookie});
   return Response.json(identity, {headers:{'Set-Cookie':'WebApi.Session=synthetic-session; Path=/'}});
  }
  if (endpoint === '/health/live') return Response.json({});
  return Response.json(await operation(endpoint, options.method ?? 'GET', body, options.headers));
 });
 return logins;
}

test('prepared fixture actors use their own password on first client login and after reauthentication', async (t) => {
 fixture(t);
 const logins = mockServer(t, endpoint => assert.fail('Unexpected endpoint '+endpoint));
 const s = await scenario();
 await s.reauthenticate();
 await s.loginActor('e2e-reader');
 assert.deepEqual(logins.map(({body}) => [body.username, body.password]), [
  ...fixtureActors.slice(0, 3), ...fixtureActors,
 ].map(name => [name, name+'-personal-synthetic']));
 assert(logins.every(({previousSession}) => previousSession === ''));
 await assert.rejects(() => s.loginActor('any-other-user'), /fixture actor/i);
});

test('client refuses to proceed with a fixture account still subject to first password change', async (t) => {
 fixture(t);
 const logins = mockServer(t, endpoint => assert.fail('Unexpected endpoint '+endpoint), {mustChangePassword:true});
 await assert.rejects(scenario, /initial password change/i);
 assert.equal(logins.length, 1);
});

test('client does not reuse bootstrap material when the prepared actor password reference is absent', async (t) => {
 fixture(t, {passwordFiles:{}});
 const logins = mockServer(t, endpoint => assert.fail('Unexpected endpoint '+endpoint));
 await assert.rejects(scenario, /fixture password file/i);
 assert.equal(logins.length, 0);
});

test('release follows route pagination, frozen review preconditions and ordered approvals', async (t) => {
 fixture(t);
 let step = 0;
 let updated = false;
 let approved = false;
 let published = false;
 const releasePath = '/releases/r1';
 const candidateHash = 'a'.repeat(64);
 const releases = '/environments/env-1/releases';
 const versions = '/apis/api-1/versions';
 const routes = '/environments/env-1/routes';
 const approvals = [
  {stepOrder:1, roleCode:'ApiApprover', actorId:'actor-1', status:'Approved'},
  {stepOrder:2, roleCode:'SecurityReviewer', actorId:'actor-2', status:'Approved'},
 ];
 mockServer(t, (endpoint, method, body, headers) => {
  if (method === 'GET' && endpoint === versions) return [{id:'v1', version:'1.0.0'}];
  if (method === 'POST' && endpoint === versions) return {id:'v2'};
  if (endpoint === '/clusters/cluster-1') return {environmentId:'env-1', destinations:[{id:'d1', name:'test-backend', revision:4}]};
  if (endpoint === '/destinations/d1') {assert.equal(headers['If-Match'], '"4"'); return {};}
  if (endpoint === routes+'?page=1&pageSize=100') return {total:101, page:1, pageSize:100, items:Array.from({length:100}, (_, i) => ({id:'other-'+i, routeName:'Orders', apiVersionId:'unrelated'}))};
  if (endpoint === routes+'?page=2&pageSize=100') return {total:101, page:2, pageSize:100, items:[{id:'route-ours', apiVersionId:'v1', routeName:'Orders', clusterId:'cluster-1', revision:8}]};
  if (endpoint === '/routes/route-ours') {assert.equal(method, 'PUT'); assert.equal(headers['If-Match'], '"8"'); assert.equal(body.apiVersionId, 'v2'); updated = true; return {};}
  if (endpoint === '/environments/env-1') return {desiredConfigVersion:3};
  const revisions = [{type:'version', id:'v2', revision:1}, {type:'route', id:'route-ours', revision:9}];
  if (endpoint === releases+'/preview') {assert.deepEqual(body,{baseConfigVersion:3, versionIds:['v2']}); assert(updated); return {resourceRevisions:revisions};}
  if (endpoint === releases) {assert.deepEqual(body.resourceRevisions,revisions); return {id:'r1'};}
  if (endpoint === releasePath+'/submit') {step = 1; return {candidateHash, state:'WaitingApproval'};}
  if (endpoint === releasePath+'/approve') {
   assert.deepEqual([body.expectedStepOrder, body.expectedCandidateHash], [step,candidateHash]);
   if (++step === 3) approved = true;
   return {candidateHash, state:approved?'Ready':'WaitingApproval', approvalSteps:approvals.slice(0,step-1)};
  }
  if (endpoint === releasePath+'/publish') {assert(approved); published = true; return {};}
  if (endpoint === releasePath) return published
   ? {state:'Succeeded', targets:[{acknowledged:true},{acknowledged:true}]}
   : {candidateHash, state:'WaitingApproval', approvalEligibility:{canAct:true,currentStepOrder:step}};
  assert.fail('Unexpected endpoint '+method+' '+endpoint);
 });
 const s = await scenario();
 const release = await s.publish('B');
 assert.equal(release.state, 'Succeeded');
 assert.equal(release.targets.length, 2);
});
