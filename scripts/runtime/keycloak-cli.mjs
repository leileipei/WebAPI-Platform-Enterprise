import path from 'node:path';import {parseArgs} from 'node:util';
import {initializeIdp,operateIdp,backupIdp,restoreIdp} from './keycloak-demo.mjs';
import {switchSsoConnection,provisionSsoDemo} from './sso-demo-platform.mjs';
try{
 const {values,positionals}=parseArgs({allowPositionals:true,options:{'runtime-directory':{type:'string'},directory:{type:'string'},port:{type:'string'},target:{type:'string'},'password-file':{type:'string'}}});
 if(positionals.length!==1||!values['runtime-directory']||!values.directory)throw Error('Explicit --runtime-directory and --directory required.');
 const options={runtimeDirectory:path.resolve(values['runtime-directory']),directory:path.resolve(values.directory),...(values.port?{port:Number(values.port)}:{}),...(values.target?{target:path.resolve(values.target)}:{}),...(values['password-file']?{passwordFile:path.resolve(values['password-file'])}:{})};let result;
 const command=positionals[0];
 if(command==='init')result=await initializeIdp(options);
 else if(['start','restart','stop','status'].includes(command))result=await operateIdp(command,options);
 else if(command==='connect'||command==='rollback'){if(command==='rollback'&&!options.passwordFile)throw Error('Rollback needs --password-file to disable provider and revoke tickets.');result=await switchSsoConnection({...options,enabled:command==='connect'});}
 else if(command==='provision'||command==='rebind-restored'){if(!options.passwordFile)throw Error('Provision needs private --password-file.');result=await provisionSsoDemo({...options,rebindRestored:command==='rebind-restored'});}
 else if(command==='backup'){if(!options.target)throw Error('Backup needs fresh --target.');result=await backupIdp(options);}
 else if(command==='restore'){if(!options.target||!options.port)throw Error('Restore needs --target and new --port.');result=await restoreIdp(options);}
 else throw Error('Unsupported command: init|start|restart|stop|status|connect|provision|rebind-restored|rollback|backup|restore');
 console.log(JSON.stringify(result,null,2));
}catch(e){console.error(e.message??'Local IdP operation failed; owned state retained.');process.exitCode=e.exitCode??3;}
