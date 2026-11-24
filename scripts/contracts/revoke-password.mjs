import fs from 'node:fs';

// The runner pins the exclusive writer on descriptor 8, and the directory as its cwd.
const owned = fs.fstatSync(8, {bigint: true});
const password = 'postgres-password';
function ownsName() {
  const entry = fs.lstatSync(password, {bigint: true, throwIfNoEntry: false});
  return entry?.dev === owned.dev && entry?.ino === owned.ino;
}
try {
  fs.ftruncateSync(8, 0);
  if (fs.fstatSync(8).size !== 0) throw Error();
} catch {
  console.error('Unable to revoke the contents of the owned PostgreSQL password.');
  process.exitCode = 3;
}
try {
  if (ownsName()) fs.unlinkSync(password);
  if (ownsName()) throw Error();
} catch {
  console.error('The owned PostgreSQL password remains or could not be inspected after local cleanup.');
  process.exitCode = 3;
}
