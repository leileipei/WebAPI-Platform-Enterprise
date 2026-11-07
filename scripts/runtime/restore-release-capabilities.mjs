import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {createHash} from 'node:crypto';
import {repository} from './build.mjs';

const exec=promisify(execFile);
const originalSecrets=Object.freeze(['cursor-signing','ip-hmac','node-a','node-b','postgres-password']);
const cacheSecrets=Object.freeze(['cache-hmac',...originalSecrets]);
const currentSecrets=Object.freeze(['cache-hmac','cursor-signing','ip-hmac','login-protection-hmac','node-a','node-b','postgres-password']);
const supportedContracts=Object.freeze({
 // Fixed historical runtime init contracts; unrecognized scripts still block.
 '9e5d28a162adfbccd67417a6ff0808062214cb30588f0dd3cccfa1bcda55337f':originalSecrets,
 'f7ee9830077e631d8894ed1bfe8e1b459ebb81980bdd5b59be44d7fadaee9e2d':cacheSecrets,
 // Fixed historical revision 2a7bcf5f54af168a3f90f33a75f9770333c2d432.
 '90bce3e04a328ba59dd23d194b2d4d547ab48aad82b5db9509e332d7c26d3565':currentSecrets,
 'c2847f71c60f459630c19dcecc87806f1f2d7e83c4f3c0bc8cea99f302053dc6':currentSecrets,
 '11c028d239cea952c037c6f8d71c17cd39dc6897e34d6de3c984c4d7e226d32a':currentSecrets
});

export function verifyRestoreRevisionIdentity(manifest,release,runtime){
 if(release?.schemaVersion!==1||release.configVersion!==1||!/^sha256:[a-f0-9]{64}$/.test(release.imageId??'')||
    !/^[a-f0-9]{40}$/.test(release.sourceRevision??'')||manifest?.sourceRevision!==release.sourceRevision||
    runtime?.releaseId!=null&&runtime.releaseId!==release.sourceRevision)throw new Error('Restore source revision identity is inconsistent');
 return release.sourceRevision;
}

export async function resolveRestoreReleaseCapabilities(release,{readAt=async(revision,file)=>
 (await exec('git',['show',revision+':'+file],{cwd:repository,maxBuffer:4*1024*1024,timeout:30000})).stdout,contracts=supportedContracts}={}){
 const revision=release?.sourceRevision;
 if(!/^[a-f0-9]{40}$/.test(revision??''))throw new Error('Restore source revision cannot be verified');
 let config,script;
 try{config=JSON.parse(await readAt(revision,'deploy/compose.runtime.yml'));script=await readAt(revision,'deploy/runtime/init-volumes.sh');}
 catch{throw new Error('Restore source revision or deployment contract is unavailable');}
 if(!config||typeof config!=='object'||!config.volumes||typeof config.volumes!=='object'||Array.isArray(config.volumes)||
    !config.services||typeof config.services!=='object'||!config.services['control-plane'])throw new Error('Restore release capabilities cannot be verified');
 const env=config.services['control-plane'].environment;
 if(!env||typeof env!=='object'||Array.isArray(env))throw new Error('Restore release capabilities cannot be verified');
 const loginValue=env.Authentication__LoginProtection__HmacSecretFile;
 if(loginValue!==undefined&&loginValue!=='/run/secrets/login-protection-hmac')throw new Error('Restore login protection contract cannot be verified');
 const contractHash=createHash('sha256').update(script).digest('hex'),contract=contracts[contractHash];
 if(!Array.isArray(contract)||!contract.includes('postgres-password')||contract.some(name=>!/^[a-z0-9-]+$/.test(name)))throw new Error('Restore release secret contract cannot be verified');
 const coreSecrets=[...new Set(contract)].sort(),cache=Object.hasOwn(config.volumes,'secrets-cache'),loginProtection=loginValue!==undefined;
 if(cache&&!coreSecrets.includes('cache-hmac')||loginProtection&&!coreSecrets.includes('login-protection-hmac'))throw new Error('Restore release secret contract cannot be verified');
 return {coreSecrets,cache,loginProtection,evidence:Object.hasOwn(config.volumes,'migration-evidence'),reports:Object.hasOwn(config.volumes,'verification-reports')};
}
