// IndexedDB is persistent across reloads. Browser/private-mode storage errors are propagated to
// the editor so it can tell the user to save a named .song file rather than promising recovery.
export function createRecoveryStore(indexedDb = globalThis.indexedDB) {
    let database;
    function open() {
        return database ??= new Promise((resolve, reject) => {
            if (!indexedDb) { reject(new Error('Browser recovery storage is unavailable. Download a .song copy to keep your work.')); return; }
            const request = indexedDb.open('musicmachine', 1);
            request.onupgradeneeded = () => request.result.createObjectStore('recovery');
            request.onerror = () => { database = null; reject(request.error); };
            request.onblocked = () => { database = null; reject(new Error('Recovery storage is blocked by another MusicMachine tab. Close the other tab, then save your song.')); };
            request.onsuccess = () => {
                const db = request.result;
                db.onversionchange = () => { db.close(); database = null; };
                resolve(db);
            };
        });
    }
    async function write(value, clear = false) {
        const db = await open();
        return new Promise((resolve, reject) => {
            const transaction = db.transaction('recovery', 'readwrite');
            const store = transaction.objectStore('recovery');
            if (clear) store.delete('song'); else store.put(value, 'song');
            transaction.oncomplete = resolve;
            transaction.onerror = () => reject(transaction.error);
            transaction.onabort = () => reject(transaction.error ?? new Error('Recovery save was interrupted. Download a .song copy.'));
        });
    }
    return {
        async load() {
            const db = await open();
            return new Promise((resolve, reject) => {
                const request = db.transaction('recovery').objectStore('recovery').get('song');
                request.onsuccess = () => resolve(request.result ?? null);
                request.onerror = () => reject(request.error);
            });
        },
        save: value => write(value),
        clear: () => write(null, true)
    };
}
