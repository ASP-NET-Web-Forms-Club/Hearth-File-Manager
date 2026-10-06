/* ==========================================================================
   HearthStore – tiny promise wrapper around IndexedDB (one object store, keyPath "id").
   Data stays in this browser only; nothing is stored on the server.

     var store = new HearthStore('hearth-ai', 'chats');
     await store.put({ id: 'abc', title: 'Home page', ... });
     var chat = await store.get('abc');
     var all  = await store.all();     // newest first by .updated
     await store.delete('abc');
   ========================================================================== */
(function () {
    'use strict';

    function HearthStore(dbName, storeName) {
        this.dbName = dbName;
        this.storeName = storeName;
        this._db = null;
    }

    HearthStore.prototype._open = function () {
        var self = this;
        if (this._db) return Promise.resolve(this._db);
        return new Promise(function (resolve, reject) {
            if (!window.indexedDB) { reject(new Error('This browser cannot store conversations (IndexedDB unavailable).')); return; }
            var req = indexedDB.open(self.dbName, 1);
            req.onupgradeneeded = function () {
                var db = req.result;
                if (!db.objectStoreNames.contains(self.storeName)) db.createObjectStore(self.storeName, { keyPath: 'id' });
            };
            req.onsuccess = function () { self._db = req.result; resolve(self._db); };
            req.onerror = function () { reject(req.error); };
        });
    };

    HearthStore.prototype._tx = function (mode, fn) {
        var self = this;
        return this._open().then(function (db) {
            return new Promise(function (resolve, reject) {
                var tx = db.transaction(self.storeName, mode);
                var result;
                tx.oncomplete = function () { resolve(result); };
                tx.onerror = function () { reject(tx.error); };
                tx.onabort = function () { reject(tx.error || new Error('Storage aborted (browser storage may be full).')); };
                fn(tx.objectStore(self.storeName), function (r) { result = r; });
            });
        });
    };

    HearthStore.prototype.get = function (id) {
        return this._tx('readonly', function (s, done) { var r = s.get(id); r.onsuccess = function () { done(r.result || null); }; });
    };
    HearthStore.prototype.put = function (obj) {
        return this._tx('readwrite', function (s) { s.put(obj); });
    };
    HearthStore.prototype.delete = function (id) {
        return this._tx('readwrite', function (s) { s.delete(id); });
    };
    HearthStore.prototype.all = function () {
        return this._tx('readonly', function (s, done) {
            var r = s.getAll();
            r.onsuccess = function () {
                done((r.result || []).sort(function (a, b) { return (b.updated || 0) - (a.updated || 0); }));
            };
        });
    };

    /** Ask the browser not to evict our data under storage pressure (best effort). */
    HearthStore.persist = function () {
        if (navigator.storage && navigator.storage.persist) navigator.storage.persist().catch(function () { });
    };

    window.HearthStore = HearthStore;
})();
