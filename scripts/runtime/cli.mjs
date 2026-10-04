import path from'node:path';import{parseArgs}from'node:util';import{buildRelease}from'./build.mjs';import{initializeRuntime,operateRuntime,completeBootstrap}from'./lifecycle.mjs';import{loadState,RuntimeError}from'./state.mjs';
try{
 if(Number(process.versions.node.split('.')[0])<22)throw new RuntimeError('Node 22+ required.',3);
 const{values,positionals}=parseArgs({allowPositionals:true,options:{revision:{type:'string'},admin:{type:'string'},'password-file':{type:'string'},'release-file':{type:'string'},'nuget-seed-volume':{type:'string'},'credentials-saved':{type:'boolean'},json:{type:'boolean'},'console-port':{type:'string'},'gateway-a-port':{type:'string'},'gateway-b-port':{type:'string'}}});const command=positionals[0];if(positionals.length!==1)throw new RuntimeError('Usage: local-runtime.sh build|init|up|start|stop|restart|status|complete-bootstrap');const directory=path.resolve('.runtime/local');let result;
 if(command==='build')result=await buildRelease({revision:values.revision,directory:path.resolve('.runtime/local-build'),nugetSeedVolume:values['nuget-seed-volume']});
 else if(command==='init')result=await initializeRuntime({directory,bootstrapUsername:values.admin,passwordFile:values['password-file'],releaseFile:values['release-file'],ports:{console:Number(values['console-port']??4190),gatewayA:Number(values['gateway-a-port']??4196),gatewayB:Number(values['gateway-b-port']??4197)}});
 else if(['up','start','stop','restart'].includes(command))result=await operateRuntime(command,await loadState(directory),{directory});
 else if(command==='complete-bootstrap'){await completeBootstrap(await loadState(directory),{directory,credentialsSaved:values['credentials-saved']});result={bootstrapFileRemoved:true};}
 else if(command==='status'){const s=await loadState(directory);result={projectName:s.projectName,initialized:s.initialized,binding:s.binding,sourceRevision:s.releaseId};}
 else throw new RuntimeError('Unsupported command.');
 console.log(JSON.stringify(result,null,2));if(command==='init')console.log('Bootstrap password file (content not printed): '+path.join(directory,'secrets/bootstrap-password'));
}catch(e){console.error(e instanceof RuntimeError?e.message:'Local runtime operation failed; owned state retained.');process.exitCode=e.exitCode??6;}
