import path from 'node:path';import fs from 'node:fs/promises';import {runSettingsBrowserQa} from './browser-qa.mjs';
const root=path.resolve(import.meta.dirname,'../..'),info=JSON.parse(await fs.readFile(path.join(root,'.runtime/settings-review.json'))),ctx=JSON.parse(await fs.readFile(info.directory+'/review-context.json'));await runSettingsBrowserQa({...ctx,root});
