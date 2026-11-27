import fs from 'node:fs/promises';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {runPromotionScenario} from './promotion-scenario.mjs';
import {validatePromotionProof} from './promotion-evidence.mjs';
import {readScenarioProof} from './scenario-evidence.mjs';
import {runDeliveryCli} from './scenario-cli.mjs';

async function verifyPromotionDirectory(directory){
 const proof=await readScenarioProof(directory),files={};
 const names=[...Object.keys(proof.build?.applications??{}),proof.backupRestore?.restoredReportPath,...(proof.screenshots??[]).map(s=>s.path)];
 for(const name of names.filter(Boolean)){
  if(path.isAbsolute(name)||name.split('/').includes('..'))throw Error('Invalid evidence path');
  files[name]=await fs.readFile(path.join(directory,name));
 }
 return validatePromotionProof(proof,files);
}

export function promotionCli(args,deps={}){
 return runDeliveryCli(args,{
  verifyDirectory:deps.verifyDirectory??verifyPromotionDirectory,configureStorage:deps.configureStorage
 },options=>(deps.runAcceptance??runPromotionScenario)({...options,browser:true}));
}

if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
 try{
  const result=await promotionCli(process.argv.slice(2));
  if(process.argv[2]==='verify'){
   console.log(JSON.stringify(result));
   if(!result.passed)process.exitCode=1;
  }
 }catch(error){console.error(error.stack);process.exitCode=1;}
}
