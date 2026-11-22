import assert from 'node:assert/strict';
import {test} from 'node:test';
import {mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import {packageLanguageServer} from './package-language-server.mjs';

function fixture(t) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'fscript-lsp-package-'));
  t.after(() => rmSync(root, {recursive: true, force: true}));
  const directory = path.join(root, 'server'), output = path.join(root, 'assets');
  mkdirSync(directory);
  for (const file of ['FScript.LanguageServer.dll', 'FScript.Language.dll', 'FScript.Runtime.dll',
    'FScript.CSharpInterop.dll', 'FScript.Extensibility.dll', 'FScript.LanguageServer.deps.json']) writeFileSync(path.join(directory, file), 'fixture');
  writeFileSync(path.join(directory, 'FScript.LanguageServer.runtimeconfig.json'), JSON.stringify({runtimeOptions: {tfm:'net11.0', framework: {version:'11.0.0'}}}));
  mkdirSync(path.join(directory, 'cs'));
  writeFileSync(path.join(directory, 'cs/messages.resources.dll'), 'nested resource');
  return {directory, output};
}

test('portable server assets contain complete dependency trees, provenance, runtime and a verifiable checksum', t => {
  const {directory,output} = fixture(t);
  const manifest = packageLanguageServer(directory,output,'0.95.4','a'.repeat(40));
  assert.deepEqual(JSON.parse(readFileSync(path.join(output,'fscript-lsp-0.95.4.json'))),manifest);
  const archive = path.join(output,manifest.archive.name);
  assert.equal(createHash('sha256').update(readFileSync(archive)).digest('hex'),manifest.archive.sha256);
  const embedded = JSON.parse(execFileSync('unzip',['-p',archive,'server.json']));
  const {archive:_, ...metadata} = manifest;
  assert.deepEqual(embedded,metadata);
  assert.match(execFileSync('unzip',['-Z1',archive],{encoding:'utf8'}),/cs\/messages.resources.dll/);
  rmSync(path.join(directory,'cs'),{recursive:true});
  packageLanguageServer(directory,output,'0.95.4','a'.repeat(40));
  assert.doesNotMatch(execFileSync('unzip',['-Z1',archive],{encoding:'utf8'}),/messages.resources.dll/);
});

test('packaging refuses incomplete servers and noncanonical release identities', t => {
  const {directory,output} = fixture(t);
  assert.throws(() => packageLanguageServer(directory,output,'v0.95.4','a'.repeat(40)));
  assert.throws(() => packageLanguageServer(directory,output,'0.95.4','main'));
  rmSync(path.join(directory,'FScript.Runtime.dll'));
  assert.throws(() => packageLanguageServer(directory,output,'0.95.4','a'.repeat(40)), /Missing FScript.Runtime/);
});
