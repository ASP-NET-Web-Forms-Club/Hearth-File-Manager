/* Settings page – Gemini key/model/rate, website address, upload limit, dev auto-login. */
(function () {
    'use strict';

    document.addEventListener('DOMContentLoaded', function () {
        var form = document.getElementById('settings-form');
        if (!form) return;
        var S = window.SETTINGS || {};
        var $ = function (id) { return document.getElementById(id); };

        $('set-key-current').textContent = S.KeyMasked || 'none';
        $('set-fallbacks').textContent = S.Fallbacks || 'none';

        /** Fills the model dropdown; the saved model is always kept as an option. */
        function fillModels(list) {
            var current = $('set-model').value || S.GeminiModel || '';
            var names = list.slice();
            if (current && names.indexOf(current) < 0) names.unshift(current);
            $('set-model').innerHTML = names.map(function (m) {
                var hint = /lite/i.test(m) ? ' — Lite (more free requests)' : /pro/i.test(m) ? ' — Pro (not free)' : '';
                return '<option value="' + Fmt.esc(m) + '">' + Fmt.esc(m + hint) + '</option>';
            }).join('');
            $('set-model').value = current;
        }

        async function loadModels(showToast) {
            var r = await Api.settings('models', { key: $('set-key').value.trim() });
            if (!r.success) { if (showToast) Toast.error(r.message); return; }
            fillModels(r.items);
            if (showToast) Toast.ok(r.items.length + ' models loaded.');
        }

        fillModels([]);
        if (S.KeyMasked) loadModels(false);
        $('set-rpm').value = S.GeminiRpm;
        $('set-maxupload').value = S.MaxUploadMb;
        $('set-site').value = S.SitePreviewUrl || '';
        $('set-devlogin').checked = !!S.DevAutoLogin;
        $('dev-block').hidden = !S.IsLocal;

        $('set-load-models').addEventListener('click', async function () {
            var btn = this;
            btn.disabled = true;
            await loadModels(true);
            btn.disabled = false;
        });

        form.addEventListener('submit', async function (e) {
            e.preventDefault();
            var r = await Api.settings('save', {
                key: $('set-key').value.trim(),
                model: $('set-model').value.trim(),
                rpm: $('set-rpm').value,
                maxupload: $('set-maxupload').value,
                site: $('set-site').value.trim(),
                devlogin: $('set-devlogin').checked
            });
            Toast.result(r);
            if (r.success) {
                $('set-key').value = '';
                $('set-key-current').textContent = r.data.KeyMasked || 'none';
            }
        });
    });
})();
