'use strict';

// Giriş sayfasının metinleri (ortak sözlüğe eklenir).
Object.assign(I18N.tr, {
  'login.title': 'Oturum Aç', 'login.user': 'Kullanıcı adı', 'login.password': 'Şifre',
  'login.hint': 'Kullanıcı adı ve şifre Monitor ayarlarında tanımlıdır (Monitor:AdminUser, Monitor:AdminPassword).',
  'login.failed': 'Giriş başarısız', 'login.offline': 'Sunucuya ulaşılamadı',
});
Object.assign(I18N.en, {
  'login.title': 'Sign in', 'login.user': 'User name', 'login.password': 'Password',
  'login.hint': 'The user name and password are set in the Monitor settings (Monitor:AdminUser, Monitor:AdminPassword).',
  'login.failed': 'Sign-in failed', 'login.offline': 'Cannot reach the server',
});

// Giriş sayfası: kullanıcı adı ve şifreyi gönderir; başarılıysa arayüze geçer.
(async function () {
  const form = document.getElementById('loginForm');
  const error = document.getElementById('loginError');

  applyI18n();
  document.querySelectorAll('[data-lang]').forEach((b) => b.addEventListener('click', () => { setLang(b.dataset.lang); error.textContent = ''; }));

  // Giriş gerekmiyorsa (şifre tanımlı değil ve localhost'tayız) veya zaten giriş yapılmışsa doğrudan arayüze geçiyoruz.
  try {
    const me = await (await fetch('/api/auth/me')).json();
    if (!me.loginRequired || me.authenticated) { location.replace('/'); return; }
  } catch { /* sunucuya ulaşılamadı; form gösterilir */ }

  document.getElementById('username').focus();

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    error.textContent = '';
    const button = form.querySelector('button[type=submit]');
    button.disabled = true;
    try {
      const res = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Lang': LANG },
        body: JSON.stringify({ username: form.username.value, password: form.password.value }),
      });
      if (res.ok) { location.replace('/'); return; }
      let message = t('login.failed');
      try { message = (await res.json()).error || message; } catch { /* gövde JSON değil */ }
      error.textContent = message;
      form.password.select();
    } catch {
      error.textContent = t('login.offline');
    } finally {
      button.disabled = false;
    }
  });
})();
