// Test-only authenticated preview of production assets. No HMR and no original identity used.
import fs from 'node:fs';import path from 'node:path';import http from 'node:http';import {scenario} from './observability-scenario.mjs';
const s=await scenario(),root=path.resolve('console/dist');
const session=s.admin.cookies.get('WebApi.Session');if(!session)throw Error('Disposable fixture session missing');fs.writeFileSync(s.directory+'/browser-cookie','WebApi.Session='+session,{mode:0o600});
const mime={'.js':'text/javascript','.css':'text/css','.html':'text/html','.svg':'image/svg+xml','.jpg':'image/jpeg'};
const server=http.createServer(async(req,res)=>{try{
 const url=new URL(req.url,'http://127.0.0.1');if(url.pathname.startsWith('/api/')){
  const chunks=[];for await(const chunk of req)chunks.push(chunk);const headers={...req.headers};headers.cookie=fs.readFileSync(s.directory+'/browser-cookie','utf8')+'; '+String(req.headers.cookie||'').split(';').map(v=>v.trim()).filter(v=>v.startsWith('WebApi.Csrf=')).join('; ');
  await new Promise((resolve,reject)=>{const outgoing=http.request(s.endpoints.control_plane+url.pathname+url.search,{method:req.method,headers},response=>{res.statusCode=response.statusCode;for(const[k,v]of Object.entries(response.headers))if(v!==undefined&&!['set-cookie','transfer-encoding'].includes(k))res.setHeader(k,v);const cookies=(response.headers['set-cookie']||[]).filter(v=>v.startsWith('WebApi.Csrf='));if(cookies.length)res.setHeader('set-cookie',cookies);response.pipe(res);response.on('end',resolve);response.on('error',reject);});outgoing.on('error',reject);outgoing.end(chunks.length?Buffer.concat(chunks):undefined);});return;
 }
 const requested=path.resolve(root,'.'+decodeURIComponent(url.pathname));if(!requested.startsWith(root+'/')){res.statusCode=400;res.end();return;}let file=fs.existsSync(requested)&&fs.statSync(requested).isFile()?requested:root+'/index.html';res.setHeader('Content-Type',mime[path.extname(file)]||'application/octet-stream');res.setHeader('Cache-Control','no-store');res.end(fs.readFileSync(file));
 }catch{res.statusCode=502;res.end('{"detail":"临时验收服务不可用"}');}});
server.listen(Number(process.env.WEBAPI_OBS_BROWSER_PORT||0),'127.0.0.1',()=>{const url='http://127.0.0.1:'+server.address().port;fs.writeFileSync(s.directory+'/browser-url.json',JSON.stringify({url,classification:'production-assets-real-source-preview-no-hmr'},null,2));console.log(url);});
const stop=()=>server.close(()=>process.exit(0));process.on('SIGINT',stop);process.on('SIGTERM',stop);
