/* Users page – list, add (with permissions), edit permissions, reset password, delete (not yourself). */
(function () {
    'use strict';
    var CFG = window.USERS_CONFIG || {};
    var users = [];

    function isMe(name) { return String(name).toLowerCase() === String(CFG.Me).toLowerCase(); }
    function label(key) { var p = CFG.Perms.find(function (x) { return x.Key === key; }); return p ? p.Label : key; }

    async function load() {
        var r = await Api.users('list', {});
        if (!r.success) { Toast.error(r.message); return; }
        users = r.items;
        document.getElementById('user-rows').innerHTML = users.map(function (u) {
            var self = isMe(u.Username);
            var tags = CFG.Perms.map(function (p) {
                var on = u.Permissions.indexOf(p.Key) >= 0;
                return '<span class="perm-tag' + (on ? ' on' : '') + '">' + (on ? '' : '<s>') + Fmt.esc(p.Label) + (on ? '' : '</s>') + '</span>';
            }).join('');
            return '<tr data-user="' + Fmt.esc(u.Username) + '">' +
                '<td><i class="fa-solid fa-circle-user" style="color:var(--text-3);margin-right:8px"></i>' + Fmt.esc(u.Username) + (self ? '<span class="badge">you</span>' : '') + '</td>' +
                '<td><div class="perm-tags">' + tags + '</div></td>' +
                '<td class="root-cell">' + (u.RootProblem ? '<i class="fa-solid fa-triangle-exclamation" style="color:var(--danger)" title="' + Fmt.esc(u.RootProblem) + '"></i> ' : '') + '<code>' + Fmt.esc(u.RootDisplay) + '</code>' + (u.RootPath ? '' : ' <small>(main root)</small>') + '</td>' +
                '<td>' + Fmt.date(u.CreatedUtc) + '</td>' +
                '<td class="col-actions">' +
                '<button type="button" class="btn" data-act="perms" title="Permissions and root folder"><i class="fa-solid fa-user-pen"></i><span>Edit</span></button> ' +
                '<button type="button" class="btn" data-act="password" title="Reset password"><i class="fa-solid fa-key"></i></button> ' +
                (self ? '' : '<button type="button" class="btn btn-danger-ghost" data-act="delete" title="Delete"><i class="fa-solid fa-trash-can"></i></button>') +
                '</td></tr>';
        }).join('');
    }

    /** Checkbox list HTML. "Use AI" ticks and locks "Manage files" because the AI changes files. */
    function permChecks(selected, lockUsers) {
        return '<div class="perm-checks">' + CFG.Perms.map(function (p) {
            var on = selected.indexOf(p.Key) >= 0;
            var locked = lockUsers && p.Key === 'users';
            return '<label class="check"><input type="checkbox" data-perm="' + p.Key + '"' + (on ? ' checked' : '') + (locked ? ' disabled' : '') + ' />' +
                '<span><b>' + Fmt.esc(p.Label) + '</b><small>' + Fmt.esc(p.Hint) + (locked ? ' (you cannot remove this from yourself)' : '') + '</small></span></label>';
        }).join('') + '</div>';
    }

    function wirePermChecks(body) {
        var ai = body.querySelector('[data-perm=ai]'), files = body.querySelector('[data-perm=files]');
        function sync() { if (ai.checked) { files.checked = true; files.disabled = true; } else files.disabled = false; }
        ai.addEventListener('change', sync);
        sync();
    }

    /** Root folder field shared by the add and edit dialogs. */
    function rootField(value) {
        return '<label class="field"><span>Root folder</span>' +
            '<input type="text" class="root-input" spellcheck="false" placeholder="(empty = main root)" value="' + Fmt.esc(value || '') + '" />' +
            '<small>Empty = the main root. <b>alex</b> = sub-folder of the main root. <b>/App_Data/public/alex</b> = inside Hearth. ' +
            '<b>D:\\websites\\alex-site</b> = absolute path. Both / and \\ work. The folder is created if missing.</small></label>';
    }

    function readPerms(body) {
        return Array.from(body.querySelectorAll('[data-perm]')).filter(function (c) { return c.checked; }).map(function (c) { return c.getAttribute('data-perm'); });
    }

    async function addUser() {
        var v = await Dialog.open({
            title: 'Add user',
            okText: 'Add user',
            html: '<label class="field"><span>Username</span><input type="text" id="nu-name" autocomplete="off" /></label>' +
                  '<label class="field"><span>Password (min. 8 characters)</span><input type="password" id="nu-pw1" autocomplete="new-password" /></label>' +
                  '<label class="field"><span>Repeat password</span><input type="password" id="nu-pw2" autocomplete="new-password" /></label>' +
                  rootField('') +
                  '<p style="margin:4px 0 8px"><b>Permissions</b></p>' + permChecks(CFG.Defaults, false),
            onOpen: function (body) { wirePermChecks(body); body.querySelector('#nu-name').focus(); },
            getValue: function (state, body) {
                var a = body.querySelector('#nu-pw1').value, b = body.querySelector('#nu-pw2').value;
                if (a !== b) { Toast.error('The two passwords do not match.'); return null; }
                return { username: body.querySelector('#nu-name').value.trim(), password: a, permissions: readPerms(body), root: body.querySelector('.root-input').value.trim() };
            }
        });
        if (!v) return;
        var r = await Api.users('add', v);
        Toast.result(r);
        if (r.success) load();
    }

    async function editPerms(name) {
        var u = users.find(function (x) { return x.Username === name; });
        var v = await Dialog.open({
            title: 'Edit ' + name,
            okText: 'Save',
            html: rootField(u.RootPath) + '<p style="margin:4px 0 8px"><b>Permissions</b></p>' + permChecks(u.Permissions, isMe(name)),
            onOpen: function (body) { wirePermChecks(body); },
            getValue: function (state, body) { return { perms: readPerms(body), root: body.querySelector('.root-input').value.trim() }; }
        });
        if (!v) return;
        var r = await Api.users('permissions', { username: name, permissions: v.perms, root: v.root });
        Toast.result(r);
        if (r.success) load();
    }

    async function resetPassword(name) {
        var pwd = await Dialog.open({
            title: (isMe(name) ? 'Change your password' : 'Reset password for ' + name),
            html: '<label class="field"><span>New password (min. 8 characters)</span><input type="password" id="pw1" autocomplete="new-password" /></label>' +
                  '<label class="field"><span>Repeat password</span><input type="password" id="pw2" autocomplete="new-password" /></label>' +
                  (isMe(name) ? '' : '<p style="font-size:13px">' + Fmt.esc(name) + ' will be signed out everywhere.</p>'),
            okText: 'Save',
            onOpen: function (body) { body.querySelector('#pw1').focus(); },
            getValue: function (state, body) {
                var a = body.querySelector('#pw1').value, b = body.querySelector('#pw2').value;
                if (a !== b) { Toast.error('The two passwords do not match.'); return null; }
                return a;
            }
        });
        if (!pwd) return;
        Toast.result(await Api.users('password', { username: name, password: pwd }));
    }

    document.addEventListener('DOMContentLoaded', function () {
        var table = document.getElementById('user-table');
        if (!table) return;

        document.getElementById('user-add').addEventListener('click', addUser);

        table.addEventListener('click', async function (e) {
            var b = e.target.closest('[data-act]');
            if (!b) return;
            var name = b.closest('tr').getAttribute('data-user');
            var act = b.getAttribute('data-act');
            if (act === 'perms') editPerms(name);
            else if (act === 'password') resetPassword(name);
            else {
                var ok = await Dialog.confirm('Delete user?', '"' + name + '" will no longer be able to sign in.', 'Delete user', true);
                if (!ok) return;
                var r = await Api.users('delete', { username: name });
                Toast.result(r);
                if (r.success) load();
            }
        });
        load();
    });
})();
