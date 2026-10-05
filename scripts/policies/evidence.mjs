import fs from 'node:fs/promises';import {createHash} from 'node:crypto';
export const sha=b=>createHash('sha256').update(b).digest('hex');
export async function saveEvidence(file,value){await fs.mkdir(new URL('.',new URL('file://'+file)),{recursive:true});await fs.writeFile(file,JSON.stringify(value,null,2)+'\n');}
export function assertNoSecrets(value,secrets){const text=JSON.stringify(value);for(const secret of secrets)if(secret&&text.includes(secret))throw Error('Secret detected in public evidence');return true;}
