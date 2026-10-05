import { loadConverterAssets } from './conversion-assets.js';
import { convertWithCore, throttleProgress } from './conversion-worker-core.js';
let started = false;
self.onmessage = async ({ data }) => {
    if (started) return;
    started = true;
    const { id, name, bytes } = data;
    let coreUrl;
    const progress = throttleProgress(value => self.postMessage({ id, type: 'progress', ...value }));
    try {
        const { js, wasm } = await loadConverterAssets({ onProgress: progress });
        progress({ phase: 'initialize', progress: 0 });
        coreUrl = URL.createObjectURL(new Blob([js], { type: 'text/javascript' }));
        const factory = (await import(coreUrl)).default;
        // Supplying verified bytes avoids any core-initiated fetch. The placeholder
        // is only used to resolve the generated glue's filename under a blob URL.
        const core = await factory({ wasmBinary: wasm, locateFile: () => 'data:application/wasm;base64,' });
        const result = await convertWithCore(core, name, bytes, progress);
        self.postMessage({ id, type: 'result', bytes: result }, [result.buffer]);
    } catch (error) {
        self.postMessage({ id, type: 'error', message: error instanceof Error ? error.message : 'Audio conversion failed.' });
    } finally {
        if (coreUrl) URL.revokeObjectURL(coreUrl);
        self.close();
    }
};
