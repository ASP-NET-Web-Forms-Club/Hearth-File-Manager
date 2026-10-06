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
