import fs from 'node:fs/promises';
import path from 'node:path';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {createHash} from 'node:crypto';
const exec=promisify(execFile);
const commitPattern=/^[a-f0-9]{40}$/;
export async function hashHarnessFiles(directory,files){
  const harnessFiles={};
  for(const file of [...files].sort()){
    const target=path.join(directory,file),info=await fs.lstat(target);
    if(!info.isFile()||info.isSymbolicLink())throw Error('Harness input must be a regular source file');
    harnessFiles[file]=createHash('sha256').update(await fs.readFile(target)).digest('hex');
  }
  return {harnessFiles,harnessFingerprint:createHash('sha256').update(JSON.stringify(harnessFiles)).digest('hex')};
}
export function parseArguments(args){
  const [mode,...rest]=args;
  if(!['validate','baseline','compare','explain','migration','cleanup'].includes(mode))throw Error('Invalid performance mode');
  const parsed={mode};const allowed=new Set(['baseline','candidate','harness','output','owner-manifest','compose','sql-audit','classifications','diagnostic']);
  for(let i=0;i<rest.length;i+=2){
    const key=rest[i].startsWith('--')?rest[i].slice(2):'';
    if(!allowed.has(key)||key in parsed||!rest[i+1]||rest[i+1].startsWith('--'))throw Error('Invalid or duplicate performance argument');
    parsed[key]=rest[i+1];
  }
  if(parsed['sql-audit']&&mode!=='validate')throw Error('SQL audit is allowed only in validation, never measurement');
  if(parsed['sql-audit']&&['compare','migration'].includes(parsed.diagnostic))throw Error('SQL audit is not supported by comparison/migration diagnostics');
  if(parsed.diagnostic&&(mode!=='validate'||!['explain','migration','compare'].includes(parsed.diagnostic)))throw Error('Diagnostic validation must be explicit EXPLAIN/migration/comparison validation, never a performance window');
  if(mode==='cleanup'){if(!parsed['owner-manifest']||!parsed.compose)throw Error('Cleanup requires owner manifest and compose file');return parsed;}
  if(mode!=='validate')for(const key of mode==='baseline'?['baseline','harness']:['baseline','candidate','harness'])if(!commitPattern.test(parsed[key]??''))throw Error(`Immutable ${key} commit required`);
  if(!parsed.output)throw Error('Output directory required');parsed.output=path.resolve(parsed.output);
  if(parsed['sql-audit']){
    parsed['sql-audit']=path.resolve(parsed['sql-audit']);
    const inside=(parent,child)=>{const relative=path.relative(parent,child);return relative!== '..'&&!relative.startsWith('..'+path.sep)&&!path.isAbsolute(relative);};
    if(inside(parsed.output,parsed['sql-audit'])||inside(parsed['sql-audit'],parsed.output))throw Error('Private SQL audit and public evidence paths must not overlap');
  }
  return parsed;
}
export async function exportInputs({repo,source,harness,directory}){
  if(!commitPattern.test(source??'')||!commitPattern.test(harness??''))throw Error('Immutable source and harness commits required');
  const git=async(...args)=>(await exec('git',['-C',repo,...args],{maxBuffer:4*1024*1024})).stdout.trim();
  for(const id of [source,harness])if(await git('rev-parse','--verify',id+'^{commit}')!==id)throw Error('Commit identity mismatch');
  const productTree=await git('rev-parse',source+':src');const harnessTree=await git('rev-parse',harness+':tools/WebApi.PerformanceHost');
  const files=(await git('ls-tree','-r','--name-only',harness)).split('\n');
  const overlay=files.filter(file=>file.startsWith('tools/WebApi.PerformanceHost/')||file.startsWith('scripts/performance/')||['deploy/compose.performance.yml','deploy/performance/Dockerfile','scripts/check-gateway-query-performance.sh'].includes(file));
  if(!overlay.some(file=>file.startsWith('tools/WebApi.PerformanceHost/')))throw Error('Harness files missing');
  await fs.mkdir(directory,{mode:0o700});
  const scratch=await fs.mkdtemp(path.join(path.dirname(directory),'.performance-archive-'));
  try {
    const sourceTar=path.join(scratch,'source.tar'),harnessTar=path.join(scratch,'harness.tar');
    await git('archive','--format=tar','--output='+sourceTar,source);await exec('tar',['-xf',sourceTar,'-C',directory]);
    await git('archive','--format=tar','--output='+harnessTar,harness,...overlay);await exec('tar',['-xf',harnessTar,'-C',directory]);
  }finally{await fs.rm(scratch,{recursive:true,force:true});}
  return {source,harness,productTree,harnessTree,overlayFiles:overlay.length,...await hashHarnessFiles(directory,overlay)};
}
