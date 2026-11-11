/* My Account page – change own password. */
document.addEventListener('DOMContentLoaded', function () {
    var form = document.getElementById('pw-form');
    if (!form) return;
    var err = document.getElementById('pw-error');

    form.addEventListener('submit', async function (e) {
        e.preventDefault();
        err.textContent = '';
        var cur = document.getElementById('pw-current').value;
        var a = document.getElementById('pw-new').value;
        var b = document.getElementById('pw-repeat').value;
        if (a !== b) { err.textContent = 'The two new passwords do not match.'; return; }

        var r = await Api.post('/accountapi', 'change-password', { current: cur, password: a });
        if (!r.success) { err.textContent = r.message; return; }
        Toast.ok(r.message);
        form.reset();
    });
});

/* My Account page – access tokens for the deploy API (/api/v1). */
document.addEventListener('DOMContentLoaded', function () {
    var rows = document.getElementById('token-rows');
    if (!rows) return;
    var CFG = window.ACCOUNT_CONFIG || {};
    var helpText = '';

    var local = ['localhost', '127.0.0.1', '[::1]'].indexOf(location.hostname) >= 0;
    if (location.protocol !== 'https:' && !local) document.getElementById('token-insecure').hidden = false;

    async function copy(text, okMsg) {
        try { await navigator.clipboard.writeText(text); Toast.ok(okMsg); }
        catch (e) { Toast.error('Could not copy automatically. Select the text and copy it yourself.'); }
    }

    async function load() {
        var r = await Api.get('/accountapi', 'token-list', {});
        if (!r.success) { Toast.error(r.message); return; }
        helpText = (r.data && r.data.Help) || '';
        if (!r.items.length) {
            rows.innerHTML = '<tr><td colspan="6" class="token-empty">No tokens yet.</td></tr>';
            return;
        }
        rows.innerHTML = r.items.map(function (t) {
            return '<tr data-id="' + Fmt.esc(t.Id) + '" data-name="' + Fmt.esc(t.Name) + '">' +
                '<td>' + Fmt.esc(t.Name) + '</td>' +
                '<td><code>' + Fmt.esc(t.Prefix) + '…</code></td>' +
                '<td>' + (t.CanWrite ? 'Read &amp; write' : 'Read only') + '</td>' +
                '<td>' + (t.Expired ? '<span class="token-expired">Expired</span>' : (t.ExpiresUtc ? Fmt.date(t.ExpiresUtc) : 'Never')) + '</td>' +
                '<td>' + (t.LastUsedUtc ? Fmt.date(t.LastUsedUtc) : 'Never') + '</td>' +
                '<td class="col-actions"><button type="button" class="btn btn-danger-ghost" data-act="revoke" title="Revoke"><i class="fa-solid fa-trash-can"></i><span>Revoke</span></button></td>' +
                '</tr>';
        }).join('');
    }

    async function addToken() {
        var v = await Dialog.open({
            title: 'New access token',
            okText: 'Create token',
            html: '<label class="field"><span>Name</span><input type="text" id="tk-name" maxlength="60" placeholder="e.g. Claude on my laptop" autocomplete="off" /></label>' +
                  '<label class="field"><span>Access</span><select id="tk-write">' +
                  (CFG.CanWrite ? '<option value="1">Read &amp; write (list, upload, delete)</option>' : '') +
                  '<option value="0">Read only (list files)</option></select>' +
                  (CFG.CanWrite ? '' : '<small>You need the “Manage files” permission for a read &amp; write token.</small>') + '</label>' +
                  '<label class="field"><span>Expires after</span><select id="tk-days">' +
                  '<option value="7">7 days</option><option value="30">30 days</option><option value="90" selected>90 days</option>' +
                  '<option value="365">1 year</option><option value="0">Never</option></select></label>',
            onOpen: function (body) { body.querySelector('#tk-name').focus(); },
            getValue: function (state, body) {
                return { name: body.querySelector('#tk-name').value.trim(), canWrite: body.querySelector('#tk-write').value === '1', days: body.querySelector('#tk-days').value };
            }
        });
        if (!v) return;
        if (!v.name) { Toast.error('Give the token a name.'); return; }
        var r = await Api.post('/accountapi', 'token-create', v);
        if (!r.success) { Toast.error(r.message); return; }
        load();

        var token = r.data.Token;
        await Dialog.open({
            title: 'Copy your new token now',
            okText: 'Done',
            cancelText: false,
            html: '<p>This is the only time the token is shown. Hearth keeps just a fingerprint of it.</p>' +
                  '<input type="text" class="token-secret" id="tk-secret" readonly />' +
                  '<p style="margin-top:12px"><button type="button" class="btn" id="tk-copy"><i class="fa-solid fa-copy"></i><span>Copy token</span></button></p>' +
                  '<p class="token-once">Store it on your computer as the environment variable <code>HEARTH_TOKEN</code>. Do not paste it into scripts, source files or chat messages.</p>',
            onOpen: function (body) {
                var input = body.querySelector('#tk-secret');
                input.value = token;
                input.addEventListener('focus', function () { input.select(); });
                body.querySelector('#tk-copy').addEventListener('click', function () { copy(token, 'Token copied'); });
            }
        });
    }

    document.getElementById('token-add').addEventListener('click', addToken);
    document.getElementById('token-help').addEventListener('click', function () {
        if (helpText) copy(helpText, 'Instructions copied. Paste them to your AI agent.');
    });
    rows.addEventListener('click', async function (e) {
        var b = e.target.closest('[data-act=revoke]');
        if (!b) return;
        var tr = b.closest('tr');
        var ok = await Dialog.confirm('Revoke token?', '"' + tr.getAttribute('data-name') + '" will stop working immediately.', 'Revoke', true);
        if (!ok) return;
        var r = await Api.post('/accountapi', 'token-revoke', { id: tr.getAttribute('data-id') });
        Toast.result(r);
        if (r.success) load();
    });
    load();
});
