import fs from 'node:fs/promises';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { buildRelease } from './build.mjs';
import { configureRuntime } from './configure.mjs';
import { completeBootstrap, initializeRuntime, operateRuntime } from './lifecycle.mjs';
import { loadState, RuntimeError } from './state.mjs';
import { getRuntimeStatus } from './status.mjs';

const commands = {
  build: '--revision <commit> [--nuget-seed-volume <volume>]',
  init: '--admin <username> [--release-file <file>] [--password-file <file>]\n       [--console-port <port>] [--gateway-a-port <port>] [--gateway-b-port <port>]',
  up: '', start: '', stop: '', restart: '',
  status: '[--json]',
  configure: '--environment <id> --origins-file <json-file> --username <name> --password-file <file> [--demo]',
  'complete-bootstrap': '--credentials-saved'
};
const usage = `Usage: local-runtime.sh <command> [options]

${Object.entries(commands).map(([name, args]) => '  ' + [name, args].filter(Boolean).join(' ')).join('\n')}

Use --directory <path> to select the installation for commands other than build.
Use -h or --help to display this help.`;

const options = {
  help: { type: 'boolean', short: 'h' },
  directory: { type: 'string' },
  environment: { type: 'string' },
  'origins-file': { type: 'string' },
  username: { type: 'string' },
  demo: { type: 'boolean' },
  revision: { type: 'string' },
  admin: { type: 'string' },
  'password-file': { type: 'string' },
  'release-file': { type: 'string' },
  'nuget-seed-volume': { type: 'string' },
  'credentials-saved': { type: 'boolean' },
  json: { type: 'boolean' },
  'console-port': { type: 'string' },
  'gateway-a-port': { type: 'string' },
  'gateway-b-port': { type: 'string' }
};

function parseCommand(args) {
  try {
    return parseArgs({ args, allowPositionals: true, options });
  } catch {
    throw new RuntimeError(usage);
  }
}

async function readOrigins(filename) {
  try {
    return JSON.parse(await fs.readFile(filename, 'utf8'));
  } catch {
    throw new RuntimeError('Could not read the origins file as JSON.');
  }
}

async function main(args) {
  if (Number(process.versions.node.split('.')[0]) < 22) throw new RuntimeError('Node 22+ required.', 3);
  const { values, positionals } = parseCommand(args);
  const [command] = positionals;
  if (positionals.length > 1 || (command !== undefined && !Object.hasOwn(commands, command))) throw new RuntimeError(usage);
  if (values.help) {
    console.log(usage);
    return;
  }
  if (!command) throw new RuntimeError(usage);
  const directory = path.resolve(values.directory ?? '.runtime/local');
  let result;
  switch (command) {
    case 'build':
      result = await buildRelease({
        revision: values.revision,
        directory: path.resolve('.runtime/local-build'),
        nugetSeedVolume: values['nuget-seed-volume']
      });
      break;
    case 'init': {
      const portOptions = { console: 'console-port', gatewayA: 'gateway-a-port', gatewayB: 'gateway-b-port' };
      const ports = Object.fromEntries(Object.entries(portOptions)
        .filter(([, key]) => values[key] !== undefined)
        .map(([name, key]) => [name, Number(values[key])]));
      result = await initializeRuntime({
        directory, bootstrapUsername: values.admin, passwordFile: values['password-file'],
        releaseFile: values['release-file'], ports
      });
      break;
    }
    case 'up':
    case 'start':
    case 'stop':
    case 'restart':
      result = await operateRuntime(command, await loadState(directory), { directory });
      break;
    case 'complete-bootstrap':
      await completeBootstrap(await loadState(directory), { directory, credentialsSaved: values['credentials-saved'] });
      result = { bootstrapFileRemoved: true };
      break;
    case 'status':
      result = await getRuntimeStatus(await loadState(directory), { directory });
      break;
    case 'configure': {
      if (!values['origins-file']) throw new RuntimeError('Explicit origins JSON file required.');
      const state = await loadState(directory);
      result = await configureRuntime(state, {
        environmentId: values.environment,
        allowedOrigins: await readOrigins(values['origins-file']),
        username: values.username,
        passwordFile: values['password-file'],
        demoEnabled: values.demo
      }, { directory });
      break;
    }
    default:
      throw new RuntimeError(usage);
  }

  console.log(JSON.stringify(result, null, 2));
  if (command === 'init') console.log('Bootstrap password file (content not printed): ' + path.join(directory, 'secrets/bootstrap-password'));
}

try {
  await main(process.argv.slice(2));
} catch (error) {
  console.error(error instanceof RuntimeError ? error.message : 'Local runtime operation failed; owned state retained.');
  process.exitCode = error.exitCode ?? 6;
}
