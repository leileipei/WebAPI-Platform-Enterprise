import fs from 'node:fs/promises';import path from 'node:path';import {cleanupSsoFixture} from './fixture.mjs';
const root=path.resolve(import.meta.dirname,'../..'),pointer=root+'/.runtime/sso-review.json',info=JSON.parse(await fs.readFile(pointer)),context=JSON.parse(await fs.readFile(info.directory+'/review-context.json'));
if(context.root!==root||context.project!==info.project||context.owner!==info.owner)throw Error('Review owner mismatch.');
const report=await cleanupSsoFixture(context);await fs.rm(pointer);await fs.writeFile(root+'/docs/evidence/sso/cleanup.json',JSON.stringify(report,null,2)+'\n');console.log('Owned SSO review fixture cleaned; containers0 volumes0 secrets0.');
