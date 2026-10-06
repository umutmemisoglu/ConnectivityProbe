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
  if (!res.ok) {
    let msg = res.statusText;
    try { msg = (await res.json()).error || msg; } catch { /* gövde JSON değil */ }
    throw new Error(msg);
  }
  try { return await res.json(); } catch { return null; }
}

// "host:port" biçiminde karşılaştırma anahtarı (şemasızsa http varsayılır): bağlantı hedefini izlenen uygulamalarla eşleştirmek için.
function targetKey(text) {
  try {
    const u = new URL(/^[a-z]+:\/\//i.test(text) ? text : 'http://' + text);
    const port = u.port || (u.protocol === 'https:' ? '443' : '80');
    const host = u.hostname.replace(/^\[|\]$/g, '').toLowerCase();
    return (host === '127.0.0.1' || host === '::1' ? 'localhost' : host) + ':' + port;
  } catch { return null; }
}

// Bağlantı hedefi Monitor'de izlenen bir uygulamaysa o uygulamanın bilinen pod'ları (bu turda tesadüfen görülmeyenler hariç).
function expectedPodsOf(conn) {
  const key = targetKey(conn.host ? targetOf(conn) : conn.target);
  const app = key && (snap?.apps || []).find((a) => targetKey(a.baseUrl) === key);
  if (!app || !app.pods?.length) return null;
  return app.pods.filter((p) => p.state !== 'unconfirmed');
}

// Hedef pod etiketi: makine (pod) adı; aynı ad birden fazla pod'da varsa ayırt etmek için kısa kimlik de eklenir.
const podLabel = (p, all) =>
  all.filter((x) => x.machineName === p.machineName).length > 1 ? `${p.machineName}·${p.instanceId.slice(0, 6)}` : p.machineName;

const timeOf = (iso) => (iso ? new Date(iso).toLocaleTimeString('tr-TR') : '-');
// Bugünse yalnızca saat, değilse tarih + saat.
const dateTimeOf = (iso) => {
  if (!iso) return '-';
  const d = new Date(iso);
  return d.toDateString() === new Date().toDateString() ? d.toLocaleTimeString('tr-TR') : d.toLocaleString('tr-TR');
};
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

// ---------------------------------------------------------------------------------------------
// Sekmeler ve üst çubuk
// ---------------------------------------------------------------------------------------------
function setTab(name) {
  tab = name;
  save('tab', name);
  $('#tab-monitor').hidden = name !== 'monitor';
  $('#tab-defs').hidden = name !== 'defs';
  $('#navTools').hidden = name !== 'monitor'; // arama ve filtre yalnızca monitörde anlamlı
  window.scrollTo(0, 0);
  $('#topbar').classList.toggle('scrolled', name !== 'monitor'); // Tanımlar'da billboard yok: menü baştan koyu
  document.querySelectorAll('.tab').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
}

// "Son tur / sonraki tur" bilgisini her saniye günceller.
function renderMeta() {
  if (!snap) { $('#meta').textContent = ''; return; }
  const parts = [];
  if (snap.running) parts.push('test çalışıyor…');
  if (snap.lastRunUtc) parts.push('son tur ' + timeOf(snap.lastRunUtc));
  if (snap.nextRunUtc && !snap.running) {
    const left = Math.max(0, Math.round((new Date(snap.nextRunUtc) - Date.now()) / 1000));
    parts.push('sonraki ' + left + ' sn');
  }
  parts.push('her ' + snap.intervalSeconds + ' sn');
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
      <span class="name">${esc(c.name)}${cpBadge(c)}</span>
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
    <div class="panel-head"><h2>${title}</h2>${team ? `<button class="btn small" data-add-app="${team.id}">+ Uygulama ekle</button>` : ''}</div>
    ${selTeam === UNASSIGNED ? '<p class="hint">Bu uygulamaların ekibi yok. ✎ ile bir ekibe taşıyın.</p>' : ''}
    <div class="apps">${apps.length ? apps.map((a) => {
      const attached = a.connectionIds.map((id) => defs.connections.find((c) => c.id === id)).filter(Boolean);
      const free = eligible(a).filter((c) => !a.connectionIds.includes(c.id)).sort(byName);
      return `
        <div class="app-card" data-app="${a.id}">
          <div class="app-head">
            <div class="grow"><div class="title">${esc(a.name)}</div><div class="url">${esc(a.baseUrl)}</div></div>
            <button class="icon-btn" data-edit-app="${a.id}" title="Düzenle / taşı">✎</button>
            <button class="icon-btn" data-del-app="${a.id}" title="Sil">✕</button>
          </div>
          <div class="dropzone">
            ${attached.length ? attached.map((c) => `
              <span class="chip"><span class="name">${esc(c.name)}${cpBadge(c)}${c.teamId ? '' : ' <span class="common" title="Ortak havuzdan">ortak</span>'}</span>
                <span class="target">${esc(targetOf(c))}</span>
                <button class="icon-btn" data-detach="${a.id}|${c.id}" title="Bu uygulamadan çıkar">✕</button></span>`).join('')
              : '<span class="placeholder">Bağlantıları buraya sürükleyip bırakın</span>'}
            ${free.length ? `<select class="add-select" data-add-select="${a.id}"><option value="">+ bağlantı seç…</option>
              ${free.map((c) => `<option value="${c.id}">${esc(c.name)}${c.teamId ? '' : ' (ortak)'}</option>`).join('')}</select>` : ''}
          </div>
        </div>`;
    }).join('') : `<div class="empty">${team ? 'Bu ekipte uygulama yok. "+ Uygulama ekle" ile ConnectivityProbe yüklü bir uygulama kaydedin.' : 'Soldan bir ekip seçin.'}</div>`}</div>`;
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

// Bağlantı formu. Checkpoint: hedef de ConnectivityProbe kullanıyor mu? İşaretliyse önce telnet, sonra hedefin pod'ları
// keşfedilir; işaretsizse (DB, Redis, dış API...) yalnızca telnet yapılır ve hedefe hiç HTTP isteği gönderilmez.
const connFields = () => [
  { name: 'name', label: 'Ad', placeholder: 'ör. Ana veritabanı' },
  { name: 'host', label: 'Host veya URL', placeholder: 'sql01, sql01:1433 veya https://orders.example.com/', hint: 'Sunucu adı, IP, sunucu:port ya da tam URL.' },
  { name: 'port', label: 'Port', type: 'number', placeholder: 'ör. 1433', hint: 'Host içinde port varsa veya URL girdiyseniz boş bırakabilirsiniz (https 443, http 80).' },
  { name: 'usesConnectivityProbe', label: 'Bu hedef de ConnectivityProbe kullanıyor', type: 'checkbox',
    hint: 'İşaretliyse: önce telnet, açıksa hedefin pod\'ları keşfedilir (hedef URL olarak yazılmalı). İşaretsizse (DB, Redis, dış servis): yalnızca telnet.' },
  { name: 'teamId', label: 'Havuz', type: 'select', options: teamOptions('Ortak havuz (herkes kullanabilir)'),
    hint: 'Ekip havuzundaki bağlantı yalnızca o ekibin uygulamalarına atanabilir.' },
];

// Havuzda ve kartlarda ConnectivityProbe kullanan hedefleri ayırt eden rozet.
const cpBadge = (c) => c.usesConnectivityProbe
  ? ' <span class="cp" title="Hedef de ConnectivityProbe kullanıyor: telnet + hedefin pod keşfi">CP</span>'
  : '';

const appFields = () => [
  { name: 'name', label: 'Ad', placeholder: 'ör. Orders API' },
  { name: 'baseUrl', label: 'Uygulama URL\'si', placeholder: 'https://orders.example.com', hint: 'Pod\'lara dağıtım yapan adres. Uygulamada ConnectivityProbe yüklü olmalı.' },
  { name: 'teamId', label: 'Ekip', type: 'select', options: teamOptions('(atanmamış)'),
    hint: 'Başka ekibe taşınırsa eski ekibin havuzundan atanmış bağlantılar çıkarılır; ortak bağlantılar kalır.' },
];

// Form değerlerini API'nin beklediği biçime çevirir (boş port -> null, boş ekip -> null).
const toConnBody = (v) => ({
  name: v.name, host: v.host, port: v.port === '' ? null : Number(v.port),
  usesConnectivityProbe: v.usesConnectivityProbe === true, teamId: v.teamId || null,
});
const toAppBody = (v) => ({ name: v.name, baseUrl: v.baseUrl, teamId: v.teamId || null });

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

    // Uygulamalar
    } else if (d.addApp) {
      openForm('Uygulama ekle', appFields(), { teamId: d.addApp }, async (v) => { await api('POST', '/api/apps', toAppBody(v)); await afterChange(); });
    } else if (d.editApp) {
      const a = defs.apps.find((x) => x.id === d.editApp);
      openForm('Uygulamayı düzenle', appFields(), { ...a, teamId: a.teamId ?? '' },
        async (v) => { await api('PUT', '/api/apps/' + a.id, toAppBody(v)); await afterChange(); });
    } else if (d.delApp) {
      const a = defs.apps.find((x) => x.id === d.delApp);
      if (confirm(`"${a.name}" uygulaması silinsin mi? İzleme durur.`)) { await api('DELETE', '/api/apps/' + a.id); await afterChange(); }
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
    const json = JSON.stringify(next, (k, v) => (k === 'nextRunUtc' || k === 'running' ? undefined : v));
    snap = next;
    // Sonuçlar değişmediyse ekranı yeniden çizmiyoruz (titremeyi ve kayan satırların sıfırlanmasını önler).
    if (json !== lastSnapJson) { lastSnapJson = json; renderMonitor(); }
    renderMeta();
  } catch { /* sunucuya geçici ulaşılamadı, bir sonraki turda tekrar denenecek */ }
}

// Bir uygulamanın bağlantılarının özeti: kaç tanesi sorunlu (başarısız, test edilemedi veya hedefin bazı pod'larına erişilemedi).
function connSummary(s) {
  const conns = s.connections || [];
  const bad = conns.filter((c) => c.callError || c.cells.some((x) => x.fresh
    && (!x.success || x.targetFailedRequests > 0 || (x.unreachedTargets || []).some((u) => u.confirmed)))).length;
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

// Arama: uygulama adı, URL, ekip ve birim adı içinde (büyük/küçük harf duyarsız).
function matches(s, q) {
  if (!q) return true;
  const team = teamOf(appTeamId(s.appId));
  const unit = team ? unitOf(team.unitId) : null;
  return [s.name, s.baseUrl, team?.name, unit?.name].some((x) => lower(x).includes(q));
}

// Satır başlığındaki durum sayaçları: ● sağlıklı ● sorunlu ● erişilemiyor.
function counters(list) {
  const n = (st) => list.filter((s) => s.state === st).length;
  return ['healthy', 'degraded', 'down', 'unknown'].filter((st) => n(st) > 0)
    .map((st) => `<span class="cnt" title="${STATE_TEXT[st]}"><i class="dot ${st}"></i>${n(st)}</span>`).join('');
}

// Hedefin, bu uygulamanın pod'larından üst üste birkaç tur erişilemeyen (alarm) pod sayısı.
const unreachedTargetsOf = (s) => new Set((s.connections || [])
  .flatMap((c) => c.cells.flatMap((x) => (x.unreachedTargets || []).filter((u) => u.confirmed).map((u) => c.connectionId + '|' + u.instanceId)))).size;

// "Pod listesini sıfırla" ne zaman gösterilir: kendiliğinden silinmeyen bir kayıt varsa, yani uygulamanın eksik bir pod'u
// veya bağlantı hedeflerinden erişilemeyen bir pod varsa. Sıfırlama ikisini de temizler; mevcut pod'lar yeniden keşfedilir.
const needsReset = (s) => missingOf(s) > 0 || unreachedTargetsOf(s) > 0;

const resetButton = (s) => (needsReset(s)
  ? `<button class="nf-btn gray" data-reset-app="${esc(s.appId)}" title="Eksik pod'lar ve erişilemeyen hedef pod'ları kendiliğinden silinmez; sorunu giderdiyseniz (veya pod sayısını bilerek azalttıysanız) buradan temizleyin.">${ICON_RESET}Pod listesini sıfırla</button>`
  : '');

// Kartın sol üst şeridi: en önemli sorun (Netflix'in "Yeni bölüm" şeridi gibi).
function flagOf(s) {
  const missing = missingOf(s);
  const { bad } = connSummary(s);
  if (s.state === 'down') return { cls: '', text: 'ERİŞİLEMİYOR' };
  if (missing) return { cls: '', text: `${missing} POD EKSİK` };
  if (bad) return { cls: 'warn', text: `${bad} BAĞLANTI SORUNLU` };
  if (s.state === 'unknown') return { cls: 'idle', text: 'BEKLİYOR' };
  return null;
}

// "3 pod", "≥ 4 pod", "erişilemiyor"
const podsText = (s) => (s.state === 'down' ? 'pod yok' : `${s.converged === false ? '≥ ' : ''}${s.podCount} pod`);

function connsText(s) {
  const { total, bad } = connSummary(s);
  if (!total) return '<span class="muted">bağlantı yok</span>';
  return bad ? `<span class="warn-t">⚠ ${bad}/${total} bağlantı</span>` : `<span class="ok">✓ ${total} bağlantı</span>`;
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
      <div class="tile-body">
        <span class="tile-name">${esc(s.name)}</span>
        <div class="tile-line"><span class="st ${s.state}">${STATE_TEXT[s.state]}</span><span class="box">${podsText(s)}</span>${connsText(s)}</div>
        <div class="tile-more">${esc(team ? team.name + ' · ' : '')}${esc(s.baseUrl)} · ${s.checkedAtUtc ? timeOf(s.checkedAtUtc) : '–'}
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

  // Detay penceresi açıksa onu da yeni sonuçlarla güncelliyoruz.
  if (modalAppId) renderModal();
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
  const missing = missingOf(s);
  const { total, bad } = connSummary(s);
  const msgClass = s.state === 'down' ? 'bad' : s.state === 'degraded' ? 'warn' : '';
  const hist = s.history || [];
  const maxPods = Math.max(1, ...hist.map((h) => h.podCount));

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
            ${s.confidence != null ? `<span class="muted" title="Başka pod olmama olasılığı">%${Math.round(s.confidence * 100)} güven</span>` : ''}
            <span class="muted">son kontrol ${s.checkedAtUtc ? timeOf(s.checkedAtUtc) : '–'}</span>
          </div>
          ${s.message ? `<p class="m-msg ${msgClass}">${esc(s.message)}</p>` : ''}
          ${needsReset(s) ? '<p class="msg">Eksik pod\'lar ve erişilemeyen hedef pod\'ları kendiliğinden silinmez; sorunu giderdiyseniz veya pod sayısını bilerek azalttıysanız "Pod listesini sıfırla" ile temizleyin.</p>' : ''}
          ${hist.length ? `<div class="m-history">${hist.map((h) => `<i class="${h.state}" style="height:${Math.max(12, (h.podCount / maxPods) * 100)}%"
              title="${timeOf(h.atUtc)} · ${STATE_TEXT[h.state] || h.state} · ${h.podCount} pod"></i>`).join('')}</div>
            <div class="m-history-label">Son ${hist.length} tur · çubuk yüksekliği pod sayısı</div>` : ''}
        </div>
        <div class="m-side">
          <div><span>URL: </span>${esc(s.baseUrl)}</div>
          <div><span>Birim: </span>${esc(unit?.name ?? '–')}</div>
          <div><span>Ekip: </span>${esc(team?.name ?? 'Atanmamış')}</div>
          <div><span>Bağlantılar: </span>${total}${bad ? ` <span class="warn-t">(${bad} sorunlu)</span>` : ''}</div>
          <div><span>Test aralığı: </span>${snap.intervalSeconds} sn</div>
        </div>
      </div>
      ${episodesHtml(s)}
      ${matrixHtml(s)}
    </div>`;
  dlg.scrollTop = keep;
}

// Pod listesi, Netflix'in bölüm listesi gibi:
//   up          -> bu turda cevap verdi (normal)
//   unconfirmed -> bu turda denk gelinmedi; rastgele dağıtım yüzünden olabilir, alarm değil (soluk)
//   missing     -> üst üste birkaç tur görünmedi ve yerine yeni pod gelmedi (kırmızı, alarm)
function episodesHtml(s) {
  if (!s.pods?.length) return '';
  const label = { up: 'ÇALIŞIYOR', unconfirmed: 'BU TURDA GÖRÜLMEDİ', missing: 'EKSİK' };
  return `<h3 class="m-sec">Pod'lar <span>${s.pods.length}</span></h3>
    <div class="episodes">${s.pods.map((p, i) => {
      const desc = p.state === 'up'
        ? esc((p.addresses || []).join(', '))
        : p.state === 'missing'
          ? `${p.missedCycles} turdur cevap vermiyor · son görülme ${timeOf(p.lastSeenUtc)}`
          : `Bu turda denk gelinmedi (alarm değil) · son görülme ${timeOf(p.lastSeenUtc)}`;
      const kvs = Object.entries(p.details || {}).slice(0, 6)
        .map(([k, v]) => `<span class="kv"><b>${esc(k)}</b> ${esc(v)}</span>`).join('');
      return `
        <div class="ep ${p.state}">
          <div class="ep-num">${i + 1}</div>
          <div class="ep-thumb" style="--h:${hueOf(p.instanceId)}">${i + 1}</div>
          <div>
            <div class="ep-title">${esc(p.details?.POD_NAME || p.machineName)}<span class="pid">${shortId(p.instanceId)}</span><span class="ep-state ${p.state}">${label[p.state] || p.state}</span></div>
            <div class="ep-desc">${desc}</div>
            ${kvs ? `<div class="kvs">${kvs}</div>` : ''}
          </div>
          <div class="ep-time">${p.startedAtUtc ? 'başladı ' + timeOf(p.startedAtUtc) : ''}</div>
        </div>`;
    }).join('')}</div>`;
}

// Satırlar: ilişkilendirilmiş bağlantılar, sütunlar: pod'lar. Hücre: o pod'un bağlantıyı test sonucu.
function matrixHtml(s) {
  const def = defs.apps.find((a) => a.id === s.appId);
  const hasConns = (def?.connectionIds?.length ?? 0) > 0;
  if (!hasConns) return '<p class="msg">Bu uygulamaya bağlantı atanmamış. "Tanımlar" sekmesinden sürükleyip bırakın.</p>';
  if (!s.connections?.length) return s.state === 'down' ? '' : '<p class="msg">Bağlantı sonuçları bekleniyor…</p>';

  const pods = s.pods || [];
  // Eksik pod'un sütun başlığı kırmızı: o pod cevap vermediği için bağlantılarını test edemiyoruz.
  const head = pods.map((p) => `<th class="${p.state === 'missing' ? 'missing' : ''}">${esc(p.machineName)}<span class="pid">${shortId(p.instanceId)}${p.state === 'missing' ? ' · eksik' : ''}</span></th>`).join('');

  const rows = s.connections.map((c) => {
    const ips = [...new Set(c.cells.flatMap((x) => x.ipResults.map((r) => r.address)))];
    // Hata, eski (soluk) sonuçlar olsa bile satırda görünsün; aksi halde "test edilemiyor" durumu gözden kaçar.
    const connDef = defs.connections.find((x) => x.id === c.connectionId);
    const rowHead = `<td class="rowhead"><span class="name">${esc(c.name)}${connDef ? cpBadge(connDef) : ''}</span><span class="target">${esc(c.target)}</span>
      ${ips.length ? `<span class="ips">IP: ${esc(ips.join(', '))}</span>` : ''}
      ${c.callError && c.cells.length ? `<span class="rowerr">⚠ ${esc(c.callError)}</span>` : ''}</td>`;

    // Probe çağrısının kendisi başarısızsa satırın tamamında hatayı gösteriyoruz.
    if (c.callError && !c.cells.length) return `<tr>${rowHead}<td class="callerr" colspan="${Math.max(1, pods.length)}">${esc(c.callError)}</td></tr>`;

    const cells = pods.map((p) => {
      const cell = c.cells.find((x) => x.instanceId === p.instanceId);

      // Pod eksikse (üst üste birkaç tur cevap vermedi) eski başarılı sonucu yeşil göstermek yanıltıcı olur: pod çalışmıyor,
      // bu bağlantıyı da kullanamıyor. Kırmızı gösteriyoruz; son bilinen sonuç yalnızca ipucunda.
      if (p.state === 'missing') {
        const lastKnown = cell
          ? `Son bilinen sonuç (${timeOf(cell.checkedAtUtc)}): ${cell.success ? 'başarılı, ' + cell.elapsedMs + ' ms' : (cell.error || 'başarısız')}`
          : 'Bu pod için daha önce sonuç alınmadı';
        return `<td class="cell fail" title="${esc(`Pod ${p.missedCycles} turdur cevap vermiyor (son görülme: ${timeOf(p.lastSeenUtc)}).\n${lastKnown}`)}">`
          + `✗<span class="reach">Pod cevap vermiyor</span><span class="since">${durationOf(p.lastSeenUtc)} görünmüyor</span></td>`;
      }

      if (!cell) return '<td class="cell none" title="Bu turda bu pod\'a denk gelinmedi">–</td>';
      const tip = [
        cell.fresh ? '' : 'Bu turda test edilemedi, son bilinen sonuç (' + timeOf(cell.checkedAtUtc) + ')',
        cell.reachedAddress ? 'Ulaşılan IP: ' + cell.reachedAddress : '',
        ...cell.ipResults.map((r) => `${r.address}: ${r.success ? 'ok ' + r.elapsedMs + ' ms' : r.error}`),
        cell.error ? 'Hata: ' + cell.error : '',
        !cell.success && cell.failingSinceUtc ? `Başarısız: ${dateTimeOf(cell.failingSinceUtc)} tarihinden beri` : '',
        !cell.success ? (cell.lastSuccessUtc ? `Son başarılı test: ${dateTimeOf(cell.lastSuccessUtc)}` : 'Monitor açıldığından beri hiç başarılı olmadı') : '',
      ].filter(Boolean).join('\n');
      // Başarısız: nedeni (timeout, reddedildi, ConnectivityProbe yok...) ve ne zamandır başarısız olduğunu yazıyoruz.
      if (!cell.success) {
        return `<td class="cell fail ${cell.fresh ? '' : 'stale'}" title="${esc(tip)}">✗<span class="reach">${esc(cell.error || 'başarısız')}</span>`
          + (cell.failingSinceUtc ? `<span class="since">${durationOf(cell.failingSinceUtc)} erişilemiyor</span>` : '') + '</td>';
      }

      // Yalnızca telnet (hedef ConnectivityProbe kullanmıyor).
      if (!cell.targetKind) {
        return `<td class="cell ok ${cell.fresh ? '' : 'stale'}" title="${esc(tip)}">✓<span class="ms">${cell.elapsedMs} ms</span>`
          + `${cell.reachedAddress ? `<span class="reach">${esc(cell.reachedAddress)}</span>` : ''}</td>`;
      }

      // ConnectivityProbe kullanan hedef: bu pod'un kendi içinden eriştiği hedef pod'ları ve erişemedikleri.
      // Erişilemeyenler iki kaynaktan gelir:
      //   1) Monitor'ün bu bağlantı için hatırladığı hedef pod'ları (sunucu): hedef Monitor'de kayıtlı olmasa bile bilinir,
      //      üst üste birkaç tur erişilemeyenler "ne zamandır" bilgisiyle gelir.
      //   2) Hedef Monitor'de ayrıca kayıtlıysa orada bilinen pod listesi: fark hemen (ilk turda) görünür.
      const reached = cell.targetPods || [];
      const reachedIds = new Set(reached.map((x) => x.instanceId));
      const remembered = cell.unreachedTargets || [];
      const confirmed = remembered.filter((u) => u.confirmed);
      const pending = remembered.filter((u) => !u.confirmed);
      const expected = expectedPodsOf(connDef || c); // null: hedef Monitor'de kayıtlı değil
      const notReached = [...confirmed];
      for (const x of expected || []) {
        if (reachedIds.has(x.instanceId) || notReached.some((u) => u.instanceId === x.instanceId)) continue;
        const known = pending.find((u) => u.instanceId === x.instanceId); // sunucu da biliyorsa süresini kullanıyoruz
        notReached.push(known || { instanceId: x.instanceId, machineName: x.machineName, podName: x.details?.POD_NAME });
      }
      const partial = notReached.length > 0 || cell.targetFailedRequests > 0;
      const total = expected ? Math.max(expected.length, reached.length + notReached.length) : reached.length + notReached.length;
      const countText = notReached.length || expected
        ? `${reached.length}/${total} pod'a erişildi`
        : `${reached.length} pod'a erişildi${cell.targetCountConverged === false ? ' (en az)' : ''}`;
      // Aynı ad birden fazla pod'da varsa ayırt etmek için kısa kimlik ekliyoruz (pod adı varsa o kullanılır).
      const allPods = [...reached, ...notReached];
      const label = (x) => x.podName || podLabel(x, allPods);
      const names = (list) => esc(list.slice(0, 3).map(label).join(', ')) + (list.length > 3 ? ` +${list.length - 3}` : '');

      const targetTip = [
        `Bu pod kendi içinden hedefin ${reached.length} pod'una erişti:`,
        ...reached.map((x) => `  ✓ ${label(x)} (${shortId(x.instanceId)}) ${x.addresses.join(', ')} · ${x.hits} cevap`),
        ...(notReached.length ? ['Erişilemeyen pod\'lar:', ...notReached.map((x) => `  ✗ ${label(x)} (${shortId(x.instanceId)})`
          + (x.sinceUtc ? ` · ${dateTimeOf(x.sinceUtc)} tarihinden beri (${x.missedCycles} tur)` : '')
          + (x.sinceUtc ? (x.lastReachedUtc ? ` · son erişim ${dateTimeOf(x.lastReachedUtc)}` : ' · bu pod hiç erişemedi') : ''))] : []),
        ...pending.filter((u) => !notReached.includes(u)).map((u) => `  … ${label(u)}: ${u.missedCycles} turdur görülmedi (henüz alarm değil, rastlantı olabilir)`),
        cell.targetFailedRequests
          ? `${cell.targetFailedRequests} istek cevap alamadı: load balancer bu istekleri cevap vermeyen bir pod'a göndermiş olabilir.`
          : '',
      ].filter(Boolean).join('\n');

      // Erişilemeyen her pod ayrı satırda: adı ve ne zamandır erişilemediği.
      const unreachedHtml = notReached.slice(0, 4).map((x) => `<span class="reach bad-line">✗ ${esc(label(x))}`
        + (x.sinceUtc ? `<span class="since">${durationOf(x.sinceUtc)} erişilemiyor</span>` : '') + '</span>').join('')
        + (notReached.length > 4 ? `<span class="reach">+${notReached.length - 4} pod daha</span>` : '');

      const body = `${partial ? '⚠' : '✓'}<span class="ms">${countText} · ${cell.elapsedMs} ms</span>`
        + `<span class="reach">${names(reached)}</span>`
        + unreachedHtml
        + (cell.targetFailedRequests && !notReached.length ? `<span class="reach warn">${cell.targetFailedRequests} istek cevap alamadı</span>` : '');
      return `<td class="cell ${partial ? 'warn' : 'ok'} ${cell.fresh ? '' : 'stale'}" title="${esc([tip, targetTip].filter(Boolean).join('\n'))}">${body}</td>`;
    }).join('');

    return `<tr>${rowHead}${cells}</tr>`;
  }).join('');

  return `<div class="section-title">Bağlantılar (her pod kendi içinden test eder)</div>
    <div class="matrix-wrap"><table class="matrix"><thead><tr><th>Bağlantı</th>${head}</tr></thead><tbody>${rows}</tbody></table></div>`;
}


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

(async function init() {
  setTab(load('tab', 'monitor'));
  await loadDefs();
  await refreshMonitor();
  setInterval(refreshMonitor, 3000);   // sonuçları 3 saniyede bir sunucudan okuyoruz
  setInterval(renderMeta, 1000);       // geri sayımı her saniye güncelliyoruz
})();
