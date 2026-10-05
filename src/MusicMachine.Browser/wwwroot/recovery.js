// The existing database/store is retained so v1 unsaved recovery survives upgrades. Clean projects
// and independent recovery sessions use different keys. No FileSystemHandle or permission is saved.
export function createProjectStore(indexedDb = globalThis.indexedDB) {
    let database;
    function open() {
        return database ??= new Promise((resolve, reject) => {
            if (!indexedDb) { reject(new Error('Browser project storage is unavailable. Download a .song copy to keep your work.')); return; }
            const request = indexedDb.open('musicmachine', 1);
            request.onupgradeneeded = () => request.result.createObjectStore('recovery');
            request.onerror = () => { database = null; reject(request.error); };
            request.onblocked = () => { database = null; reject(new Error('Project storage is blocked by another MusicMachine tab. Close the other tab, then save your song.')); };
            request.onsuccess = () => {
                const db = request.result;
                db.onversionchange = () => { db.close(); database = null; };
                resolve(db);
            };
        });
    }
    function validateKey(key) { if (typeof key !== 'string' || !/^[a-zA-Z0-9-]+$/.test(key)) throw new Error('Invalid project storage key.'); }
    async function transaction(mode, operation) {
        const db = await open();
        return new Promise((resolve, reject) => {
            const tx = db.transaction('recovery', mode);
            let value;
            tx.oncomplete = () => resolve(value);
            tx.onerror = () => reject(tx.error ?? new Error('Project storage failed. Download a .song copy.'));
            tx.onabort = () => reject(tx.error ?? new Error('Project storage was interrupted. Download a .song copy.'));
            const request = operation(tx.objectStore('recovery'));
            request.onsuccess = () => { value = request.result ?? null; };
            request.onerror = () => reject(request.error);
        });
    }
    return {
        async read(key) { validateKey(key); return transaction('readonly', store => store.get(key)); },
        async write(key, value) { validateKey(key); if (typeof value !== 'string') throw new Error('A project snapshot must be encoded song data.'); await transaction('readwrite', store => store.put(value, key)); },
        async remove(key) { validateKey(key); await transaction('readwrite', store => store.delete(key)); },
        async list(prefix) {
            validateKey(prefix);
            const keys = await transaction('readonly', store => store.getAllKeys());
            return keys.filter(key => typeof key === 'string' && key.startsWith(prefix)).sort().join('\n');
        }
    };
}

// Compatibility surface for earlier hosts. It only touches the legacy unsaved-recovery key.
export function createRecoveryStore(indexedDb = globalThis.indexedDB) {
    const storage = createProjectStore(indexedDb);
    return { load: () => storage.read('song'), save: value => storage.write('song', value), clear: () => storage.remove('song') };
}
