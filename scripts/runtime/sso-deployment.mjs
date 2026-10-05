import fs from 'node:fs/promises';
import path from 'node:path';
import {safeFile, validGuid, RuntimeError, writePrivate} from './state.mjs';
import {docker} from './docker.mjs';
export const ssoConfigName='sso-deployment.json';
function fields(value,names){if(!value||typeof value!=='object'||Array.isArray(value)||Object.keys(value).some(n=>!names.includes(n)))throw new RuntimeError('Invalid SSO deployment schema.');}
export function validateSsoDeployment(config,state){
 fields(config,['schemaVersion','ownerId','projectName','enabled','publicBaseUrl','idp']);
 if(config.schemaVersion!==1||config.ownerId!==state.ownerId||config.projectName!==state.projectName||typeof config.enabled!=='boolean')throw new RuntimeError('SSO deployment owner mismatch.');
 if(config.publicBaseUrl!==`http://127.0.0.1:${state.ports.console}`)throw new RuntimeError('SSO public origin must match this local console.');
 const idp=config.idp;fields(idp,['ownerId','projectName','port','networkAlias','secretVolume']);
 if(!validGuid(idp.ownerId)||idp.projectName!=='webapi-local-idp-'+idp.ownerId||idp.networkAlias!=='webapi-local-idp-'+idp.ownerId.slice(0,8)||idp.secretVolume!==idp.projectName+'_client-secret')throw new RuntimeError('Invalid SSO IdP identity.');
 if(!Number.isInteger(idp.port)||idp.port<1024||idp.port>65535||[...Object.values(state.ports),4190,5060,5061,6000,6566,6665,6666,6667,6668,6669,6697,10080].includes(idp.port))throw new RuntimeError('Invalid SSO IdP port.');
 return config;
}
export async function loadSsoDeployment(directory,state){
 const file=path.join(directory,ssoConfigName);try{await safeFile(file);}catch(e){if(e.code==='ENOENT')return null;throw e;}
 return validateSsoDeployment(JSON.parse(await fs.readFile(file,'utf8')),state);
}
export function ssoOverlay(config){
 if(!config?.enabled)return {};
 const origin=`http://localhost:${config.idp.port}`;
 return {environment:{Sso__FixtureEnabled:'true',Sso__PublicBaseUrl:config.publicBaseUrl,Sso__AllowedOrigins__0:origin,'Sso__SecretFiles__local-demo':'/run/sso/client-secret',Sso__FixtureConnectOverridesJson:JSON.stringify({[origin]:config.idp.networkAlias})},volumes:[{type:'volume',source:'sso-client-secret',target:'/run/sso',read_only:true}]};
}
export function assertIdpOwnership(idp,resources){
 for(const r of resources){const labels=r.Labels??r.Config?.Labels??{};if(labels['com.docker.compose.project']!==idp.projectName||labels['com.webapi.idp.owner']!==idp.ownerId)throw new RuntimeError('SSO IdP resource owner mismatch; refusing adoption.',3);}
}
export async function assertSsoSecretVolume(config,run=docker){
 if(!config?.enabled)return;
 const result=await run(['volume','inspect',config.idp.secretVolume]);const resources=JSON.parse(result.stdout);assertIdpOwnership(config.idp,resources);
 if(resources.length!==1||resources[0].Name!==config.idp.secretVolume)throw new RuntimeError('SSO secret volume identity mismatch.',3);
}
// A restored platform must never reuse the original callback or silently attach to its live IdP.
export function rebindRestoredSso(config,state){return validateSsoDeployment({...config,ownerId:state.ownerId,projectName:state.projectName,publicBaseUrl:`http://127.0.0.1:${state.ports.console}`,enabled:false},state);}
export async function copySsoBackup(directory,target,state){
 const config=await loadSsoDeployment(directory,state);if(config)await writePrivate(path.join(target,ssoConfigName),config);
 const demo=path.join(directory,'demo');for(const name of ['local-demo-api-approver.password','local-demo-security-reviewer.password','consumer-api-key']){
  const file=path.join(demo,name);try{await safeFile(file);}catch(e){if(e.code==='ENOENT')continue;throw e;}
  await fs.mkdir(path.join(target,'demo'),{mode:0o700,recursive:true});await writePrivate(path.join(target,'demo',name),await fs.readFile(file,'utf8'));
 }
}
export async function restoreSsoBackup(target,directory,state){
 try{await safeFile(path.join(target,ssoConfigName));const config=JSON.parse(await fs.readFile(path.join(target,ssoConfigName),'utf8'));await writePrivate(path.join(directory,ssoConfigName),rebindRestoredSso(config,state));}catch(e){if(e.code!=='ENOENT')throw e;}
 for(const name of ['local-demo-api-approver.password','local-demo-security-reviewer.password','consumer-api-key']){
  const file=path.join(target,'demo',name);try{await safeFile(file);}catch(e){if(e.code==='ENOENT')continue;throw e;}
  await fs.mkdir(path.join(directory,'demo'),{mode:0o700,recursive:true});await writePrivate(path.join(directory,'demo',name),await fs.readFile(file,'utf8'));
 }
}
