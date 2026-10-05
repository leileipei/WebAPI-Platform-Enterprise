import fs from 'node:fs/promises';import path from 'node:path';import {LocalClient} from '../local-client.mjs';import {keycloakAdmin} from './scenario.mjs';import {composeFor,waitFor} from './fixture.mjs';
const root=path.resolve(import.meta.dirname,'../..'),info=JSON.parse(await fs.readFile(root+'/.runtime/sso-review.json')),context=JSON.parse(await fs.readFile(info.directory+'/review-context.json')),admin=new LocalClient(context.endpoints.console);await admin.login('fixture-admin',await fs.readFile(context.directory+'/admin-password','utf8'));
const action=process.argv[2],write=(url,body,revision,method='POST')=>admin.request(url,{method,body,headers:revision===undefined?undefined:{'If-Match':'"'+revision+'"'}});
async function user(username){return(await admin.request('/users')).items.find(u=>u.username===username);}
async function assign(username,roleIds){const current=await user(username);return write('/users/'+current.id+'/roles',{roleIds},current.revision,'PUT');}
const platform=(await admin.request('/roles')).find(r=>r.code==='PlatformAdmin'&&r.isSystem&&!r.organizationId);
if(action==='prepare'){
 const password=await fs.readFile(context.directory+'/admin-password','utf8');let actor=await user('fixture-ui-admin');if(!actor)actor=await write('/users',{username:'fixture-ui-admin',displayName:'SSO 界面验收管理员',password,email:null});await assign(actor.username,[platform.id]);await assign('fixture-sso',[platform.id]);
 const org=await write('/organizations',{code:'SSO-UI-OTHER',name:'界面验收第二组织'});context.otherOrganizationId=org.id;await fs.writeFile(context.directory+'/review-context.json',JSON.stringify(context,null,2),{mode:0o600});console.log(JSON.stringify({prepared:true,uiAdmin:'fixture-ui-admin',otherOrganizationId:org.id}));
}else if(action==='scopes'){
 const actor=await user('fixture-ui-admin');await write('/users/'+actor.id+'/scopes',{scopes:[{scope:context.scope,accessMode:'read_write'},{scope:{organizationId:context.otherOrganizationId,projectId:null,environmentId:null},accessMode:'read_write'}]},actor.revision,'PUT');console.log(JSON.stringify({ownedOrganizationScopesAssigned:true}));
}else if(action==='conflict'){
 const p=await admin.request('/settings/sso/providers/'+context.providerId);await write('/settings/sso/providers/'+p.id,{organizationId:p.organizationId,name:p.name+' · 并发修订',issuer:p.issuer,clientId:p.clientId,secretRef:p.secretRef,scopes:p.scopes,claimMapping:p.claimMapping},p.revision,'PUT');console.log(JSON.stringify({serverRevisionAdvanced:true}));
 }else if(action==='hold-command'){
 await fs.rm(context.directory+'/observations/command-held',{force:true});await fs.writeFile(context.directory+'/observations/hold-command','true',{mode:0o600});console.log(JSON.stringify({holdingCommand:true}));
}else if(action==='wait-command'){
 await waitFor(()=>fs.readFile(context.directory+'/observations/command-held','utf8'),value=>value==='true',10000);console.log(JSON.stringify({held:true}));
}else if(action==='release-command'){
 await fs.rm(context.directory+'/observations/hold-command',{force:true});console.log(JSON.stringify({released:true}));
}else if(action==='refresh-during-command'){
 await fs.writeFile(context.directory+'/observations/deny-scope-once','true',{mode:0o600});console.log(JSON.stringify({nextScopeRequestDenied:true}));
}else if(action==='hold-detail'){
 await fs.rm(context.directory+'/observations/detail-held',{force:true});await fs.writeFile(context.directory+'/observations/hold-detail','true',{mode:0o600});console.log(JSON.stringify({holdingDetails:true}));
}else if(action==='wait-held'){
 await waitFor(()=>fs.readFile(context.directory+'/observations/detail-held','utf8'),value=>value==='true',10000);console.log(JSON.stringify({held:true}));
}else if(action==='release-detail'){
 await fs.rm(context.directory+'/observations/hold-detail',{force:true});console.log(JSON.stringify({released:true}));
}else if(action==='revoke'){
 let role=(await admin.request('/roles')).find(r=>r.code==='SsoUiFunctionalOnly');if(!role){role=await write('/roles',{code:'SsoUiFunctionalOnly',name:'有功能码但无平台管理员身份',organizationId:null});await write('/roles/'+role.id+'/permissions',{permissions:['user.manage','system.sso.manage']},role.revision,'PUT');}await assign('fixture-ui-admin',[role.id]);console.log(JSON.stringify({systemAdministratorRevoked:true,functionalSsoCodeRetained:true}));
}else if(action==='restore'){
 await assign('fixture-ui-admin',[platform.id]);await assign('fixture-sso',[platform.id]);console.log(JSON.stringify({restored:true}));
}else if(action==='wrong-secret'){
 const secret=await fs.readFile(context.directory+'/client-secret','utf8');await fs.writeFile(context.directory+'/correct-secret',secret,{mode:0o600});await fs.writeFile(context.directory+'/client-secret',crypto.randomUUID(),{mode:0o600});const p=await admin.request('/settings/sso/providers/'+context.providerId);await write('/settings/sso/providers/'+p.id+'/rotate',{},p.revision);console.log(JSON.stringify({wrongFixtureSecretInstalled:true}));
}else if(action==='restore-secret'){
 await fs.writeFile(context.directory+'/client-secret',await fs.readFile(context.directory+'/correct-secret','utf8'),{mode:0o600});await fs.rm(context.directory+'/correct-secret');const p=await admin.request('/settings/sso/providers/'+context.providerId);await write('/settings/sso/providers/'+p.id+'/rotate',{},p.revision);console.log(JSON.stringify({fixtureSecretRestored:true}));
}else if(action==='cookie-evidence'){
 const rows=(await fs.readFile(context.directory+'/observations/cookie-requests.jsonl','utf8')).trim().split('\n').map(line=>JSON.parse(line));console.log(JSON.stringify({observations:rows.filter(row=>row.fetchSite!=='unknown')}));
}else throw Error('Unknown owned review action.');
