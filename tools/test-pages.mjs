import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { gzipSync, gunzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
const script = fileURLToPath(new URL('./prepare-pages.mjs', import.meta.url));
async function fixture(t) {
  const root = await mkdtemp(join(tmpdir(), 'music-pages-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const source = join(root, 'wwwroot'), output = join(root, 'pages');
  await mkdir(source); await mkdir(join(source, '_framework'));
  await writeFile(join(source, 'index.html'), '<!doctype html><script type="module" src="./main.js"></script>');
  await writeFile(join(source, 'main.js'), '// browser fixture');
  const run = () => spawnSync(process.execPath, [script, source, output], { encoding: 'utf8', env: { ...process.env, MUSIC_RELEASE_VERSION: '1.2.3', GITHUB_SHA: 'a'.repeat(40), GITHUB_REPOSITORY: 'example/MusicMachine' } });
  return { source, output, run };
}
test('preserves app and adds versioned download page, icons and exact commit', async t => {
  const f = await fixture(t), result = f.run(); assert.equal(result.status, 0, result.stderr);
  assert.match(await readFile(join(f.output, 'index.html'), 'utf8'), /main.js/);
  assert.equal(JSON.parse(await readFile(join(f.output, 'build.json'))).commit, 'a'.repeat(40));
  assert.match(await readFile(join(f.output, 'download/index.html'), 'utf8'), /releases\/download\/v1.2.3\/MusicMachine-win-x64-setup.exe/);
  assert.ok((await readFile(join(f.output, 'favicon.ico'))).length > 0);
  assert.match(await readFile(join(f.output, '_headers'), 'utf8'), /Cross-Origin-Embedder-Policy: require-corp/);
  assert.notEqual(f.run().status, 0, 'Must reject stale output');
});
test('compresses oversized WASM and injects integrity-checked resource loader', async t => {
  const f = await fixture(t), wasm = Buffer.alloc(26 * 1024 * 1024, 3);
  await writeFile(join(f.source, '_framework/app.wasm'), wasm);
  const result = f.run(); assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(gunzipSync(await readFile(join(f.output, '_framework/app.wasm.gz'))), wasm);
  const html = await readFile(join(f.output, 'index.html'), 'utf8');
  const bootstrap = html.match(/src="\.\/([^\"]+)"/)[1];
  const code = await readFile(join(f.output, bootstrap), 'utf8');
  assert.match(code, /DecompressionStream/); assert.match(code, /hash !== integrity/);
});
test('rejects a mismatched supplied gzip', async t => {
  const f = await fixture(t);
  await writeFile(join(f.source, '_framework/app.wasm'), Buffer.alloc(26 * 1024 * 1024));
  await writeFile(join(f.source, '_framework/app.wasm.gz'), gzipSync(Buffer.from('wrong')));
  const result = f.run(); assert.notEqual(result.status, 0); assert.match(result.stderr, /Gzip content mismatch/);
});
test('rejects unexpected hosting code and oversized non-WASM assets', async t => {
  const f = await fixture(t); await writeFile(join(f.source, '_worker.js'), 'unsafe');
  assert.notEqual(f.run().status, 0);
  await rm(f.output, { recursive: true, force: true }); await rm(join(f.source, '_worker.js'));
  await writeFile(join(f.source, 'large.bin'), Buffer.alloc(26 * 1024 * 1024));
  const result = f.run(); assert.notEqual(result.status, 0); assert.match(result.stderr, /exceeds 25 MiB/);
});
test('rejects symbolic links', { skip: process.platform === 'win32' }, async t => {
  const f = await fixture(t); await symlink(join(f.source, 'main.js'), join(f.source, 'link.js'));
  assert.notEqual(f.run().status, 0);
});


test('missing Pages secrets skip deployment without exposing values', async t => {
  const { pagesCredentialStatus } = await import('./check-pages-credentials.mjs');
  assert.deepEqual(pagesCredentialStatus({}), { configured: false, missing: ['CLOUDFLARE_ACCOUNT_ID', 'CLOUDFLARE_API_TOKEN'] });
  assert.deepEqual(pagesCredentialStatus({ CLOUDFLARE_ACCOUNT_ID: 'test-account' }), { configured: false, missing: ['CLOUDFLARE_API_TOKEN'] });
  assert.equal(pagesCredentialStatus({ CLOUDFLARE_ACCOUNT_ID: 'test-account', CLOUDFLARE_API_TOKEN: 'test-token' }).configured, true);
  const root = await mkdtemp(join(tmpdir(), 'music-pages-credentials-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const output = join(root, 'output'), summary = join(root, 'summary');
  const result = spawnSync(process.execPath, [fileURLToPath(new URL('./check-pages-credentials.mjs', import.meta.url))], {
    encoding: 'utf8', env: { ...process.env, CLOUDFLARE_ACCOUNT_ID: '', CLOUDFLARE_API_TOKEN: 'never-print-this-test-value', GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary }
  });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(await readFile(output, 'utf8'), 'configured=false\n');
  assert.match(result.stdout, /deployment skipped/);
  assert.doesNotMatch(result.stdout, /never-print-this-test-value/);
  assert.doesNotMatch(await readFile(summary, 'utf8'), /never-print-this-test-value/);
  const workflow = await readFile(new URL('../.github/workflows/pages.yml', import.meta.url), 'utf8');
  assert.match(workflow, /if: steps\.credentials\.outputs\.configured == 'true'/);
  const build = await readFile(new URL('../.github/workflows/build.yml', import.meta.url), 'utf8');
  assert.match(build, /needs: \[prepare, desktop\]/);
  assert.doesNotMatch(build, /CLOUDFLARE_API_TOKEN/);
});


test('deployment configuration uses the requested music-machine-synth Pages project', async () => {
  const config = JSON.parse(await readFile(new URL('../wrangler.jsonc', import.meta.url), 'utf8'));
  assert.equal(config.name, 'music-machine-synth');
  const workflow = await readFile(new URL('../.github/workflows/pages.yml', import.meta.url), 'utf8');
  assert.match(workflow, /--project-name music-machine-synth /);
  assert.match(workflow, /https:\/\/music-machine-synth\.pages\.dev/);
  assert.doesNotMatch(workflow, /https:\/\/musicmachine\.pages\.dev/);
  const ensure = await readFile(new URL('./ensure-pages-project.mjs', import.meta.url), 'utf8');
  assert.match(ensure, /name: 'music-machine-synth'/);
});

test('Pages requires its own successful trusted browser job, independent of native release status', async () => {
  const { successfulBrowserBuild } = await import('./check-browser-build.mjs');
  const run = { status: 'completed', conclusion: 'failure', name: 'Build and release', path: '.github/workflows/build.yml',
    event: 'push', head_branch: 'main', head_sha: 'a'.repeat(40), head_repository: { full_name: 'owner/MusicMachine' } };
  const jobs = { jobs: [{ name: 'browser', status: 'completed', conclusion: 'success' }, { name: 'desktop', conclusion: 'failure' }] };
  assert.equal(successfulBrowserBuild(run, jobs, 'owner/MusicMachine'), true);
  for (const conclusion of ['failure', 'cancelled', 'skipped', null])
    assert.equal(successfulBrowserBuild(run, { jobs: [{ name: 'browser', status: 'completed', conclusion }] }, 'owner/MusicMachine'), false);
  assert.equal(successfulBrowserBuild(run, { jobs: [] }, 'owner/MusicMachine'), false);
  assert.equal(successfulBrowserBuild(run, { jobs: [jobs.jobs[0], jobs.jobs[0]] }, 'owner/MusicMachine'), false);
  for (const change of [{ event: 'pull_request' }, { head_branch: 'build/untrusted' }, { status: 'in_progress' },
    { path: 'other.yml' }, { head_sha: 'bad' }, { head_repository: { full_name: 'foreign/MusicMachine' } }])
    assert.throws(() => successfulBrowserBuild({ ...run, ...change }, jobs, 'owner/MusicMachine'), /Untrusted/);
  const workflow = await readFile(new URL('../.github/workflows/pages.yml', import.meta.url), 'utf8');
  assert.match(workflow, /check-browser-build\.mjs/);
  assert.doesNotMatch(workflow, /workflow_run\.conclusion == 'success'/);
});
