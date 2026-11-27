import assert from 'node:assert/strict';
import {constants} from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';
import {assertOwnedSsoResources,validateSsoDirectory,ssoDirectoryIdentity} from './evidence.mjs';

const pointerPath=root=>path.join(root,'.runtime','sso-review.json');

async function readPrivate(file) {
  const handle=await fs.open(file,constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const st=await handle.stat({bigint:true});
    assert(st.isFile() && st.uid===BigInt(process.getuid()) && !(st.mode & 0o077n),'Invalid private SSO review file');
    return {value:JSON.parse(await handle.readFile('utf8')),identity:st.dev+':'+st.ino};
  } finally { await handle.close(); }
}

export async function publishReviewPointer(context) {
  const value={directory:context.directory,owner:context.owner,project:context.project,url:context.endpoints.console,
    directoryIdentity:context.directoryIdentity ?? await ssoDirectoryIdentity(context.directory)};
  const pointer=pointerPath(context.root);
  const handle=await fs.open(pointer,'wx',0o600);
  try {
    const st=await handle.stat({bigint:true});
    await handle.writeFile(JSON.stringify(value));
    return {value,identity:st.dev+':'+st.ino};
  } catch(error) {
    const entry=await fs.lstat(pointer,{bigint:true}), opened=await handle.stat({bigint:true});
    if (entry.dev===opened.dev && entry.ino===opened.ino) await fs.unlink(pointer);
    throw error;
  } finally { await handle.close(); }
}

export async function ownedReview(root) {
  assert((await fs.lstat(path.join(root,'.runtime'))).isDirectory(),'Invalid SSO runtime parent');
  const pointer=await readPrivate(pointerPath(root)), {owner,project,directory}=pointer.value;
  assertOwnedSsoResources({owner,project},[]);
  assert.equal(directory,path.join(root,'.runtime',project),'Foreign SSO review directory');
  const identity={root,owner,project,directory,directoryIdentity:pointer.value.directoryIdentity ?? await ssoDirectoryIdentity(directory)};
  await validateSsoDirectory(identity);
  return {pointer,identity};
}

export async function reviewContext(identity) {
  const {value}=await readPrivate(path.join(identity.directory,'review-context.json'));
  for (const key of ['root','directory','owner','project']) assert.equal(value[key],identity[key],'Foreign SSO review context');
  return value;
}

export async function releaseReviewPointer(root,expected) {
  const actual=await readPrivate(pointerPath(root));
  assert.equal(actual.identity,expected.identity,'SSO review pointer changed');
  assert.deepEqual(actual.value,expected.value,'SSO review ownership changed');
  await fs.unlink(pointerPath(root));
}
