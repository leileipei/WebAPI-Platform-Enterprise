import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {parseArguments} from './sources.mjs';
import {cleanupOwned} from './owner.mjs';
const exec=promisify(execFile);
try {
  const options=parseArguments(process.argv.slice(2));
  if(options.mode==='cleanup'){
    const manifest=JSON.parse(await fs.readFile(options['owner-manifest'],'utf8'));const receipt=await cleanupOwned(manifest,options.compose);process.stdout.write(JSON.stringify(receipt)+'\n');
  }else{
    const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
    let runner=new URL('./runner.mjs',import.meta.url),archiveDirectory;
    try {
      if(options.mode!=='validate') {
        // The launcher switches to H's driver before creating owners/builds. A dirty
        // local driver cannot silently change measured windows or request selection.
        const resolved=(await exec('git',['-C',repo,'rev-parse','--verify',options.harness+'^{commit}'])).stdout.trim();if(resolved!==options.harness)throw Error('Harness commit identity mismatch');
        await fs.mkdir(path.join(repo,'.runtime'),{recursive:true});archiveDirectory=await fs.mkdtemp(path.join(repo,'.runtime','performance-driver-'));await fs.chmod(archiveDirectory,0o700);
        const archive=path.join(archiveDirectory,'driver.tar');await exec('git',['-C',repo,'archive','--output',archive,options.harness,'scripts/performance']);await exec('tar',['-xf',archive,'-C',archiveDirectory]);
        runner=pathToFileURL(path.join(archiveDirectory,'scripts/performance/runner.mjs'));
      }
      const result=await (await import(runner.href)).runPerformance(options,repo);process.stdout.write(JSON.stringify({mode:result.mode,owner:result.owner,output:options.output,gatewayCells:result.gatewayCells,gatewayCellsComplete:result.gatewayCellsComplete,fullBaselineComplete:result.fullBaselineComplete,fullComparisonComplete:result.fullComparisonComplete,...result.measurementScope?{measurementScope:result.measurementScope,diagnosticComplete:result.diagnosticComplete??result.complete}:{}})+'\n');
    }finally{if(archiveDirectory)await fs.rm(archiveDirectory,{recursive:true,force:true});}
  }
}catch(error){process.stderr.write('Performance run failed: '+error.message+'\n');process.exitCode=3;}
