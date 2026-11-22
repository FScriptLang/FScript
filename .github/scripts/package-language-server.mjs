import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

// A managed, framework-dependent distribution: runtime acquisition is the client's job.
export function packageLanguageServer(directory, output, version, revision) {
  assert.match(version, /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/);
  assert.match(revision, /^[a-f0-9]{40}$/);
  const files = readdirSync(directory);
  for (const name of ['FScript.LanguageServer.dll', 'FScript.Language.dll', 'FScript.Runtime.dll',
    'FScript.CSharpInterop.dll', 'FScript.Extensibility.dll', 'FScript.LanguageServer.deps.json',
    'FScript.LanguageServer.runtimeconfig.json']) assert.ok(files.includes(name), `Missing ${name}`);
  const runtime = JSON.parse(readFileSync(path.join(directory, 'FScript.LanguageServer.runtimeconfig.json')));
  const runtimeVersion = runtime.runtimeOptions.framework.version;
  assert.match(runtimeVersion, /^\d+\.\d+\.\d+$/);
  const metadata = {schemaVersion: 1, version, revision, runtime: runtimeVersion.split('.').slice(0, 2).join('.')};
  assert.equal(runtime.runtimeOptions.tfm, `net${metadata.runtime}`);
  writeFileSync(path.join(directory, 'server.json'), JSON.stringify(metadata, null, 2) + '\n');
  mkdirSync(output, {recursive: true});
  const name = `fscript-lsp-${version}-dotnet.zip`;
  // zip updates existing archives; remove any previous output to avoid retaining stale files.
  rmSync(path.join(output, name), {force: true});
  execFileSync('zip', ['-q', '-r', path.resolve(output, name), '.'], {cwd: directory});
  const sha256 = createHash('sha256').update(readFileSync(path.join(output, name))).digest('hex');
  const manifest = {...metadata, archive: {name, sha256}};
  writeFileSync(path.join(output, `fscript-lsp-${version}.json`), JSON.stringify(manifest, null, 2) + '\n');
  return manifest;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [directory, output, version, revision] = process.argv.slice(2);
  packageLanguageServer(directory, output, version, revision);
}
