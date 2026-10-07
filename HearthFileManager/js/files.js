/* ==========================================================================
   File manager page – list/grid views, selection, drag & drop chunked upload,
   context menu, lightbox. Server calls go through Api (site.js).
   ========================================================================== */
(function () {
    'use strict';

    var CFG = window.FILES_CONFIG || {};
    var ICONS = {
        folder: 'fa-folder', image: 'fa-file-image', code: 'fa-file-code', text: 'fa-file-lines',
        archive: 'fa-file-zipper', doc: 'fa-file-pdf', audio: 'fa-file-audio', video: 'fa-file-video',
        database: 'fa-database', font: 'fa-font', other: 'fa-file'
    };
    var RECYCLE = '$recycle';   // virtual path of the recycle bin
    var SYSTEM = { 'App_Data': 'private · databases', 'web.config': 'IIS settings' };

    var S = {
        path: '',
        items: [],
        isRecycle: false,
        sel: new Set(),
        anchor: null,
        view: 'list',
        sort: { key: 'Name', dir: 1 },
        filter: ''
    };
    var el = {};

    function store(k, v) { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch (e) { return null; } }

    // ------------------------------------------------------------------ load & render

    async function load(path, keepSel) {
        var r = await Api.get('/fileapi', 'list', { path: path });
        if (!r.success) {
            Toast.error(r.message);
            if (path !== '') navigate('');
            return;
        }
        S.path = r.data.Path;
        S.isRecycle = r.data.IsRecycle;
        S.items = r.items;
        if (!keepSel) { S.sel.clear(); S.anchor = null; }
        else S.sel.forEach(function (p) { if (!S.items.some(function (i) { return i.Path === p; })) S.sel.delete(p); });
        el.fm.classList.toggle('is-recycle', S.isRecycle);
        document.querySelectorAll('.sidebar .nav-item').forEach(function (a) {
            var m = a.getAttribute('data-menu');
            if (m === 'files') a.classList.toggle('active', !S.isRecycle);
            if (m === 'recycle') a.classList.toggle('active', S.isRecycle);
        });
        renderCrumbs();
        render();
    }

    function refresh() { return load(S.path, true); }

    function navigate(path) {
        var h = '#' + path;
        if (location.hash === h) load(path); else location.hash = h;
    }

    function renderCrumbs() {
        var parts = S.path ? S.path.split('/') : [];
        var html = '<a href="#" data-path="" data-drop="1" title="' + Fmt.esc(CFG.RootDisplay || '') + '"><i class="fa-solid fa-house"></i></a>';
        var acc = '';
        parts.forEach(function (p) {
            acc = acc ? acc + '/' + p : p;
            var text = acc === RECYCLE ? 'Recycle Bin' : p;
            html += '<span class="sep"><i class="fa-solid fa-chevron-right"></i></span><a href="#' + Fmt.esc(acc) + '" data-path="' + Fmt.esc(acc) + '"' + (acc === RECYCLE ? '' : ' data-drop="1"') + '>' + Fmt.esc(text) + '</a>';
        });
        el.crumbs.innerHTML = html;
    }

    function visibleItems() {
        var f = S.filter.toLowerCase();
        var list = S.items.filter(function (i) { return !f || i.Name.toLowerCase().indexOf(f) >= 0; });
        var k = S.sort.key, d = S.sort.dir;
        list.sort(function (a, b) {
            if (a.IsDir !== b.IsDir) return a.IsDir ? -1 : 1;
            var x = a[k], y = b[k];
            if (k === 'Name') return d * x.localeCompare(y, undefined, { numeric: true, sensitivity: 'base' });
            return d * (x < y ? -1 : x > y ? 1 : 0);
        });
        return list;
    }

    function iconHtml(it) {
        return '<i class="fi k-' + it.Kind + ' fa-solid ' + (ICONS[it.Kind] || ICONS.other) + '"></i>';
    }

    function nameExtra(it) {
        var html = '';
        if (S.path === '' && SYSTEM[it.Name]) html += '<span class="sys-tag">' + SYSTEM[it.Name] + '</span>';
        if (it.OriginalPath) html += '<div class="orig">from /' + Fmt.esc(it.OriginalPath) + (it.DeletedBy === 'gemini' ? ' · deleted by AI' : '') + '</div>';
        return html;
    }

    function render() {
        var list = visibleItems();
        if (!list.length) {
            el.content.innerHTML = S.filter
                ? '<div class="empty"><i class="fa-solid fa-magnifying-glass"></i>No matches for "' + Fmt.esc(S.filter) + '"</div>'
                : S.isRecycle
                    ? '<div class="empty"><i class="fa-solid fa-trash-can"></i>The recycle bin is empty</div>'
                    : '<div class="empty"><i class="fa-solid fa-cloud-arrow-up"></i>This folder is empty.<br>Drag files here or click <b>Upload</b>.</div>';
            updateSelectionUi();
            return;
        }
        if (S.view === 'grid') renderGrid(list); else renderList(list);
        updateSelectionUi();
    }

    function sortHead(key, label, cls) {
        var on = S.sort.key === key;
        var arrow = on ? '<i class="fa-solid ' + (S.sort.dir > 0 ? 'fa-arrow-up' : 'fa-arrow-down') + '"></i>' : '';
        return '<th class="' + cls + (on ? ' sorted' : '') + '" data-sort="' + key + '">' + label + arrow + '</th>';
    }

    function renderList(list) {
        var html = '<table class="fm-table"><thead><tr>' +
            '<th class="c-check"><input type="checkbox" class="check" id="check-all" title="Select all" /></th>' +
            sortHead('Name', 'Name', 'c-name') + sortHead('Size', 'Size', 'c-size') + sortHead('ModifiedUtc', S.isRecycle ? 'Deleted' : 'Modified', 'c-date') +
            '<th class="c-more"></th></tr></thead><tbody>';
        list.forEach(function (it) {
            var sel = S.sel.has(it.Path);
            html += '<tr class="fm-row' + (sel ? ' selected' : '') + '" data-path="' + Fmt.esc(it.Path) + '" draggable="true"' + (it.IsDir ? ' data-drop="1"' : '') + '>' +
                '<td class="c-check"><input type="checkbox" class="check"' + (sel ? ' checked' : '') + ' /></td>' +
                '<td class="c-name"><div class="name-cell">' + iconHtml(it) + '<div class="nm">' + Fmt.esc(it.Name) + nameExtra(it) + '</div></div></td>' +
                '<td class="c-size">' + (it.IsDir ? '—' : Fmt.size(it.Size)) + '</td>' +
                '<td class="c-date">' + Fmt.date(it.ModifiedUtc) + '</td>' +
                '<td class="c-more"><button type="button" class="btn-icon row-more" data-more="1" title="More"><i class="fa-solid fa-ellipsis-vertical"></i></button></td>' +
                '</tr>';
        });
        el.content.innerHTML = html + '</tbody></table>';
    }

    function renderGrid(list) {
        var html = '<div class="fm-grid">';
        list.forEach(function (it) {
            var sel = S.sel.has(it.Path);
            var thumb = it.Kind === 'image'
                ? '<img loading="lazy" decoding="async" alt="" src="' + Fmt.esc(Api.url('/fileapi', 'raw', { path: it.Path, v: it.ModifiedUtc })) + '" />'
                : iconHtml(it);
            html += '<div class="fm-card' + (sel ? ' selected' : '') + '" data-path="' + Fmt.esc(it.Path) + '" draggable="true"' + (it.IsDir ? ' data-drop="1"' : '') + '>' +
                '<input type="checkbox" class="check"' + (sel ? ' checked' : '') + ' />' +
                (it.Ext && !it.IsDir ? '<span class="ext-badge">' + Fmt.esc(it.Ext) + '</span>' : '') +
                '<div class="thumb">' + thumb + '</div>' +
                '<div class="meta"><div class="nm" title="' + Fmt.esc(it.Name) + '">' + Fmt.esc(it.Name) + '</div><small>' + (it.IsDir ? (SYSTEM[it.Name] && S.path === '' ? SYSTEM[it.Name] : 'Folder') : Fmt.size(it.Size)) + '</small></div>' +
                '</div>';
        });
        el.content.innerHTML = html + '</div>';
    }

    function setView(v) {
        S.view = v;
        store('hfm.view', v);
        document.querySelectorAll('[data-view]').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-view') === v); });
        render();
    }

    // ------------------------------------------------------------------ selection

    function item(path) { return S.items.find(function (i) { return i.Path === path; }); }
    function selected() { return S.items.filter(function (i) { return S.sel.has(i.Path); }); }

    function select(path, e) {
        var list = visibleItems().map(function (i) { return i.Path; });
        if (e && e.shiftKey && S.anchor) {
            var a = list.indexOf(S.anchor), b = list.indexOf(path);
            if (!(e.ctrlKey || e.metaKey)) S.sel.clear();
            for (var i = Math.min(a, b); i <= Math.max(a, b); i++) S.sel.add(list[i]);
        } else if (e && (e.ctrlKey || e.metaKey || e.type === 'toggle')) {
            if (S.sel.has(path)) S.sel.delete(path); else S.sel.add(path);
            S.anchor = path;
        } else {
            S.sel.clear();
            S.sel.add(path);
            S.anchor = path;
        }
        syncSelectionDom();
    }

    function syncSelectionDom() {
        el.content.querySelectorAll('[data-path]').forEach(function (n) {
            var on = S.sel.has(n.getAttribute('data-path'));
            n.classList.toggle('selected', on);
            var cb = n.querySelector('input.check');
            if (cb) cb.checked = on;
        });
        updateSelectionUi();
    }

    function updateSelectionUi() {
        var sel = selected();
        el.fm.classList.toggle('has-selection', sel.length > 0);
        el.selCount.textContent = sel.length + ' selected';
        var one = sel.length === 1 ? sel[0] : null;
        el.toolbar.querySelectorAll('[data-single]').forEach(function (b) {
            var need = b.getAttribute('data-single');
            var ok = !!one && (need === 'any' ||
                (need === 'text' && !one.IsDir && (one.Kind === 'code' || one.Kind === 'text' || one.Ext === '' || one.Ext === 'svg')) ||
                (need === 'zip' && one.Ext === 'zip'));
            b.classList.toggle('single-hidden', !ok);
        });
        var all = document.getElementById('check-all');
        if (all) {
            var vis = visibleItems();
            all.checked = vis.length > 0 && vis.every(function (i) { return S.sel.has(i.Path); });
            all.indeterminate = !all.checked && sel.length > 0;
        }
    }

    // ------------------------------------------------------------------ open

    function open(it) {
        if (it.IsDir) { navigate(it.Path); return; }
        if (S.isRecycle) { Toast.error('Restore this item first to open it.'); return; }
        if (it.Kind === 'image') { lightbox(it); return; }
        if (it.Kind === 'code' || it.Kind === 'text' || it.Ext === '') { HearthEditor.open(it.Path, { onSaved: refresh, readOnly: !CFG.CanEdit }); return; }
        if (it.Ext === 'zip' && CFG.CanEdit) { unzip(it); return; }
        if (it.Ext === 'pdf' || it.Kind === 'video' || it.Kind === 'audio') { window.open(Api.url('/fileapi', 'raw', { path: it.Path }), '_blank', 'noopener'); return; }
        download([it.Path]);
    }

    // ------------------------------------------------------------------ actions

    var actions = {
        'upload': function () { el.fileInput.click(); },
        'new-folder': async function () {
            var name = await Dialog.prompt('New folder', 'Folder name:', 'new-folder', 'Create');
            if (!name) return;
            var r = await Api.files('mkdir', { path: S.path, name: name.trim() });
            Toast.result(r);
            if (r.success) { await refresh(); select(r.data.Path); }
        },
        'new-file': async function () {
            var name = await Dialog.prompt('New file', 'File name (for example about.html, contact.php, style.css):', 'new-page.html', 'Create');
            if (!name) return;
            var r = await Api.files('newfile', { path: S.path, name: name.trim() });
            if (!r.success) { Toast.error(r.message); return; }
            await refresh();
            select(r.data.Path);
            HearthEditor.open(r.data.Path, { onSaved: refresh });
        },
        'edit': function () { var s = selected(); if (s.length === 1) HearthEditor.open(s[0].Path, { onSaved: refresh }); },
        'download': function () { download(selected().map(function (i) { return i.Path; })); },
        'rename': async function () {
            var s = selected(); if (s.length !== 1) return;
            var name = await Dialog.prompt('Rename', 'New name:', s[0].Name, 'Rename');
            if (!name || name === s[0].Name) return;
            var r = await Api.files('rename', { path: s[0].Path, name: name.trim() });
            Toast.result(r);
            if (r.success) { await refresh(); select(r.data.Path); }
        },
        'move': function () { moveOrCopy('move'); },
        'copy': function () { moveOrCopy('copy'); },
        'zip': async function () {
            var s = selected(); if (!s.length) return;
            var def = (s.length === 1 ? s[0].Name.replace(/\.[^.]+$/, '') || s[0].Name : 'archive') + '.zip';
            var name = await Dialog.prompt('Create zip file', 'Zip file name:', def, 'Create zip');
            if (!name) return;
            busy(true);
            var r = await Api.files('zip', { paths: s.map(function (i) { return i.Path; }), name: name.trim() });
            busy(false);
            Toast.result(r);
            if (r.success) { await refresh(); select(r.data.Path); }
        },
        'unzip': function () { var s = selected(); if (s.length === 1) unzip(s[0]); },
        'delete': async function () {
            var s = selected(); if (!s.length) return;
            var what = s.length === 1 ? '"' + s[0].Name + '"' : s.length + ' items';
            var ok = S.isRecycle
                ? await Dialog.confirm('Delete forever?', what + ' will be permanently deleted. This cannot be undone.', 'Delete forever', true)
                : await Dialog.confirm('Delete ' + what + '?', 'They will be moved to the recycle bin. You can restore them later.', 'Move to recycle bin', true);
            if (!ok) return;
            var r = await Api.files('delete', { paths: s.map(function (i) { return i.Path; }) });
            Toast.result(r);
            refresh();
        },
        'restore': async function () {
            var s = selected(); if (!s.length) return;
            var r = await Api.files('restore', { paths: s.map(function (i) { return i.Path; }) });
            Toast.result(r);
            refresh();
        },
        'empty-recycle': async function () {
            if (!S.items.length) { Toast.ok('The recycle bin is already empty.'); return; }
            var ok = await Dialog.confirm('Empty the recycle bin?', 'All ' + S.items.length + ' item(s) will be permanently deleted. This cannot be undone.', 'Empty recycle bin', true);
            if (!ok) return;
            var r = await Api.files('empty-recycle', {});
            Toast.result(r);
            refresh();
        },
        'open': function () { var s = selected(); if (s.length === 1) open(s[0]); }
    };

    // view-only users (no "Manage files" permission) may only open and download
    var READ_ONLY_ACTIONS = ['open', 'download'];
    function run(name) {
        if (!actions[name]) return;
        if (!CFG.CanEdit && READ_ONLY_ACTIONS.indexOf(name) < 0) { Toast.error('You can view and download files, but not change them.'); return; }
        actions[name]();
    }

    async function moveOrCopy(kind) {
        var s = selected(); if (!s.length) return;
        var dest = await Dialog.pickFolder((kind === 'move' ? 'Move ' : 'Copy ') + s.length + ' item(s) to…', S.path, kind === 'move' ? 'Move here' : 'Copy here');
        if (dest === null || dest === undefined) return;
        await moveTo(s.map(function (i) { return i.Path; }), dest, kind);
    }

    async function moveTo(paths, dest, kind) {
        busy(true);
        var r = await Api.files(kind || 'move', { paths: paths, dest: dest });
        busy(false);
        Toast.result(r);
        refresh();
    }

    async function unzip(it) {
        var base = it.Name.replace(/\.zip$/i, '');
        var choice = await Dialog.open({
            title: 'Extract "' + it.Name + '"',
            html: '<p>Where should the files go?</p>' +
                '<label class="check"><input type="radio" name="uz" value="here" checked /> <span>Here, in <b>/' + Fmt.esc(S.path) + '</b></span></label>' +
                '<label class="check" style="margin-top:8px"><input type="radio" name="uz" value="folder" /> <span>Into a new folder <b>' + Fmt.esc(base) + '</b></span></label>' +
                '<p style="font-size:13px">Files with the same name are replaced (old copies go to the recycle bin).</p>',
            okText: 'Extract',
            getValue: function () { return document.querySelector('input[name=uz]:checked').value; }
        });
        if (!choice) return;
        busy(true);
        var dest = choice === 'folder' ? (S.path ? S.path + '/' : '') + base : S.path;
        var r = await Api.files('unzip', { path: it.Path, dest: dest });
        busy(false);
        Toast.result(r);
        refresh();
    }

    function download(paths) {
        if (!paths.length) return;
        var a = document.createElement('a');
        a.href = Api.url('/fileapi', 'download', paths.length === 1 ? { path: paths[0] } : { paths: paths });
        a.download = '';
        document.body.appendChild(a);
        a.click();
        a.remove();
    }

    function busy(on) { document.body.style.cursor = on ? 'progress' : ''; }

    // ------------------------------------------------------------------ context menu

    function showMenu(x, y) {
        var s = selected();
        var one = s.length === 1 ? s[0] : null;
        var items = [];
        if (S.isRecycle) {
            if (s.length) items.push(['restore', 'fa-trash-arrow-up', 'Restore'], ['download', 'fa-download', 'Download'], '-', ['delete', 'fa-trash-can', 'Delete forever', 'danger']);
            else items.push(['empty-recycle', 'fa-dumpster', 'Empty recycle bin', 'danger']);
        } else if (!s.length) {
            items.push(['upload', 'fa-cloud-arrow-up', 'Upload files'], ['new-folder', 'fa-folder-plus', 'New folder'], ['new-file', 'fa-file-circle-plus', 'New file']);
        } else {
            if (one) items.push(['open', one.IsDir ? 'fa-folder-open' : 'fa-up-right-from-square', 'Open']);
            if (one && !one.IsDir && (one.Kind === 'code' || one.Kind === 'text' || one.Ext === 'svg')) items.push(['edit', 'fa-pen-to-square', 'Edit']);
            items.push(['download', 'fa-download', 'Download']);
            if (one) items.push(['rename', 'fa-i-cursor', 'Rename']);
            items.push(['move', 'fa-arrows-up-down-left-right', 'Move to…'], ['copy', 'fa-copy', 'Copy to…'], ['zip', 'fa-file-zipper', 'Add to zip']);
            if (one && one.Ext === 'zip') items.push(['unzip', 'fa-box-open', 'Extract (unzip)']);
            items.push('-', ['delete', 'fa-trash-can', 'Delete', 'danger']);
        }
        if (!CFG.CanEdit) {
            items = items.filter(function (m) { return m !== '-' && READ_ONLY_ACTIONS.indexOf(m[0]) >= 0; });
            if (!items.length) return;
        }
        el.menu.innerHTML = items.map(function (m) {
            return m === '-' ? '<hr />' : '<button type="button" data-act="' + m[0] + '" class="' + (m[3] || '') + '"><i class="fa-solid ' + m[1] + '"></i>' + m[2] + '</button>';
        }).join('');
        el.menu.classList.add('open');
        var w = el.menu.offsetWidth, h = el.menu.offsetHeight;
        el.menu.style.left = Math.min(x, window.innerWidth - w - 8) + 'px';
        el.menu.style.top = Math.min(y, window.innerHeight - h - 8) + 'px';
    }
    function hideMenu() { el.menu.classList.remove('open'); }

    // ------------------------------------------------------------------ lightbox

    var lb = { list: [], i: 0 };
    function lightbox(it) {
        lb.list = visibleItems().filter(function (i) { return i.Kind === 'image'; });
        lb.i = Math.max(0, lb.list.findIndex(function (i) { return i.Path === it.Path; }));
        showLb();
        el.lightbox.classList.add('open');
    }
    function showLb() {
        var it = lb.list[lb.i];
        if (!it) return;
        el.lbImg.src = Api.url('/fileapi', 'raw', { path: it.Path, v: it.ModifiedUtc });
        el.lbCap.textContent = it.Name + ' · ' + Fmt.size(it.Size) + ' · ' + (lb.i + 1) + ' / ' + lb.list.length;
    }
    function lbStep(d) { lb.i = (lb.i + d + lb.list.length) % lb.list.length; showLb(); }
    function lbClose() { el.lightbox.classList.remove('open'); el.lbImg.src = ''; }

    // ------------------------------------------------------------------ upload (chunked, queued)

    var queue = [], uploading = false, uploadSeq = 0;

    function uid() {
        var a = new Uint8Array(12);
        crypto.getRandomValues(a);
        return Array.from(a, function (b) { return ('0' + b.toString(16)).slice(-2); }).join('');
    }

    /** files: [{ file: File, rel: 'sub/folder/name.ext' }] uploaded into folder `dest`. */
    function enqueue(files, dest) {
        if (FsIsRecycle(dest)) { Toast.error('You cannot upload into the recycle bin.'); return; }
        files.forEach(function (f) {
            if (f.file.size > CFG.MaxUploadBytes) { Toast.error(f.file.name + ' is larger than the ' + Fmt.size(CFG.MaxUploadBytes) + ' limit.'); return; }
            var li = document.createElement('li');
            li.innerHTML = '<div class="up-top"><span class="up-name"></span><span class="up-status">Waiting</span></div><div class="up-bar"><span></span></div>';
            li.querySelector('.up-name').textContent = f.rel;
            el.upList.appendChild(li);
            queue.push({ file: f.file, dest: (dest ? dest + '/' : '') + f.rel, li: li });
        });
        el.upPanel.classList.add('open');
        updateUploadTitle();
        if (!uploading) runQueue();
    }

    function FsIsRecycle(p) { return p === RECYCLE || p.indexOf(RECYCLE + '/') === 0; }

    function updateUploadTitle() {
        var left = queue.length + (uploading ? 1 : 0);
        el.upTitle.textContent = left ? 'Uploading ' + left + ' file(s)…' : 'Uploads complete';
    }

    async function runQueue() {
        uploading = true;
        var touched = false;
        while (queue.length) {
            var job = queue.shift();
            updateUploadTitle();
            var ok = await uploadOne(job);
            touched = touched || ok;
        }
        uploading = false;
        updateUploadTitle();
        if (touched) refresh();
    }

    async function uploadOne(job) {
        var size = job.file.size, chunk = CFG.ChunkBytes || 5242880;
        var total = Math.max(1, Math.ceil(size / chunk));
        var id = uid() + (++uploadSeq);
        var bar = job.li.querySelector('.up-bar span');
        var status = job.li.querySelector('.up-status');
        for (var i = 0; i < total; i++) {
            var blob = job.file.slice(i * chunk, Math.min(size, (i + 1) * chunk));
            var r, tries = 0;
            do {
                r = await Api.upload('/fileapi', 'upload-chunk', { uploadId: id, index: i, total: total, dest: job.dest, chunk: blob }, function (loaded) {
                    var pct = size ? Math.min(100, Math.round((i * chunk + loaded) / size * 100)) : 100;
                    bar.style.width = pct + '%';
                    status.textContent = pct + '%';
                });
                tries++;
            } while (!r.success && /network/i.test(r.message) && tries < 3);
            if (!r.success) {
                job.li.classList.add('failed');
                status.textContent = 'Failed';
                job.li.title = r.message;
                Toast.error(job.file.name + ': ' + r.message);
                return false;
            }
        }
        bar.style.width = '100%';
        job.li.classList.add('done');
        status.innerHTML = '<i class="fa-solid fa-check"></i> Done';
        return true;
    }

    /** Reads dropped files AND folders (keeps the folder structure). */
    async function collectDropped(dt) {
        var out = [];
        var entries = [];
        if (dt.items && dt.items.length && dt.items[0].webkitGetAsEntry) {
            for (var i = 0; i < dt.items.length; i++) {
                var en = dt.items[i].webkitGetAsEntry && dt.items[i].webkitGetAsEntry();
                if (en) entries.push(en);
            }
        }
        if (!entries.length) {
            Array.from(dt.files || []).forEach(function (f) { out.push({ file: f, rel: f.name }); });
            return out;
        }
        async function walk(entry, prefix) {
            if (entry.isFile) {
                var file = await new Promise(function (res, rej) { entry.file(res, rej); });
                out.push({ file: file, rel: prefix + file.name });
            } else if (entry.isDirectory) {
                var reader = entry.createReader();
                var batch;
                do {
                    batch = await new Promise(function (res, rej) { reader.readEntries(res, rej); });
                    for (var j = 0; j < batch.length; j++) await walk(batch[j], prefix + entry.name + '/');
                } while (batch.length);
            }
        }
        for (var k = 0; k < entries.length; k++) await walk(entries[k], '');
        return out;
    }

    // ------------------------------------------------------------------ drag & drop wiring

    var dragDepth = 0, internalDrag = null;

    function dropTargetPath(e) {
        var t = e.target.closest('[data-drop]');
        if (!t) return null;
        return t.getAttribute('data-path');
    }

    function clearDropHover() {
        document.querySelectorAll('.drop-hover').forEach(function (n) { n.classList.remove('drop-hover'); });
    }

    function wireDragDrop() {
        // internal drag (move items onto folders / breadcrumb)
        document.addEventListener('dragstart', function (e) {
            var n = e.target.closest && e.target.closest('[data-path][draggable]');
            if (!n) return;
            if (!CFG.CanEdit) { e.preventDefault(); return; }
            var p = n.getAttribute('data-path');
            if (!S.sel.has(p)) select(p);
            internalDrag = Array.from(S.sel);
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', internalDrag.join('\n'));
        });
        document.addEventListener('dragend', function () { internalDrag = null; clearDropHover(); });

        el.body.addEventListener('dragenter', function (e) {
            if (internalDrag || !hasFiles(e) || !CFG.CanEdit) return;
            e.preventDefault();
            dragDepth++;
            el.dropTarget.textContent = 'into /' + S.path;
            el.body.classList.add('dragging');
        });
        el.body.addEventListener('dragleave', function () {
            if (internalDrag) return;
            if (--dragDepth <= 0) { dragDepth = 0; el.body.classList.remove('dragging'); }
        });
        document.addEventListener('dragover', function (e) {
            if (!internalDrag && !hasFiles(e)) return;
            e.preventDefault();
            clearDropHover();
            var target = dropTargetPath(e);
            if (internalDrag && target !== null && internalDrag.indexOf(target) < 0 && target !== S.path) {
                e.target.closest('[data-drop]').classList.add('drop-hover');
                e.dataTransfer.dropEffect = 'move';
            } else if (internalDrag) e.dataTransfer.dropEffect = 'none';
            else {
                var row = e.target.closest('.fm-row[data-drop], .fm-card[data-drop]');
                if (row) row.classList.add('drop-hover');
                el.dropTarget.textContent = 'into /' + (row ? row.getAttribute('data-path') : S.path);
                e.dataTransfer.dropEffect = 'copy';
            }
        });
        document.addEventListener('drop', async function (e) {
            if (!internalDrag && !hasFiles(e)) return;
            e.preventDefault();
            var target = dropTargetPath(e);
            clearDropHover();
            dragDepth = 0;
            el.body.classList.remove('dragging');
            if (internalDrag) {
                var paths = internalDrag;
                internalDrag = null;
                if (target !== null && paths.indexOf(target) < 0 && target !== S.path) moveTo(paths, target);
                return;
            }
            if (!CFG.CanEdit) { Toast.error('You can view and download files, but not upload.'); return; }
            if (S.isRecycle) { Toast.error('You cannot upload into the recycle bin.'); return; }
            var row = e.target.closest('.fm-row[data-drop], .fm-card[data-drop]');
            var dest = row ? row.getAttribute('data-path') : S.path;
            var files = await collectDropped(e.dataTransfer);
            if (files.length) enqueue(files, dest);
        });
    }

    function hasFiles(e) {
        return e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types || [], 'Files') >= 0;
    }

    // ------------------------------------------------------------------ events

    function wire() {
        el.toolbar.addEventListener('click', function (e) {
            var b = e.target.closest('[data-act]');
            if (b) run(b.getAttribute('data-act'));
            var v = e.target.closest('[data-view]');
            if (v) setView(v.getAttribute('data-view'));
        });

        el.menu.addEventListener('click', function (e) {
            var b = e.target.closest('[data-act]');
            hideMenu();
            if (b) run(b.getAttribute('data-act'));
        });
        document.addEventListener('mousedown', function (e) { if (!e.target.closest('.ctx-menu')) hideMenu(); });
        window.addEventListener('blur', hideMenu);

        el.crumbs.addEventListener('click', function (e) {
            var a = e.target.closest('a[data-path]');
            if (!a) return;
            e.preventDefault();
            navigate(a.getAttribute('data-path'));
        });

        el.content.addEventListener('click', function (e) {
            var th = e.target.closest('th[data-sort]');
            if (th) {
                var k = th.getAttribute('data-sort');
                S.sort = { key: k, dir: S.sort.key === k ? -S.sort.dir : 1 };
                render();
                return;
            }
            if (e.target.id === 'check-all') {
                var vis = visibleItems();
                if (e.target.checked) vis.forEach(function (i) { S.sel.add(i.Path); }); else S.sel.clear();
                syncSelectionDom();
                return;
            }
            var n = e.target.closest('[data-path]');
            if (!n) { if (!e.ctrlKey && !e.shiftKey) { S.sel.clear(); syncSelectionDom(); } return; }
            var p = n.getAttribute('data-path');
            if (e.target.closest('[data-more]')) {
                if (!S.sel.has(p)) select(p);
                var r = e.target.closest('[data-more]').getBoundingClientRect();
                showMenu(r.left - 160, r.bottom + 4);
                return;
            }
            if (e.target.matches('input.check')) { select(p, { type: 'toggle' }); return; }
            // touch devices: single tap opens, like a phone file app
            if (window.matchMedia('(hover: none)').matches && !S.sel.size) { open(item(p)); return; }
            select(p, e);
        });

        el.content.addEventListener('dblclick', function (e) {
            var n = e.target.closest('[data-path]');
            if (n && !e.target.matches('input.check')) open(item(n.getAttribute('data-path')));
        });

        el.body.addEventListener('contextmenu', function (e) {
            if (e.target.closest('input, textarea')) return;
            e.preventDefault();
            var n = e.target.closest('[data-path]');
            if (n) { var p = n.getAttribute('data-path'); if (!S.sel.has(p)) select(p); }
            else { S.sel.clear(); syncSelectionDom(); }
            showMenu(e.clientX, e.clientY);
        });

        el.filter.addEventListener('input', function () { S.filter = el.filter.value.trim(); render(); });

        el.fileInput.addEventListener('change', function () {
            var files = Array.from(el.fileInput.files).map(function (f) { return { file: f, rel: f.name }; });
            el.fileInput.value = '';
            if (files.length) enqueue(files, S.path);
        });
        document.getElementById('upload-close').addEventListener('click', function () {
            el.upPanel.classList.remove('open');
            el.upList.querySelectorAll('li.done, li.failed').forEach(function (li) { li.remove(); });
        });

        el.lightbox.addEventListener('click', function (e) {
            var b = e.target.closest('[data-lb]');
            if (b) { var a = b.getAttribute('data-lb'); if (a === 'close') lbClose(); else lbStep(a === 'next' ? 1 : -1); }
            else if (e.target === el.lightbox) lbClose();
        });

        document.addEventListener('keydown', function (e) {
            if (document.querySelector('.modal-backdrop, .hed-modal')) return;
            if (el.lightbox.classList.contains('open')) {
                if (e.key === 'Escape') lbClose();
                if (e.key === 'ArrowRight') lbStep(1);
                if (e.key === 'ArrowLeft') lbStep(-1);
                return;
            }
            if (e.target.closest('input, textarea, select')) return;
            if (e.key === 'Delete') run('delete');
            else if (e.key === 'F2') run('rename');
            else if (e.key === 'Enter') run('open');
            else if (e.key === 'Escape') { S.sel.clear(); syncSelectionDom(); hideMenu(); }
            else if (e.key === 'Backspace' && S.path) { e.preventDefault(); navigate(S.path.split('/').slice(0, -1).join('/')); }
            else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'a') {
                e.preventDefault();
                visibleItems().forEach(function (i) { S.sel.add(i.Path); });
                syncSelectionDom();
            }
        });

        window.addEventListener('hashchange', function () { load(decodeURIComponent(location.hash.slice(1))); });
        window.addEventListener('beforeunload', function (e) { if (uploading) { e.preventDefault(); e.returnValue = ''; } });
    }

    document.addEventListener('DOMContentLoaded', function () {
        el.fm = document.getElementById('fm');
        if (!el.fm) return;
        el.crumbs = document.getElementById('fm-breadcrumb');
        el.toolbar = document.getElementById('fm-toolbar');
        el.body = document.getElementById('fm-body');
        el.content = document.getElementById('fm-content');
        el.filter = document.getElementById('fm-filter');
        el.selCount = document.getElementById('sel-count');
        el.fileInput = document.getElementById('fm-file-input');
        el.upPanel = document.getElementById('upload-panel');
        el.upList = document.getElementById('upload-list');
        el.upTitle = document.getElementById('upload-title');
        el.dropTarget = document.getElementById('drop-target');
        el.menu = document.getElementById('ctx-menu');
        el.lightbox = document.getElementById('lightbox');
        el.lbImg = document.getElementById('lightbox-img');
        el.lbCap = document.getElementById('lightbox-cap');

        el.fm.classList.toggle('read-only', !CFG.CanEdit);
        S.view = store('hfm.view') === 'grid' ? 'grid' : 'list';
        document.querySelectorAll('[data-view]').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-view') === S.view); });
        wire();
        wireDragDrop();
        load(location.hash.length > 1 ? decodeURIComponent(location.hash.slice(1)) : '');
    });
})();
