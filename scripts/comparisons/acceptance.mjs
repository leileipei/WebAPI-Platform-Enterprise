import {cleanupAcceptanceRun, withAcceptanceSignals} from '../acceptance-credentials.mjs';
import fs from 'node:fs/promises';import path from 'node:path';import assert from 'node:assert/strict';import {randomUUID,randomBytes} from 'node:crypto';import {buildRelease} from '../runtime/build.mjs';import {safeFile,ownedDirectory,writePrivate} from '../runtime/state.mjs';import {createComparisonWorkspace,validateComparisonDirectory,validateComparisonResources,validateComparisonCleanupResources,validateComparisonArchive,finalizeComparisonEvidence,secretNames,sha,assertNoSecrets,toolManifest,verifyToolManifest,verifyComparisonUiProof} from './evidence.mjs';import {runComparisonScenario} from './scenario.mjs';
import {execFileAndWait as exec} from '../runtime/exec-file.mjs';
const repository=path.resolve(import.meta.dirname,'../..');
const applicationNames={'control-plane':'ControlPlane',worker:'Worker','gateway-a':'Gateway','gateway-b':'Gateway','backend-a':'TestBackend','backend-b':'TestBackend',console:'ConsoleHost'};
const comparisonVolumes = ['pg', 'nuget', 'lkg-a', 'lkg-b', 'evidence'];
function composeArguments(directory,project){return['compose','-p',project,'-f',path.join(directory,'source/deploy/compose.e2e.yml'),'-f',path.join(directory,'overlay.json'),'-f',path.join(directory,'volumes.yml')];}
async function runnerCommand(directory, env, binary, args, options = {}) {
 try {
  const {stdout, stderr} = await exec(binary, args, {cwd: directory, env, maxBuffer: 32 * 1024 * 1024, ...options});
  if (stdout || stderr) await fs.appendFile(path.join(directory, 'operations.log'), stdout + stderr, {mode: 0o600});
  return stdout.trim();
 } catch (error) {
  options.signal?.throwIfAborted();
  await fs.appendFile(path.join(directory, 'operations.log'), (error.stdout || '') + (error.stderr || ''), {mode: 0o600});
  throw Error('Isolated comparison command failed; see private operations.log');
 }
}
async function readPrivateJson(directory, name) {
 const file = path.join(directory, name);
 await safeFile(file);
 return JSON.parse(await fs.readFile(file, 'utf8'));
}

async function ownedComparisonIdentity(root, directory) {
 assert.equal(path.dirname(directory), path.join(path.resolve(root), '.runtime'));
 assert.equal(await ownedDirectory(directory), directory, 'Comparison directory must be canonical');
 const marker = await readPrivateJson(directory, 'owner.json');
 validateComparisonDirectory(directory, marker);
 const {project, owner} = marker;
 assert.deepEqual(marker, {project, owner}, 'Comparison owner marker mismatch');
 return {project, owner, directory};
}

function verifyEvidenceVolume(labels, nonce) {
 assert.equal(labels['com.docker.compose.volume'], 'evidence');
 assert(nonce && labels['com.webapi.migration.evidence-nonce'] === nonce, 'Migration evidence volume was not created for this run');
}

async function teardownComparison({directory, project, owner, environment, evidenceNonce}) {
 const env = {...process.env, WEBAPI_E2E_PROJECT: project, WEBAPI_E2E_DIRECTORY: directory,
  WEBAPI_E2E_RELATIVE_DIR: '.runtime/' + project, WEBAPI_ENVIRONMENT_ID: environment.WEBAPI_ENVIRONMENT_ID};
 const command = (...args) => runnerCommand(directory, env, 'docker', args);
 const list = async kind => {
  const args = kind === 'container' ? ['ps', '-aq'] : [kind, 'ls', '-q'];
  const ids = await command(...args, '--filter', 'label=com.docker.compose.project=' + project);
  return ids.split('\n').filter(Boolean);
 };
 const kinds = ['container', 'volume', 'network'];
 const resources = [];
 for (const kind of kinds) {
  for (const id of await list(kind)) {
   const inspect = kind === 'container' ? ['inspect', '--format', '{{json .Config.Labels}}'] : [kind, 'inspect', '--format', '{{json .Labels}}'];
   const labels = JSON.parse(await command(...inspect, id));
   resources.push({kind, labels});
   if (kind === 'volume') assert(comparisonVolumes.some(name => id === project + '_' + name), 'Unexpected volume');
   if (kind === 'volume' && id === project + '_evidence') verifyEvidenceVolume(labels, evidenceNonce);
  }
 }
 validateComparisonCleanupResources({project, owner}, resources);
 await command(...composeArguments(directory, project), 'down', '--volumes', '--remove-orphans');
 const receipt = {};
 for (const kind of kinds) receipt[kind + 'sRemaining'] = (await list(kind)).length;
 if (Object.values(receipt).some(count => count !== 0)) return {...receipt, failed: true, error: 'Comparison resources remain after cleanup.'};
 return receipt;
}

async function guardedCleanup(root, {directory, owner, project}, resourceContext) {
 try {
  assert.deepEqual(await ownedComparisonIdentity(root, directory), {project, owner, directory});
  return await cleanupAcceptanceRun(directory, secretNames, () => {
   assert(resourceContext, 'Comparison review context is unavailable for resource teardown');
   // Retained runs from earlier revisions have no composeAttempted flag.
   return resourceContext.composeAttempted === false ? {} : teardownComparison(resourceContext);
  });
 } catch {
  return {failed: true, secretsRemaining: null, error: 'Comparison workspace cleanup could not be verified.'};
 }
}

async function removeReviewContext(directory) {
 for (const name of ['context.json', 'review-context.json']) await fs.rm(path.join(directory, name), {force: true});
}

async function recordFailure(publicDirectory, {project, sourceRevision}, error, cleanup) {
 error.cleanup = cleanup;
 const receipt = {complete: false, project, sourceRevision, message: 'Comparison acceptance failed.', cleanup};
 try {
  await fs.writeFile(path.join(publicDirectory, 'last-failure.json'), JSON.stringify(receipt, null, 2) + '\n');
 } catch {
  error.failureReceiptWritten = false;
 }
}

async function readReviewProof(root, context) {
 await verifyToolManifest(root, context.tools);
 const proof = await readPrivateJson(context.directory, 'result.json');
 let suppliedUi;
 try {
  suppliedUi = await readPrivateJson(context.directory, 'ui-proof.json');
 } catch (error) {
  if (error.code !== 'ENOENT') throw error;
 }
 if (suppliedUi) proof.ui = await verifyComparisonUiProof(root, suppliedUi);
 return proof;
}

export async function cleanupComparisonAcceptance({root = repository, directory}) {
 directory = path.resolve(directory);
 const identity = await ownedComparisonIdentity(root, directory);
 let context, readError;
 try {
  context = await readPrivateJson(directory, 'review-context.json');
 } catch (error) {
  readError = error;
 }
 const claims = [[context?.directory, directory], [context?.owner, identity.owner], [context?.project, identity.project],
  [context?.environment?.WEBAPI_E2E_DIRECTORY, directory], [context?.environment?.WEBAPI_E2E_PROJECT, identity.project]];
 for (const [value, expected] of claims) {
  if (value !== undefined) assert.equal(value, expected, 'Conflicting comparison review ownership');
 }
 const publicDirectory = path.join(root, 'docs/evidence/version-comparison');
 let cleanupReceipt, resourceContext = null;
 const cleanup = () => cleanupReceipt ??= guardedCleanup(root, identity, resourceContext);
 try {
  if (readError) throw readError;
  assert(claims.every(([value]) => value !== undefined), 'Incomplete comparison review context');
  resourceContext = context;
  const proof = await readReviewProof(root, context);
  proof.cleanup = await cleanup();
  if (proof.cleanup.failed) throw Error(proof.cleanup.error);
  const finalized = finalizeComparisonEvidence(proof);
  await fs.writeFile(path.join(publicDirectory, 'verification.json'), JSON.stringify(finalized, null, 2) + '\n');
  await removeReviewContext(directory);
  return finalized;
 } catch (error) {
  await recordFailure(publicDirectory, identity, error, await cleanup());
  throw error;
 }
}

export async function runComparisonAcceptance({root=repository,revision,retainForReview=false,signal}){
 root=path.resolve(root);assert(revision&&!revision.startsWith('-'),'Explicit immutable revision required');const sourceRevision=(await exec('git',['rev-parse',revision+'^{commit}'],{cwd:root,signal})).stdout.trim();assert.match(sourceRevision,/^[a-f0-9]{40}$/);const entries=(await exec('git',['ls-tree','-r','--name-only',sourceRevision],{cwd:root,signal})).stdout.trim().split('\n');validateComparisonArchive(entries);
 const publicDirectory = path.join(root, 'docs/evidence/version-comparison');
 await fs.mkdir(publicDirectory, {recursive: true});
 const {owner, project, directory} = await createComparisonWorkspace(root);
 const environment = {WEBAPI_E2E_PROJECT: project, WEBAPI_E2E_DIRECTORY: directory,
  WEBAPI_E2E_RELATIVE_DIR: '.runtime/' + project, WEBAPI_ENVIRONMENT_ID: randomUUID()};
 const env = {...process.env, ...environment};
 const command = (binary, args, options) => runnerCommand(directory, env, binary, args, {...options, signal});
 let context = {directory, owner, project, environment, composeAttempted: false};
 let cleanupReceipt;
 const cleanup = () => cleanupReceipt ??= guardedCleanup(root, context, context);
 try {
  signal?.throwIfAborted();
  await fs.writeFile(path.join(directory, 'review-context.json'), JSON.stringify(context), {mode: 0o600, flag: 'wx'});
  const archive=path.join(directory,'source.tar'),source=path.join(directory,'source');await fs.mkdir(source,{mode:0o700});await command('git',['-C',root,'archive','--output',archive,sourceRevision]);const archiveHash=sha(await fs.readFile(archive));await command('tar',['-xf',archive,'-C',source]);const tools=await toolManifest(source);assert.deepEqual(await toolManifest(root),tools,'Runner tools differ from immutable source');const image=await buildRelease({root,revision:sourceRevision,directory:path.join(directory,'build'),nugetSeedVolume:'webapi-enterprise-core-test_nuget-test',signal});signal?.throwIfAborted();await fs.writeFile(path.join(publicDirectory,'source-manifest.json'),JSON.stringify({sourceRevision,archiveHash,image,toolManifestHash:tools.sha256},null,2)+'\n');
  const labels={'com.webapi.comparisons.owner':owner},overlay={services:{},volumes:{},networks:{default:{labels}},secrets:{password:{file:path.join(directory,'password')}}};for(const service of ['postgres','redis','sdk',...Object.keys(applicationNames)])overlay.services[service]={labels};for (const name of comparisonVolumes) overlay.volumes[name] = {labels};
  const evidenceNonce = randomUUID();
  overlay.volumes.evidence.labels = {...labels, 'com.webapi.migration.evidence-nonce': evidenceNonce};
  Object.assign(overlay.services.sdk, {
   image: image.imageId, user: '10001:10001', working_dir: '/app',
   secrets: ['postgres-password', 'password'], environment: {WEBAPI_BOOTSTRAP_PASSWORD_FILE: '/run/secrets/password'}
  });
  overlay.secrets['login-hmac'] = {file: path.join(directory, 'login-hmac')};
  for(const[service,name]of Object.entries(applicationNames)){Object.assign(overlay.services[service],{image:image.imageId,user:'10001:10001',working_dir:'/app',command:['dotnet',`/app/${service.startsWith('gateway')?'gateway':service.startsWith('backend')?'demo-backend':service==='console'?'console-host':service}/WebApi.${name}.dll`]});if(['control-plane','gateway-a','gateway-b','console'].includes(service))overlay.services[service].ports=['127.0.0.1::8080'];}
  Object.assign(overlay.services['control-plane'],{secrets:['postgres-password','node-a','node-b','login-hmac'],environment:{Authentication__LoginProtection__HmacSecretFile:'/run/secrets/login-hmac',Authentication__CookieName:'WebApi.Comparisons.'+owner+'.Session',Antiforgery__CookieName:'WebApi.Comparisons.'+owner+'.Csrf'}});overlay.services.console.environment={Console__DistDirectory:'/app/console-dist'};
  await fs.writeFile(path.join(directory,'overlay.json'),JSON.stringify(overlay,null,2),{mode:0o600});const override='services:\n'+Object.keys(applicationNames).map(s=>`  ${s}:\n    volumes: !override ${s.startsWith('gateway')?'[lkg-'+s.at(-1)+':/lkg]':'[]'}\n`+(['control-plane','gateway-a','gateway-b','console'].includes(s)?'    ports: !override ["127.0.0.1::8080"]\n':'')).join('')+'  sdk:\n    volumes: !override [evidence:/evidence]\n';await fs.writeFile(path.join(directory,'volumes.yml'),override,{mode:0o600});const args=composeArguments(directory,project),compose=(...parts)=>command('docker',[...args,...parts]);context={...context,tools,image,evidenceNonce};
  signal?.throwIfAborted();
  for (const name of secretNames) await fs.writeFile(path.join(directory, name), randomBytes(32).toString(name === 'login-hmac' ? 'base64' : 'base64url'), {mode: 0o600, flag: 'wx'});
  await writePrivate(path.join(directory, 'review-context.json'), {...context, composeAttempted: true});
  context.composeAttempted = true;
  await compose('up', '-d', '--wait', 'postgres', 'redis');
  await compose('create', 'sdk');
  const evidenceVolume = project + '_evidence';
  const evidenceLabels = JSON.parse(await command('docker', ['volume', 'inspect', '--format', '{{json .Labels}}', evidenceVolume]));
  validateComparisonResources({project, owner}, [evidenceLabels]);
  verifyEvidenceVolume(evidenceLabels, evidenceNonce);
  const sdk = JSON.parse(await fs.readFile(path.join(source, 'deploy/images.lock.json'))).sdk;
  await command('docker', ['run', '--rm', '--network', 'none', '--user', '0:0', '--cap-drop', 'ALL', '--cap-add', 'CHOWN', '--cap-add', 'FOWNER',
   '-e', 'WEBAPI_MIGRATION_EVIDENCE_NEW_VOLUME=1', '-v', evidenceVolume + ':/evidence',
   '-v', path.join(source, 'deploy/runtime/init-migration-evidence.sh') + ':/init-evidence.sh:ro', sdk, 'bash', '/init-evidence.sh', '/evidence']);
  await compose('run', '--rm', '--no-deps', '--entrypoint', 'dotnet', 'sdk', '/app/migrator/WebApi.Migrator.dll', '--bootstrap', '--seed-catalog');
  await compose('up','-d','control-plane');const endpoint=async service=>'http://'+await compose('port',service,'8080');env.WEBAPI_E2E_CONTROL_PLANE_URL=await endpoint('control-plane');await command(process.execPath,[path.join(source,'scripts/wait-http.mjs'),env.WEBAPI_E2E_CONTROL_PLANE_URL+'/health/live']);await command(process.execPath,[path.join(source,'scripts/prepare-e2e.mjs')]);const fixture=JSON.parse(await fs.readFile(path.join(directory,'context.json'),'utf8'));env.WEBAPI_ENVIRONMENT_ID=environment.WEBAPI_ENVIRONMENT_ID=fixture.environmentId;await compose('up','-d','--force-recreate','control-plane');env.WEBAPI_E2E_CONTROL_PLANE_URL=await endpoint('control-plane');await compose('create','gateway-a','gateway-b');for(const name of ['lkg-a','lkg-b']){const volume=project+'_'+name;validateComparisonResources({project,owner},[JSON.parse(await command('docker',['volume','inspect','--format','{{json .Labels}}',volume]))]);await command('docker',['run','--rm','--user','0:0','-v',volume+':/comparison-lkg',sdk,'bash','-c','chown 10001:10001 /comparison-lkg && chmod 700 /comparison-lkg']);}await compose('up','-d',...Object.keys(applicationNames));await command(process.execPath,[path.join(source,'scripts/wait-http.mjs'),env.WEBAPI_E2E_CONTROL_PLANE_URL+'/health/live']);await command(process.execPath,[path.join(source,'scripts/wait-nodes.mjs')]);const endpoints={control_plane:env.WEBAPI_E2E_CONTROL_PLANE_URL,console:await endpoint('console'),gateway_a:await endpoint('gateway-a'),gateway_b:await endpoint('gateway-b')};context={...context,endpoints,context:fixture};await writePrivate(path.join(directory,'review-context.json'),context);const result=await runComparisonScenario({...context,compose,signal});signal?.throwIfAborted();result.toolManifestHash=tools.sha256;await verifyToolManifest(root,tools);assertNoSecrets(result,await Promise.all([...secretNames,'credential'].map(n=>fs.readFile(path.join(directory,n),'utf8'))));await fs.writeFile(path.join(directory,'result.json'),JSON.stringify(result,null,2),{mode:0o600});
  if (!retainForReview) {
   result.cleanup = await cleanup();
   if (result.cleanup.failed) throw Error(result.cleanup.error);
  }
  const finalized = finalizeComparisonEvidence(result);
  const resultPath = path.join(publicDirectory, 'verification.json');
  await fs.writeFile(resultPath, JSON.stringify(finalized, null, 2) + '\n');
  if (!retainForReview) await removeReviewContext(directory);
  signal?.throwIfAborted();
  console.log(retainForReview ? 'Comparison review: ' + endpoints.console : 'Comparison API scenario verified; UI proof pending');
  return {sourceRevision, image, resultPath, reviewContextPath: retainForReview ? path.join(directory, 'review-context.json') : null};
 } catch (error) {
  await recordFailure(publicDirectory, {project, sourceRevision}, error, await cleanup());
  throw error;
 }
}
if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
 const args = process.argv.slice(2), value = flag => args[args.indexOf(flag) + 1];
 await withAcceptanceSignals(async signal => {
  if (args.includes('--cleanup')) console.log(JSON.stringify(await cleanupComparisonAcceptance({directory: path.resolve(value('--cleanup'))})));
  else if (args.includes('--revision')) console.log(JSON.stringify(await runComparisonAcceptance({revision: value('--revision'), retainForReview: args.includes('--review'), signal})));
  else throw Error('Use --revision <commit> --review or --cleanup <owned-private-directory>');
 });
}
