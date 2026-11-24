import {execFile} from 'node:child_process';
import {promisify} from 'node:util';

const exec = promisify(execFile);

export async function execFileAndWait(file, args, options = {}) {
  const {signal} = options;
  signal?.throwIfAborted();
  const result = exec(file, args, options);
  if (!signal) return result;
  // execFile can reject on abort while the child is still using its working directory.
  const closed = new Promise(resolve => result.child.once('close', resolve));
  try {
    return await result;
  } finally {
    await closed;
  }
}
