import path from 'node:path';
import {RuntimeError} from './state.mjs';
export function validateFixture(projectName,directory){
 if(!/^webapi-enterprise-local-test-[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(projectName))throw new RuntimeError('Explicit disposable runtime project required.',3);
 if(path.basename(path.resolve(directory))!==projectName)throw new RuntimeError('Disposable directory must match project name.',3);
}
