// Test-only provisioning. Existing deployments and retained runs are never reused.
import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {randomBytes,randomUUID,createHash} from 'node:crypto';
import assert from 'node:assert/strict';
import {sourceHashes,verifySourceManifest} from './project-counts-guards.mjs';
const exec=promisify(execFile),root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const [mode,argument,portArgument,subnet]=process.argv.slice(2);
const run=async(command,args,options={})=>(await exec(command,args,{cwd:root,maxBuffer:8*1024*1024,...options})).stdout;
const write=async(file,value)=>fs.writeFile(file,typeof value==='string'?value:JSON.stringify(value,null,2)+'\n',{flag:'wx',mode:0o600});
if(mode==='prepare') {
  assert.match(argument??'',/^[a-f0-9]{40}$/);
  const port=Number(portArgument);assert.ok(Number.isInteger(port)&&port>=1024&&port<=65535&&!['4192','4194','4196','4197'].includes(portArgument));
  assert.match(subnet??'',/^10\.251\.(?:[0-9]{1,2}|1[0-9]{2}|2[0-4][0-9]|25[0-5])\.0\/24$/);
  await run('git',['cat-file','-e',argument+'^{commit}']);
  const networks=(await run('docker',['network','ls','-q'])).trim().split('\n').filter(Boolean);
  if(networks.length){const existing=JSON.parse(await run('docker',['network','inspect',...networks]));assert.ok(!existing.some(n=>n.IPAM?.Config?.some(c=>c.Subnet===subnet)),'Subnet already in use.');}
  const owner=randomUUID(),name='product-counts-'+owner,directory=path.join(root,'.runtime',name),project='webapi-'+name;
  await fs.mkdir(directory,{recursive:true,mode:0o700});
  await write(path.join(directory,'postgres-password'),randomBytes(32).toString('base64url'));
  const images=JSON.parse(await run('git',['show',argument+':deploy/images.lock.json']));
  for(const image of [images.sdk,images.postgres,images.redis])await run('docker',['image','inspect',image]);
  const seed='webapi-enterprise-core-test_nuget-test';await run('docker',['volume','inspect',seed]);
  const source=path.join(directory,'source');await fs.mkdir(source);
  const inputs=['src','console','third_party','tests/WebApi.TestSupport','tests/WebApi.NotificationFixtureHost','.config','Directory.Build.props','Directory.Packages.props','global.json'];
  await run('git',['archive','--format=tar','--output='+path.join(directory,'product-input.tar'),argument,...inputs]);
  await run('tar',['-xf',path.join(directory,'product-input.tar'),'-C',source]);
  const tool='tools/WebApi.ProjectCounts.BrowserHost';await fs.mkdir(path.join(source,tool),{recursive:true});
  const toolFiles={};for(const name of ['Program.cs','WebApi.ProjectCounts.BrowserHost.csproj','packages.lock.json']) {
    const relative=tool+'/'+name,bytes=await fs.readFile(path.join(root,relative));await fs.writeFile(path.join(source,relative),bytes,{flag:'wx'});
    toolFiles[relative]=createHash('sha256').update(bytes).digest('hex');
  }
  for(const name of ['package.json','pnpm-lock.yaml'])assert.deepEqual(await fs.readFile(path.join(source,'console',name)),await fs.readFile(path.join(root,'console',name)),'Installed dependency lock differs from frozen product.');
  await fs.symlink(path.join(root,'console/node_modules'),path.join(source,'console/node_modules'),'dir');
  const buildLogs=[];
  for(const command of [[path.join(root,'console/node_modules/typescript/bin/tsc'),'--noEmit'],[path.join(root,'console/node_modules/vite/bin/vite.js'),'build']])
    buildLogs.push(await run(process.execPath,command,{cwd:path.join(source,'console')}));
  await write(path.join(directory,'console-build.log'),buildLogs.join('\n'));
  const files=await sourceHashes(source),manifestText=JSON.stringify({productRevision:argument,toolFiles,files},null,2)+'\n';
  await write(path.join(directory,'source-inputs.json'),manifestText);
  const sourceManifestSha256=createHash('sha256').update(manifestText).digest('hex');
  const backendFiles=Object.keys(files).filter(file=>!file.startsWith('console/')).sort();
  await write(path.join(directory,'backend-files.list'),backendFiles.join('\0')+'\0');
  await write(path.join(directory,'backend-sha256.txt'),backendFiles.map(file=>files[file]+'  '+file).join('\n')+'\n');
  const dist=path.join(source,'console/dist'),staticFiles=await sourceHashes(dist);
  await write(path.join(dist,'acceptance-identity.json'),{owner,productRevision:argument,sourceManifestSha256,staticFiles});
  const target='/evidence/'+name;
  const limits={WEBAPI_MIGRATION_TRANSACTION_SECONDS:'900',WEBAPI_MIGRATION_SETTINGS_MAX_BYTES:'65536',WEBAPI_MIGRATION_RULE_MAX_BYTES:'16384',WEBAPI_MIGRATION_RULE_MAX_COUNT:'100000',WEBAPI_MIGRATION_TOTAL_MAX_BYTES:'67108864',WEBAPI_MIGRATION_RECEIPT_MAX_ROWS:'100000'};
  const config={name:project,services:{
    postgres:{image:images.postgres,environment:{POSTGRES_USER:'webapi',POSTGRES_DB:'webapi',POSTGRES_PASSWORD_FILE:'/run/secrets/postgres-password'},secrets:['postgres-password'],volumes:['pg:/var/lib/postgresql/data'],healthcheck:{test:['CMD-SHELL','pg_isready -U webapi -d webapi'],interval:'2s',timeout:'3s',retries:30}},
    redis:{image:images.redis,healthcheck:{test:['CMD','redis-cli','ping'],interval:'2s',timeout:'3s',retries:30}},
    host:{image:images.sdk,working_dir:'/test-src',volumes:[source+':/source:ro',directory+':'+target,dist+':/console-dist:ro','build:/test-src','nuget:/root/.nuget/packages','seed:/nuget-seed:ro'],secrets:['postgres-password'],ports:[`127.0.0.1:${port}:18080`],environment:{...limits,DOTNET_CLI_TELEMETRY_OPTOUT:'1',WEBAPI_PRODUCT_ACCEPTANCE_OWNER:owner,WEBAPI_PRODUCT_REVISION:argument,WEBAPI_TEST_DB_HOST:'postgres',WEBAPI_TEST_RETAIN_DATABASES:'1',WEBAPI_TEST_SQL_MIGRATION_LIMITS:'1'},command:['bash','-c',[
      'set -euo pipefail',
      'tar -C /source --null -T '+target+'/backend-files.list -cf - | tar -xf - -C /test-src',
      'sha256sum --check '+target+'/backend-sha256.txt > '+target+'/backend-source-verified.log',
      'cp -a /nuget-seed/. /root/.nuget/packages/',
      'dotnet restore tools/WebApi.ProjectCounts.BrowserHost/WebApi.ProjectCounts.BrowserHost.csproj --locked-mode',
      'dotnet build tools/WebApi.ProjectCounts.BrowserHost/WebApi.ProjectCounts.BrowserHost.csproj --no-restore -c Release',
      'exec dotnet tools/WebApi.ProjectCounts.BrowserHost/bin/Release/net10.0/WebApi.ProjectCounts.BrowserHost.dll '+target
    ].join('\n')]}
  },secrets:{'postgres-password':{file:path.join(directory,'postgres-password')}},volumes:{pg:{},build:{},nuget:{},seed:{external:true,name:seed}},networks:{default:{ipam:{config:[{subnet}]}}}};
  for(const service of Object.values(config.services))service.labels={'com.webapi.product-acceptance.owner':owner};
  await write(path.join(directory,'compose.json'),config);
  await write(path.join(directory,'fixture.json'),{owner,directory,project,url:`http://127.0.0.1:${port}`,productRevision:argument,sourceManifestSha256,staticFiles,toolFiles});
  console.log(directory);
} else if(['start','stop'].includes(mode)) {
  const directory=path.resolve(argument),fixture=JSON.parse(await fs.readFile(path.join(directory,'fixture.json')));
  assert.equal(fixture.directory,directory);assert.equal(path.basename(directory),'product-counts-'+fixture.owner);
  assert.match(fixture.owner,/^[0-9a-f-]{36}$/);assert.equal(fixture.project,'webapi-product-counts-'+fixture.owner);
  const compose=['compose','-p',fixture.project,'-f',path.join(directory,'compose.json')];
  const ids=(await run('docker',[...compose,'ps','-aq'])).trim().split('\n').filter(Boolean);
  for(const id of ids){const [info]=JSON.parse(await run('docker',['inspect',id]));assert.equal(info.Config.Labels['com.webapi.product-acceptance.owner'],fixture.owner);}
  if(mode==='start') {
    const manifestFile=await fs.readFile(path.join(directory,'source-inputs.json'));
    assert.equal(createHash('sha256').update(manifestFile).digest('hex'),fixture.sourceManifestSha256);
    await verifySourceManifest(path.join(directory,'source'),JSON.parse(manifestFile).files);
    const staticFiles=await sourceHashes(path.join(directory,'source/console/dist'));delete staticFiles['acceptance-identity.json'];
    assert.deepEqual(staticFiles,fixture.staticFiles,'Built static assets changed.');
    assert.equal(ids.length,0,'Never restart an existing run implicitly; preserve it and prepare a fresh run.');
    await run('docker',[...compose,'up','-d','--wait','--pull','never','postgres','redis']);
    await run('docker',[...compose,'up','-d','--pull','never','host']);
  } else {
    await write(path.join(directory,'stop'),'stop\n');
    await run('docker',[...compose,'stop','-t','15']);
    await write(path.join(directory,'retention.json'),{owner:fixture.owner,stopped:true,containersRetained:ids,volumesRetained:true});
  }
  console.log(mode+': '+fixture.project);
} else throw new Error('Usage: project-counts-fixture.mjs prepare <full SHA> <loopback port> <free 10.251.x.0/24> | start|stop <owned directory>');
