import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
const root = fileURLToPath(new URL('../../', import.meta.url));
const pins = JSON.parse(await readFile(new URL('./sources.json', import.meta.url)));
const downloads = join(root, '.ffmpeg-build/downloads'); await mkdir(downloads, { recursive: true });
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const version = pins.toolchain.packageVersion;
for (const [name, sha] of Object.entries(pins.toolchain.packages)) {
    const path = join(process.env.DOTNET_ROOT ?? '/workspace/shared/dotnet-11', 'packs', name, version, name + '.nupkg');
    let bytes = await readFile(path).catch(e => { if (e.code === 'ENOENT') return null; throw e; });
    if (!bytes) {
        // Some workload installers remove the archive after extracting the pack.
        // Retrieve only its exact official NuGet archive for provenance checks;
        // no alternative compiler is installed or executed here.
        const cache = join(root, '.ffmpeg-build/toolchain-archives'); await mkdir(cache, { recursive: true });
        const archive = join(cache, name + '.nupkg');
        bytes = await readFile(archive).catch(e => { if (e.code === 'ENOENT') return null; throw e; });
        if (!bytes) {
            const id = name.toLowerCase();
            const response = await fetch(`https://api.nuget.org/v3-flatcontainer/${id}/${version}/${id}.${version}.nupkg`, { signal: AbortSignal.timeout(120000), redirect: 'error' });
            if (!response.ok) throw new Error(`Pinned toolchain archive unavailable: ${name}`);
            bytes = new Uint8Array(await response.arrayBuffer());
        }
        if (digest(bytes) !== sha) throw new Error(`Official SDK package checksum mismatch: ${name}`);
        await writeFile(archive, bytes);
    }
    if (digest(bytes) !== sha) throw new Error(`Official SDK package checksum mismatch: ${name}`);
}
for (const [name, url, sha] of [
    [`ffmpeg-${pins.ffmpeg.version}.tar.xz`, pins.ffmpeg.url, pins.ffmpeg.sha256],
    [`ffmpeg-${pins.ffmpeg.version}.tar.xz.asc`, pins.ffmpeg.signatureUrl, pins.ffmpeg.signatureSha256],
    ['ffmpeg-devel.asc', pins.ffmpeg.signingKeyUrl, pins.ffmpeg.signingKeySha256]
]) {
    const path = join(downloads, name);
    let bytes = await readFile(path).catch(e => { if (e.code === 'ENOENT') return null; throw e; });
    if (!bytes) {
        const response = await fetch(url, { signal: AbortSignal.timeout(120000), redirect: 'error' });
        if (!response.ok) throw new Error(`Source download failed: ${url} (${response.status})`);
        bytes = new Uint8Array(await response.arrayBuffer());
    }
    if (digest(bytes) !== sha) throw new Error(`Pinned source checksum mismatch: ${name}`);
    await writeFile(path, bytes);
}
const homedir = join(root, '.ffmpeg-build/gnupg'); await mkdir(homedir, { recursive: true, mode: 0o700 });
const options = ['--batch', '--no-autostart', '--homedir', homedir];
execFileSync('gpg', [...options, '--import', join(downloads, 'ffmpeg-devel.asc')], { stdio: ['ignore', 'ignore', 'pipe'] });
const result = execFileSync('gpg', [...options, '--status-fd', '1', '--verify', join(downloads, `ffmpeg-${pins.ffmpeg.version}.tar.xz.asc`), join(downloads, `ffmpeg-${pins.ffmpeg.version}.tar.xz`)], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
if (!result.includes(`[GNUPG:] VALIDSIG ${pins.ffmpeg.fingerprint} `)) throw new Error('FFmpeg signature did not match the pinned official fingerprint');
console.log('FFmpeg source/signature and official toolchain package checksums verified.');
