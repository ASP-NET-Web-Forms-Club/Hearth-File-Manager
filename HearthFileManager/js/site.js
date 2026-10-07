/* ==========================================================================
   Hearth – app-wide JavaScript
   - Api:    the ONE place that talks to the server (fetch / XHR). Every page uses it.
   - Toast:  small notifications.
   - Dialog: promise-based alert / confirm / prompt / folder picker.
   - Fmt:    formatting helpers.
   ========================================================================== */
(function () {
    'use strict';

    // ------------------------------------------------------------------ Api
    var Api = {
        /** Builds FormData. Arrays/objects are JSON-encoded; Blobs are appended as files. */
        form: function (action, data) {
            var fd = new FormData();
            fd.append('action', action);
            Object.keys(data || {}).forEach(function (k) {
                var v = data[k];
                if (v === undefined || v === null) return;
                if (v instanceof Blob) fd.append(k, v, v.name || 'blob');
                else if (typeof v === 'object') fd.append(k, JSON.stringify(v));
                else if (typeof v === 'boolean') fd.append(k, v ? '1' : '0');
                else fd.append(k, String(v));
            });
            return fd;
        },

        /** POST an action; resolves to the JSON body { success, message, data, items }. Never throws on 4xx. */
        post: async function (endpoint, action, data) {
            var res;
            try {
                res = await fetch(endpoint, {
                    method: 'POST',
                    body: Api.form(action, data),
                    headers: { 'X-Hearth': '1' },
                    credentials: 'same-origin'
                });
            } catch (e) {
                return { success: false, message: 'Cannot reach the server. Check your internet connection.' };
            }
            return Api._parse(res);
        },

        /** GET an action with query parameters (read-only calls). */
        get: async function (endpoint, action, params) {
            var res;
            try { res = await fetch(Api.url(endpoint, action, params), { credentials: 'same-origin' }); }
            catch (e) { return { success: false, message: 'Cannot reach the server. Check your internet connection.' }; }
            return Api._parse(res);
        },

        /** URL for GET endpoints such as downloads and image previews. */
        url: function (endpoint, action, params) {
            var q = new URLSearchParams();
            q.set('action', action);
            Object.keys(params || {}).forEach(function (k) {
                var v = params[k];
                if (v === undefined || v === null) return;
                q.set(k, typeof v === 'object' ? JSON.stringify(v) : String(v));
            });
            return endpoint + '?' + q.toString();
        },

        /** POST with upload progress (XHR). onProgress(loadedBytes). */
        upload: function (endpoint, action, data, onProgress) {
            return new Promise(function (resolve) {
                var xhr = new XMLHttpRequest();
                xhr.open('POST', endpoint);
                xhr.setRequestHeader('X-Hearth', '1');
                xhr.upload.onprogress = function (e) { if (onProgress) onProgress(e.loaded); };
                xhr.onload = function () {
                    if (xhr.status === 401) { Api._signin(); return; }
                    try { resolve(JSON.parse(xhr.responseText)); }
                    catch (e) { resolve({ success: false, message: 'Server error ' + xhr.status }); }
                };
                xhr.onerror = function () { resolve({ success: false, message: 'Network error during upload.' }); };
                xhr.send(Api.form(action, data));
            });
        },

        _parse: async function (res) {
            if (res.status === 401) { Api._signin(); return { success: false, message: 'Please sign in again.' }; }
            try { return await res.json(); }
            catch (e) { return { success: false, message: 'Server error ' + res.status }; }
        },

        _signin: function () {
            Toast.error('Your session has ended. Taking you to the sign-in page…');
            setTimeout(function () { location.href = '/login'; }, 1200);
        }
    };

    // Short-hands for each API endpoint
    Api.files = function (action, data) { return Api.post('/fileapi', action, data); };
    Api.ai = function (action, data) { return Api.post('/aiapi', action, data); };
    Api.users = function (action, data) { return Api.post('/userapi', action, data); };
    Api.settings = function (action, data) { return Api.post('/settingsapi', action, data); };

    // ------------------------------------------------------------------ Fmt
    var Fmt = {
        esc: function (s) {
            return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
                return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
            });
        },
        size: function (n) {
            if (n == null) return '';
            if (n < 1024) return n + ' B';
            var u = ['KB', 'MB', 'GB', 'TB'], i = -1;
            do { n /= 1024; i++; } while (n >= 1024 && i < u.length - 1);
            return (n < 10 ? n.toFixed(1) : Math.round(n)) + ' ' + u[i];
        },
        date: function (iso) {
            if (!iso) return '';
            var d = new Date(/Z$|[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z');
            var now = new Date();
            var sameDay = d.toDateString() === now.toDateString();
            return sameDay
                ? 'Today ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
                : d.toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' }) + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        }
    };

    // ------------------------------------------------------------------ Toast
    var Toast = {
        show: function (msg, isError) {
            var host = document.getElementById('toast-host');
            if (!host) { if (isError) alert(msg); return; }
            var el = document.createElement('div');
            el.className = 'toast' + (isError ? ' error' : '');
            el.innerHTML = '<i class="fa-solid ' + (isError ? 'fa-circle-exclamation' : 'fa-circle-check') + '"></i><div></div>';
            el.lastChild.textContent = msg;
            host.appendChild(el);
            setTimeout(function () { el.classList.add('hide'); setTimeout(function () { el.remove(); }, 300); }, isError ? 6000 : 3000);
        },
        ok: function (msg) { Toast.show(msg, false); },
        error: function (msg) { Toast.show(msg, true); },
        /** Shows the message of an API result in the right colour. */
        result: function (r) { Toast.show(r.message, !r.success); }
    };

    // ------------------------------------------------------------------ Dialog
    var Dialog = {
        /** Low-level: opts { title, html, input, inputValue, okText, cancelText, danger, onOpen(body) } → Promise<value|null> */
        open: function (opts) {
            return new Promise(function (resolve) {
                var back = document.createElement('div');
                back.className = 'modal-backdrop';
                back.innerHTML =
                    '<div class="modal" role="dialog" aria-modal="true">' +
                    '<header></header><div class="modal-body"></div>' +
                    '<footer>' +
                    (opts.cancelText === false ? '' : '<button type="button" class="btn" data-r="cancel"></button>') +
                    '<button type="button" class="btn ' + (opts.danger ? 'btn-danger' : 'btn-primary') + '" data-r="ok"></button>' +
                    '</footer></div>';
                back.querySelector('header').textContent = opts.title || '';
                var body = back.querySelector('.modal-body');
                if (opts.html) body.innerHTML = opts.html;
                var input = null;
                if (opts.input) {
                    input = document.createElement('input');
                    input.type = opts.input === true ? 'text' : opts.input;
                    input.value = opts.inputValue || '';
                    body.appendChild(input);
                }
                back.querySelector('[data-r=ok]').textContent = opts.okText || 'OK';
                var cancelBtn = back.querySelector('[data-r=cancel]');
                if (cancelBtn) cancelBtn.textContent = opts.cancelText || 'Cancel';

                var state = {};
                function close(ok) {
                    // read the value BEFORE removing the dialog: getValue() may look up its inputs
                    var value = null;
                    if (ok) {
                        try { value = input ? input.value : (opts.getValue ? opts.getValue(state, body) : true); }
                        catch (e) { value = null; }
                    }
                    document.removeEventListener('keydown', onKey, true);
                    back.remove();
                    resolve(value);
                }
                function onKey(e) {
                    if (e.key === 'Escape') { e.preventDefault(); close(false); }
                    else if (e.key === 'Enter' && e.target.tagName !== 'TEXTAREA') { e.preventDefault(); close(true); }
                }
                back.addEventListener('mousedown', function (e) { if (e.target === back) close(false); });
                back.querySelector('[data-r=ok]').addEventListener('click', function () { close(true); });
                if (cancelBtn) cancelBtn.addEventListener('click', function () { close(false); });
                document.addEventListener('keydown', onKey, true);
                document.body.appendChild(back);
                if (opts.onOpen) opts.onOpen(body, state);
                if (input) {
                    input.focus();
                    // select file name without extension
                    var dot = input.value.lastIndexOf('.');
                    input.setSelectionRange(0, dot > 0 ? dot : input.value.length);
                } else back.querySelector('[data-r=ok]').focus();
            });
        },
        alert: function (title, text) {
            return Dialog.open({ title: title, html: '<p>' + Fmt.esc(text) + '</p>', cancelText: false });
        },
        confirm: function (title, text, okText, danger) {
            return Dialog.open({ title: title, html: '<p>' + Fmt.esc(text) + '</p>', okText: okText || 'OK', danger: !!danger });
        },
        prompt: function (title, label, value, okText) {
            return Dialog.open({ title: title, html: label ? '<p>' + Fmt.esc(label) + '</p>' : '', input: true, inputValue: value || '', okText: okText || 'OK' });
        },
        /** Lets the user browse folders; resolves to the chosen folder path or null. */
        pickFolder: function (title, startPath, okText) {
            return Dialog.open({
                title: title,
                okText: okText || 'Choose this folder',
                html: '<ul class="folder-picker"></ul><div class="fp-current"></div>',
                getValue: function (state) { return state.path; },
                onOpen: function (body, state) {
                    var list = body.querySelector('.folder-picker');
                    var cur = body.querySelector('.fp-current');
                    async function load(path) {
                        state.path = path;
                        cur.innerHTML = 'Destination: <b>/' + Fmt.esc(path) + '</b>';
                        list.innerHTML = '<li><span class="spinner"></span></li>';
                        var r = await Api.get('/fileapi', 'list', { path: path });
                        if (!r.success) { list.innerHTML = '<li>' + Fmt.esc(r.message) + '</li>'; return; }
                        var html = path ? '<li class="fp-up" data-p="' + Fmt.esc(path.split('/').slice(0, -1).join('/')) + '"><i class="fa-solid fa-arrow-turn-up"></i> Up</li>' : '';
                        r.items.forEach(function (it) {
                            if (it.IsDir && it.Path.charAt(0) !== '$') html += '<li data-p="' + Fmt.esc(it.Path) + '"><i class="fa-solid fa-folder"></i>' + Fmt.esc(it.Name) + '</li>';
                        });
                        list.innerHTML = html || '<li class="fp-up">No sub-folders</li>';
                    }
                    list.addEventListener('click', function (e) {
                        var li = e.target.closest('li[data-p]');
                        if (li) load(li.getAttribute('data-p'));
                    });
                    load(startPath || '');
                }
            });
        }
    };

    window.Api = Api;
    window.Fmt = Fmt;
    window.Toast = Toast;
    window.Dialog = Dialog;

    // ------------------------------------------------------------------ sidebar (mobile)
    document.addEventListener('DOMContentLoaded', function () {
        var toggle = document.getElementById('sidebar-toggle');
        var backdrop = document.getElementById('sidebar-backdrop');
        if (!toggle) return;
        toggle.addEventListener('click', function () { document.body.classList.toggle('sidebar-open'); });
        backdrop.addEventListener('click', function () { document.body.classList.remove('sidebar-open'); });
        document.querySelectorAll('.sidebar .nav-item').forEach(function (a) {
            a.addEventListener('click', function () { document.body.classList.remove('sidebar-open'); });
        });
    });
})();
