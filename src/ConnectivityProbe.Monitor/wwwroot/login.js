'use strict';

// Giriş sayfası: kullanıcı adı ve şifreyi gönderir; başarılıysa arayüze geçer.
(async function () {
  const form = document.getElementById('loginForm');
  const error = document.getElementById('loginError');

  // Giriş gerekmiyorsa (şifre tanımlı değil ve localhost'tayız) veya zaten giriş yapılmışsa doğrudan arayüze geçiyoruz.
  try {
    const me = await (await fetch('/api/auth/me')).json();
    if (!me.loginRequired || me.authenticated) { location.replace('/'); return; }
  } catch { /* sunucuya ulaşılamadı; form gösterilir */ }

  document.getElementById('username').focus();

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    error.textContent = '';
    const button = form.querySelector('button');
    button.disabled = true;
    try {
      const res = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: form.username.value, password: form.password.value }),
      });
      if (res.ok) { location.replace('/'); return; }
      let message = 'Giriş başarısız';
      try { message = (await res.json()).error || message; } catch { /* gövde JSON değil */ }
      error.textContent = message;
      form.password.select();
    } catch {
      error.textContent = 'Sunucuya ulaşılamadı';
    } finally {
      button.disabled = false;
    }
  });
})();
