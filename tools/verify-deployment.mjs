import { setTimeout } from 'node:timers/promises';
const [base, sha] = process.argv.slice(2);
if (!base?.startsWith('https://') || !/^[a-f0-9]{40}$/.test(sha || '')) throw new Error('Expected production HTTPS URL and commit SHA.');
let ready = false, deployedBuild;
for (let attempt = 0; attempt < 18; attempt++) {
  try {
    const response = await fetch(`${base}/build.json?verify=${sha}`, { signal: AbortSignal.timeout(20000), cache: 'no-store' });
    if (response.ok) {
      const build = await response.json();
      if (build.commit === sha) { deployedBuild = build; ready = true; break; }
    }
  } catch { /* Allow the new edge deployment to propagate. */ }
  await setTimeout(10000);
}
if (!ready) throw new Error('Production did not serve the expected commit within three minutes.');
for (const path of ['/', '/download/', '/favicon.svg', '/favicon.ico']) {
  const response = await fetch(base + path, { signal: AbortSignal.timeout(20000) });
  if (!response.ok) throw new Error(`${path}: HTTP ${response.status}`);
  const data = await response.arrayBuffer();
  if (!data.byteLength) throw new Error(`Empty ${path}`);
  if (path === '/download/') {
    const html = new TextDecoder().decode(data);
    for (const name of ['MusicMachine-win-x64-setup.exe', 'install-musicmachine.sh', 'MusicMachine-linux-x64.tar.gz', 'release.json']) {
      if (!html.includes(name)) throw new Error(`Download page lacks ${name}`);
    }
    if (html.includes('__DOWNLOAD_BASE__')) throw new Error('Unresolved download links.');
  }
  console.log(`Verified ${path} (${data.byteLength} bytes)`);
}
console.log(`Production serves commit ${sha}.`);

if (!/^[\w.-]+\/[\w.-]+$/.test(deployedBuild.repository) || !/^\d+\.\d+\.\d+$/.test(deployedBuild.version))
  throw new Error('Production release identity is invalid.');
const releaseBase = `https://github.com/${deployedBuild.repository}/releases/download/v${deployedBuild.version}/`;
const manifestResponse = await fetch(releaseBase + 'release.json', { signal: AbortSignal.timeout(30000) });
if (!manifestResponse.ok) throw new Error(`Release manifest: HTTP ${manifestResponse.status}`);
const manifest = await manifestResponse.json();
if (manifest.commit !== sha || manifest.version !== deployedBuild.version || !Array.isArray(manifest.assets))
  throw new Error('Published release manifest does not match the production app.');
const requiredAssets = ['MusicMachine-win-x64-setup.exe', 'MusicMachine-win-x64.zip', 'MusicMachine-linux-x64.tar.gz',
  'install-musicmachine.sh', 'install-musicmachine.ps1', 'MusicMachine-browser-wasm.zip', 'MusicMachine-source.zip'];
const names = new Set(manifest.assets.map(asset => asset.name));
if (names.size !== manifest.assets.length || !requiredAssets.every(name => names.has(name)))
  throw new Error('Published release is missing required downloads or contains duplicate names.');
for (const asset of manifest.assets) {
  if (!asset.url.startsWith(releaseBase) || !/^[a-f0-9]{64}$/.test(asset.sha256) || !(asset.size > 0))
    throw new Error(`Invalid release metadata for ${asset.name}`);
  const response = await fetch(asset.url, { method: 'HEAD', signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error(`Release download ${asset.name}: HTTP ${response.status}`);
  const length = response.headers.get('content-length');
  if (length && Number(length) !== asset.size) throw new Error(`Release size mismatch: ${asset.name}`);
  console.log(`Verified release download ${asset.name} (${asset.size} bytes)`);
}
