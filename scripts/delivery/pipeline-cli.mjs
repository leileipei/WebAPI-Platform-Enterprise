import fs from 'node:fs/promises';import path from 'node:path';import {pathToFileURL} from 'node:url';import {validatePipelineProof} from './pipeline-evidence.mjs';
import {readScenarioProof} from './scenario-evidence.mjs';
import {runDeliveryCli} from './scenario-cli.mjs';
export async function verifyPipelineDirectory(directory){const root=path.resolve(directory),proof=await readScenarioProof(root),files={};for(const name of [proof.backupRestore?.reportPath,...(proof.backupRestore?.reports??[]).map(r=>r.reportPath),...(proof.browser?.screenshots??[]).map(s=>s.path)].filter(Boolean)){if(path.isAbsolute(name)||name.split('/').includes('..'))throw Error('Unsafe evidence path');const file=path.join(root,name);if((await fs.lstat(file)).isSymbolicLink())throw Error('Evidence symlink');files[name]=await fs.readFile(file);}return validatePipelineProof(proof,files);}
export function pipelineCli(args,deps={}){
 return runDeliveryCli(args,{
  verifyDirectory:deps.verifyDirectory??verifyPipelineDirectory,configureStorage:deps.configureStorage
 },async options=>{
  const run=deps.runAcceptance??(await import('./pipeline-scenario.mjs')).runPipelineScenario;
  return run({...options,chains:[2,4],faults:options.mode!=='browser',browser:options.mode!=='faults'});
 });
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href)try{const result=await pipelineCli(process.argv.slice(2));console.log(JSON.stringify(result));if(result.passed===false)process.exitCode=1;}catch(e){console.error(e.stack);process.exitCode=1;}
