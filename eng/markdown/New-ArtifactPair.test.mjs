import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const producer = path.join(root, 'eng/markdown/New-ArtifactPair.mjs');
const workflow = path.join(root, '.github/workflows/component.yml');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8'));

// Keep the real pair for a subsequent authorized source-host check. Nothing here
// dispatches a producer workflow, records an immutable issuance, or publishes.
test('release version policy and one private artifact pair', {timeout: 300_000}, async t => {
  const temporaryBase = process.env.SYNTAMARK_TEST_ROOT || os.tmpdir();
  fs.mkdirSync(temporaryBase, {recursive: true});
  const output = fs.mkdtempSync(path.join(temporaryBase, 'syntamark-artifact-tests-'));
  const commands = [];
  const env = {...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1'};
  function run(command, args, options = {}) {
    const result = spawnSync(command, args, {
      cwd: root, env, encoding: 'utf8', windowsHide: true,
      timeout: 120_000, maxBuffer: 16 * 1024 * 1024, ...options,
    });
    commands.push({command, arguments: args, exitCode: result.status});
    if (result.error) throw result.error;
    return result;
  }
  function success(result) {
    assert.equal(result.status, 0, (result.stdout + result.stderr).slice(-16_384));
    return result.stdout.trim();
  }
  function failure(result, message) {
    assert.notEqual(result.status, 0, 'The actual CLI unexpectedly accepted an invalid operation');
    assert.match(result.stdout + result.stderr, message);
  }
  const cli = (...args) => run(process.execPath, [producer, ...args]);
  const sourceCommit = success(run('git', ['rev-parse', 'HEAD']));
  assert.match(sourceCommit, /^[0-9a-f]{40}$/);
  const recorded = new Set(readJson(path.join(root, 'eng/markdown/immutable-versions.json')).map(item => item.componentVersion));
  let previewNumber = 4;
  while (recorded.has('2.0.0-preview.' + previewNumber)) previewNumber++;
  const unusedPreview = '2.0.0-preview.' + previewNumber;
  // After production records 1.1.0, private CI must respect its immutable status.
  const pairVersion = recorded.has('1.1.0') ? unusedPreview : '1.1.0';
  const stage = path.join(output, 'private-pair');

  // An unissued 1.1.0 fails on the old allowlist before any pack work.
  const prepared = JSON.parse(success(cli('prepare', sourceCommit, pairVersion, stage)));
  assert.equal(prepared.state, 'PREPARED_NOT_PACKED');
  assert.equal(prepared.componentVersion, pairVersion);
  assert.equal(readJson(path.join(stage, 'prepared.json')).metadata.sourceCommit, sourceCommit);

  await t.test('prepare keeps supported versions and refuses unsupported boundaries and immutable stages', () => {
    for (const version of ['1.0.0', '1.0.1', '1.1.0', unusedPreview, '2.0.0-preview.20261106']) {
      const candidate = path.join(output, 'accepted-' + version);
      if (recorded.has(version)) {
        failure(cli('prepare', sourceCommit, version, candidate), /Version is already immutable/);
        assert.equal(fs.existsSync(candidate), false);
        continue;
      }
      const accepted = JSON.parse(success(cli('prepare', sourceCommit, version, candidate)));
      assert.equal(accepted.componentVersion, version);
      assert.equal(readJson(path.join(candidate, 'prepared.json')).metadata.sourceCommit, sourceCommit);
    }
    for (const version of ['', '1.0.2', '1.1.1', '1.2.0', '2.0.0', '1.1', '01.1.0',
      '1.1.0-preview.1', '2.0.0-preview.0', '2.0.0-preview.01', '3.0.0']) {
      const candidate = path.join(output, 'rejected-' + (version || 'empty'));
      failure(cli('prepare', sourceCommit, version, candidate), /Require one new immutable/);
      assert.equal(fs.existsSync(candidate), false, 'Rejected versions must not create a stage');
    }
    const immutableStage = path.join(output, 'immutable-preview-3');
    failure(cli('prepare', sourceCommit, '2.0.0-preview.3', immutableStage), /Version is already immutable/);
    assert.equal(fs.existsSync(immutableStage), false);
    const before = fs.readFileSync(path.join(stage, 'prepared.json'));
    failure(cli('prepare', sourceCommit, pairVersion, stage), /Never overwrite an artifact stage/);
    assert.deepEqual(fs.readFileSync(path.join(stage, 'prepared.json')), before);
    assert.equal(JSON.parse(success(cli('check-stage', stage))).state, 'STAGE_BYTES_PASS');
  });

  await t.test('the actual formal-workflow guard rejects reruns and consumes failed issuance attempts', () => {
    const text = fs.readFileSync(workflow, 'utf8').replaceAll('\r\n', '\n');
    const step = text.split('      - name: Refuse repeated formal issuance\n')[1]?.split('\n      - ')[0];
    assert.ok(step, 'Find the real formal issuance step, rather than a copied policy');
    const inline = step.match(/node --input-type=module <<'JS'\r?\n([\s\S]*?)^\s*JS\s*$/m);
    assert.ok(inline, 'Find the actual workflow inline JavaScript');
    const guard = inline[1].split('\n').map(line => line.replace(/^ {10}/, '')).join('\n');
    const guardDirectory = path.join(output, 'workflow-guard');
    fs.mkdirSync(guardDirectory);
    const previous = {id: '999', event: 'workflow_dispatch', head_branch: 'main',
      path: '.github/workflows/component.yml', display_title: 'Formal Markdown pair 1.1.0',
      status: 'completed', conclusion: 'failure'};
    function invoke(version, attempt = '1', runs = []) {
      fs.writeFileSync(path.join(guardDirectory, 'previous-runs.json'), JSON.stringify([{workflow_runs: runs}]));
      return run(process.execPath, ['--input-type=module'], {cwd: guardDirectory, input: guard,
        env: {...env, COMPONENT_VERSION: version, GITHUB_RUN_ATTEMPT: attempt, GITHUB_RUN_ID: '1000'}});
    }
    for (const version of ['1.0.0', '1.0.1', '1.1.0', '2.0.0-preview.4', '2.0.0-preview.20261106'])
      success(invoke(version));
    for (const version of ['1.0.2', '1.1.1', '2.0.0', '01.1.0', '1.1.0-preview.1', '2.0.0-preview.0'])
      failure(invoke(version), /Select a fresh supported version/);
    failure(invoke('1.1.0', '2'), /Formal pairs cannot be regenerated by a rerun/);
    failure(invoke('1.1.0', '1', [previous]), /A formal issuance attempt already exists/);
    success(invoke('1.1.0', '1', [{...previous, id: '1000'}]));
    success(invoke('1.1.0', '1', [{...previous, display_title: 'Formal Markdown pair 1.0.1'}]));
    success(invoke('1.1.0', '1', [{...previous, event: 'push'}]));
  });

  await t.test('packaged READMEs update the three install selections without rewriting history or icon text', () => {
    const originalReadme = fs.readFileSync(path.join(root, 'README.md'));
    const historical = 'Historical 1.0.1 remains immutable; ![Historical 1.0.1 icon](images/icon.png?version=1.0.1)';
    function installSelections(readme, version) {
      assert.ok(readme.includes('dotnet add package Syntamark --version ' + version));
      assert.ok(readme.includes('<SyntamarkSourceVersion>' + version + '</SyntamarkSourceVersion>'));
      assert.ok(readme.includes('<PackageReference Include="Syntamark.Source" Version="[' + version + ']"'));
    }
    for (const kind of ['runtime', 'source']) {
      const actual = fs.readFileSync(path.join(stage, kind, 'README.md'), 'utf8');
      installSelections(actual, pairVersion);
      assert.ok(actual.includes('公開済み1.0.1'), 'Keep the actual historical release statement');
    }
    const fixture = path.join(output, 'readme-fixture');
    success(run('git', ['clone', '--local', '--no-hardlinks', '--quiet', root, fixture]));
    // A clone has committed files; exercise the current producer under review.
    const fixtureProducer = path.join(fixture, 'eng/markdown/New-ArtifactPair.mjs');
    fs.copyFileSync(producer, fixtureProducer);
    for (const legacy of ['1.0.0', '1.0.1', '1.1.0', '2.0.0-preview.20261106']) {
      const selected = legacy === pairVersion ? unusedPreview : pairVersion;
      const changed = originalReadme.toString()
        .replace(/(dotnet add package Syntamark --version )\S+/, (_, prefix) => prefix + legacy)
        .replace(/(<SyntamarkSourceVersion>)[^<]+/, (_, prefix) => prefix + legacy)
        .replace(/(<PackageReference Include="Syntamark.Source" Version="\[)[^\]]+/, (_, prefix) => prefix + legacy)
        + '\n' + historical + '\n';
      fs.writeFileSync(path.join(fixture, 'README.md'), changed);
      success(run('git', ['add', '--', 'README.md'], {cwd: fixture}));
      const tree = success(run('git', ['write-tree'], {cwd: fixture}));
      const fixtureCommit = success(run('git', ['-c', 'user.name=Artifact test', '-c', 'user.email=artifact-test@example.invalid',
        'commit-tree', tree, '-p', sourceCommit, '-m', 'Disposable README fixture'], {cwd: fixture}));
      const fixtureStage = path.join(output, 'readme-' + legacy);
      success(run(process.execPath, [fixtureProducer, 'prepare', fixtureCommit, selected, fixtureStage]));
      for (const kind of ['runtime', 'source']) {
        const actual = fs.readFileSync(path.join(fixtureStage, kind, 'README.md'), 'utf8');
        installSelections(actual, selected);
        assert.ok(actual.includes(historical), 'Keep historical version and icon text byte-for-byte');
      }
    }
    assert.deepEqual(fs.readFileSync(path.join(root, 'README.md')), originalReadme);
  });

  await t.test('one real restored and packed pair seals, checks, and refuses altered bytes and identities', () => {
    const packages = path.join(stage, 'packages');
    const feed = path.join(stage, 'feed');
    const dotnet = process.env.SYNTAMARK_DOTNET || 'dotnet';
    for (const kind of ['runtime', 'source']) {
      const project = path.join(stage, kind, kind + '.csproj');
      success(run(dotnet, ['restore', project, '--configfile', path.join(stage, 'NuGet.Config'), '--packages', packages],
        {env: {...env, NUGET_PACKAGES: packages}}));
    }
    success(cli('check-stage', stage));
    for (const kind of ['runtime', 'source']) {
      const project = path.join(stage, kind, kind + '.csproj');
      success(run(dotnet, ['pack', project, '-c', 'Release', '--no-restore', '-o', feed,
        '-m:1', '-nr:false', '-p:UseSharedCompilation=false'], {env: {...env, NUGET_PACKAGES: packages}}));
    }
    const sealed = JSON.parse(success(cli('seal', stage)));
    assert.equal(sealed.state, 'PACKED_NOT_HOST_VERIFIED');
    const manifestPath = path.join(stage, 'pair-manifest.json');
    const manifestBytes = fs.readFileSync(manifestPath);
    const manifestSha256 = hash(manifestBytes);
    const manifest = JSON.parse(manifestBytes);
    assert.equal(manifest.sourceCommit, sourceCommit);
    assert.equal(manifest.componentVersion, pairVersion);
    const checked = JSON.parse(success(cli('check-pair', stage, manifestSha256)));
    assert.equal(checked.state, 'SEALED_PAIR_BYTES_PASS');
    assert.equal(checked.hostVerificationIncluded, false);
    failure(cli('seal', stage), /Never reseal\/overwrite a pair/);
    assert.deepEqual(fs.readFileSync(manifestPath), manifestBytes);
    failure(cli('check-pair', stage, '0'.repeat(64)), /Sealed manifest hash mismatch/);

    function copyPair(name) {
      const copy = path.join(output, name);
      fs.mkdirSync(path.join(copy, 'feed'), {recursive: true});
      fs.copyFileSync(manifestPath, path.join(copy, 'pair-manifest.json'));
      for (const artifact of Object.values(manifest.artifacts))
        fs.copyFileSync(path.join(feed, artifact.fileName), path.join(copy, 'feed', artifact.fileName));
      return copy;
    }
    const corrupt = copyPair('copied-byte-mutation');
    const corruptPackage = path.join(corrupt, 'feed', manifest.artifacts.runtime.fileName);
    const bytes = fs.readFileSync(corruptPackage);
    bytes[bytes.length - 1] ^= 1;
    fs.writeFileSync(corruptPackage, bytes);
    failure(cli('check-pair', corrupt, manifestSha256), /Sealed runtime package hash mismatch/);
    const wrongIdentity = copyPair('copied-artifact-identity');
    const changed = structuredClone(manifest);
    changed.artifacts.source.packageId = 'Wrong.Source';
    const changedBytes = Buffer.from(JSON.stringify(changed, null, 2) + '\n');
    fs.writeFileSync(path.join(wrongIdentity, 'pair-manifest.json'), changedBytes);
    failure(cli('check-pair', wrongIdentity, hash(changedBytes)), /Artifact identity mismatch/);
    success(cli('check-pair', stage, manifestSha256));

    const receipt = {scope: 'PRIVATE_LOCAL_TEST_PAIR_NOT_FORMAL_ISSUANCE',
      producerWorkflowDispatched: false, published: false, hostVerificationIncluded: false,
      stage, feed, packages, sourceCommit, componentVersion: pairVersion, manifestSha256,
      canonicalSourceHash: manifest.canonicalSourceHash, parserVersion: manifest.parserVersion,
      producerSha256: hash(fs.readFileSync(producer)), workflowSha256: hash(fs.readFileSync(workflow)),
      artifacts: manifest.artifacts, commands};
    const receiptPath = path.join(output, 'private-pair-receipt.json');
    fs.writeFileSync(receiptPath, JSON.stringify(receipt, null, 2) + '\n', {flag: 'wx'});
    console.log('PRIVATE_ARTIFACT_TEST_RECEIPT=' + receiptPath);
  });
});
