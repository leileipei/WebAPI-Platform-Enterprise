import {spawn} from 'node:child_process';
import {createServer} from 'node:http';
import {createRequire} from 'node:module';
import {readFile,readdir,mkdir,writeFile} from 'node:fs/promises';
import {extname,join,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

const root=fileURLToPath(new URL('../',import.meta.url));
const dist=resolve(process.env.WEBAPI_CONSOLE_DIST||join(root,'console/dist'));
const tests=join(root,'console/tests'),reports=process.env.WEBAPI_BROWSER_RESULTS;
const anchor=process.env.WEBAPI_PLAYWRIGHT_PACKAGE;
if(!anchor)throw Error('Set WEBAPI_PLAYWRIGHT_PACKAGE to the installed Playwright package.json.');
const executable=process.env.WEBAPI_CHROMIUM_EXECUTABLE||createRequire(anchor)('playwright').chromium.executablePath();
const env={...process.env,WEBAPI_CONSOLE_DIST:dist,WEBAPI_CHROMIUM_EXECUTABLE:executable};
const files=(await readdir(tests)).filter(name=>name.endsWith('.browser.mjs')).sort();
if(!files.length)throw Error('No browser suites found in '+tests);
const index=await readFile(join(dist,'index.html'));
const assets=new Set(await readdir(join(dist,'assets')));
const mime={'.css':'text/css','.js':'text/javascript','.woff2':'font/woff2','.svg':'image/svg+xml','.png':'image/png'};
const server=createServer(async(req,res)=>{
  try{
    const {pathname}=new URL(req.url,'http://127.0.0.1');
    const asset=pathname.startsWith('/assets/')?pathname.slice('/assets/'.length):undefined;
    if(req.method!=='GET'||pathname.startsWith('/api/')||(asset!==undefined&&!assets.has(asset))){res.writeHead(404).end();return;}
    const content=asset===undefined?index:await readFile(join(dist,'assets',asset));
    res.writeHead(200,{'Content-Type':asset===undefined?'text/html; charset=utf-8':mime[extname(asset)]||'application/octet-stream'}).end(content);
  }catch{res.writeHead(500).end();}
});
function resultCount(output){
  const pass=output.match(/^[ℹ#] pass\s+(\d+)\s*$/m);
  if(pass){
    for(const name of ['fail','cancelled','skipped','todo'])
      if(!new RegExp('^[ℹ#] '+name+'\\s+0\\s*$','m').test(output))throw Error('Incomplete browser tests: '+name);
    return Number(pass[1]);
  }
  const value=JSON.parse(output);
  if(!Array.isArray(value.results)||value.results.length!==value.passed)throw Error('Invalid browser fixture summary');
  return value.passed;
}
if(reports)await mkdir(reports,{recursive:true});
await new Promise((ok,fail)=>server.once('error',fail).listen(0,'127.0.0.1',ok));
const preview='http://127.0.0.1:'+server.address().port;
const suites=[];
try{
  for(const name of files){
    const child=spawn(process.execPath,[join(tests,name),preview],{cwd:root,env,stdio:['ignore','pipe','pipe']});
    const chunks=[];
    child.stdout.on('data',chunk=>chunks.push(chunk));
    child.stderr.on('data',chunk=>chunks.push(chunk));
    const exitCode=await new Promise((ok,fail)=>{child.once('error',fail);child.once('close',ok);});
    const output=Buffer.concat(chunks).toString();
    if(reports)await writeFile(join(reports,name+'.log'),output);
    let passed=0;
    try{
      if(exitCode!==0)throw Error('Browser process exited with '+exitCode);
      passed=resultCount(output);
      if(!Number.isInteger(passed)||passed<=0)throw Error('Empty browser suite');
    }catch(error){process.exitCode=1;console.error(name,error.message,output);}
    suites.push({name,exitCode,passed});
    console.log(name,passed+' passed');
  }
}finally{await new Promise(ok=>server.close(ok));}
if(reports)await writeFile(join(reports,'browser-suites.json'),JSON.stringify(suites,null,2)+'\n');
console.log(files.length+' browser suites, '+suites.reduce((n,s)=>n+s.passed,0)+' passed');
