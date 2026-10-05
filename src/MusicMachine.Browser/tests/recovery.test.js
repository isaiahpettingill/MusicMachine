import test from 'node:test';
import assert from 'node:assert/strict';
import { createProjectStore, createRecoveryStore } from '../wwwroot/recovery.js';

// Transactional IndexedDB test double: requests succeed before commit; abort rolls back writes.
function indexedDbFixture(initial = []) {
    const data = new Map(initial);
    const state = { data, abortNext: false, holdNext: false, releases: [], opens: 0 };
    const db = {
        close() {}, createObjectStore() {},
        transaction(name, mode) {
            assert.equal(name, 'recovery');
            const pending = new Map(data), abort = state.abortNext, hold = state.holdNext;
            state.abortNext = state.holdNext = false;
            const tx = { error: null };
            function request(action) {
                const req = {};
                queueMicrotask(() => {
                    req.result = action(); req.onsuccess?.();
                    const finish = () => {
                        if (abort) { tx.error = new Error('transaction aborted'); tx.onabort?.(); }
                        else { if (mode === 'readwrite') { data.clear(); for (const pair of pending) data.set(...pair); } tx.oncomplete?.(); }
                    };
                    if (hold) state.releases.push(finish); else queueMicrotask(finish);
                });
                return req;
            }
            tx.objectStore = () => ({
                get: key => request(() => pending.get(key)),
                put: (value, key) => request(() => { pending.set(key, value); return key; }),
                delete: key => request(() => { pending.delete(key); }),
                getAllKeys: () => request(() => [...pending.keys()])
            });
            return tx;
        }
    };
    state.db = db;
    state.indexedDb = { open(name, version) { assert.equal(name, 'musicmachine'); assert.equal(version, 1); state.opens++; const req = { result: db }; queueMicrotask(() => req.onsuccess()); return req; } };
    return state;
}
const tick = () => new Promise(resolve => setTimeout(resolve, 0));

test('first browser launch has no project or recovery and never fabricates a demo', async () => {
    const f = indexedDbFixture(); const store = createProjectStore(f.indexedDb);
    assert.equal(await store.read('last-project'), null); assert.equal(await store.list('recovery-'), ''); assert.equal(f.data.size, 0);
});
test('clean project survives fresh host instances and New clears only its startup target', async () => {
    const f = indexedDbFixture(); const first = createProjectStore(f.indexedDb);
    await first.write('last-project', 'clean-cbor'); await first.write('recovery-1', 'unsaved-cbor');
    const reload = createProjectStore(f.indexedDb);
    assert.equal(await reload.read('last-project'), 'clean-cbor'); assert.equal(await reload.read('recovery-1'), 'unsaved-cbor');
    await reload.remove('last-project'); assert.equal(await first.read('last-project'), null); assert.equal(await first.read('recovery-1'), 'unsaved-cbor');
});
test('separate sessions and legacy recovery are never cleared by saving another project', async () => {
    const f = indexedDbFixture([['song', 'v1-recovery']]); const legacy = createRecoveryStore(f.indexedDb), store = createProjectStore(f.indexedDb);
    assert.equal(await legacy.load(), 'v1-recovery');
    await store.write('recovery-2', 'second-unsaved'); await store.write('recovery-1', 'first-unsaved'); await store.write('last-project', 'new-clean');
    await store.remove('recovery-2'); assert.equal(await store.list('recovery-'), 'recovery-1');
    assert.equal(await legacy.load(), 'v1-recovery'); await legacy.clear(); assert.equal(await store.read('last-project'), 'new-clean'); assert.equal(await store.read('recovery-1'), 'first-unsaved');
});
test('write and delete aborts preserve previous persisted data', async () => {
    const f = indexedDbFixture([['last-project', 'previous']]); const store = createProjectStore(f.indexedDb);
    f.abortNext = true; await assert.rejects(store.write('last-project', 'failed'), /aborted/); assert.equal(f.data.get('last-project'), 'previous');
    f.abortNext = true; await assert.rejects(store.remove('last-project'), /aborted/); assert.equal(f.data.get('last-project'), 'previous');
});
test('save success is reported only after transaction commit and failed reads do not resolve early', async () => {
    const f = indexedDbFixture(); const store = createProjectStore(f.indexedDb); let done = false;
    f.holdNext = true; const write = store.write('last-project', 'committed').then(() => { done = true; });
    await tick(); assert.equal(done, false); assert.equal(f.data.has('last-project'), false); f.releases.shift()(); await write; assert.equal(done, true);
    f.abortNext = true; await assert.rejects(store.read('last-project'), /aborted/);
});
test('corrupt snapshots are preserved for editor validation, and unavailable storage reports an error', async () => {
    const f = indexedDbFixture([['last-project', 'not-cbor']]); const store = createProjectStore(f.indexedDb);
    assert.equal(await store.read('last-project'), 'not-cbor'); assert.equal(f.data.get('last-project'), 'not-cbor');
    await assert.rejects(createProjectStore(null).read('last-project'), /unavailable/);
    await assert.rejects(store.write('../escape', 'invalid'), /Invalid/); assert.equal(f.data.size, 1);
});
test('opening errors can be retried and database version changes reopen storage', async () => {
    const f = indexedDbFixture(); let fail = true;
    const wrapped = { open(...args) { if (!fail) return f.indexedDb.open(...args); const req = { error: new Error('open failed') }; queueMicrotask(() => req.onerror()); return req; } };
    const store = createProjectStore(wrapped); await assert.rejects(store.read('song'), /open failed/); fail = false;
    await store.write('last-project', 'retry'); assert.equal(await store.read('last-project'), 'retry');
    const opens = f.opens; f.db.onversionchange(); assert.equal(await store.read('last-project'), 'retry'); assert.equal(f.opens, opens + 1);
});
