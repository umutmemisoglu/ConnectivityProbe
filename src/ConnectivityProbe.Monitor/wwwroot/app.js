'use strict';

// ---------------------------------------------------------------------------------------------
// Durum
// ---------------------------------------------------------------------------------------------
const $ = (sel) => document.querySelector(sel);
let defs = { units: [], teams: [], apps: [], connections: [] }; // tanımlar
let snap = null;                            // monitörün son anlık görüntüsü
let lastSnapJson = '';                      // değişmediyse ekranı yeniden çizmemek için
let tab = 'monitor';

const UNASSIGNED = '__unassigned';          // ekibi olmayan (eski) uygulamaların sanal grubu
let selTeam = load('selTeam', null);         // Tanımlar'da seçili ekip

const STATE_TEXT = { healthy: 'Sağlıklı', degraded: 'Sorunlu', down: 'Erişilemiyor', unknown: 'Bekliyor' };
const STATE_ORDER = { down: 0, degraded: 1, unknown: 2, healthy: 3 };

// Tarayıcıda kalıcı küçük tercihler (seçili ekip, açık gruplar). Depolama kapalıysa sessizce varsayılan kullanılır.
function load(key, fallback) {
  try { const v = localStorage.getItem(key); return v == null ? fallback : JSON.parse(v); } catch { return fallback; }
}
function save(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* depolama kapalı olabilir */ }
}

// HTML'e basılacak metni güvenli hale getirir (ad/URL gibi kullanıcı girdileri için).
function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// Sunucuya istek atar; hata dönerse sunucunun mesajıyla Error fırlatır.
async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  // Oturum süresi dolduysa giriş sayfasına yönlendiriyoruz.
  if (res.status === 401 && !url.startsWith('/api/auth')) { location.href = '/login.html'; throw new Error('Giriş gerekli'); }
  if (!res.ok) {
    let msg = res.statusText;
    try { msg = (await res.json()).error || msg; } catch { /* gövde JSON değil */ }
    throw new Error(msg);
  }
  try { return await res.json(); } catch { return null; }
}

const timeOf = (iso) => (iso ? new Date(iso).toLocaleTimeString('tr-TR') : '-');
// Bugünse yalnızca saat, değilse tarih + saat.
const dateTimeOf = (iso) => {
  if (!iso) return '-';
  const d = new Date(iso);
  return d.toDateString() === new Date().toDateString() ? d.toLocaleTimeString('tr-TR') : d.toLocaleString('tr-TR');
};
// Kısa "ne kadar önce": "8 sn önce", "3 dk önce", "2 sa önce".
function agoOf(iso) {
  const sec = Math.max(0, Math.round((Date.now() - new Date(iso)) / 1000));
  if (sec < 60) return `${sec} sn önce`;
  if (sec < 3600) return `${Math.floor(sec / 60)} dk önce`;
  return `${Math.floor(sec / 3600)} sa önce`;
}

// Bir andan bu yana geçen süre: "45 saniyedir", "12 dakikadır", "2 saat 5 dakikadır", "3 gündür".
function durationOf(iso) {
  const sec = Math.max(0, Math.round((Date.now() - new Date(iso)) / 1000));
  if (sec < 60) return `${sec} saniyedir`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min} dakikadır`;
  const h = Math.floor(min / 60);
  if (h < 24) return min % 60 ? `${h} saat ${min % 60} dakikadır` : `${h} saattir`;
  return `${Math.floor(h / 24)} gündür`;
}
const shortId = (id) => (id || '').slice(0, 8);
const targetOf = (c) => c.host + (c.port ? ':' + c.port : '');
const byName = (a, b) => a.name.localeCompare(b.name, 'tr');
const lower = (s) => String(s ?? '').toLocaleLowerCase('tr');

const teamOf = (id) => defs.teams.find((t) => t.id === id);
const unitOf = (id) => defs.units.find((u) => u.id === id);
const appTeamId = (appId) => {
  const a = defs.apps.find((x) => x.id === appId);
  return a && teamOf(a.teamId) ? a.teamId : UNASSIGNED;
};
const teamLabel = (t) => `${unitOf(t.unitId)?.name ?? '?'} / ${t.name}`;

// Pod'un görünen adı (Kubernetes'te pod adı, değilse makine adı) ve sürüm etiketi ("1.4.0 · b7e2c1a0").
const podName = (p) => p.details?.POD_NAME || p.details?.HOSTNAME || p.machineName;
const versionLabel = (p) => (p.appVersion || '?') + (p.buildId ? ' · ' + p.buildId.slice(0, 6) : '');
// Uygulamanın canlı pod'larındaki farklı sürüm/build'ler.
const buildsOf = (s) => [...new Set((s.pods || []).filter((p) => p.state === 'up').map(versionLabel))];

// ---------------------------------------------------------------------------------------------
// Sekmeler ve üst çubuk
// ---------------------------------------------------------------------------------------------
function setTab(name) {
  tab = name;
  save('tab', name);
  $('#tab-monitor').hidden = name !== 'monitor';
  $('#tab-defs').hidden = name !== 'defs';
  $('#tab-versions').hidden = name !== 'versions';
  if (name === 'versions') renderVersions();
  $('#navTools').hidden = name !== 'monitor'; // arama ve filtre yalnızca monitörde anlamlı
  window.scrollTo(0, 0);
  $('#topbar').classList.toggle('scrolled', name !== 'monitor'); // Tanımlar'da billboard yok: menü baştan koyu
  document.querySelectorAll('.tab').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
}

// "Son tur / sonraki tur" bilgisini her saniye günceller.
function renderMeta() {
  if (!snap) { $('#meta').textContent = ''; return; }
  const parts = [];
  if (snap.lastRunUtc) parts.push('güncellendi ' + timeOf(snap.lastRunUtc));
  parts.push('pod\'lar her ' + snap.intervalSeconds + ' sn test eder');
  $('#meta').textContent = parts.join(' · ');
}

// ---------------------------------------------------------------------------------------------
// Form penceresi (ekle/düzenle için ortak)
// ---------------------------------------------------------------------------------------------
// fields: [{ name, label, placeholder, type (text|number|checkbox|select), options [{value,label}], hint }]
function openForm(title, fields, values, onSubmit) {
  $('#dlgTitle').textContent = title;
  $('#dlgError').textContent = '';
  $('#dlgFields').innerHTML = fields.map((f) => {
    const hint = f.hint ? `<div class="fh">${esc(f.hint)}</div>` : '';
    if (f.type === 'checkbox') {
      return `<div class="field"><label class="check"><input id="f_${f.name}" name="${f.name}" type="checkbox" ${values?.[f.name] ? 'checked' : ''}> ${esc(f.label)}</label>${hint}</div>`;
    }
    if (f.type === 'select') {
      const opts = f.options.map((o) => `<option value="${esc(o.value)}" ${String(values?.[f.name] ?? '') === String(o.value) ? 'selected' : ''}>${esc(o.label)}</option>`).join('');
      return `<div class="field"><label for="f_${f.name}">${esc(f.label)}</label><select id="f_${f.name}" name="${f.name}">${opts}</select>${hint}</div>`;
    }
    return `<div class="field"><label for="f_${f.name}">${esc(f.label)}</label>
      <input id="f_${f.name}" name="${f.name}" type="${f.type || 'text'}" placeholder="${esc(f.placeholder || '')}"
             value="${esc(values?.[f.name] ?? '')}" autocomplete="off">${hint}</div>`;
  }).join('');

  const form = $('#dlgForm');
  form.onsubmit = async (e) => {
    e.preventDefault();
    const result = {};
    fields.forEach((f) => {
      const el = form.elements[f.name];
      result[f.name] = f.type === 'checkbox' ? el.checked : el.value;
    });
    try {
      await onSubmit(result);
      $('#dlg').close();
    } catch (err) {
      $('#dlgError').textContent = err.message;
    }
  };
  $('#dlg').showModal();
  form.elements[fields[0].name].focus();
}

// Ekip seçim listesi (uygulamayı başka ekibe taşımak / bağlantının havuzunu değiştirmek için).
const teamOptions = (emptyLabel) => [
  { value: '', label: emptyLabel },
  ...defs.teams.slice().sort((a, b) => teamLabel(a).localeCompare(teamLabel(b), 'tr')).map((t) => ({ value: t.id, label: teamLabel(t) })),
];

// ---------------------------------------------------------------------------------------------
// Tanımlar sekmesi: Birim -> Ekip ağacı, ekip havuzu + ortak havuz, ekibin uygulamaları
// ---------------------------------------------------------------------------------------------
async function loadDefs() {
  defs = await api('GET', '/api/definitions');
  defs.units ??= []; defs.teams ??= [];
  lastSnapJson = ''; // tanımlar değişti: monitör ekranı da yeniden çizilsin
  // Seçili ekip artık yoksa ilk ekibe (yoksa atanmamışlara) geçiyoruz.
  if (selTeam !== UNASSIGNED && !teamOf(selTeam)) selTeam = defs.teams[0]?.id ?? UNASSIGNED;
  renderDefs();
}

function renderDefs() {
  renderTree();
  renderPool();
  renderApps();
  wireDragAndDrop();
}

// step 1: Sol: birimler ve altlarındaki ekipler. Ekibe tıklayınca ortada havuzu, sağda uygulamaları açılır.
function renderTree() {
  const unassigned = defs.apps.filter((a) => !teamOf(a.teamId));
  const units = defs.units.slice().sort(byName);
  $('#tree').innerHTML = (units.length ? units.map((u) => {
    const teams = defs.teams.filter((t) => t.unitId === u.id).sort(byName);
    return `
      <div class="tree-unit">
        <div class="tree-unit-head">
          <b class="grow">${esc(u.name)}</b>
          <button class="icon-btn" data-add-team="${u.id}" title="Bu birime ekip ekle">+ Ekip</button>
          <button class="icon-btn" data-edit-unit="${u.id}" title="Düzenle">✎</button>
          <button class="icon-btn" data-del-unit="${u.id}" title="Sil">✕</button>
        </div>
        ${teams.length ? teams.map((t) => {
          const n = defs.apps.filter((a) => a.teamId === t.id).length;
          return `<div class="tree-team ${selTeam === t.id ? 'sel' : ''}" data-select-team="${t.id}">
              <span class="grow">${esc(t.name)}</span><span class="count">${n} uyg.</span>
              <button class="icon-btn" data-edit-team="${t.id}" title="Düzenle">✎</button>
              <button class="icon-btn" data-del-team="${t.id}" title="Sil">✕</button>
            </div>`;
        }).join('') : '<div class="hint tree-empty">Ekip yok. "+ Ekip" ile ekleyin.</div>'}
      </div>`;
  }).join('') : '<div class="hint">Önce bir birim (ör. Efatura), sonra altına ekipler ekleyin.</div>')
  + (unassigned.length ? `<div class="tree-team unassigned ${selTeam === UNASSIGNED ? 'sel' : ''}" data-select-team="${UNASSIGNED}">
        <span class="grow">Atanmamış uygulamalar</span><span class="count">${unassigned.length} uyg.</span></div>` : '');
}

// Havuzdaki bir bağlantı kutusu (sürüklenebilir).
const poolChip = (c) => `
  <div class="chip" draggable="true" data-conn="${c.id}">
    <span class="grip">⋮⋮</span>
    <span class="grow conn-info">
      <span class="name">${esc(c.name)}${targetBadge(c)}</span>
      <span class="conn-target"><span class="host">${esc(c.host)}</span>${c.port ? `<span class="port">${c.port}</span>` : ''}</span>
    </span>
    <button class="icon-btn" data-edit-conn="${c.id}" title="Düzenle">✎</button>
    <button class="icon-btn" data-del-conn="${c.id}" title="Sil">✕</button>
  </div>`;

// step 2: Orta: seçili ekibin havuzu ve herkesin kullanabileceği ortak havuz.
function renderPool() {
  const team = teamOf(selTeam);
  const teamConns = team ? defs.connections.filter((c) => c.teamId === team.id).sort(byName) : [];
  const common = defs.connections.filter((c) => !c.teamId).sort(byName);
  $('#poolPanel').innerHTML = `
    <div class="panel-head"><h2>Bağlantı havuzu</h2></div>
    <p class="hint">Bir bağlantıyı tutup sağdaki uygulamanın üzerine bırakın. Ekip bağlantıları yalnızca o ekibin uygulamalarına, ortak bağlantılar herkese atanabilir.</p>
    ${team ? `
      <div class="pool-head"><b>${esc(team.name)} havuzu</b><button class="btn small" data-add-conn="${team.id}">+ Ekle</button></div>
      <div class="pool">${teamConns.length ? teamConns.map(poolChip).join('') : '<div class="hint">Bu ekibin kendi bağlantısı yok.</div>'}</div>`
    : '<p class="hint">Ekip havuzu için soldan bir ekip seçin.</p>'}
    <div class="pool-head"><b>Ortak havuz</b><button class="btn small" data-add-conn="">+ Ekle</button></div>
    <div class="pool">${common.length ? common.map(poolChip).join('') : '<div class="hint">Ortak bağlantı yok (ör. merkezi SQL, LDAP).</div>'}</div>`;
}

// step 3: Sağ: seçili ekibin uygulamaları; her kartın içi, bağlantıların bırakılacağı alan.
function renderApps() {
  const team = teamOf(selTeam);
  const apps = defs.apps.filter((a) => (team ? a.teamId === team.id : selTeam === UNASSIGNED && !teamOf(a.teamId))).sort(byName);
  const title = team ? `${esc(team.name)} uygulamaları` : selTeam === UNASSIGNED ? 'Atanmamış uygulamalar' : 'Uygulamalar';
  const eligible = (a) => defs.connections.filter((c) => !c.teamId || c.teamId === a.teamId);

  $('#appsPanel').innerHTML = `
    <div class="panel-head"><h2>${title}</h2></div>
    <details class="sk-setup add-help"><summary>Yeni uygulama nasıl eklenir?</summary>
      <p class="hint">Uygulamalar burada eklenmez. ConnectivityProbe'u uygulamaya ekleyip başlangıçta aşağıdaki satırı çağırın;
        uygulama ilk açılışta anahtarıyla kendini kaydeder ve "Atanmamış uygulamalar" altında görünür. Sonra ✎ ile ekibine taşıyıp bağlantılarını atayın.</p>
      <pre>${esc(setupSnippet('uygulama-anahtari', 'Uygulama Adı'))}</pre>
    </details>
    ${selTeam === UNASSIGNED ? '<p class="hint">Bu uygulamalar kendini kaydetti ama henüz bir ekibe atanmadı. ✎ ile ekibine taşıyın.</p>' : ''}
    <div class="apps">${apps.length ? apps.map((a) => {
      const attached = a.connectionIds.map((id) => defs.connections.find((c) => c.id === id)).filter(Boolean);
      const free = eligible(a).filter((c) => !a.connectionIds.includes(c.id)).sort(byName);
      return `
        <div class="app-card" data-app="${a.id}">
          <div class="app-head">
            <div class="grow"><div class="title">${esc(a.name)}</div>
              <div class="url">anahtar <code>${esc(a.appKey)}</code>${a.registeredAtUtc > '2000' ? ` · kayıt ${dateTimeOf(a.registeredAtUtc)}` : ''}</div></div>
            <button class="icon-btn" data-copy="${esc(a.appKey)}" title="Anahtarı kopyala">⧉</button>
            <button class="icon-btn" data-edit-app="${a.id}" title="Adını değiştir / ekibe taşı">✎</button>
            <button class="icon-btn" data-del-app="${a.id}" title="Sil">✕</button>
          </div>
          <div class="dropzone">
            ${attached.length ? attached.map((c) => `
              <span class="chip"><span class="name">${esc(c.name)}${targetBadge(c)}${c.teamId ? '' : ' <span class="common" title="Ortak havuzdan">ortak</span>'}</span>
                <span class="target">${esc(targetOf(c))}</span>
                <button class="icon-btn" data-detach="${a.id}|${c.id}" title="Bu uygulamadan çıkar">✕</button></span>`).join('')
              : '<span class="placeholder">Bağlantıları buraya sürükleyip bırakın</span>'}
            ${free.length ? `<select class="add-select" data-add-select="${a.id}"><option value="">+ bağlantı seç…</option>
              ${free.map((c) => `<option value="${c.id}">${esc(c.name)}${c.teamId ? '' : ' (ortak)'}</option>`).join('')}</select>` : ''}
          </div>
        </div>`;
    }).join('') : `<div class="empty">${team ? 'Bu ekipte uygulama yok. Kendini kaydeden uygulamalar "Atanmamış uygulamalar" altında görünür; oradan bu ekibe taşıyın.' : 'Soldan bir ekip seçin.'}</div>`}</div>`;
}

// Bir bağlantıyı bir uygulamayla ilişkilendirir ve tanımları yeniler.
async function attach(appId, connId) {
  try { await api('PUT', `/api/apps/${appId}/connections/${connId}`); }
  catch (err) { alert(err.message); }
  await loadDefs();
}

// Sürükle-bırak bağlantıları: havuzdaki kutuları sürüklenebilir yapar, uygulama kartlarını bırakma alanı yapar.
function wireDragAndDrop() {
  // step 1: Sürükleme başlayınca bağlantının kimliğini taşıyoruz.
  document.querySelectorAll('#poolPanel .chip').forEach((chip) => {
    chip.addEventListener('dragstart', (e) => {
      e.dataTransfer.setData('text/plain', chip.dataset.conn);
      e.dataTransfer.effectAllowed = 'copy';
    });
  });

  document.querySelectorAll('.app-card').forEach((card) => {
    // step 2: Kartın üstüne gelince "bırakılabilir" olarak vurguluyoruz (preventDefault olmadan bırakma çalışmaz).
    card.addEventListener('dragover', (e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; card.classList.add('drag-over'); });
    card.addEventListener('dragleave', (e) => { if (!card.contains(e.relatedTarget)) card.classList.remove('drag-over'); });

    // step 3: Bırakınca bağlantıyı bu uygulamayla ilişkilendiriyoruz (sunucu ekip kuralını kontrol eder).
    card.addEventListener('drop', (e) => {
      e.preventDefault();
      card.classList.remove('drag-over');
      const connId = e.dataTransfer.getData('text/plain');
      if (connId) attach(card.dataset.app, connId);
    });
  });

  // Sürüklemeye alternatif: kartın içindeki listeden seçerek ekleme.
  document.querySelectorAll('[data-add-select]').forEach((sel) => {
    sel.addEventListener('change', () => { if (sel.value) attach(sel.dataset.addSelect, sel.value); });
  });
}

// Bağlantı formu. Her pod bağlantıyı kendi içinden TCP (telnet) ile test eder. Hedef de Monitor'e kayıtlı bir uygulamaysa
// seçilebilir; o zaman bağlantı satırında hedef uygulamanın pod sayısı ve durumu da görünür.
const connFields = () => [
  { name: 'name', label: 'Ad', placeholder: 'ör. Ana veritabanı' },
  { name: 'host', label: 'Host veya URL', placeholder: 'sql01, sql01:1433 veya https://orders.example.com/', hint: 'Sunucu adı, IP, sunucu:port ya da tam URL.' },
  { name: 'port', label: 'Port', type: 'number', placeholder: 'ör. 1433', hint: 'Host içinde port varsa veya URL girdiyseniz boş bırakabilirsiniz (https 443, http 80).' },
  { name: 'targetAppId', label: 'Hedef uygulama (isteğe bağlı)', type: 'select',
    options: [{ value: '', label: '(yok: veritabanı, kuyruk, dış servis...)' }, ...defs.apps.slice().sort(byName).map((a) => ({ value: a.id, label: a.name }))],
    hint: 'Hedef de ConnectivityProbe kullanan, Monitor\x27e kayıtlı bir uygulamaysa seçin: bağlantı satırında onun pod sayısı ve durumu da görünür.' },
  { name: 'teamId', label: 'Havuz', type: 'select', options: teamOptions('Ortak havuz (herkes kullanabilir)'),
    hint: 'Ekip havuzundaki bağlantı yalnızca o ekibin uygulamalarına atanabilir.' },
];

// Havuzda ve kartlarda hedefi kayıtlı bir uygulama olan bağlantıları gösteren rozet.
const targetBadge = (c) => {
  const target = c.targetAppId && defs.apps.find((a) => a.id === c.targetAppId);
  return target ? ' <span class="cp" title="Hedef uygulama: ' + esc(target.name) + '">→ ' + esc(target.name) + '</span>' : '';
};

const appFields = () => [
  { name: 'name', label: 'Ad', placeholder: 'ör. Orders API', hint: 'İlk kayıtta uygulamanın bildirdiği ad; burada değiştirebilirsiniz. Anahtar değişmez.' },
  { name: 'teamId', label: 'Ekip', type: 'select', options: teamOptions('(atanmamış)'),
    hint: 'Başka ekibe taşınırsa eski ekibin havuzundan atanmış bağlantılar çıkarılır; ortak bağlantılar kalır.' },
];

// Form değerlerini API'nin beklediği biçime çevirir (boş port -> null, boş ekip -> null).
const toConnBody = (v) => ({
  name: v.name, host: v.host, port: v.port === '' ? null : Number(v.port), teamId: v.teamId || null, targetAppId: v.targetAppId || null,
});
const toAppBody = (v) => ({ name: v.name, teamId: v.teamId || null });

// Uygulamaya eklenecek satır (Monitor'ün kendi adresiyle).
const setupSnippet = (key, name) => `// dotnet add package ConnectivityProbe
// ASP.NET Core / Worker / konsol: Program.cs'te başlangıçta.  IIS / klasik ASP.NET: Global.asax Application_Start içinde.
ConnectivityProbe.ConnectivityProbeAgent.Start(
    monitorUrl: "${location.origin}",
    appKey: "${key}",
    appName: "${name}");`;

// Panoya kopyalar (güvenli olmayan bağlamda yedek yöntem).
async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; }
  catch {
    const ta = document.createElement('textarea');
    ta.value = text; document.body.appendChild(ta); ta.select();
    const ok = document.execCommand('copy'); ta.remove(); return ok;
  }
}

async function afterChange() { await loadDefs(); await refreshMonitor(); }

// Tanımlar sekmesindeki tüm tıklamalar tek yerden yönetilir.
$('#tab-defs').addEventListener('click', async (e) => {
  const t = e.target.closest('button, [data-select-team]');
  if (!t) return;
  const d = t.dataset;

  try {
    // Birimler
    if (t.id === 'addUnit') {
      openForm('Birim ekle', [{ name: 'name', label: 'Birim adı', placeholder: 'ör. Efatura' }], {},
        async (v) => { await api('POST', '/api/units', v); await afterChange(); });
    } else if (d.editUnit) {
      const u = unitOf(d.editUnit);
      openForm('Birimi düzenle', [{ name: 'name', label: 'Birim adı' }], u,
        async (v) => { await api('PUT', '/api/units/' + u.id, v); await afterChange(); });
    } else if (d.delUnit) {
      const u = unitOf(d.delUnit);
      if (confirm(`"${u.name}" birimi silinsin mi?`)) { await api('DELETE', '/api/units/' + u.id); await afterChange(); }

    // Ekipler
    } else if (d.addTeam) {
      openForm('Ekip ekle', [{ name: 'name', label: 'Ekip adı', placeholder: 'ör. Fatura Ekibi' }], {},
        async (v) => { const team = await api('POST', '/api/teams', { name: v.name, unitId: d.addTeam }); selTeam = team.id; save('selTeam', selTeam); await afterChange(); });
    } else if (d.editTeam) {
      const tm = teamOf(d.editTeam);
      openForm('Ekibi düzenle', [
        { name: 'name', label: 'Ekip adı' },
        { name: 'unitId', label: 'Birim', type: 'select', options: defs.units.slice().sort(byName).map((u) => ({ value: u.id, label: u.name })) },
      ], tm, async (v) => { await api('PUT', '/api/teams/' + tm.id, v); await afterChange(); });
    } else if (d.delTeam) {
      const tm = teamOf(d.delTeam);
      if (confirm(`"${tm.name}" ekibi silinsin mi?`)) { await api('DELETE', '/api/teams/' + tm.id); await afterChange(); }
    } else if (d.selectTeam) {
      selTeam = d.selectTeam; save('selTeam', selTeam); renderDefs();

    // Bağlantılar
    } else if (d.addConn !== undefined) {
      openForm('Bağlantı ekle', connFields(), { teamId: d.addConn }, async (v) => { await api('POST', '/api/connections', toConnBody(v)); await afterChange(); });
    } else if (d.editConn) {
      const c = defs.connections.find((x) => x.id === d.editConn);
      openForm('Bağlantıyı düzenle', connFields(), { ...c, port: c.port ?? '', teamId: c.teamId ?? '' },
        async (v) => { await api('PUT', '/api/connections/' + c.id, toConnBody(v)); await afterChange(); });
    } else if (d.delConn) {
      const c = defs.connections.find((x) => x.id === d.delConn);
      if (confirm(`"${c.name}" bağlantısı havuzdan ve tüm uygulamalardan silinsin mi?`)) { await api('DELETE', '/api/connections/' + c.id); await afterChange(); }

    // Uygulamalar (kendini kaydeder; burada yalnızca ad, ekip ve bağlantılar düzenlenir)
    } else if (d.editApp) {
      const a = defs.apps.find((x) => x.id === d.editApp);
      openForm('Uygulamayı düzenle', appFields(), { ...a, teamId: a.teamId ?? '' },
        async (v) => { await api('PUT', '/api/apps/' + a.id, toAppBody(v)); await afterChange(); });
    } else if (d.copy !== undefined) {
      const ok = await copyText(d.copy);
      t.textContent = ok ? '✓' : '!';
      setTimeout(() => { t.textContent = t.dataset.copy.includes('\n') ? 'Kopyala' : '⧉'; }, 1200);
    } else if (d.delApp) {
      const a = defs.apps.find((x) => x.id === d.delApp);
      if (confirm(`"${a.name}" uygulaması silinsin mi?\n\nPod'ları hâlâ çalışıyorsa bir sonraki bildirimde kendini yeniden kaydeder (ekipsiz ve bağlantısız). Kalıcı olarak kaldırmak için uygulamadan ConnectivityProbe'u da çıkarın.`)) {
        await api('DELETE', '/api/apps/' + a.id); await afterChange();
      }
    } else if (d.detach) {
      const [appId, connId] = d.detach.split('|');
      await api('DELETE', `/api/apps/${appId}/connections/${connId}`);
      await afterChange();
    }
  } catch (err) {
    alert(err.message);
  }
});

$('#dlgCancel').addEventListener('click', () => $('#dlg').close());

// ---------------------------------------------------------------------------------------------
// Monitör sekmesi (Netflix tarzı): üstte öne çıkan uygulama (billboard), altında her ekip için yatay kayan kart satırı.
// Karta tıklayınca detay penceresi (pod'lar, bağlantı matrisi, geçmiş) açılır.
// ---------------------------------------------------------------------------------------------
let modalAppId = null; // detay penceresi açık olan uygulama

async function refreshMonitor() {
  try {
    const next = await api('GET', '/api/monitor');
    // Her yenilemede değişen zaman alanları karşılaştırmaya girmez; yoksa ekran sürekli baştan çizilirdi.
    const json = JSON.stringify(next, (k, v) => (k === 'lastRunUtc' || k === 'checkedAtUtc' || k === 'lastSeenUtc' ? undefined : v));
    snap = next;
    // Sonuçlar değişmediyse ekranı yeniden çizmiyoruz (titremeyi ve kayan satırların sıfırlanmasını önler).
    if (json !== lastSnapJson) { lastSnapJson = json; renderMonitor(); }
    else if (modalAppId) renderModal(); // açık detay penceresinde "son bildirim" zamanları güncel kalsın
    renderMeta();
  } catch { /* sunucuya geçici ulaşılamadı, bir sonraki turda tekrar denenecek */ }
}

// Bir uygulamanın bağlantılarının özeti: kaç tanesi sorunlu (başarısız, test edilemedi veya hedefin bazı pod'larına erişilemedi).
function connSummary(s) {
  const conns = s.connections || [];
  const bad = conns.filter((c) => c.cells.some((x) => x.fresh && !x.success)).length;
  return { total: conns.length, bad };
}

const problemOf = (s) => s.state === 'down' || s.state === 'degraded';
const missingOf = (s) => (s.pods || []).filter((p) => p.state === 'missing').length;
const sortApps = (list) => list.slice().sort((a, b) => (STATE_ORDER[a.state] - STATE_ORDER[b.state]) || a.name.localeCompare(b.name, 'tr'));

// Kart rengi: uygulama adından türetilen sabit bir ton (her uygulamanın kendi "afişi" olsun, her turda değişmesin).
function hueOf(text) {
  let h = 0;
  for (const ch of String(text)) h = (h * 31 + ch.codePointAt(0)) % 360;
  return h;
}

// Afişteki büyük harfler: "MULTI POD APP" -> "MP", "ORDERS" -> "OR".
function monogram(name) {
  const words = String(name).trim().split(/[\s._-]+/).filter(Boolean);
  return (words.length > 1 ? words[0][0] + words[1][0] : (words[0] || '?').slice(0, 2)).toLocaleUpperCase('tr');
}

// Arama: uygulama adı, URL, ekip, birim adı ve mod ("strict" / "discover") içinde (büyük/küçük harf duyarsız).
function matches(s, q) {
  if (!q) return true;
  const team = teamOf(appTeamId(s.appId));
  const unit = team ? unitOf(team.unitId) : null;
  const extra = (s.pods || []).flatMap((p) => [p.appVersion, p.clusterName, podName(p)]);
  return [s.name, s.appKey, team?.name, unit?.name, ...extra].some((x) => lower(x).includes(q));
}

// Satır başlığındaki durum sayaçları: ● sağlıklı ● sorunlu ● erişilemiyor.
function counters(list) {
  const n = (st) => list.filter((s) => s.state === st).length;
  return ['healthy', 'degraded', 'down', 'unknown'].filter((st) => n(st) > 0)
    .map((st) => `<span class="cnt" title="${STATE_TEXT[st]}"><i class="dot ${st}"></i>${n(st)}</span>`).join('');
}

// "Pod listesini sıfırla" ne zaman gösterilir: kendiliğinden silinmeyen eksik pod varsa.
const needsReset = (s) => missingOf(s) > 0;

const resetButton = (s) => (needsReset(s)
  ? `<button class="nf-btn gray" data-reset-app="${esc(s.appId)}" title="Eksik pod'lar kendiliğinden silinmez; sorunu giderdiyseniz (veya pod sayısını bilerek azalttıysanız) buradan temizleyin.">${ICON_RESET}Pod listesini sıfırla</button>`
  : '');

// Kartın sol üst şeridi: en önemli sorun (Netflix'in "Yeni bölüm" şeridi gibi).
function flagOf(s) {
  const missing = missingOf(s);
  const { bad } = connSummary(s);
  if (s.state === 'down') return { cls: '', text: 'ERİŞİLEMİYOR' };
  if (missing) return { cls: '', text: `${missing} POD EKSİK` };
  if (bad) return { cls: 'warn', text: `${bad} BAĞLANTI SORUNLU` };
  const builds = buildsOf(s).length;
  if (builds > 1) return { cls: 'warn', text: `${builds} SÜRÜM` };
  if (s.state === 'unknown') return { cls: 'idle', text: 'BEKLİYOR' };
  return null;
}

// "3 pod", "pod yok"
const podsText = (s) => (s.state === 'down' ? 'pod yok' : `${s.podCount} pod`);

function connsText(s) {
  const { total, bad } = connSummary(s);
  if (!total) return '<span class="muted">bağlantı yok</span>';
  return bad ? `<span class="warn-t">⚠ ${bad}/${total} bağlantı</span>` : `<span class="ok">✓ ${total} bağlantı</span>`;
}

// Kartın sağ üstünde uygulama sürümü; birden fazla sürüm/build çalışıyorsa sarı "N sürüm".
function versionBadge(s) {
  const builds = buildsOf(s);
  if (!builds.length) return '';
  return builds.length === 1
    ? `<span class="tile-ver" title="Uygulama sürümü · build">${esc(builds[0])}</span>`
    : `<span class="tile-ver mixed" title="${esc(builds.join(', '))}">${builds.length} sürüm</span>`;
}

// Bir uygulama kartı.
function tile(s) {
  const team = teamOf(appTeamId(s.appId));
  const flag = flagOf(s);
  const pods = s.pods || [];
  const up = pods.filter((p) => p.state !== 'missing').length;
  const health = s.state === 'down' ? 0 : pods.length ? Math.round((up / pods.length) * 100) : s.state === 'unknown' ? 0 : 100;
  const hist = (s.history || []).slice(-12).map((h) => `<i class="${h.state}"></i>`).join('');

  return `
    <button class="tile ${s.state}" data-open-app="${esc(s.appId)}" style="--h:${hueOf(s.name)}" title="${esc(s.message || STATE_TEXT[s.state])}">
      <div class="tile-art"><span class="mono">${esc(monogram(s.name))}</span></div>
      ${flag ? `<span class="flag ${flag.cls}">${flag.text}</span>` : ''}
      ${versionBadge(s)}
      <div class="tile-body">
        <span class="tile-name">${esc(s.name)}</span>
        <div class="tile-line"><span class="st ${s.state}">${STATE_TEXT[s.state]}</span><span class="box">${podsText(s)}</span>${connsText(s)}</div>
        <div class="tile-more">${esc(team ? team.name + ' · ' : '')}${esc(s.appKey)} · ${s.checkedAtUtc ? timeOf(s.checkedAtUtc) : '–'}
          ${hist ? `<div class="tile-hist">${hist}</div>` : ''}</div>
      </div>
      <div class="tile-bar"><i style="width:${health}%"></i></div>
    </button>`;
}

// Yatay kayan kart satırı.
function rowHtml(key, title, kicker, list) {
  return `
    <section class="nf-row" data-row="${esc(key)}">
      <h3 class="row-title">${kicker ? `<span class="kicker">${esc(kicker)}</span>` : ''}${esc(title)}
        <span class="row-count">${list.length}</span>${counters(list)}</h3>
      <div class="row-wrap">
        <button class="row-arrow left" data-scroll="-1" aria-label="Sola kaydır">‹</button>
        <div class="row-track">${sortApps(list).map(tile).join('')}</div>
        <button class="row-arrow right" data-scroll="1" aria-label="Sağa kaydır">›</button>
      </div>
    </section>`;
}

// Okların görünürlüğü: satırda sola/sağa kaydırılacak kart varsa gösteriyoruz.
function updateArrows(wrap) {
  const t = wrap.querySelector('.row-track');
  wrap.classList.toggle('can-left', t.scrollLeft > 4);
  wrap.classList.toggle('can-right', t.scrollLeft + t.clientWidth < t.scrollWidth - 4);
}

// Üstteki öne çıkan alan: en kritik sorunlu uygulama; sorun yoksa "tüm sistemler ayakta".
function renderBillboard(all) {
  const n = (st) => all.filter((s) => s.state === st).length;
  const stats = `
    <div class="bb-stats">
      <div><b>${all.length}</b><span>uygulama</span></div>
      <div class="ok"><b>${n('healthy')}</b><span>sağlıklı</span></div>
      <div class="warn"><b>${n('degraded')}</b><span>sorunlu</span></div>
      <div class="bad"><b>${n('down')}</b><span>erişilemiyor</span></div>
    </div>`;

  if (!all.length) {
    $('#billboard').innerHTML = `
      <div class="bb-art" style="--h:355"><span class="mono">CM</span></div>
      <div class="bb-content">
        <div class="bb-kicker"><i class="live"></i>Connectivity Monitor</div>
        <h1 class="bb-title">Henüz izlenen uygulama yok</h1>
        <p class="bb-desc">Tanımlar sekmesinden birim, ekip ve ConnectivityProbe yüklü uygulamaları ekleyin.</p>
        <div class="bb-actions"><button class="nf-btn white" data-goto-defs>Tanımlara git</button></div>
      </div>`;
    return;
  }

  const feat = sortApps(all.filter(problemOf))[0];
  if (!feat) {
    $('#billboard').innerHTML = `
      <div class="bb-art" style="--h:140"><span class="mono">OK</span></div>
      <div class="bb-content">
        <div class="bb-kicker"><i class="live ok"></i>Canlı izleme</div>
        <h1 class="bb-title">Tüm sistemler ayakta</h1>
        <p class="bb-desc">${all.length} uygulamanın ${n('healthy')} tanesi sağlıklı${n('unknown') ? `, ${n('unknown')} tanesi ilk testi bekliyor` : ''}. Her ${snap.intervalSeconds} saniyede bir pod'lar ve bağlantılar yeniden test ediliyor.</p>
        <div class="bb-actions"><button class="nf-btn white" data-run>${ICON_PLAY}Şimdi test et</button></div>
      </div>${stats}`;
    return;
  }

  const team = teamOf(appTeamId(feat.appId));
  const unit = team ? unitOf(team.unitId) : null;
  $('#billboard').innerHTML = `
    <div class="bb-art ${feat.state}" style="--h:${hueOf(feat.name)}"><span class="mono">${esc(monogram(feat.name))}</span></div>
    <div class="bb-content">
      <div class="bb-kicker"><i class="live"></i>Dikkat gerektiriyor${unit ? ' · ' + esc(unit.name) : ''}${team ? ' · ' + esc(team.name) : ''}</div>
      <h1 class="bb-title">${esc(feat.name)}</h1>
      <div class="bb-meta"><span class="st ${feat.state}">${STATE_TEXT[feat.state]}</span><span class="box">${podsText(feat)}</span>${connsText(feat)}</div>
      ${feat.message ? `<p class="bb-desc">${esc(feat.message)}</p>` : ''}
      <div class="bb-actions">
        <button class="nf-btn white" data-open-app="${esc(feat.appId)}">${ICON_PLAY}Detayları gör</button>
        <button class="nf-btn gray" data-run>${ICON_INFO}Şimdi test et</button>
        ${resetButton(feat)}
      </div>
    </div>${stats}`;
}

const ICON_PLAY = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 4v16a1 1 0 0 0 1.5.86l13-8a1 1 0 0 0 0-1.72l-13-8A1 1 0 0 0 6 4Z"/></svg>';
const ICON_RESET = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4a8 8 0 1 1-7.75 10h2.08A6 6 0 1 0 12 6c-1.66 0-3.15.67-4.24 1.76L10 10H4V4l2.35 2.35A7.97 7.97 0 0 1 12 4Z"/></svg>';
const ICON_INFO ='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 2a10 10 0 1 1 0 20 10 10 0 0 1 0-20Zm0 2a8 8 0 1 0 0 16 8 8 0 0 0 0-16Zm1 7v6h-2v-6h2Zm-1-4a1.25 1.25 0 1 1 0 2.5A1.25 1.25 0 0 1 12 7Z"/></svg>';

function renderMonitor() {
  const all = snap?.apps ?? [];
  const q = lower($('#search').value.trim());
  const onlyProblems = $('#onlyProblems').checked;
  const list = $('#monitorList');

  // step 1: Yeniden çizmeden önce satırların kaydırma konumunu saklıyoruz; yoksa her turda satırlar başa döner.
  const scroll = {};
  list.querySelectorAll('[data-row]').forEach((r) => { scroll[r.dataset.row] = r.querySelector('.row-track').scrollLeft; });

  // step 2: Arama veya filtre varken billboard yerine ızgara halinde sonuçlar.
  const wasSearching = list.classList.contains('searching');
  if (q || onlyProblems) {
    $('#billboard').hidden = true;
    if (!wasSearching) window.scrollTo(0, 0); // sonuçlar sayfanın başından başlasın
    const found = sortApps(all.filter((s) => (!onlyProblems || problemOf(s)) && matches(s, q)));
    const what = [q ? `"<b>${esc($('#search').value.trim())}</b>"` : '', onlyProblems ? '<b>sorunlu</b>' : ''].filter(Boolean).join(' · ');
    list.innerHTML = `<p class="search-head">${what} için ${found.length} uygulama</p>`
      + (found.length ? `<div class="nf-grid">${found.map(tile).join('')}</div>` : '<div class="empty">Uyan uygulama yok.</div>');
    list.classList.add('searching');
  } else {
    $('#billboard').hidden = false;
    list.classList.remove('searching');
    renderBillboard(all);

    // step 3: Önce tüm ekiplerden sorunlular, sonra Birim -> Ekip satırları, en sonda atanmamışlar.
    const byTeam = new Map();
    for (const s of all) {
      const tid = appTeamId(s.appId);
      if (!byTeam.has(tid)) byTeam.set(tid, []);
      byTeam.get(tid).push(s);
    }
    const html = [];
    const problems = all.filter(problemOf);
    if (problems.length) html.push(rowHtml('problems', 'Dikkat gerektirenler', 'Canlı', problems));
    for (const u of defs.units.slice().sort(byName)) {
      const teams = defs.teams.filter((t) => t.unitId === u.id && byTeam.has(t.id)).sort(byName);
      if (!teams.length) continue;
      const count = teams.reduce((n, t) => n + byTeam.get(t.id).length, 0);
      html.push(`<div class="unit-head"><b>${esc(u.name)}</b><span>${teams.length} ekip · ${count} uygulama</span></div>`);
      teams.forEach((t) => html.push(rowHtml('t:' + t.id, t.name, null, byTeam.get(t.id))));
    }
    if (byTeam.has(UNASSIGNED)) html.push(rowHtml('t:' + UNASSIGNED, 'Atanmamış uygulamalar', null, byTeam.get(UNASSIGNED)));
    list.innerHTML = html.join('');

    // step 4: Kaydırma konumlarını geri yüklüyoruz (animasyonsuz) ve okları güncelliyoruz.
    list.querySelectorAll('[data-row]').forEach((r) => {
      const t = r.querySelector('.row-track');
      if (scroll[r.dataset.row]) { t.style.scrollBehavior = 'auto'; t.scrollLeft = scroll[r.dataset.row]; t.style.scrollBehavior = ''; }
      updateArrows(r.querySelector('.row-wrap'));
    });
  }

  // Billboard yokken üst menü sonuçların üstünde saydam kalmasın.
  $('#topbar').classList.toggle('scrolled', list.classList.contains('searching') || window.scrollY > 20);

  // Detay penceresi ve Sürümler sekmesi açıksa onları da yeni sonuçlarla güncelliyoruz.
  if (modalAppId) renderModal();
  if (tab === 'versions') renderVersions();
}

// ---------------------------------------------------------------------------------------------
// Detay penceresi
// ---------------------------------------------------------------------------------------------
function openModal(appId) {
  modalAppId = appId;
  renderModal();
  const dlg = $('#appModal');
  if (!dlg.open) { dlg.showModal(); dlg.scrollTop = 0; }
}

function renderModal() {
  const s = (snap?.apps || []).find((a) => a.appId === modalAppId);
  const dlg = $('#appModal');
  if (!s) { if (dlg.open) dlg.close(); return; }
  const keep = dlg.scrollTop;

  const team = teamOf(appTeamId(s.appId));
  const unit = team ? unitOf(team.unitId) : null;
  const { total, bad } = connSummary(s);
  const builds = buildsOf(s);
  const msgClass = s.state === 'down' ? 'bad' : s.state === 'degraded' ? 'warn' : '';
  const hist = s.history || [];
  const maxPods = Math.max(1, ...hist.map((h) => h.podCount));
  const clusters = [...new Set((s.pods || []).map((p) => p.clusterName || '?'))];

  $('#appModalBody').innerHTML = `
    <div class="m-hero">
      <div class="m-art ${s.state}" style="--h:${hueOf(s.name)}"><span class="mono">${esc(monogram(s.name))}</span></div>
      <button class="m-close" data-close-modal aria-label="Kapat">✕</button>
      <div class="m-hero-text">
        <div class="m-kicker">${esc([unit?.name, team?.name].filter(Boolean).join(' · ') || 'Atanmamış')}</div>
        <h2>${esc(s.name)}</h2>
        <div class="m-actions">
          <button class="nf-btn white" data-run>${ICON_PLAY}Şimdi test et</button>
          ${resetButton(s)}
        </div>
      </div>
    </div>
    <div class="m-body">
      <div class="m-cols">
        <div>
          <div class="m-meta">
            <span class="st ${s.state}">${STATE_TEXT[s.state]}</span>
            <span class="box">${podsText(s)}</span>
            ${builds.length === 1 ? `<span class="box">${esc(builds[0])}</span>` : builds.length > 1 ? `<span class="warn-t">${builds.length} farklı sürüm</span>` : ''}
            <span class="muted">güncellendi ${s.checkedAtUtc ? timeOf(s.checkedAtUtc) : '–'}</span>
          </div>
          ${s.message ? `<p class="m-msg ${msgClass}">${esc(s.message)}</p>` : ''}
          ${needsReset(s) ? '<p class="msg">Eksik pod\'lar kendiliğinden silinmez; sorunu giderdiyseniz veya pod sayısını bilerek azalttıysanız "Pod listesini sıfırla" ile temizleyin.</p>' : ''}
          ${hist.length ? `<div class="m-history">${hist.map((h) => `<i class="${h.state}" style="height:${Math.max(12, (h.podCount / maxPods) * 100)}%"
              title="${timeOf(h.atUtc)} · ${STATE_TEXT[h.state] || h.state} · ${h.podCount} pod"></i>`).join('')}</div>
            <div class="m-history-label">Son ${hist.length} test aralığı · çubuk yüksekliği pod sayısı</div>` : ''}
        </div>
        <div class="m-side">
          <div><span>Anahtar: </span><code>${esc(s.appKey)}</code></div>
          <div><span>Birim: </span>${esc(unit?.name ?? '–')}</div>
          <div><span>Ekip: </span>${esc(team?.name ?? 'Atanmamış')}</div>
          <div><span>Cluster: </span>${esc(clusters.join(', ') || '–')}</div>
          <div><span>Bağlantılar: </span>${total}${bad ? ` <span class="warn-t">(${bad} sorunlu)</span>` : ''}</div>
          <div><span>Test aralığı: </span>${snap.intervalSeconds} sn</div>
        </div>
      </div>
      ${episodesHtml(s)}
      ${matrixHtml(s)}
    </div>`;
  dlg.scrollTop = keep;
}

// Pod listesi cluster'a göre gruplanır (Netflix'in bölüm listesi gibi):
//   up          -> bildirim gönderiyor
//   unconfirmed -> bildirimi gecikti (alarm değil)
//   missing     -> MissingAfterCycles test aralığı boyunca bildirim yok (kırmızı, alarm)
// Grup başlığında o cluster'daki pod sayısı ve çalışan sürümler; uygulamada birden fazla sürüm varsa azınlıktakiler sarı.
function episodesHtml(s) {
  if (!s.pods?.length) return '';
  const label = { up: 'ÇALIŞIYOR', unconfirmed: 'BİLDİRİM GECİKTİ', missing: 'EKSİK' };
  const builds = buildsOf(s);
  // En çok pod'da çalışan sürüm "ana" sürüm; diğerleri (eski/yeni) vurgulanır.
  const count = (b) => s.pods.filter((p) => p.state === 'up' && versionLabel(p) === b).length;
  const main = builds.slice().sort((a, b) => count(b) - count(a))[0];

  const groups = new Map();
  for (const p of s.pods) {
    const key = p.clusterKey || '?';
    if (!groups.has(key)) groups.set(key, { name: p.clusterName || 'Bilinmeyen cluster', pods: [] });
    groups.get(key).pods.push(p);
  }

  let n = 0;
  return `<h3 class="m-sec">Pod'lar <span>${s.pods.length} pod · ${groups.size} cluster</span></h3>
    ${[...groups.values()].map((g) => {
      const versions = [...new Set(g.pods.filter((p) => p.state === 'up').map(versionLabel))];
      return `
      <div class="ep-cluster">
        <b>${esc(g.name)}</b>
        <span>${g.pods.filter((p) => p.state === 'up').length}/${g.pods.length} pod</span>
        ${versions.map((v) => `<span class="ver ${builds.length > 1 && v !== main ? 'odd' : ''}">${esc(v)}</span>`).join('')}
      </div>
      <div class="episodes">${g.pods.map((p) => {
        n++;
        const odd = builds.length > 1 && p.state === 'up' && versionLabel(p) !== main;
        const desc = p.state === 'up'
          ? esc((p.addresses || []).join(', ')) + ` · son bildirim ${agoOf(p.lastSeenUtc)}`
          : p.state === 'missing'
            ? `${durationOf(p.lastSeenUtc)} bildirim göndermiyor · son görülme ${timeOf(p.lastSeenUtc)}`
            : `Bildirimi gecikti · son bildirim ${timeOf(p.lastSeenUtc)}`;
        const kvs = [
          ['sürüm', versionLabel(p)],
          ...(p.buildDateUtc ? [['build', dateTimeOf(p.buildDateUtc)]] : []),
          ...(p.namespace ? [['namespace', p.namespace]] : []),
          ['ConnectivityProbe', p.probeVersion || '?'],
          ...Object.entries(p.details || {}).filter(([k]) => k !== 'POD_NAME' && k !== 'HOSTNAME').slice(0, 4),
        ].map(([k, v]) => `<span class="kv ${k === 'sürüm' && odd ? 'odd' : ''}"><b>${esc(k)}</b> ${esc(v)}</span>`).join('');
        return `
          <div class="ep ${p.state}">
            <div class="ep-num">${n}</div>
            <div class="ep-thumb" style="--h:${hueOf(p.instanceId)}">${n}</div>
            <div>
              <div class="ep-title">${esc(podName(p))}<span class="pid">${shortId(p.instanceId)}</span><span class="ep-state ${p.state}">${label[p.state] || p.state}</span>
                ${odd ? '<span class="ep-state odd">FARKLI SÜRÜM</span>' : ''}</div>
              <div class="ep-desc">${desc}</div>
              <div class="kvs">${kvs}</div>
            </div>
            <div class="ep-time">${p.startedAtUtc ? 'başladı ' + dateTimeOf(p.startedAtUtc) : ''}</div>
          </div>`;
      }).join('')}</div>`;
    }).join('')}`;
}

// Satırlar: atanmış bağlantılar, sütunlar: pod'lar. Hücre: o pod'un bağlantıyı kendi içinden TCP ile test ettiği son sonuç.
function matrixHtml(s) {
  const def = defs.apps.find((a) => a.id === s.appId);
  const hasConns = (def?.connectionIds?.length ?? 0) > 0;
  if (!hasConns) return '<p class="msg">Bu uygulamaya bağlantı atanmamış. "Tanımlar" sekmesinden sürükleyip bırakın.</p>';
  if (!s.connections?.length) return s.state === 'down' ? '' : '<p class="msg">Bağlantı sonuçları bekleniyor…</p>';

  const pods = s.pods || [];
  // Eksik pod'un sütun başlığı kırmızı: o pod bildirim göndermediği için bağlantılarını test edemiyor.
  const head = pods.map((p) => `<th class="${p.state === 'missing' ? 'missing' : ''}">${esc(podName(p))}<span class="pid">${esc(p.clusterName || '')} · ${shortId(p.instanceId)}${p.state === 'missing' ? ' · eksik' : ''}</span></th>`).join('');

  const rows = s.connections.map((c) => {
    const ips = [...new Set(c.cells.flatMap((x) => x.ipResults.map((r) => r.address)))];
    // Hedef de Monitor'e kayıtlı bir uygulamaysa onun pod sayısı ve durumu.
    const target = c.targetAppId && (snap?.apps || []).find((a) => a.appId === c.targetAppId);
    const targetInfo = target
      ? `<span class="target-app"><i class="dot ${target.state}"></i>hedef: ${esc(target.name)} · ${target.podCount} pod · ${STATE_TEXT[target.state]}</span>` : '';
    const rowHead = `<td class="rowhead"><span class="name">${esc(c.name)}</span><span class="target">${esc(c.target)}</span>
      ${ips.length ? `<span class="ips">IP: ${esc(ips.join(', '))}</span>` : ''}${targetInfo}</td>`;

    const cells = pods.map((p) => {
      const cell = c.cells.find((x) => x.instanceId === p.instanceId);

      // Pod eksikse eski başarılı sonucu yeşil göstermek yanıltıcı olur: pod çalışmıyor, bu bağlantıyı da kullanamıyor.
      if (p.state === 'missing') {
        const lastKnown = cell
          ? `Son bilinen sonuç (${timeOf(cell.checkedAtUtc)}): ${cell.success ? 'başarılı, ' + cell.elapsedMs + ' ms' : (cell.error || 'başarısız')}`
          : 'Bu pod için daha önce sonuç alınmadı';
        return `<td class="cell fail" title="${esc(`Pod ${durationOf(p.lastSeenUtc)} bildirim göndermiyor (son: ${timeOf(p.lastSeenUtc)}).\n${lastKnown}`)}">`
          + `✗<span class="reach">Pod bildirim göndermiyor</span><span class="since">${durationOf(p.lastSeenUtc)}</span></td>`;
      }

      if (!cell) return '<td class="cell none" title="Bu pod henüz test sonucu göndermedi">–</td>';
      const tip = [
        cell.fresh ? '' : 'Son bilinen sonuç (' + timeOf(cell.checkedAtUtc) + ')',
        cell.reachedAddress ? 'Ulaşılan IP: ' + cell.reachedAddress : '',
        ...cell.ipResults.map((r) => `${r.address}: ${r.success ? 'ok ' + r.elapsedMs + ' ms' : r.error}`),
        cell.error ? 'Hata: ' + cell.error : '',
        !cell.success && cell.failingSinceUtc ? `Başarısız: ${dateTimeOf(cell.failingSinceUtc)} tarihinden beri` : '',
        !cell.success ? (cell.lastSuccessUtc ? `Son başarılı test: ${dateTimeOf(cell.lastSuccessUtc)}` : 'Monitor açıldığından beri hiç başarılı olmadı') : '',
      ].filter(Boolean).join('\n');

      if (!cell.success) {
        return `<td class="cell fail ${cell.fresh ? '' : 'stale'}" title="${esc(tip)}">✗<span class="reach">${esc(cell.error || 'başarısız')}</span>`
          + (cell.failingSinceUtc ? `<span class="since">${durationOf(cell.failingSinceUtc)} erişilemiyor</span>` : '') + '</td>';
      }
      return `<td class="cell ok ${cell.fresh ? '' : 'stale'}" title="${esc(tip)}">✓<span class="ms">${cell.elapsedMs} ms</span>`
        + `${cell.reachedAddress ? `<span class="reach">${esc(cell.reachedAddress)}</span>` : ''}</td>`;
    }).join('');

    return `<tr>${rowHead}${cells}</tr>`;
  }).join('');

  return `<div class="section-title">Bağlantılar (her pod kendi içinden test eder)</div>
    <div class="matrix-wrap"><table class="matrix"><thead><tr><th>Bağlantı</th>${head}</tr></thead><tbody>${rows}</tbody></table></div>`;
}

// ---------------------------------------------------------------------------------------------
// Sürümler sekmesi: hangi cluster'da hangi uygulamanın hangi sürümü kaç pod'da çalışıyor
// ---------------------------------------------------------------------------------------------
function renderVersions() {
  if (!snap) return;
  const apps = (snap.apps || []).filter((a) => (a.pods || []).length).slice().sort(byName);
  const clusters = (snap.clusters || []).filter((c) => apps.some((a) => a.pods.some((p) => p.clusterKey === c.key)));
  const mixedApps = apps.filter((a) => buildsOf(a).length > 1).length;

  $('#tab-versions').innerHTML = `
    <div class="v-head">
      <h2>Ortamlar ve sürümler</h2>
      <p class="hint">Pod'lar bulundukları cluster'a göre kendiliğinden gruplanır (Kubernetes cluster sertifikası; Kubernetes dışında bildirimin
        geldiği ağ adresi). Cluster adlarını ✎ ile değiştirebilirsiniz. Bir uygulamada birden fazla sürüm/build çalışıyorsa azınlıktaki sarıyla işaretlenir.</p>
      ${mixedApps ? `<p class="warn-t">${mixedApps} uygulamada birden fazla sürüm çalışıyor.</p>` : ''}
    </div>
    <div class="v-clusters">${clusters.length ? clusters.map((c) => `
      <div class="v-cluster">
        <div class="grow"><b>${esc(c.name)}</b><span class="muted"> ${c.pods} pod · ${c.apps} uygulama</span>
          <div class="pid">${esc(c.key)}</div></div>
        <button class="icon-btn" data-rename-cluster="${esc(c.key)}" title="Adını değiştir">✎</button>
      </div>`).join('') : '<div class="empty">Henüz pod bildirimi yok.</div>'}</div>
    ${apps.length && clusters.length ? `
    <div class="matrix-wrap"><table class="matrix v-table">
      <thead><tr><th>Uygulama</th>${clusters.map((c) => `<th>${esc(c.name)}</th>`).join('')}</tr></thead>
      <tbody>${apps.map((a) => {
        const builds = buildsOf(a);
        const count = (b) => a.pods.filter((p) => p.state === 'up' && versionLabel(p) === b).length;
        const main = builds.slice().sort((x, y) => count(y) - count(x))[0];
        return `<tr>
          <td class="rowhead"><span class="name">${esc(a.name)}</span><span class="target">${esc(a.appKey)}</span>
            ${builds.length > 1 ? `<span class="warn-t">${builds.length} farklı sürüm</span>` : ''}</td>
          ${clusters.map((c) => {
            const pods = a.pods.filter((p) => p.clusterKey === c.key);
            if (!pods.length) return '<td class="cell none">–</td>';
            const byVersion = new Map();
            for (const p of pods) {
              const v = versionLabel(p);
              const e = byVersion.get(v) || { up: 0, other: 0 };
              if (p.state === 'up') e.up++; else e.other++;
              byVersion.set(v, e);
            }
            return `<td>${[...byVersion.entries()].map(([v, e]) =>
              `<div class="ver ${builds.length > 1 && v !== main ? 'odd' : ''}" title="${e.up} pod çalışıyor${e.other ? ', ' + e.other + ' pod eksik/gecikmiş' : ''}">${esc(v)} <b>×${e.up}</b>${e.other ? ` <span class="bad">+${e.other}</span>` : ''}</div>`).join('')}</td>`;
          }).join('')}
        </tr>`;
      }).join('')}</tbody>
    </table></div>` : ''}`;
}

$('#tab-versions').addEventListener('click', async (e) => {
  const t = e.target.closest('[data-rename-cluster]');
  if (!t) return;
  const c = (snap?.clusters || []).find((x) => x.key === t.dataset.renameCluster);
  openForm('Cluster adını değiştir', [{ name: 'name', label: 'Ad', placeholder: 'ör. Prod İstanbul', hint: 'Anahtar: ' + c.key }], c,
    async (v) => { await api('PUT', '/api/clusters/' + encodeURIComponent(c.key), v); lastSnapJson = ''; await refreshMonitor(); renderVersions(); });
});

// ---------------------------------------------------------------------------------------------
// Başlangıç ve olaylar
// ---------------------------------------------------------------------------------------------
document.querySelectorAll('.tab').forEach((b) => b.addEventListener('click', () => setTab(b.dataset.tab)));

// Testi hemen başlatır (üst menü, billboard ve detay penceresindeki düğmeler).
async function runNow() {
  const buttons = document.querySelectorAll('#runNow, [data-run]');
  buttons.forEach((b) => { b.disabled = true; });
  try { await api('POST', '/api/monitor/run'); } catch (err) { alert(err.message); }
  setTimeout(() => document.querySelectorAll('#runNow, [data-run]').forEach((b) => { b.disabled = false; }), 1500);
  setTimeout(refreshMonitor, 1000);
}
$('#runNow').addEventListener('click', runNow);

// Monitör ekranı ve detay penceresindeki tüm tıklamalar.
document.addEventListener('click', async (e) => {
  const el = e.target.closest('[data-open-app], [data-run], [data-reset-app], [data-close-modal], [data-scroll], [data-goto-defs]');
  if (!el) return;
  const d = el.dataset;

  if (d.openApp) openModal(d.openApp);
  else if (d.run !== undefined) runNow();
  else if (d.closeModal !== undefined) $('#appModal').close();
  else if (d.gotoDefs !== undefined) setTab('defs');
  else if (d.scroll) {
    const t = el.parentElement.querySelector('.row-track');
    t.scrollBy({ left: Number(d.scroll) * t.clientWidth * 0.85 });
  } else if (d.resetApp) {
    // "Pod listesini sıfırla": Monitor'ün bu uygulama için hatırladığı pod'lar (eksikler dahil) silinir, mevcutlar yeniden keşfedilir.
    const app = (snap?.apps || []).find((a) => a.appId === d.resetApp);
    if (!confirm(`"${app?.name}" için hatırlanan pod listesi (eksik pod'lar ve erişilemeyen hedef pod'ları dahil) silinecek; mevcut pod'lar yeniden keşfedilecek. Devam edilsin mi?`)) return;
    el.disabled = true;
    try { await api('POST', `/api/apps/${d.resetApp}/reset`); } catch (err) { alert(err.message); }
    lastSnapJson = '';
    setTimeout(refreshMonitor, 1500);
  }
});

// Pencerenin dışına (karartılmış alana) tıklayınca kapanır; Esc zaten kapatır.
$('#appModal').addEventListener('click', (e) => { if (e.target === e.currentTarget) e.currentTarget.close(); });
$('#appModal').addEventListener('close', () => { modalAppId = null; });

// Satır kaydırıldıkça okları güncelliyoruz (scroll olayı kabarcıklanmadığı için capture ile dinliyoruz).
$('#monitorList').addEventListener('scroll', (e) => {
  if (e.target.classList?.contains('row-track')) updateArrows(e.target.parentElement);
}, true);
window.addEventListener('resize', () => document.querySelectorAll('.row-wrap').forEach(updateArrows));

// Üst menü: sayfa kaydırılınca koyulaşır.
window.addEventListener('scroll', () => $('#topbar').classList.toggle('scrolled',
  window.scrollY > 20 || $('#monitorList').classList.contains('searching') || tab !== 'monitor'), { passive: true });

$('#search').addEventListener('input', renderMonitor);
$('#onlyProblems').addEventListener('change', renderMonitor);

$('#logout').addEventListener('click', async () => {
  try { await api('POST', '/api/auth/logout'); } finally { location.href = '/login.html'; }
});

(async function init() {
  // Giriş zorunluysa (Monitor:AdminPassword tanımlı) çıkış düğmesini gösteriyoruz.
  try { const me = await api('GET', '/api/auth/me'); $('#logout').hidden = !me.loginRequired; } catch { /* yönlendirildi */ }
  setTab(load('tab', 'monitor'));
  await loadDefs();
  await refreshMonitor();
  setInterval(refreshMonitor, 3000);   // sonuçları 3 saniyede bir sunucudan okuyoruz
  setInterval(renderMeta, 1000);       // geri sayımı her saniye güncelliyoruz
})();
