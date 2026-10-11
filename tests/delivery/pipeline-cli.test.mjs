import test from 'node:test';import assert from 'node:assert/strict';import {pipelineCli} from '../../scripts/delivery/pipeline-cli.mjs';
for(const args of [[],['verify'],['e2e','main'],['unknown','a'.repeat(40)],['verify','dir','unexpected']])test('CLI rejects ambiguous/empty scope '+args.join(' '),async()=>assert.rejects(pipelineCli(args)));
