document.addEventListener('DOMContentLoaded', function () {
    var form = document.getElementById('login-form');
    if (!form) return;
    var err = document.getElementById('login-error');
    var btn = document.getElementById('login-submit');

    form.addEventListener('submit', async function (e) {
        e.preventDefault();
        err.textContent = '';
        btn.disabled = true;
        var r = await Api.post('/loginapi', 'login', {
            username: document.getElementById('login-username').value,
            password: document.getElementById('login-password').value
        });
        btn.disabled = false;
        if (r.success) location.href = '/files';
        else err.textContent = r.message;
    });
});
