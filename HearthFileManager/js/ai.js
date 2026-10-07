/* ==========================================================================
   AI Website Builder – chat UI. Conversation history lives in IndexedDB
   (HearthStore) in this browser; the server keeps nothing but undo snapshots.
   Flow: Api.ai('start') → poll Api.get('/aiapi','poll') until done → save.
   ========================================================================== */
(function () {
    'use strict';

    var CFG = window.AI_CONFIG || {};
    var store = new HearthStore('hearth-ai', 'chats');
    var chat = null;          // current conversation
    var pending = [];         // images attached to the next message: { MimeType, Data, Url }
    var running = null;       // { taskId, chatId, since, liveEl }
    var el = {};

    var EXAMPLES = [
        'Write a simple "under maintenance" landing page for my website.',
        'Build a home page with our company name, a short introduction, opening hours and a contact section.',
        'Add a contact form that saves messages to a database, and a private page to read them.',
        'Make the website look more modern and mobile friendly.'
    ];

    function newChat() {
        return { id: 'c' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6), title: 'New conversation', created: Date.now(), updated: Date.now(), contents: [], messages: [] };
    }

    // ------------------------------------------------------------------ markdown-lite

    function md(text) {
        var blocks = [];
        var s = String(text || '').replace(/```[\w-]*\n?([\s\S]*?)```/g, function (m, code) {
            blocks.push('<pre><code>' + Fmt.esc(code.replace(/\n$/, '')) + '</code></pre>');
            return '\u0000' + (blocks.length - 1) + '\u0000';
        });
        s = Fmt.esc(s)
            .replace(/`([^`\n]+)`/g, '<code>$1</code>')
            .replace(/\*\*([^*\n]+)\*\*/g, '<b>$1</b>')
            .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<i>$2</i>')
            .replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');

        var out = [], list = null;
        s.split('\n').forEach(function (line) {
            var m;
            if ((m = line.match(/^\s*[-*•]\s+(.*)/)) || (m = line.match(/^\s*\d+[.)]\s+(.*)/))) {
                var tag = /^\s*\d/.test(line) ? 'ol' : 'ul';
                if (!list || list.tag !== tag) { if (list) out.push('</' + list.tag + '>'); list = { tag: tag }; out.push('<' + tag + '>'); }
                out.push('<li>' + m[1] + '</li>');
                return;
            }
            if (list) { out.push('</' + list.tag + '>'); list = null; }
            if ((m = line.match(/^#{1,4}\s+(.*)/))) out.push('<h4>' + m[1] + '</h4>');
            else if (/^\s*(-{3,}|\*{3,})\s*$/.test(line)) out.push('<hr />');
            else if (line.trim() !== '') out.push('<p>' + line + '</p>');
        });
        if (list) out.push('</' + list.tag + '>');
        return out.join('').replace(/\u0000(\d+)\u0000/g, function (m, i) { return blocks[+i]; });
    }

    // ------------------------------------------------------------------ rendering

    function siteUrlFor(path) {
        // UrlPrefix: where this user's root sits inside the public site ('' or 'alex/'); null = not reachable by URL
        if (!CFG.SitePreviewUrl || CFG.UrlPrefix === null || /^App_Data\//i.test(path)) return null;
        return CFG.SitePreviewUrl.replace(/\/$/, '') + '/' + (CFG.UrlPrefix + path).split('/').map(encodeURIComponent).join('/');
    }

    function renderMessages() {
        if (!chat.messages.length) {
            el.messages.innerHTML =
                '<div class="ai-welcome">' +
                '<div class="ai-hero"><i class="fa-solid fa-wand-magic-sparkles"></i></div>' +
                '<h2>What would you like to build?</h2>' +
                '<p>Describe it in your own words. I will create or change the website files for you, and you can undo any change.</p>' +
                (CFG.HasKey ? '' : '<div class="ai-warn"><i class="fa-solid fa-key"></i> No Gemini API key yet. <a href="/settings">Add one in Settings</a> to start.</div>') +
                '<div class="ai-examples">' + EXAMPLES.map(function (x) { return '<button type="button" class="example" data-example="' + Fmt.esc(x) + '"><i class="fa-regular fa-lightbulb"></i>' + Fmt.esc(x) + '</button>'; }).join('') + '</div>' +
                '</div>';
            return;
        }
        el.messages.innerHTML = chat.messages.map(msgHtml).join('');
        scrollDown();
    }

    function msgHtml(m, i) {
        if (m.role === 'user') {
            var imgs = (m.images || []).map(function (u) { return '<img src="' + Fmt.esc(u) + '" alt="" />'; }).join('');
            return '<div class="msg user"><div class="bubble">' + (imgs ? '<div class="msg-imgs">' + imgs + '</div>' : '') + Fmt.esc(m.text).replace(/\n/g, '<br />') + '</div></div>';
        }
        var html = '<div class="msg ai" data-i="' + i + '"><div class="avatar-ai"><i class="fa-solid fa-wand-magic-sparkles"></i></div><div class="bubble">';
        if (m.activity && m.activity.length) {
            html += '<details class="activity"><summary><i class="fa-solid fa-list-check"></i> ' + m.activity.length + ' step(s)</summary><ul>' +
                m.activity.map(function (a) { return '<li>' + Fmt.esc(a) + '</li>'; }).join('') + '</ul></details>';
        }
        if (m.text) html += '<div class="md">' + md(m.text) + '</div>';
        if (m.error) html += '<div class="msg-error"><i class="fa-solid fa-triangle-exclamation"></i> ' + Fmt.esc(m.error) + '</div>';
        if (m.changed && m.changed.length) {
            html += '<div class="changed' + (m.undone ? ' undone' : '') + '"><div class="changed-head"><i class="fa-solid fa-pen-ruler"></i> ' +
                (m.undone ? 'Changes undone' : 'Changed ' + m.changed.length + ' item(s)') + '</div><ul>' +
                m.changed.map(function (p) {
                    var site = siteUrlFor(p);
                    return '<li><a href="/files#' + Fmt.esc(p.split('/').slice(0, -1).join('/')) + '" title="Show in My Files">/' + Fmt.esc(p) + '</a>' +
                        (site && !m.undone ? ' <a class="ext" href="' + Fmt.esc(site) + '" target="_blank" rel="noopener" title="Open on the website"><i class="fa-solid fa-up-right-from-square"></i></a>' : '') + '</li>';
                }).join('') + '</ul>';
            if (m.undoId && !m.undone) html += '<button type="button" class="btn btn-sm" data-undo="' + i + '"><i class="fa-solid fa-rotate-left"></i> Undo these changes</button>';
            html += '</div>';
        }
        return html + '</div></div>';
    }

    function scrollDown() { el.messages.scrollTop = el.messages.scrollHeight; }

    async function renderChatList() {
        var all = [];
        try { all = await store.all(); } catch (e) { Toast.error(e.message); }
        el.chatList.innerHTML = all.length ? all.map(function (c) {
            return '<li class="' + (chat && c.id === chat.id ? 'active' : '') + '" data-id="' + c.id + '">' +
                '<i class="fa-regular fa-message"></i><span class="t">' + Fmt.esc(c.title) + '</span>' +
                '<button type="button" class="btn-icon" data-chat-act="rename" title="Rename"><i class="fa-solid fa-pen"></i></button>' +
                '<button type="button" class="btn-icon" data-chat-act="delete" title="Delete"><i class="fa-solid fa-trash-can"></i></button></li>';
        }).join('') : '<li class="none">No conversations yet</li>';
    }

    async function openChat(id) {
        var c = id ? await store.get(id) : null;
        chat = c || newChat();
        try { localStorage.setItem('hfm.chat', chat.id); } catch (e) { }
        el.title.textContent = chat.title;
        renderMessages();
        renderChatList();
        document.body.classList.remove('chats-open');
        if (chat.pendingTask && !running) resume(chat.pendingTask);
    }

    async function saveChat() {
        chat.updated = Date.now();
        try { await store.put(chat); }
        catch (e) { Toast.error('Could not save the conversation in this browser: ' + e.message); }
        renderChatList();
    }

    // ------------------------------------------------------------------ sending

    function setBusy(on) {
        el.send.hidden = on;
        el.stop.hidden = !on;
        el.input.disabled = false;
        el.composer.classList.toggle('busy', on);
    }

    async function send(text) {
        text = (text || '').trim();
        if (running) return;
        if (!text && !pending.length) return;
        if (!CFG.HasKey) { Dialog.alert('Gemini API key needed', 'Please add your free Gemini API key on the Settings page first.'); return; }

        var images = pending;
        pending = [];
        renderAttachments();
        el.input.value = '';
        autoGrow();

        chat.messages.push({ role: 'user', text: text, images: images.map(function (p) { return p.Url; }) });
        if (chat.title === 'New conversation' && text) chat.title = text.length > 48 ? text.slice(0, 46) + '…' : text;
        el.title.textContent = chat.title;
        renderMessages();
        await saveChat();

        setBusy(true);
        var r = await Api.ai('start', {
            message: text,
            history: chat.contents,
            images: images.map(function (p) { return { MimeType: p.MimeType, Data: p.Data }; })
        });
        if (!r.success) {
            chat.messages.push({ role: 'ai', text: '', error: r.message });
            renderMessages();
            await saveChat();
            setBusy(false);
            return;
        }
        chat.pendingTask = r.data.TaskId;
        await saveChat();
        resume(r.data.TaskId);
    }

    function resume(taskId) {
        setBusy(true);
        var live = document.createElement('div');
        live.className = 'msg ai live';
        live.innerHTML = '<div class="avatar-ai"><i class="fa-solid fa-wand-magic-sparkles"></i></div><div class="bubble"><div class="live-status"><span class="spinner"></span> <span class="ls-text">Thinking…</span></div><ul class="live-steps"></ul></div>';
        el.messages.appendChild(live);
        scrollDown();
        running = { taskId: taskId, chatId: chat.id, since: 0, live: live, steps: [] };
        poll();
    }

    async function poll() {
        if (!running) return;
        var r = await Api.get('/aiapi', 'poll', { taskId: running.taskId, since: running.since });
        if (!running) return;
        if (!r.success) {
            if (/reach the server/i.test(r.message)) { setTimeout(poll, 3000); return; } // transient network blip
            finish({ FinalText: '', Error: r.message, Changed: [] });
            return;
        }
        var d = r.data;
        running.since = d.Next;
        var statusEl = running.live.querySelector('.ls-text');
        var stepsEl = running.live.querySelector('.live-steps');
        d.Events.forEach(function (ev) {
            if (ev.Kind === 'tool') {
                running.steps.push(ev.Text);
                var li = document.createElement('li');
                li.innerHTML = '<i class="fa-solid fa-check"></i> ';
                li.appendChild(document.createTextNode(ev.Text));
                stepsEl.appendChild(li);
            } else if (ev.Kind === 'status' || ev.Kind === 'wait') statusEl.textContent = ev.Text;
        });
        if (d.Events.length) scrollDown();
        if (d.Done) finish(d.Result || { Error: 'No result.' });
        else setTimeout(poll, 1200);
    }

    async function finish(res) {
        var job = running;
        running = null;
        job.live.remove();
        setBusy(false);

        // the user may have switched conversation while waiting
        var target = chat.id === job.chatId ? chat : await store.get(job.chatId);
        if (!target) return;
        if (res.Contents) target.contents = res.Contents;
        delete target.pendingTask;
        target.messages.push({
            role: 'ai',
            text: res.FinalText || (res.Error ? '' : 'Done.'),
            error: res.Error || null,
            changed: res.Changed || [],
            undoId: res.UndoId || null,
            activity: job.steps
        });
        if (target === chat) { renderMessages(); await saveChat(); }
        else { target.updated = Date.now(); await store.put(target); renderChatList(); Toast.ok('"' + target.title + '" finished.'); }
    }

    async function stop() {
        if (!running) return;
        running.live.querySelector('.ls-text').textContent = 'Stopping…';
        await Api.ai('stop', { taskId: running.taskId });
    }

    async function undo(i) {
        var m = chat.messages[i];
        if (!m || !m.undoId) return;
        var later = chat.messages.slice(i + 1).some(function (x) { return x.role === 'ai' && x.changed && x.changed.length && !x.undone; });
        var ok = await Dialog.confirm('Undo these changes?',
            'The ' + m.changed.length + ' item(s) changed in this reply will be put back the way they were.' +
            (later ? ' Note: later replies changed files too; undoing this one may also revert parts of those.' : ''), 'Undo changes', true);
        if (!ok) return;
        var r = await Api.ai('undo', { undoId: m.undoId });
        Toast.result(r);
        if (!r.success) return;
        m.undone = true;
        // tell Gemini, keeping user/model turns alternating
        chat.contents.push({ role: 'user', parts: [{ text: '[Note from the system: the user pressed Undo. Every change you made in your reply that changed ' + m.changed.join(', ') + ' has been reverted. Re-read files before editing them again.]' }] });
        chat.contents.push({ role: 'model', parts: [{ text: 'Understood – those changes were reverted.' }] });
        renderMessages();
        await saveChat();
    }

    // ------------------------------------------------------------------ attachments

    /** Shrinks large pictures before sending (saves tokens and upload time). */
    function readImage(file) {
        return new Promise(function (resolve) {
            var url = URL.createObjectURL(file);
            var img = new Image();
            img.onload = function () {
                var max = 1600, w = img.naturalWidth, h = img.naturalHeight;
                var scale = Math.min(1, max / Math.max(w, h));
                var c = document.createElement('canvas');
                c.width = Math.round(w * scale); c.height = Math.round(h * scale);
                c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
                URL.revokeObjectURL(url);
                var type = file.type === 'image/png' && scale === 1 ? 'image/png' : 'image/jpeg';
                var dataUrl = c.toDataURL(type, 0.85);
                resolve({ MimeType: type, Data: dataUrl.split(',')[1], Url: dataUrl });
            };
            img.onerror = function () { URL.revokeObjectURL(url); resolve(null); };
            img.src = url;
        });
    }

    async function addFiles(files) {
        for (var i = 0; i < files.length; i++) {
            if (!/^image\//.test(files[i].type)) continue;
            if (pending.length >= 4) { Toast.error('Up to 4 pictures per message.'); break; }
            var p = await readImage(files[i]);
            if (p) pending.push(p);
        }
        renderAttachments();
    }

    function renderAttachments() {
        el.attach.innerHTML = pending.map(function (p, i) {
            return '<div class="att"><img src="' + p.Url + '" alt="" /><button type="button" data-rm="' + i + '" title="Remove"><i class="fa-solid fa-xmark"></i></button></div>';
        }).join('');
        el.attach.hidden = !pending.length;
    }

    function autoGrow() {
        el.input.style.height = 'auto';
        el.input.style.height = Math.min(el.input.scrollHeight, 220) + 'px';
    }

    function exportChat() {
        var blob = new Blob([JSON.stringify(chat, null, 2)], { type: 'application/json' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = (chat.title || 'conversation').replace(/[^\w\- ]+/g, '').slice(0, 40) + '.json';
        a.click();
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
    }

    // ------------------------------------------------------------------ init

    document.addEventListener('DOMContentLoaded', async function () {
        el.messages = document.getElementById('ai-messages');
        if (!el.messages) return;
        el.composer = document.getElementById('ai-composer');
        el.input = document.getElementById('ai-input');
        el.send = document.getElementById('ai-send');
        el.stop = document.getElementById('ai-stop');
        el.attach = document.getElementById('attach-strip');
        el.chatList = document.getElementById('chat-list');
        el.title = document.getElementById('ai-title');

        HearthStore.persist();
        var preview = document.getElementById('ai-preview');
        if (CFG.SitePreviewUrl) { preview.href = CFG.SitePreviewUrl; preview.hidden = false; }

        el.composer.addEventListener('submit', function (e) { e.preventDefault(); send(el.input.value); });
        el.input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); send(el.input.value); }
        });
        el.input.addEventListener('input', autoGrow);
        el.input.addEventListener('paste', function (e) {
            var files = Array.from((e.clipboardData || {}).files || []);
            if (files.length) { e.preventDefault(); addFiles(files); }
        });
        el.composer.addEventListener('dragover', function (e) { e.preventDefault(); });
        el.composer.addEventListener('drop', function (e) { e.preventDefault(); addFiles(Array.from(e.dataTransfer.files || [])); });
        document.getElementById('ai-attach').addEventListener('change', function (e) { addFiles(Array.from(e.target.files)); e.target.value = ''; });
        el.attach.addEventListener('click', function (e) {
            var b = e.target.closest('[data-rm]');
            if (b) { pending.splice(+b.getAttribute('data-rm'), 1); renderAttachments(); }
        });
        el.stop.addEventListener('click', stop);

        el.messages.addEventListener('click', function (e) {
            var ex = e.target.closest('[data-example]');
            if (ex) { el.input.value = ex.getAttribute('data-example'); autoGrow(); el.input.focus(); return; }
            var u = e.target.closest('[data-undo]');
            if (u) undo(+u.getAttribute('data-undo'));
        });

        document.getElementById('ai-new-chat').addEventListener('click', function () {
            if (running) { Toast.error('Please wait until the current request finishes.'); return; }
            openChat(null);
            el.input.focus();
        });
        document.getElementById('ai-chats-toggle').addEventListener('click', function () { document.body.classList.toggle('chats-open'); });
        document.getElementById('ai-export').addEventListener('click', exportChat);

        el.chatList.addEventListener('click', async function (e) {
            var li = e.target.closest('li[data-id]');
            if (!li) return;
            var id = li.getAttribute('data-id');
            var act = e.target.closest('[data-chat-act]');
            if (!act) {
                if (running) { Toast.error('Please wait until the current request finishes.'); return; }
                openChat(id);
                return;
            }
            var c = await store.get(id);
            if (act.getAttribute('data-chat-act') === 'rename') {
                var t = await Dialog.prompt('Rename conversation', '', c.title, 'Rename');
                if (!t) return;
                c.title = t.trim();
                await store.put(c);
                if (chat.id === id) { chat.title = c.title; el.title.textContent = c.title; }
                renderChatList();
            } else {
                if (running && running.chatId === id) { Toast.error('This conversation is still working.'); return; }
                var ok = await Dialog.confirm('Delete conversation?', '"' + c.title + '" will be removed from this browser. Website files are not affected.', 'Delete', true);
                if (!ok) return;
                await store.delete(id);
                if (chat.id === id) openChat(null); else renderChatList();
            }
        });

        var last = null;
        try { last = localStorage.getItem('hfm.chat'); } catch (e) { }
        await openChat(last);
        el.input.focus();
    });
})();
