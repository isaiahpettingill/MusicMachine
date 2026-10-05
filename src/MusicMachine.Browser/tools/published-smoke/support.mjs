import { readFile, realpath, stat } from 'node:fs/promises';
import { extname, resolve, sep } from 'node:path';
import { inflateSync } from 'node:zlib';

const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
    '.json': 'application/json', '.wasm': 'application/wasm', '.gz': 'application/gzip', '.css': 'text/css; charset=utf-8',
    '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.png': 'image/png', '.woff': 'font/woff', '.woff2': 'font/woff2',
    '.ttf': 'font/ttf', '.txt': 'text/plain; charset=utf-8', '.md': 'text/plain; charset=utf-8', '.xz': 'application/x-xz' };
export function parsePagesHeaders(text) {
    const rules = []; let rule;
    for (const line of text.split(/\r?\n/)) {
        if (!line.trim() || line.trimStart().startsWith('#')) continue;
        if (!/^\s/.test(line)) {
            if (!line.startsWith('/') || /[\r\n]/.test(line)) throw new Error('Unsupported Pages header path');
            const escaped = line.trim().split('*').map(p => p.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('.*');
            rule = { match: new RegExp('^' + escaped + '$'), headers: {} }; rules.push(rule);
        } else {
            const match = /^\s+([A-Za-z0-9-]+):\s*(.+)$/.exec(line);
            if (!rule || !match) throw new Error('Malformed Pages header rule');
            rule.headers[match[1].toLowerCase()] = match[2];
        }
    }
    return rules;
}
export async function readPreparedAsset(root, requestUrl, rules) {
    let pathname;
    try { pathname = decodeURIComponent(requestUrl.split('?')[0]); } catch { return { status: 400, body: Buffer.from('Bad URL'), headers: {} }; }
    if (!pathname.startsWith('/') || pathname.includes('\\') || pathname.includes('\0') || pathname.split('/').some(p => p === '..' || p.startsWith('.')) || /^\/_(headers|redirects|worker\.js)$/.test(pathname))
        return { status: 403, body: Buffer.from('Forbidden'), headers: {} };
    const file = resolve(root, '.' + (pathname.endsWith('/') ? pathname + 'index.html' : pathname));
    try {
        const actualRoot = await realpath(root), actualFile = await realpath(file);
        if (!actualFile.startsWith(actualRoot + sep) || !(await stat(actualFile)).isFile()) return { status: 403, body: Buffer.from('Forbidden'), headers: {} };
        const headers = { 'content-type': types[extname(file)] ?? 'application/octet-stream' };
        for (const rule of rules) if (rule.match.test(pathname)) Object.assign(headers, rule.headers);
        const body = await readFile(actualFile); headers['content-length'] = String(body.length);
        // Deliberately no Content-Encoding: the actual Pages gzip resource loader
        // receives a gzip file and verifies the decompressed WASM itself.
        return { status: 200, body, headers };
    } catch (error) {
        if (['ENOENT', 'ENOTDIR'].includes(error.code)) return { status: 404, body: Buffer.from('Not found'), headers: {} };
        throw error;
    }
}
export function viewPreferences(text) {
    const values = Object.fromEntries(String(text ?? '').split('\n').filter(line => line.includes('=')).map(line => { const i = line.indexOf('='); return [line.slice(0, i), line.slice(i + 1).trim()]; }));
    return { library: values.library !== 'false', inspector: values.inspector === 'true', theme: values.theme ?? 'catppuccin-mocha', workspace: values.workspace ?? 'Tracker' };
}
export function inspectPng(png, includeSamples = false) {
    if (!Buffer.isBuffer(png) || !png.subarray(0, 8).equals(Buffer.from('89504e470d0a1a0a', 'hex'))) throw new Error('Not a PNG screenshot');
    let width, height, channels, interlace; const compressed = [];
    for (let offset = 8; offset + 12 <= png.length;) {
        const length = png.readUInt32BE(offset), type = png.toString('ascii', offset + 4, offset + 8), begin = offset + 8;
        if (begin + length + 4 > png.length) throw new Error('Truncated PNG');
        if (type === 'IHDR') {
            width = png.readUInt32BE(begin); height = png.readUInt32BE(begin + 4);
            const depth = png[begin + 8], color = png[begin + 9]; interlace = png[begin + 12];
            channels = color === 2 ? 3 : color === 6 ? 4 : 0;
            if (!channels || depth !== 8 || interlace !== 0 || !width || !height || width > 4096 || height > 4096) throw new Error('Unsupported screenshot PNG');
        } else if (type === 'IDAT') compressed.push(png.subarray(begin, begin + length));
        offset = begin + length + 4;
    }
    if (!width || !height || !channels) throw new Error('Missing screenshot dimensions');
    const stride = width * channels, raw = inflateSync(Buffer.concat(compressed), { maxOutputLength: (stride + 1) * height });
    if (raw.length !== (stride + 1) * height) throw new Error('Invalid PNG pixel payload');
    let previous = Buffer.alloc(stride); const colors = new Set(), samples = []; let darkest = 255, lightest = 0, opaque = 0, sampled = 0;
    const paeth = (a, b, c) => { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; };
    for (let y = 0; y < height; y++) {
        const filter = raw[y * (stride + 1)], line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
        for (let x = 0; x < stride; x++) {
            const a = x >= channels ? line[x - channels] : 0, b = previous[x], c = x >= channels ? previous[x - channels] : 0;
            const predictor = filter === 0 ? 0 : filter === 1 ? a : filter === 2 ? b : filter === 3 ? Math.floor((a + b) / 2) : filter === 4 ? paeth(a, b, c) : NaN;
            if (!Number.isFinite(predictor)) throw new Error('Invalid PNG filter');
            line[x] = (line[x] + predictor) & 255;
        }
        if (y % 3 === 0) for (let x = 0; x < width; x += 3) {
            const at = x * channels, alpha = channels === 4 ? line[at + 3] : 255; sampled++;
            if (includeSamples) samples.push((line[at] << 16) | (line[at + 1] << 8) | line[at + 2]);
            if (alpha < 200) continue;
            opaque++; colors.add((line[at] << 16) | (line[at + 1] << 8) | line[at + 2]);
            const luma = (line[at] + line[at + 1] + line[at + 2]) / 3; darkest = Math.min(darkest, luma); lightest = Math.max(lightest, luma);
        }
        previous = line;
    }
    return { width, height, uniqueColors: colors.size, lumaRange: lightest - darkest, opaqueFraction: opaque / sampled, ...(includeSamples ? { samples } : {}) };
}
export function requireRenderedCanvas(pixels) {
    if (pixels.width < 600 || pixels.height < 400 || pixels.uniqueColors < 16 || pixels.lumaRange < 30 || pixels.opaqueFraction < 0.95)
        throw new Error(`Canvas screenshot is blank or insufficiently rendered: ${JSON.stringify(pixels)}`);
}

export function renderedDifference(before, after) {
    const a = inspectPng(before, true), b = inspectPng(after, true);
    if (a.width !== b.width || a.height !== b.height) throw new Error('Screenshots have different canvas dimensions');
    let changed = 0;
    for (let i = 0; i < a.samples.length; i++) if (a.samples[i] !== b.samples[i]) changed++;
    return changed / a.samples.length;
}
