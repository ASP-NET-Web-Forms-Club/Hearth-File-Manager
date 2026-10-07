/* ==========================================================================
   HearthEditor – portable plain-textarea code editor (line numbers, Tab indent,
   auto-indent, Ctrl+S). No dependencies besides site.js (Api, Toast, Dialog).

   One-liners:
     HearthEditor.open('index.php');                    // full-screen editor for a server file
     HearthEditor.open('css/site.css', { onSaved: reload });    // with a callback
     HearthEditor.open('css/site.css', { readOnly: true });     // view only (no Save)
   Embedded:
     var ed = new HearthEditor(mountEl, { value: '...', onSave: function (text) {...} });
     ed.getValue(); ed.setValue('x'); ed.destroy();
   ========================================================================== */
(function () {
    'use strict';

    var INDENT = '    ';

    function HearthEditor(mount, options) {
        this.opts = options || {};
        this.mount = mount;
        this.root = document.createElement('div');
        this.root.className = 'hed';
        this.root.innerHTML = '<div class="hed-gutter" aria-hidden="true"></div><textarea class="hed-text" spellcheck="false" autocapitalize="off" autocomplete="off" wrap="off"></textarea>';
        mount.appendChild(this.root);
        this.gutter = this.root.querySelector('.hed-gutter');
        this.ta = this.root.querySelector('.hed-text');
        this.ta.value = this.opts.value || '';
        this.saved = this.ta.value;
        this._lines = 0;

        var self = this;
        this._onInput = function () { self._renderGutter(); if (self.opts.onChange) self.opts.onChange(self.isDirty()); };
        this._onScroll = function () { self.gutter.scrollTop = self.ta.scrollTop; };
        this._onKey = function (e) { self._key(e); };
        this.ta.addEventListener('input', this._onInput);
        this.ta.addEventListener('scroll', this._onScroll);
        this.ta.addEventListener('keydown', this._onKey);
        this._renderGutter();
    }

    HearthEditor.prototype.getValue = function () { return this.ta.value; };
    HearthEditor.prototype.setValue = function (v) { this.ta.value = v; this.saved = v; this._renderGutter(); };
    HearthEditor.prototype.markSaved = function () { this.saved = this.ta.value; if (this.opts.onChange) this.opts.onChange(false); };
    HearthEditor.prototype.isDirty = function () { return this.ta.value !== this.saved; };
    HearthEditor.prototype.focus = function () { this.ta.focus(); this.ta.setSelectionRange(0, 0); this.ta.scrollTop = 0; };
    HearthEditor.prototype.destroy = function () {
        this.ta.removeEventListener('input', this._onInput);
        this.ta.removeEventListener('scroll', this._onScroll);
        this.ta.removeEventListener('keydown', this._onKey);
        this.root.remove();
    };

    HearthEditor.prototype._renderGutter = function () {
        var n = this.ta.value.split('\n').length;
        if (n === this._lines) return;
        this._lines = n;
        var out = [];
        for (var i = 1; i <= n; i++) out.push(i);
        this.gutter.textContent = out.join('\n') + '\n';
        this.gutter.scrollTop = this.ta.scrollTop;
    };

    /** Replace the selection with text, keeping native undo (execCommand where supported). */
    function insert(ta, text) {
        if (!document.execCommand || !document.execCommand('insertText', false, text)) {
            ta.setRangeText(text, ta.selectionStart, ta.selectionEnd, 'end');
            ta.dispatchEvent(new Event('input'));
        }
    }

    HearthEditor.prototype._key = function (e) {
        var ta = this.ta;
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
            e.preventDefault();
            if (this.opts.onSave) this.opts.onSave(ta.value);
            return;
        }
        if (e.key === 'Tab') {
            e.preventDefault();
            var v = ta.value, s = ta.selectionStart, en = ta.selectionEnd;
            var lineStart = v.lastIndexOf('\n', s - 1) + 1;
            if (s === en && !e.shiftKey) { insert(ta, INDENT); return; }
            // indent / outdent all selected lines
            var block = v.substring(lineStart, en);
            var lines = block.split('\n');
            var changed = lines.map(function (l) {
                if (!e.shiftKey) return INDENT + l;
                if (l.indexOf(INDENT) === 0) return l.substring(INDENT.length);
                return l.replace(/^\t| {1,3}/, '');
            }).join('\n');
            ta.setSelectionRange(lineStart, en);
            insert(ta, changed);
            ta.setSelectionRange(lineStart, lineStart + changed.length);
            return;
        }
        if (e.key === 'Enter' && !e.ctrlKey && !e.altKey) {
            e.preventDefault();
            var val = ta.value, pos = ta.selectionStart;
            var ls = val.lastIndexOf('\n', pos - 1) + 1;
            var indent = (val.substring(ls, pos).match(/^[ \t]*/) || [''])[0];
            var prev = val.charAt(pos - 1);
            if (prev === '{' || prev === '(' || prev === '[' || /<[a-zA-Z][^\/>]*>$/.test(val.substring(ls, pos))) indent += INDENT;
            insert(ta, '\n' + indent);
        }
    };

    // ------------------------------------------------------------------ full-screen file editor

    var openCount = 0;

    HearthEditor.open = async function (path, opts) {
        opts = opts || {};
        var r = await Api.get('/fileapi', 'read', { path: path });
        if (!r.success) { Toast.error(r.message); return null; }

        var name = path.split('/').pop();
        var back = document.createElement('div');
        back.className = 'hed-modal';
        back.innerHTML =
            '<div class="hed-window" role="dialog" aria-modal="true">' +
            '  <header class="hed-bar">' +
            '    <i class="fa-solid fa-file-code"></i>' +
            '    <div class="hed-title"><b></b><small></small></div>' +
            '    <span class="hed-state"></span>' +
            '    <button type="button" class="btn btn-primary" data-ed="save"><i class="fa-solid fa-floppy-disk"></i><span>Save</span></button>' +
            '    <button type="button" class="btn-icon" data-ed="close" title="Close (Esc)"><i class="fa-solid fa-xmark"></i></button>' +
            '  </header>' +
            '  <div class="hed-mount"></div>' +
            '  <footer class="hed-foot">Ctrl+S save · Tab / Shift+Tab indent · Esc close</footer>' +
            '</div>';
        back.querySelector('.hed-title b').textContent = name;
        back.querySelector('.hed-title small').textContent = '/' + path;
        document.body.appendChild(back);
        document.body.classList.add('hed-open');
        openCount++;

        var state = back.querySelector('.hed-state');
        var saveBtn = back.querySelector('[data-ed=save]');
        var ed;

        async function save() {
            if (opts.readOnly) return;
            saveBtn.disabled = true;
            state.textContent = 'Saving…';
            var res = await Api.files('save', { path: path, content: ed.getValue() });
            saveBtn.disabled = false;
            if (res.success) {
                ed.markSaved();
                state.textContent = 'Saved';
                Toast.ok(name + ' saved');
                if (opts.onSaved) opts.onSaved(path);
            } else { state.textContent = 'Not saved'; Toast.error(res.message); }
        }

        async function close() {
            if (ed.isDirty()) {
                var ok = await Dialog.confirm('Discard changes?', 'You have unsaved changes in ' + name + '.', 'Discard', true);
                if (!ok) return;
            }
            document.removeEventListener('keydown', onKey, true);
            window.removeEventListener('beforeunload', onUnload);
            ed.destroy();
            back.remove();
            if (--openCount === 0) document.body.classList.remove('hed-open');
            if (opts.onClose) opts.onClose(path);
        }

        function onKey(e) {
            if (e.key === 'Escape' && !document.querySelector('.modal-backdrop')) { e.preventDefault(); close(); }
        }
        function onUnload(e) { if (ed.isDirty()) { e.preventDefault(); e.returnValue = ''; } }

        ed = new HearthEditor(back.querySelector('.hed-mount'), {
            value: r.data.Content,
            onSave: save,
            onChange: function (dirty) { state.textContent = dirty ? 'Unsaved changes' : ''; }
        });
        if (opts.readOnly) {
            ed.ta.readOnly = true;
            saveBtn.hidden = true;
            state.innerHTML = '<i class="fa-solid fa-eye"></i> View only';
            back.querySelector('.hed-foot').textContent = 'View only · Esc close';
        }
        saveBtn.addEventListener('click', save);
        back.querySelector('[data-ed=close]').addEventListener('click', close);
        document.addEventListener('keydown', onKey, true);
        window.addEventListener('beforeunload', onUnload);
        ed.focus();
        return ed;
    };

    window.HearthEditor = HearthEditor;
})();
