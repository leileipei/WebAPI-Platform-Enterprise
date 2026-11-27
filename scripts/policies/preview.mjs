// Temporary UI QA host for an explicitly built console; API requests still reach the owned real fixture.
import http from 'node:http';import fs from 'node:fs';import path from 'node:path';
import {changePolicyReview, readPolicyReview} from './review-pointer.mjs';
const root=path.resolve(import.meta.dirname,'../..'),recordFile=path.join(root,'.runtime/policy-review.json'),record=await readPolicyReview(root),c=JSON.parse(fs.readFileSync(record.directory+'/review-context.json')),assets=path.join(root,'console/dist');
const server=http.createServer(async(req,res)=>{const url=new URL(req.url,'http://127.0.0.1');if(url.pathname.startsWith('/api/')){const outgoing=http.request(c.endpoints.control_plane+url.pathname+url.search,{method:req.method,headers:req.headers},response=>{res.writeHead(response.statusCode,response.headers);response.pipe(res);});outgoing.on('error',()=>{res.writeHead(502);res.end();});req.pipe(outgoing);return;}const requested=path.resolve(assets,'.'+decodeURIComponent(url.pathname));if(requested!==assets&&!requested.startsWith(assets+'/')){res.writeHead(400);res.end();return;}const file=fs.existsSync(requested)&&fs.statSync(requested).isFile()?requested:path.join(assets,'index.html');res.setHeader('Content-Type',{'.js':'text/javascript','.css':'text/css','.html':'text/html'}[path.extname(file)]||'application/octet-stream');res.setHeader('Cache-Control','no-store');fs.createReadStream(file).pipe(res);});
server.listen(0, '127.0.0.1', async () => {
 const preview = {...record, url: 'http://127.0.0.1:' + server.address().port, preview: true};
 if (!await changePolicyReview(recordFile, record, preview)) { server.close(); return; }
 console.log('Owned temporary UI QA preview ready');
});
const close = () => server.close(async () => {
 await changePolicyReview(recordFile, record, record);
 process.exit(0);
});
process.once('SIGINT', close);
process.once('SIGTERM', close);
