'use strict';

// ---------------------------------------------------------------------------------------------
// Durum
// ---------------------------------------------------------------------------------------------
const $ = (sel) => document.querySelector(sel);
let defs = { units: [], teams: [], apps: [], connections: [] }; // tanımlar
let snap = null;                            // monitörün son anlık görüntüsü
let lastSnapJson = '';                      // değişmediyse ekranı yeniden çizmemek için
let tab = 'monitor';

const UNASSIGNED = '__unassigned';          // ekibi olmayan uygulamaların sanal grubu
let selTeam = load('selTeam', null);         // Tanımlar'da seçili ekip

const STATE_ORDER = { down: 0, degraded: 1, unknown: 2, healthy: 3 };
const stateText = (st) => t('state.' + st);

// Tarayıcıda kalıcı küçük tercihler (seçili ekip, sekme). Depolama kapalıysa sessizce varsayılan kullanılır.
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

// Sunucuya istek atar; hata dönerse sunucunun mesajıyla (seçili dilde) Error fırlatır.
async function api(method, url, body) {
  const headers = { 'X-Lang': LANG };
  if (body) headers['Content-Type'] = 'application/json';
  const res = await fetch(url, { method, headers, body: body ? JSON.stringify(body) : undefined });
  // Oturum süresi dolduysa giriş sayfasına yönlendiriyoruz.
  if (res.status === 401 && !url.startsWith('/api/auth')) { location.href = '/login.html'; throw new Error(t('err.login')); }
  if (!res.ok) {
    let msg = res.statusText;
    try { msg = (await res.json()).error || msg; } catch { /* gövde JSON değil */ }
    throw new Error(msg);
  }
  try { return await res.json(); } catch { return null; }
}

const timeOf = (iso) => (iso ? new Date(iso).toLocaleTimeString(LOCALE()) : '-');
// Bugünse yalnızca saat, değilse tarih + saat.
const dateTimeOf = (iso) => {
  if (!iso) return '-';
  const d = new Date(iso);
  return d.toDateString() === new Date().toDateString() ? d.toLocaleTimeString(LOCALE()) : d.toLocaleString(LOCALE());
};
// Kısa "ne kadar önce": "8 sn önce", "3 dk önce", "2 sa önce".
function agoOf(iso) {
  const sec = Math.max(0, Math.round((Date.now() - new Date(iso)) / 1000));
  if (sec < 60) return t('ago.s', { n: sec });
  if (sec < 3600) return t('ago.m', { n: Math.floor(sec / 60) });
  return t('ago.h', { n: Math.floor(sec / 3600) });
}

// Bir andan bu yana geçen süre: "45 saniyedir", "12 dakikadır", "2 saat 5 dakikadır", "3 gündür".
function durationOf(iso) {
  const sec = Math.max(0, Math.round((Date.now() - new Date(iso)) / 1000));
  if (sec < 60) return t('for.s', { n: sec });
  const min = Math.floor(sec / 60);
  if (min < 60) return t('for.m', { n: min });
  const h = Math.floor(min / 60);
  if (h < 24) return min % 60 ? t('for.hm', { h, m: min % 60 }) : t('for.h', { h });
  return t('for.d', { n: Math.floor(h / 24) });
}
const shortId = (id) => (id || '').slice(0, 8);
const targetOf = (c) => c.host + (c.port ? ':' + c.port : '');
const byName = (a, b) => a.name.localeCompare(b.name, LANG);
const lower = (s) => String(s ?? '').toLocaleLowerCase(LANG);

const teamOf = (id) => defs.teams.find((x) => x.id === id);
const unitOf = (id) => defs.units.find((u) => u.id === id);
const appTeamId = (appId) => {
  const a = defs.apps.find((x) => x.id === appId);
  return a && teamOf(a.teamId) ? a.teamId : UNASSIGNED;
};
const teamLabel = (x) => `${unitOf(x.unitId)?.name ?? '?'} / ${x.name}`;

// Pod'un görünen adı (Kubernetes'te pod adı, değilse makine adı) ve sürüm etiketi ("1.4.0 · b7e2c1").
const podName = (p) => p.details?.POD_NAME || p.details?.HOSTNAME || p.machineName;
const versionLabel = (p) => (p.appVersion || '?') + (p.buildId ? ' · ' + p.buildId.slice(0, 6) : '');
// Uygulamanın canlı pod'larındaki farklı sürüm/build'ler.
const buildsOf = (s) => [...new Set((s.pods || []).filter((p) => p.state === 'up').map(versionLabel))];
// En çok pod'da çalışan sürüm "ana" sürüm; diğerleri (eski/yeni) vurgulanır.
function mainBuildOf(s) {
  const count = (b) => s.pods.filter((p) => p.state === 'up' && versionLabel(p) === b).length;
  return buildsOf(s).sort((a, b) => count(b) - count(a))[0];
}

// Durum notları (sunucudan kod olarak gelir) seçili dilde metne çevrilir.
function noteText(n) {
  return t('note.' + n.code, { n: n.count, c: snap?.missingAfterCycles ?? 3, t: n.atUtc ? dateTimeOf(n.atUtc) : '–' });
}
const messageOf = (s) => (s.notes || []).map(noteText).join('; ');

// Pod'un ağı (cluster): sunucunun verdiği görünen ad (elle verilen ad ya da pod ağı, ör. 10.42.0.0/16).
const clusterOf = (key) => (snap?.clusters || []).find((c) => c.key === key);
const clusterLabel = (key, fallback) => clusterOf(key)?.name || fallback || t('m.unknownNet');
// Elle ad verildiyse altında ağ adresleri de gösterilir.
const clusterSub = (key) => { const c = clusterOf(key); return c?.custom && c.networks?.length ? c.networks.join(', ') : ''; };

// Pod'ları ağlarına (cluster) göre gruplar; sıra sunucunun sırasıdır (ada göre).
function groupByCluster(pods) {
  const groups = new Map();
  for (const p of pods) {
    const key = p.clusterKey || '?';
    if (!groups.has(key)) groups.set(key, { key, name: clusterLabel(key, p.clusterName), pods: [] });
    groups.get(key).pods.push(p);
  }
  return [...groups.values()];
}

// ---------------------------------------------------------------------------------------------
// Sekmeler, dil ve üst çubuk
// ---------------------------------------------------------------------------------------------
function setTab(name) {
  if (name !== 'defs') name = 'monitor';
  tab = name;
  save('tab', name);
  $('#tab-monitor').hidden = name !== 'monitor';
  $('#tab-defs').hidden = name !== 'defs';
  $('#navTools').hidden = name !== 'monitor'; // arama ve filtre yalnızca monitörde anlamlı
  window.scrollTo(0, 0);
  $('#topbar').classList.toggle('scrolled', name !== 'monitor'); // Tanımlar'da billboard yok: menü baştan koyu
  document.querySelectorAll('.tab').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
}

// Dil değişince sabit metinler ve tüm ekranlar yeniden çizilir.
function changeLang(lang) {
  setLang(lang);
  lastSnapJson = '';
  renderDefs();
  if (snap) { if (detailAppId) renderDetail(); else renderMonitor(); }
  renderMeta();
}

// "Son tur / test aralığı" bilgisini her saniye günceller.
function renderMeta() {
  if (!snap) { $('#meta').textContent = ''; return; }
  const parts = [];
  if (snap.lastRunUtc) parts.push(t('meta.updated', { t: timeOf(snap.lastRunUtc) }));
  parts.push(t('meta.interval', { n: snap.intervalSeconds }));
  $('#meta').textContent = parts.join(' · ');
}

// ---------------------------------------------------------------------------------------------
// Form penceresi (ekle/düzenle için ortak)
// ---------------------------------------------------------------------------------------------
// fields: [{ name, label, placeholder, type (text|number|checkbox|select), options [{value,label}], hint }]
function openForm(title, fields, values, onSubmit) {
  $('#dlgTitle').textContent = title;
  $('#dlgError').textContent = '';
  $('#dlgWarn').hidden = true;
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
  ...defs.teams.slice().sort((a, b) => teamLabel(a).localeCompare(teamLabel(b), LANG)).map((x) => ({ value: x.id, label: teamLabel(x) })),
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

// Monitör sonuçları değişince Tanımlar'daki durum noktaları ve uygulama kartları güncellenir (sürükleme sırasında değil).
// Yalnızca Tanımlar'da gösterilen durum değiştiyse yeniden çizilir; CPU/bellek gibi her bildirimde değişen değerler dahil
// edilmez, yoksa ekran her 3 saniyede yenilenir ve fare altındaki öğeler değişirdi.
let dragging = false;
let defsStatusSig = '';
function refreshDefsStatus() {
  if (dragging || document.activeElement?.matches?.('.add-select')) return;
  const sig = JSON.stringify((snap?.apps || []).map((s) => [s.appId, s.state, s.podCount, buildsOf(s).join(),
    (s.connections || []).map((c) => [c.connectionId, c.cells.filter((x) => x.fresh).length, c.cells.filter((x) => x.fresh && x.success).length])]));
  if (sig === defsStatusSig) return;
  defsStatusSig = sig;
  renderPoolList();
  renderApps();
  wireDragAndDrop();
}

// step 1: Sol: birimler ve altlarındaki ekipler. Ekibe tıklayınca ortada havuzu, sağda uygulamaları açılır.
//         Ekipler (ve "Atanmamış uygulamalar") uygulama kartlarının bırakılabileceği hedeflerdir: ekibe taşır.
function renderTree() {
  const unassigned = defs.apps.filter((a) => !teamOf(a.teamId));
  const units = defs.units.slice().sort(byName);
  $('#tree').innerHTML = (units.length ? units.map((u) => {
    const teams = defs.teams.filter((x) => x.unitId === u.id).sort(byName);
    return `
      <div class="tree-unit">
        <div class="tree-unit-head hover-actions">
          <b class="grow">${esc(u.name)}</b>
          <button class="icon-btn" data-add-team="${u.id}" title="${esc(t('defs.addTeam.title'))}">${esc(t('defs.addTeam'))}</button>
          <button class="icon-btn act" data-edit-unit="${u.id}" title="${esc(t('common.edit'))}">✎</button>
          <button class="icon-btn act" data-del-unit="${u.id}" title="${esc(t('common.delete'))}">✕</button>
        </div>
        ${teams.length ? teams.map((x) => {
          const n = defs.apps.filter((a) => a.teamId === x.id).length;
          return `<div class="tree-team hover-actions ${selTeam === x.id ? 'sel' : ''}" data-select-team="${x.id}" data-team-drop="${x.id}">
              <span class="grow">${esc(x.name)}</span><span class="count">${esc(t('defs.appsCount', { n }))}</span>
              <button class="icon-btn act" data-edit-team="${x.id}" title="${esc(t('common.edit'))}">✎</button>
              <button class="icon-btn act" data-del-team="${x.id}" title="${esc(t('common.delete'))}">✕</button>
            </div>`;
        }).join('') : `<div class="hint tree-empty">${esc(t('defs.noTeams'))}</div>`}
      </div>`;
  }).join('') : `<div class="hint">${esc(t('defs.noUnits'))}</div>`)
  + `<div class="tree-team unassigned ${selTeam === UNASSIGNED ? 'sel' : ''}" data-select-team="${UNASSIGNED}" data-team-drop="">
        <span class="grow">${esc(t('defs.unassignedApps'))}</span><span class="count">${esc(t('defs.appsCount', { n: unassigned.length }))}</span></div>`;
}

// Bir bağlantının tüm uygulamalardaki son test durumu: taze sonuçlarda kaç pod erişiyor.
function connHealth(connId) {
  let ok = 0, all = 0;
  for (const s of snap?.apps || []) {
    for (const c of s.connections || []) {
      if (c.connectionId !== connId) continue;
      for (const x of c.cells) if (x.fresh) { all++; if (x.success) ok++; }
    }
  }
  return { ok, all, cls: !all ? 'none' : ok === all ? 'ok' : ok === 0 ? 'bad' : 'warn' };
}

// Bağlantıyı kullanan uygulamalar.
const usersOf = (connId) => defs.apps.filter((a) => a.connectionIds.includes(connId));

// Havuzdaki bir bağlantı (sürüklenebilir). q verilirse eşleşen kısımlar işaretlenir.
function poolChip(c, q) {
  const users = usersOf(c.id);
  const health = connHealth(c.id);
  const hl = (text) => (q ? Search.highlight(text, q, esc) : esc(text));
  return `
  <div class="chip hover-actions" draggable="true" data-conn="${c.id}">
    <span class="grip">⋮⋮</span>
    <i class="health ${health.cls}" title="${esc(health.all ? t('pool.status', { ok: health.ok, all: health.all }) : t('pool.noStatus'))}"></i>
    <span class="grow conn-info">
      <span class="name">${hl(c.name)}${targetBadge(c)}${c.tls ? ' <span class="tls-tag" title="TLS">🔒</span>' : ''}</span>
      <span class="conn-target"><span class="host">${hl(c.host)}</span>${c.port ? `<span class="port">${hl(String(c.port))}</span>` : ''}
        <span class="usage ${users.length ? '' : 'unused'}" title="${esc(users.length ? t('pool.usedByTip', { names: users.map((a) => a.name).join(', ') }) : t('pool.unusedTip'))}">${esc(users.length ? t('pool.usedBy', { n: users.length }) : t('pool.unused'))}</span></span>
    </span>
    <button class="icon-btn act" data-edit-conn="${c.id}" title="${esc(t('common.edit'))}">✎</button>
    <button class="icon-btn act" data-del-conn="${c.id}" title="${esc(t('common.delete'))}">✕</button>
  </div>`;
}

// step 2: Sol: bağlantı havuzu. Tek havuzdur, birim ve ekiplerden bağımsızdır (ekip seçmek havuzu değiştirmez); her
//         bağlantı her uygulamaya atanabilir. Panel bir kez çizilir; arama yazarken yalnızca liste yenilenir.
let poolQuery = '';
function renderPool() {
  $('#poolPanel').innerHTML = `
    <div class="panel-head"><h2>${esc(t('pool.title'))}</h2>
      <button class="btn small" data-add-conn="">${esc(t('pool.add'))}</button></div>
    <label class="pool-search"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M10.5 3a7.5 7.5 0 0 1 5.9 12.1l4.8 4.8-1.4 1.4-4.8-4.8A7.5 7.5 0 1 1 10.5 3Zm0 2a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11Z"/></svg>
      <input id="poolSearch" type="search" autocomplete="off" placeholder="${esc(t('pool.search'))}" value="${esc(poolQuery)}"></label>
    <div id="poolList"></div>`;
  $('#poolSearch').addEventListener('input', (e) => { poolQuery = e.target.value; renderPoolList(); wireDragAndDrop(); });
  renderPoolList();
}

// Arama terimi alanları: ad, host, port (birebir), hedef uygulama, kullanan uygulamalar, havuz (ekip).
function searchEntry(c) {
  const target = c.targetAppId && defs.apps.find((a) => a.id === c.targetAppId);
  return { item: c, fields: [
    { value: c.name, weight: 3 },
    { value: c.host, weight: 2 },
    { value: c.port, weight: 1.5, exact: true },
    { value: target?.name, weight: 2 },
    { value: usersOf(c.id).map((a) => a.name).join(' '), weight: 1 },
  ] };
}

function renderPoolList() {
  const list = $('#poolList');
  if (!list) return;
  const q = poolQuery.trim();

  // Arama varken alaka sırasına göre, yokken ada göre tüm bağlantılar.
  if (q) {
    const results = Search.rank(q, defs.connections.map(searchEntry));
    list.innerHTML = `<p class="hint">${esc(results.length ? t('pool.results', { n: results.length }) : t('pool.noResults'))}</p>
      <div class="pool">${results.map((r) => poolChip(r.item, q)).join('')}</div>`;
    return;
  }
  const all = defs.connections.slice().sort(byName);
  list.innerHTML = `<p class="hint">${esc(t('pool.help'))}</p>
    <div class="pool">${all.length ? all.map((c) => poolChip(c)).join('') : `<div class="hint">${esc(t('pool.empty'))}</div>`}</div>`;
}

// step 3: Sağ: seçili ekibin uygulamaları. Kartın içi bağlantıların bırakılacağı alan; kartın kendisi soldaki bir ekibe
//         sürüklenerek taşınabilir. Kartta uygulamanın canlı durumu (pod, sürüm) ve bağlantılarının durumu görünür.
function renderApps() {
  const team = teamOf(selTeam);
  const apps = defs.apps.filter((a) => (team ? a.teamId === team.id : selTeam === UNASSIGNED && !teamOf(a.teamId))).sort(byName);
  const title = team ? t('apps.teamTitle', { name: team.name }) : selTeam === UNASSIGNED ? t('defs.unassignedApps') : t('apps.title');
  const keep = $('#appsPanel').scrollTop;

  $('#appsPanel').innerHTML = `
    <div class="panel-head"><h2>${esc(title)}</h2></div>
    <details class="sk-setup add-help"><summary>${esc(t('apps.howTo'))}</summary>
      <p class="hint">${esc(t('apps.howToText'))}</p>
      <pre>${esc(setupSnippet(t('snippet.key'), t('snippet.name')))}</pre>
    </details>
    ${selTeam === UNASSIGNED ? `<p class="hint">${esc(t('apps.unassignedHint'))}</p>` : ''}
    <div class="apps">${apps.length ? apps.map((a) => {
      const s = (snap?.apps || []).find((x) => x.appId === a.id);
      const builds = s ? buildsOf(s) : [];
      const attached = a.connectionIds.map((id) => defs.connections.find((c) => c.id === id)).filter(Boolean);
      const free = defs.connections.filter((c) => !a.connectionIds.includes(c.id)).sort(byName);
      // Bu uygulamanın pod'larından bu bağlantının durumu.
      const connState = (connId) => {
        const cells = (s?.connections || []).find((c) => c.connectionId === connId)?.cells.filter((x) => x.fresh) || [];
        const ok = cells.filter((x) => x.success).length;
        return { cls: !cells.length ? 'none' : ok === cells.length ? 'ok' : ok === 0 ? 'bad' : 'warn',
          tip: cells.length ? t('pool.status', { ok, all: cells.length }) : t('pool.noStatus') };
      };
      return `
        <div class="app-card hover-actions" data-app="${a.id}" draggable="true" title="${esc(t('apps.dragToTeam'))}">
          <div class="app-head">
            <div class="grow"><div class="title">${esc(a.name)}</div>
              <div class="url">${esc(t('apps.key'))} <code>${esc(a.appKey)}</code>${a.registeredAtUtc > '2000' ? ' · ' + esc(t('apps.registered', { t: dateTimeOf(a.registeredAtUtc) })) : ''}</div>
              <div class="app-live">${s && s.pods?.length ? `<span class="st ${s.state}">${esc(stateText(s.state))}</span>
                <span class="box">${esc(podsText(s))}</span>
                ${builds.length === 1 ? `<span class="ver">${esc(builds[0])}</span>` : builds.length > 1 ? `<span class="ver odd">${esc(t('ver.n', { n: builds.length }))}</span>` : ''}`
                : `<span class="muted">${esc(t('apps.noReport'))}</span>`}</div></div>
            <button class="btn small" data-open-app="${a.id}">${esc(t('apps.open'))} ↗</button>
            <button class="icon-btn act" data-copy="${esc(a.appKey)}" title="${esc(t('apps.copyKey'))}">⧉</button>
            <button class="icon-btn act" data-edit-app="${a.id}" title="${esc(t('apps.editTitle'))}">✎</button>
            <button class="icon-btn act" data-del-app="${a.id}" title="${esc(t('common.delete'))}">✕</button>
          </div>
          <div class="dropzone">
            ${attached.length ? attached.map((c) => { const st = connState(c.id); return `
              <span class="chip hover-actions"><i class="health ${st.cls}" title="${esc(st.tip)}"></i><span class="name">${esc(c.name)}${targetBadge(c)}</span>
                <span class="target">${esc(targetOf(c))}</span>
                <button class="icon-btn act" data-detach="${a.id}|${c.id}" title="${esc(t('apps.detach'))}">✕</button></span>`; }).join('')
              : `<span class="placeholder">${esc(t('apps.dropHere'))}</span>`}
            ${free.length ? `<select class="add-select" data-add-select="${a.id}"><option value="">${esc(t('apps.pickConn'))}</option>
              ${free.map((c) => `<option value="${c.id}">${esc(c.name)}</option>`).join('')}</select>` : ''}
          </div>
        </div>`;
    }).join('') : `<div class="empty">${esc(team ? t('apps.emptyTeam') : t('apps.selectTeam'))}</div>`}</div>`;
  $('#appsPanel').scrollTop = keep;
}

// Bir bağlantıyı bir uygulamayla ilişkilendirir ve tanımları yeniler.
async function attach(appId, connId) {
  try { await api('PUT', `/api/apps/${appId}/connections/${connId}`); }
  catch (err) { alert(err.message); }
  await loadDefs();
}

// Uygulamayı başka ekibe taşır (adı aynı kalır; sunucu eski ekibin havuzundan atanmış bağlantıları çıkarır).
async function moveApp(appId, teamId) {
  const a = defs.apps.find((x) => x.id === appId);
  if (!a || (a.teamId || '') === (teamId || '')) return;
  try { await api('PUT', '/api/apps/' + a.id, { name: a.name, teamId: teamId || null }); }
  catch (err) { alert(err.message); }
  await afterChange();
}

// Sürükle-bırak:
//   - havuzdaki bağlantı -> uygulama kartı: bağlantıyı uygulamaya atar
//   - uygulama kartı -> soldaki ekip (veya "Atanmamış uygulamalar"): uygulamayı o ekibe taşır
// Sürüklenen şeyin türü ayrı veri tipleriyle taşınır; böylece her hedef yalnızca kendi türünü kabul eder.
const CONN_TYPE = 'application/x-cp-conn';
const APP_TYPE = 'application/x-cp-app';

function wireDragAndDrop() {
  const start = (el, type, id) => !el.dataset.dragWired && (el.dataset.dragWired = '1') && el.addEventListener('dragstart', (e) => {
    e.stopPropagation();
    e.dataTransfer.setData(type, id);
    e.dataTransfer.setData('text/plain', id);
    e.dataTransfer.effectAllowed = type === CONN_TYPE ? 'copy' : 'move';
    dragging = true;
    document.body.classList.add(type === APP_TYPE ? 'dragging-app' : 'dragging-conn');
  });
  const end = (el) => el.addEventListener('dragend', () => {
    dragging = false;
    document.body.classList.remove('dragging-app', 'dragging-conn');
  });
  const target = (el, type, onDrop) => {
    if (el.dataset.dropWired) return; // ağaç her çizimde yenilenmez; iki kez bağlanmasın
    el.dataset.dropWired = '1';
    el.addEventListener('dragover', (e) => {
      if (!e.dataTransfer.types.includes(type)) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = type === CONN_TYPE ? 'copy' : 'move';
      el.classList.add('drag-over');
    });
    el.addEventListener('dragleave', (e) => { if (!el.contains(e.relatedTarget)) el.classList.remove('drag-over'); });
    el.addEventListener('drop', (e) => {
      el.classList.remove('drag-over');
      const id = e.dataTransfer.getData(type);
      if (!id) return;
      e.preventDefault();
      e.stopPropagation();
      onDrop(id);
    });
  };

  document.querySelectorAll('#poolPanel .chip[data-conn]').forEach((chip) => { if (!chip.dataset.dragWired) end(chip); start(chip, CONN_TYPE, chip.dataset.conn); });
  document.querySelectorAll('.app-card').forEach((card) => {
    if (!card.dataset.dragWired) end(card);
    start(card, APP_TYPE, card.dataset.app);
    target(card, CONN_TYPE, (connId) => attach(card.dataset.app, connId));
  });
  document.querySelectorAll('[data-team-drop]').forEach((el) => target(el, APP_TYPE, (appId) => moveApp(appId, el.dataset.teamDrop)));

  // Sürüklemeye alternatif: kartın içindeki listeden seçerek ekleme.
  document.querySelectorAll('[data-add-select]').forEach((sel) => {
    if (sel.dataset.wired) return;
    sel.dataset.wired = '1';
    sel.addEventListener('change', () => { if (sel.value) attach(sel.dataset.addSelect, sel.value); });
  });
}

// Bağlantı formu. Her pod bağlantıyı kendi içinden TCP (telnet) ile test eder. Hedef de Monitor'e kayıtlı bir uygulamaysa
// seçilebilir; o zaman bağlantı satırında hedef uygulamanın pod sayısı ve durumu da görünür.
const connFields = () => [
  { name: 'name', label: t('form.name'), placeholder: t('form.connNamePh') },
  { name: 'host', label: t('form.host'), placeholder: 'sql01, sql01:1433, https://orders.example.com/', hint: t('form.hostHint') },
  { name: 'port', label: t('form.port'), type: 'number', placeholder: t('form.portPh'), hint: t('form.portHint') },
  { name: 'targetAppId', label: t('form.target'), type: 'select',
    options: [{ value: '', label: t('form.targetNone') }, ...defs.apps.slice().sort(byName).map((a) => ({ value: a.id, label: a.name }))],
    hint: t('form.targetHint') },
  { name: 'tlsCheck', label: t('form.tls'), type: 'select', hint: t('form.tlsHint'),
    options: [{ value: 'auto', label: t('form.tlsAuto') }, { value: 'on', label: t('form.tlsOn') }, { value: 'off', label: t('form.tlsOff') }] },
];

// Bağlantının test edilen adresi "host:port" (sunucudaki kuralla aynı): testler host ve port üzerinden yapıldığı için
// "sql01:1433" ile "SQL01" + 1433, "https://github.com/" ile "https://github.com/login" aynı adrestir. Port alanı
// doluysa host içindeki porttan önce gelir; URL'de port yoksa https 443, http 80.
function endpointKey(host, port) {
  let h = String(host ?? '').trim();
  let p = port === '' || port == null ? null : Number(port);
  const url = h.match(/^([a-z][a-z0-9+.-]*):\/\/(\[[^\]]+\]|[^\/:?#]+)(?::(\d+))?/i);
  if (url) {
    h = url[2];
    if (p == null) p = url[3] ? Number(url[3]) : ({ https: 443, http: 80 })[url[1].toLowerCase()] ?? null;
  } else {
    const hp = h.match(/^(\[[^\]]+\]|[^:]+):(\d+)$/);
    if (hp) { h = hp[1]; if (p == null) p = Number(hp[2]); }
  }
  h = h.replace(/^\[|\]$/g, '').replace(/\.$/, '').toLowerCase();
  return h && p ? h + ':' + p : null;
}

// Bağlantı formunda adres yazılırken havuzda aynı adres varsa uyarır. Düzenlemede adres değişmediyse uyarmaz
// (sunucu da yalnızca adres değiştiğinde çakışmayı reddeder).
function watchDuplicate(current) {
  const form = $('#dlgForm');
  const original = current ? endpointKey(current.host, current.port) : null;
  const check = () => {
    const key = endpointKey(form.elements.host.value, form.elements.port.value);
    const dup = key && key !== original ? defs.connections.find((c) => c.id !== current?.id && endpointKey(c.host, c.port) === key) : null;
    $('#dlgWarn').hidden = !dup;
    if (dup) $('#dlgWarn').textContent = t('form.duplicate', { name: dup.name, key });
  };
  form.elements.host.addEventListener('input', check);
  form.elements.port.addEventListener('input', check);
}

// Havuzda ve kartlarda hedefi kayıtlı bir uygulama olan bağlantıları gösteren rozet.
const targetBadge = (c) => {
  const target = c.targetAppId && defs.apps.find((a) => a.id === c.targetAppId);
  return target ? ` <span class="cp" title="${esc(t('target.title', { name: target.name }))}">→ ${esc(target.name)}</span>` : '';
};

const appFields = () => [
  { name: 'name', label: t('form.name'), placeholder: t('form.appNamePh'), hint: t('form.appNameHint') },
  { name: 'teamId', label: t('form.team'), type: 'select', options: teamOptions(t('form.teamNone')), hint: t('form.teamHint') },
];

// Form değerlerini API'nin beklediği biçime çevirir (boş port -> null, boş ekip -> null).
const toConnBody = (v) => ({
  name: v.name, host: v.host, port: v.port === '' ? null : Number(v.port), targetAppId: v.targetAppId || null,
  tlsCheck: v.tlsCheck || 'auto',
});
const toAppBody = (v) => ({ name: v.name, teamId: v.teamId || null });

// Uygulamaya eklenecek satır (Monitor'ün kendi adresiyle).
const setupSnippet = (key, name) => `// dotnet add package ConnectivityProbe
// ${t('snippet.where')}
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
  const el = e.target.closest('button, [data-select-team]');
  if (!el) return;
  const d = el.dataset;

  try {
    // Birimler
    if (el.id === 'addUnit') {
      openForm(t('form.unitAdd'), [{ name: 'name', label: t('form.unitName'), placeholder: t('form.unitPh') }], {},
        async (v) => { await api('POST', '/api/units', v); await afterChange(); });
    } else if (d.editUnit) {
      const u = unitOf(d.editUnit);
      openForm(t('form.unitEdit'), [{ name: 'name', label: t('form.unitName') }], u,
        async (v) => { await api('PUT', '/api/units/' + u.id, v); await afterChange(); });
    } else if (d.delUnit) {
      const u = unitOf(d.delUnit);
      if (confirm(t('confirm.delUnit', { name: u.name }))) { await api('DELETE', '/api/units/' + u.id); await afterChange(); }

    // Ekipler
    } else if (d.addTeam) {
      openForm(t('form.teamAdd'), [{ name: 'name', label: t('form.teamName'), placeholder: t('form.teamPh') }], {},
        async (v) => { const team = await api('POST', '/api/teams', { name: v.name, unitId: d.addTeam }); selTeam = team.id; save('selTeam', selTeam); await afterChange(); });
    } else if (d.editTeam) {
      const tm = teamOf(d.editTeam);
      openForm(t('form.teamEdit'), [
        { name: 'name', label: t('form.teamName') },
        { name: 'unitId', label: t('form.unit'), type: 'select', options: defs.units.slice().sort(byName).map((u) => ({ value: u.id, label: u.name })) },
      ], tm, async (v) => { await api('PUT', '/api/teams/' + tm.id, v); await afterChange(); });
    } else if (d.delTeam) {
      const tm = teamOf(d.delTeam);
      if (confirm(t('confirm.delTeam', { name: tm.name }))) { await api('DELETE', '/api/teams/' + tm.id); await afterChange(); }
    } else if (d.selectTeam) {
      selTeam = d.selectTeam; save('selTeam', selTeam); renderTree(); renderApps(); wireDragAndDrop(); // havuz ekipten bağımsız

    // Bağlantılar
    } else if (d.addConn !== undefined) {
      openForm(t('form.connAdd'), connFields(), { tlsCheck: 'auto' }, async (v) => { await api('POST', '/api/connections', toConnBody(v)); await afterChange(); });
      watchDuplicate(null);
    } else if (d.editConn) {
      const c = defs.connections.find((x) => x.id === d.editConn);
      openForm(t('form.connEdit'), connFields(), { ...c, port: c.port ?? '' },
        async (v) => { await api('PUT', '/api/connections/' + c.id, toConnBody(v)); await afterChange(); });
      watchDuplicate(c);
    } else if (d.delConn) {
      const c = defs.connections.find((x) => x.id === d.delConn);
      if (confirm(t('confirm.delConn', { name: c.name }))) { await api('DELETE', '/api/connections/' + c.id); await afterChange(); }

    // Uygulamalar (kendini kaydeder; burada yalnızca ad, ekip ve bağlantılar düzenlenir)
    } else if (d.editApp) {
      const a = defs.apps.find((x) => x.id === d.editApp);
      openForm(t('form.appEdit'), appFields(), { ...a, teamId: a.teamId ?? '' },
        async (v) => { await api('PUT', '/api/apps/' + a.id, toAppBody(v)); await afterChange(); });
    } else if (d.copy !== undefined) {
      const ok = await copyText(d.copy);
      el.textContent = ok ? '✓' : '!';
      setTimeout(() => { el.textContent = el.dataset.copy.includes('\n') ? t('copy') : '⧉'; }, 1200);
    } else if (d.delApp) {
      const a = defs.apps.find((x) => x.id === d.delApp);
      if (confirm(t('confirm.delApp', { name: a.name }))) { await api('DELETE', '/api/apps/' + a.id); await afterChange(); }
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
// Karta tıklayınca uygulamanın detay sayfası (sürümler, bağlantı matrisi, pod'lar, geçmiş) yeni sekmede açılır.
// ---------------------------------------------------------------------------------------------
// Detay sayfasındaysak (/?app=<kimlik>) gösterilen uygulama; değilsek null (monitör ekranı).
const detailAppId = new URLSearchParams(location.search).get('app');

// Karşılaştırmaya girmeyen, her bildirimde değişen alanlar (zamanlar ve kaynak ölçümleri; uyarıları alerts taşır).
const SKIP_KEYS = new Set(['lastRunUtc', 'checkedAtUtc', 'lastSeenUtc', 'resources', 'resourceHistory', 'recentMs', 'baselineMs', 'elapsedMs', 'dnsMs']);

async function refreshMonitor() {
  try {
    const next = await api('GET', '/api/monitor');
    // Her yenilemede değişen zaman alanları karşılaştırmaya girmez; yoksa ekran sürekli baştan çizilirdi.
    const json = JSON.stringify(next, (k, v) => (SKIP_KEYS.has(k) ? undefined : v));
    snap = next;
    if (detailAppId) renderDetail(); // detay sayfasında "son bildirim" zamanları her turda güncel kalsın
    // Sonuçlar değişmediyse ekranı yeniden çizmiyoruz (titremeyi ve kayan satırların sıfırlanmasını önler).
    else if (json !== lastSnapJson) {
      lastSnapJson = json;
      renderMonitor();
      if (tab === 'defs') refreshDefsStatus(); // Tanımlar'daki durum noktaları ve uygulama kartları
    }
    renderMeta();
  } catch { /* sunucuya geçici ulaşılamadı, bir sonraki turda tekrar denenecek */ }
}

// Bir uygulamanın bağlantılarının özeti: kaç tanesi sorunlu.
function connSummary(s) {
  const conns = s.connections || [];
  const bad = conns.filter((c) => c.cells.some((x) => x.fresh && !x.success)).length;
  return { total: conns.length, bad };
}

const problemOf = (s) => s.state === 'down' || s.state === 'degraded';
const missingOf = (s) => (s.pods || []).filter((p) => p.state === 'missing').length;
const sortApps = (list) => list.slice().sort((a, b) => (STATE_ORDER[a.state] - STATE_ORDER[b.state]) || a.name.localeCompare(b.name, LANG));

// Kart rengi: uygulama adından türetilen sabit bir ton (her uygulamanın kendi "afişi" olsun, her turda değişmesin).
function hueOf(text) {
  let h = 0;
  for (const ch of String(text)) h = (h * 31 + ch.codePointAt(0)) % 360;
  return h;
}

// Afişteki büyük harfler: "MULTI POD APP" -> "MP", "ORDERS" -> "OR".
function monogram(name) {
  const words = String(name).trim().split(/[\s._-]+/).filter(Boolean);
  return (words.length > 1 ? words[0][0] + words[1][0] : (words[0] || '?').slice(0, 2)).toLocaleUpperCase(LANG);
}

// Arama: uygulama adı, anahtar, ekip, birim, sürüm, ağ ve pod adı içinde (büyük/küçük harf duyarsız).
// Arama motoru gibi alaka sıralı (bkz. search.js): yazım hatası ve eksik Türkçe karakter tolere edilir, en alakalı üstte.
function searchApps(list, q) {
  return Search.rank(q, list.map((s) => {
    const team = teamOf(appTeamId(s.appId));
    const unit = team ? unitOf(team.unitId) : null;
    const pods = s.pods || [];
    return { item: s, fields: [
      { value: s.name, weight: 3 },
      { value: s.appKey, weight: 2 },
      { value: team?.name, weight: 1.5 },
      { value: unit?.name, weight: 1 },
      { value: [...new Set(pods.map((p) => p.appVersion))].join(' '), weight: 1 },
      { value: [...new Set(pods.flatMap((p) => [p.clusterName, p.network, p.primaryAddress]))].join(' '), weight: 1 },
      { value: pods.map(podName).join(' '), weight: 1 },
    ] };
  })).map((r) => r.item);
}

// Satır başlığındaki durum sayaçları: ● sağlıklı ● sorunlu ● erişilemiyor.
function counters(list) {
  const n = (st) => list.filter((s) => s.state === st).length;
  return ['healthy', 'degraded', 'down', 'unknown'].filter((st) => n(st) > 0)
    .map((st) => `<span class="cnt" title="${esc(stateText(st))}"><i class="dot ${st}"></i>${n(st)}</span>`).join('');
}

// "Pod listesini sıfırla" ne zaman gösterilir: kendiliğinden silinmeyen eksik pod varsa.
const needsReset = (s) => missingOf(s) > 0;

const resetButton = (s) => (needsReset(s)
  ? `<button class="nf-btn gray" data-reset-app="${esc(s.appId)}" title="${esc(t('reset.title'))}">${ICON_RESET}${esc(t('reset'))}</button>`
  : '');

// Pod'un bellek kullanımı (container limitine göre %; limit yoksa null).
const memPercent = (p) => {
  const r = p.resources;
  return r?.memoryLimitBytes > 0 && r.memoryBytes != null ? Math.round((r.memoryBytes / r.memoryLimitBytes) * 100) : null;
};
const portPercent = (p) => {
  const r = p.resources;
  return r?.ephemeralPorts > 0 && r.tcpEstablished != null ? Math.round(((r.tcpEstablished + (r.tcpTimeWait || 0)) / r.ephemeralPorts) * 100) : null;
};
// Uygulamanın en erken biten sertifikası (TLS kontrolü yapılan bağlantılarda; gün).
function certDaysOf(s) {
  const ends = (s.connections || []).flatMap((c) => c.cells).filter((x) => x.fresh && x.tls?.notAfterUtc).map((x) => new Date(x.tls.notAfterUtc));
  return ends.length ? Math.floor((Math.min(...ends) - Date.now()) / 86400000) : null;
}
const hasNote = (s, code) => (s.notes || []).some((n) => n.code === code);

// Kartın sol üst şeridi: en önemli sorun (Netflix'in "Yeni bölüm" şeridi gibi).
function flagOf(s) {
  const missing = missingOf(s);
  const { bad } = connSummary(s);
  const pods = (s.pods || []).filter((p) => p.state === 'up');
  if (s.state === 'down') return { cls: '', text: t('flag.down') };
  if (missing) return { cls: '', text: t('flag.missing', { n: missing }) };
  if (pods.some((p) => p.alerts?.includes('oom'))) return { cls: '', text: t('flag.oom') };
  if (pods.some((p) => p.alerts?.includes('memHigh'))) return { cls: '', text: t('flag.memHigh', { n: Math.max(...pods.map((p) => memPercent(p) ?? 0)) }) };
  if (pods.some((p) => p.alerts?.includes('restart'))) return { cls: 'warn', text: t('flag.restart') };
  if (hasNote(s, 'certExpiring')) { const days = certDaysOf(s); return { cls: 'warn', text: days < 0 ? t('flag.certExpired') : t('flag.cert', { n: days }) }; }
  if (pods.some((p) => p.alerts?.includes('portsHigh'))) return { cls: 'warn', text: t('flag.ports', { n: Math.max(...pods.map((p) => portPercent(p) ?? 0)) }) };
  if (bad) return { cls: 'warn', text: t('flag.conn', { n: bad }) };
  const builds = buildsOf(s).length;
  if (builds > 1) return { cls: 'warn', text: t('flag.versions', { n: builds }) };
  if (s.state === 'unknown') return { cls: 'idle', text: t('flag.waiting') };
  return null;
}

// "3 pod", "pod yok"
const podsText = (s) => (s.state === 'down' ? t('pods.none') : t('pods.n', { n: s.podCount }));

function connsText(s) {
  const { total, bad } = connSummary(s);
  if (!total) return `<span class="muted">${esc(t('conns.none'))}</span>`;
  return bad ? `<span class="warn-t">${esc(t('conns.bad', { bad, total }))}</span>` : `<span class="ok">${esc(t('conns.ok', { n: total }))}</span>`;
}

// Kartın sağ üstünde uygulama sürümü; birden fazla sürüm/build çalışıyorsa sarı "N sürüm".
function versionBadge(s) {
  const builds = buildsOf(s);
  if (!builds.length) return '';
  return builds.length === 1
    ? `<span class="tile-ver" title="${esc(t('ver.title'))}">${esc(builds[0])}</span>`
    : `<span class="tile-ver mixed" title="${esc(builds.join(', '))}">${esc(t('ver.n', { n: builds.length }))}</span>`;
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
    <button class="tile ${s.state}" data-open-app="${esc(s.appId)}" style="--h:${hueOf(s.name)}" title="${esc(messageOf(s) || stateText(s.state))}">
      <div class="tile-art"><span class="mono">${esc(monogram(s.name))}</span></div>
      ${flag ? `<span class="flag ${flag.cls}">${esc(flag.text)}</span>` : ''}
      ${versionBadge(s)}
      <div class="tile-body">
        <span class="tile-name">${esc(s.name)}</span>
        <div class="tile-line"><span class="st ${s.state}">${esc(stateText(s.state))}</span><span class="box">${esc(podsText(s))}</span>${connsText(s)}</div>
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
        <button class="row-arrow left" data-scroll="-1" aria-label="${esc(t('scroll.left'))}">‹</button>
        <div class="row-track">${sortApps(list).map(tile).join('')}</div>
        <button class="row-arrow right" data-scroll="1" aria-label="${esc(t('scroll.right'))}">›</button>
      </div>
    </section>`;
}

// Okların görünürlüğü: satırda sola/sağa kaydırılacak kart varsa gösteriyoruz.
function updateArrows(wrap) {
  const tr = wrap.querySelector('.row-track');
  wrap.classList.toggle('can-left', tr.scrollLeft > 4);
  wrap.classList.toggle('can-right', tr.scrollLeft + tr.clientWidth < tr.scrollWidth - 4);
}

// Üstteki öne çıkan alan: en kritik sorunlu uygulama; sorun yoksa "tüm sistemler ayakta".
function renderBillboard(all) {
  const n = (st) => all.filter((s) => s.state === st).length;
  const stats = `
    <div class="bb-stats">
      <div><b>${all.length}</b><span>${esc(t('bb.apps'))}</span></div>
      <div class="ok"><b>${n('healthy')}</b><span>${esc(t('bb.healthy'))}</span></div>
      <div class="warn"><b>${n('degraded')}</b><span>${esc(t('bb.degraded'))}</span></div>
      <div class="bad"><b>${n('down')}</b><span>${esc(t('bb.down'))}</span></div>
    </div>`;

  if (!all.length) {
    $('#billboard').innerHTML = `
      <div class="bb-art" style="--h:355"><span class="mono">CM</span></div>
      <div class="bb-content">
        <div class="bb-kicker"><i class="live"></i>Connectivity Monitor</div>
        <h1 class="bb-title">${esc(t('bb.emptyTitle'))}</h1>
        <p class="bb-desc">${esc(t('bb.emptyDesc'))}</p>
        <div class="bb-actions"><button class="nf-btn white" data-goto-defs>${esc(t('bb.gotoDefs'))}</button></div>
      </div>`;
    return;
  }

  const feat = sortApps(all.filter(problemOf))[0];
  if (!feat) {
    const wait = n('unknown') ? t('bb.waitPart', { n: n('unknown') }) : '';
    $('#billboard').innerHTML = `
      <div class="bb-art" style="--h:140"><span class="mono">OK</span></div>
      <div class="bb-content">
        <div class="bb-kicker"><i class="live ok"></i>${esc(t('bb.live'))}</div>
        <h1 class="bb-title">${esc(t('bb.allUp'))}</h1>
        <p class="bb-desc">${esc(t('bb.allUpDesc', { all: all.length, ok: n('healthy'), wait, n: snap.intervalSeconds }))}</p>
      </div>${stats}`;
    return;
  }

  const team = teamOf(appTeamId(feat.appId));
  const unit = team ? unitOf(team.unitId) : null;
  const message = messageOf(feat);
  $('#billboard').innerHTML = `
    <div class="bb-art ${feat.state}" style="--h:${hueOf(feat.name)}"><span class="mono">${esc(monogram(feat.name))}</span></div>
    <div class="bb-content">
      <div class="bb-kicker"><i class="live"></i>${esc(t('bb.attention'))}${unit ? ' · ' + esc(unit.name) : ''}${team ? ' · ' + esc(team.name) : ''}</div>
      <h1 class="bb-title">${esc(feat.name)}</h1>
      <div class="bb-meta"><span class="st ${feat.state}">${esc(stateText(feat.state))}</span><span class="box">${esc(podsText(feat))}</span>${connsText(feat)}</div>
      ${message ? `<p class="bb-desc">${esc(message)}</p>` : ''}
      <div class="bb-actions">
        <button class="nf-btn white" data-open-app="${esc(feat.appId)}">${ICON_PLAY}${esc(t('bb.details'))}</button>
        ${resetButton(feat)}
      </div>
    </div>${stats}`;
}

const ICON_PLAY = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 4v16a1 1 0 0 0 1.5.86l13-8a1 1 0 0 0 0-1.72l-13-8A1 1 0 0 0 6 4Z"/></svg>';
const ICON_RESET = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4a8 8 0 1 1-7.75 10h2.08A6 6 0 1 0 12 6c-1.66 0-3.15.67-4.24 1.76L10 10H4V4l2.35 2.35A7.97 7.97 0 0 1 12 4Z"/></svg>';

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
    // Arama varsa alaka sırasıyla, yalnızca "sorunlular" seçiliyse önem sırasıyla.
    const pool = all.filter((s) => !onlyProblems || problemOf(s));
    const found = q ? searchApps(pool, q) : sortApps(pool);
    const what = [q ? `"<b>${esc($('#search').value.trim())}</b>"` : '', onlyProblems ? `<b>${esc(t('search.problems'))}</b>` : ''].filter(Boolean).join(' · ');
    list.innerHTML = `<p class="search-head">${t('search.head', { what, n: found.length })}</p>`
      + (found.length ? `<div class="nf-grid">${found.map(tile).join('')}</div>` : `<div class="empty">${esc(t('search.empty'))}</div>`);
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
    if (problems.length) html.push(rowHtml('problems', t('row.problems'), t('row.live'), problems));
    for (const u of defs.units.slice().sort(byName)) {
      const teams = defs.teams.filter((x) => x.unitId === u.id && byTeam.has(x.id)).sort(byName);
      if (!teams.length) continue;
      const count = teams.reduce((n, x) => n + byTeam.get(x.id).length, 0);
      html.push(`<div class="unit-head"><b>${esc(u.name)}</b><span>${esc(t('unit.summary', { teams: teams.length, apps: count }))}</span></div>`);
      teams.forEach((x) => html.push(rowHtml('t:' + x.id, x.name, null, byTeam.get(x.id))));
    }
    if (byTeam.has(UNASSIGNED)) html.push(rowHtml('t:' + UNASSIGNED, t('defs.unassignedApps'), null, byTeam.get(UNASSIGNED)));
    list.innerHTML = html.join('');

    // step 4: Kaydırma konumlarını geri yüklüyoruz (animasyonsuz) ve okları güncelliyoruz.
    list.querySelectorAll('[data-row]').forEach((r) => {
      const tr = r.querySelector('.row-track');
      if (scroll[r.dataset.row]) { tr.style.scrollBehavior = 'auto'; tr.scrollLeft = scroll[r.dataset.row]; tr.style.scrollBehavior = ''; }
      updateArrows(r.querySelector('.row-wrap'));
    });
  }

  // Billboard yokken üst menü sonuçların üstünde saydam kalmasın.
  $('#topbar').classList.toggle('scrolled', list.classList.contains('searching') || window.scrollY > 20);
}

// ---------------------------------------------------------------------------------------------
// Detay sayfası: her uygulama kendi sekmesinde (/?app=<kimlik>); birden fazla uygulama yan yana açık tutulabilir.
// ---------------------------------------------------------------------------------------------
function openDetail(appId) {
  window.open('/?app=' + encodeURIComponent(appId), '_blank');
}

function renderDetail() {
  const s = (snap?.apps || []).find((a) => a.appId === detailAppId);
  if (!s) {
    $('#detailBody').innerHTML = `<div class="empty detail-missing">${esc(t('m.notFound'))} <a href="/">${esc(t('m.back'))}</a></div>`;
    return;
  }
  document.title = s.name + ' · Connectivity';

  const team = teamOf(appTeamId(s.appId));
  const unit = team ? unitOf(team.unitId) : null;
  const { total, bad } = connSummary(s);
  const builds = buildsOf(s);
  const msgClass = s.state === 'down' ? 'bad' : s.state === 'degraded' ? 'warn' : '';
  const message = messageOf(s);
  const hist = s.history || [];
  const maxPods = Math.max(1, ...hist.map((h) => h.podCount));
  const networks = groupByCluster(s.pods || []).map((g) => g.name);

  // Yeniden çizimde tabloların yatay kaydırma konumu korunur (sayfa her turda güncellenir).
  const scrolls = [...document.querySelectorAll('#detailBody .matrix-wrap')].map((w) => w.scrollLeft);

  $('#detailBody').innerHTML = `
    <div class="m-hero">
      <div class="m-art ${s.state}" style="--h:${hueOf(s.name)}"><span class="mono">${esc(monogram(s.name))}</span></div>
      <button class="m-close" data-close-detail title="${esc(t('m.close'))}" aria-label="${esc(t('m.close'))}">✕</button>
      <div class="m-hero-text">
        <div class="m-kicker">${esc([unit?.name, team?.name].filter(Boolean).join(' · ') || t('m.unassigned'))}</div>
        <h2>${esc(s.name)}</h2>
        <div class="m-actions">
          <button class="nf-btn gray" data-run title="${esc(t('runNow.title'))}">${ICON_PLAY}${esc(t('runNow'))}</button>
          ${resetButton(s)}
        </div>
      </div>
    </div>
    <div class="m-body">
      <div class="m-cols">
        <div>
          <div class="m-meta">
            <span class="st ${s.state}">${esc(stateText(s.state))}</span>
            <span class="box">${esc(podsText(s))}</span>
            ${builds.length === 1 ? `<span class="box">${esc(builds[0])}</span>` : builds.length > 1 ? `<span class="warn-t">${esc(t('m.versionsN', { n: builds.length }))}</span>` : ''}
            <span class="muted">${esc(t('m.updated', { t: s.checkedAtUtc ? timeOf(s.checkedAtUtc) : '–' }))}</span>
          </div>
          ${message ? `<p class="m-msg ${msgClass}">${esc(message)}</p>` : ''}
          ${needsReset(s) ? `<p class="msg">${esc(t('reset.help'))}</p>` : ''}
          ${hist.length ? `<div class="m-history">${hist.map((h) => `<i class="${h.state}" style="height:${Math.max(12, (h.podCount / maxPods) * 100)}%"
              title="${esc(`${timeOf(h.atUtc)} · ${stateText(h.state)} · ${t('pods.n', { n: h.podCount })}`)}"></i>`).join('')}</div>
            <div class="m-history-label">${esc(t('m.historyLabel', { n: hist.length }))}</div>` : ''}
        </div>
        <div class="m-side">
          <div><span>${esc(t('m.key'))}: </span><code>${esc(s.appKey)}</code></div>
          <div><span>${esc(t('m.unit'))}: </span>${esc(unit?.name ?? '–')}</div>
          <div><span>${esc(t('m.team'))}: </span>${esc(team?.name ?? t('m.unassigned'))}</div>
          <div><span>${esc(t('m.networks'))}: </span>${esc(networks.join(', ') || '–')}</div>
          <div><span>${esc(t('m.conns'))}: </span>${total}${bad ? ` <span class="warn-t">${esc(t('m.connsBad', { n: bad }))}</span>` : ''}</div>
          <div><span>${esc(t('m.interval'))}: </span>${esc(t('m.intervalVal', { n: snap.intervalSeconds }))}</div>
        </div>
      </div>
      ${versionsHtml(s)}
      ${matrixHtml(s)}
      ${resourcesHtml(s)}
      ${episodesHtml(s)}
    </div>`;
  document.querySelectorAll('#detailBody .matrix-wrap').forEach((w, i) => { if (scrolls[i]) w.scrollLeft = scrolls[i]; });
}

// Ağ (cluster) başlığı: ad, elle ad verildiyse altında ağ adresi ve ad verme düğmesi.
function clusterHead(g, extra) {
  const sub = clusterSub(g.key);
  const canRename = g.key !== '?' && clusterOf(g.key);
  return `<div class="ep-cluster">
      <b>${esc(g.name)}</b>${sub ? `<span class="net">${esc(sub)}</span>` : ''}
      ${canRename ? `<button class="icon-btn" data-rename-cluster="${esc(g.key)}" title="${esc(t('m.rename'))}">✎</button>` : ''}
      ${extra}
    </div>`;
}

// Sürümler: satırlar sürüm/build, sütunlar ağlar (cluster); hücre o ağda o sürümü çalıştıran canlı pod sayısı.
// Uygulamada birden fazla sürüm varsa azınlıktakiler sarı.
function versionsHtml(s) {
  const pods = s.pods || [];
  if (!pods.length) return '';
  const groups = groupByCluster(pods);
  const builds = buildsOf(s);
  const all = [...new Set(pods.map(versionLabel))];
  const main = mainBuildOf(s);
  const count = (list, v) => list.filter((p) => versionLabel(p) === v && p.state === 'up').length;
  const other = (list, v) => list.filter((p) => versionLabel(p) === v && p.state !== 'up').length;

  return `<h3 class="m-sec">${esc(t('m.versions'))}</h3>
    <p class="hint">${esc(t('m.versionsHelp'))}</p>
    <div class="matrix-wrap"><table class="matrix v-table">
      <thead><tr><th>${esc(t('m.version'))}</th>${groups.map((g) => `<th>${esc(g.name)}${clusterSub(g.key) ? `<span class="pid">${esc(clusterSub(g.key))}</span>` : ''}</th>`).join('')}<th>${esc(t('m.total'))}</th></tr></thead>
      <tbody>${all.map((v) => {
        const odd = builds.length > 1 && v !== main;
        const build = pods.find((p) => versionLabel(p) === v);
        return `<tr>
          <td class="rowhead"><span class="ver ${odd ? 'odd' : ''}">${esc(v)}</span>
            ${build?.buildDateUtc ? `<span class="target">${esc(t('kv.build'))} ${esc(dateTimeOf(build.buildDateUtc))}</span>` : ''}</td>
          ${groups.map((g) => {
            const up = count(g.pods, v), rest = other(g.pods, v);
            return up || rest ? `<td class="vcount ${odd ? 'odd' : ''}"><b>${up}</b>${rest ? ` <span class="bad">+${rest}</span>` : ''}</td>` : '<td class="cell none">–</td>';
          }).join('')}
          <td class="vcount"><b>${count(pods, v)}</b></td>
        </tr>`;
      }).join('')}</tbody>
    </table></div>`;
}

// ---------------------------------------------------------------------------------------------
// Kaynaklar: her pod'un CPU, bellek, thread, TCP soket ve ağ kullanımı (+ son ~10 dakikanın küçük grafikleri)
// ---------------------------------------------------------------------------------------------
const fmtBytes = (b) => {
  if (b == null) return '–';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0; let v = b;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return v.toLocaleString(LOCALE(), { maximumFractionDigits: v < 10 && i > 0 ? 1 : 0 }) + ' ' + units[i];
};
const pctText = (n) => (LANG === 'en' ? `${n}%` : `%${n}`);
const fmtNum = (n, digits = 2) => (n == null ? '–' : n.toLocaleString(LOCALE(), { maximumFractionDigits: digits }));

// Küçük çizgi grafik (SVG). max verilirse ölçek ona göre (ör. limit), yoksa en büyük değere göre.
function sparkline(values, max, cls) {
  const v = values.filter((x) => x != null);
  if (v.length < 2) return '';
  const top = Math.max(max || 0, ...v) || 1;
  const w = 90, h = 22;
  const pts = values.map((x, i) => (x == null ? null : `${((i / (values.length - 1)) * w).toFixed(1)},${(h - (x / top) * (h - 2) - 1).toFixed(1)}`)).filter(Boolean).join(' ');
  return `<svg class="spark ${cls || ''}" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true"><polyline points="${pts}"/></svg>`;
}

// Ağ hızı: geçmişteki son iki ölçümün farkından (byte/sn).
function netRate(p) {
  const h = (p.resourceHistory || []).filter((x) => x.netRxBytes != null);
  if (h.length < 2) return null;
  const [a, b] = h.slice(-2);
  const sec = (new Date(b.atUtc) - new Date(a.atUtc)) / 1000;
  return sec > 0 ? { rx: Math.max(0, (b.netRxBytes - a.netRxBytes) / sec), tx: Math.max(0, (b.netTxBytes - a.netTxBytes) / sec) } : null;
}

// Pod uyarı rozetleri (bellek limite yakın, throttling, yeniden başlama, OOM, port tükenmesi).
function alertBadges(p) {
  return (p.alerts || []).map((a) => {
    const text = a === 'memHigh' ? t('alert.memHigh', { n: memPercent(p) })
      : a === 'throttled' ? t('alert.throttled', { n: Math.round(p.resources?.cpuThrottledPercent ?? 0) })
      : a === 'restart' ? t('alert.restart', { t: timeOf(p.lastRestartUtc) })
      : a === 'oom' ? t('alert.oom', { t: timeOf(p.lastOomUtc) })
      : a === 'portsHigh' ? t('alert.portsHigh', { n: portPercent(p) }) : a;
    return `<span class="ep-state ${a === 'throttled' ? 'odd' : 'missing'}">${esc(text)}</span>`;
  }).join('');
}

function resourcesHtml(s) {
  const groups = groupByCluster((s.pods || []).filter((p) => p.state !== 'missing'));
  const pods = groups.flatMap((g) => g.pods);
  if (!pods.length) return '';
  if (!pods.some((p) => p.resources)) return `<h3 class="m-sec">${esc(t('res.title'))}</h3><p class="msg">${esc(t('res.none'))}</p>`;

  const rows = pods.map((p, i) => {
    const r = p.resources;
    const first = i === 0 || pods[i - 1].clusterKey !== p.clusterKey;
    const groupRow = first && groups.length > 1 ? `<tr class="grp-row"><td colspan="8">${esc(clusterLabel(p.clusterKey, p.clusterName))}</td></tr>` : '';
    if (!r) return `${groupRow}<tr><td class="rowhead"><span class="name">${esc(podName(p))}</span></td><td colspan="7" class="muted">${esc(t('res.none'))}</td></tr>`;

    const hist = p.resourceHistory || [];
    const mem = r.memoryBytes ?? r.workingSetBytes;
    const memPct = memPercent(p);
    const ports = portPercent(p);
    const rate = netRate(p);
    const has = (a) => p.alerts?.includes(a);
    const cpuLine = `${esc(t('res.cores', { n: fmtNum(r.cpuCores) }))} <span class="muted">/ ${esc(r.cpuLimitCores ? t('res.limit', { n: fmtNum(r.cpuLimitCores) }) : t('res.noLimit'))}</span>`;
    return `${groupRow}<tr>
      <td class="rowhead"><span class="name">${esc(podName(p))}</span><span class="target">${esc(p.primaryAddress || '')} · ${shortId(p.instanceId)}</span>${alertBadges(p)}</td>
      <td><div class="res-val">${cpuLine}</div>
        ${r.cpuThrottledPercent != null ? `<div class="res-sub ${has('throttled') ? 'warn-t' : ''}">${esc(t('res.throttle', { n: fmtNum(r.cpuThrottledPercent, 1) }))}</div>` : ''}
        ${sparkline(hist.map((h) => h.cpuCores), r.cpuLimitCores, 'cpu')}</td>
      <td><div class="res-val ${has('memHigh') ? 'bad' : ''}">${esc(fmtBytes(mem))}${r.memoryLimitBytes ? ` <span class="muted">/ ${esc(fmtBytes(r.memoryLimitBytes))}</span> <b>${pctText(memPct)}</b>` : ''}</div>
        <div class="res-sub">${esc(t('res.heap', { n: fmtBytes(r.gcHeapBytes) }))} · ${esc(t('res.gc', { a: r.gen0Collections, b: r.gen1Collections, c: r.gen2Collections }))}</div>
        ${sparkline(hist.map((h) => h.memoryBytes), r.memoryLimitBytes, has('memHigh') ? 'bad' : 'mem')}</td>
      <td><div class="res-val">${r.threads}</div><div class="res-sub">${esc(t('res.pool', { busy: r.threadPoolBusy }))}${r.handles != null ? ` · handle ${r.handles}` : ''}</div></td>
      <td>${r.tcpTotal != null ? `<div class="res-val ${has('portsHigh') ? 'bad' : ''}">${r.tcpTotal}</div>
        <div class="res-sub">${esc(t('res.est', { n: r.tcpEstablished }))} · ${esc(t('res.tw', { n: r.tcpTimeWait }))}${ports != null ? ` · ${esc(t('res.ports', { n: ports }))}` : ''}</div>` : '<span class="muted">–</span>'}</td>
      <td>${rate ? `<div class="res-val">↓ ${esc(fmtBytes(rate.rx))}/s</div><div class="res-sub">↑ ${esc(fmtBytes(rate.tx))}/s</div>` : '<span class="muted">–</span>'}</td>
      <td>${p.restarts ? `<span class="${has('restart') ? 'warn-t' : ''}">${esc(t('res.restartsVal', { n: p.restarts, t: dateTimeOf(p.lastRestartUtc) }))}</span>` : '<span class="muted">0</span>'}</td>
      <td class="muted">${r.processorCount} CPU</td>
    </tr>`;
  }).join('');

  return `<h3 class="m-sec">${esc(t('res.title'))}</h3>
    <p class="hint">${esc(t('res.help'))}</p>
    <div class="matrix-wrap"><table class="matrix res-table">
      <thead><tr><th>${esc(t('res.pod'))}</th><th>${esc(t('res.cpu'))}</th><th>${esc(t('res.memory'))}</th><th>${esc(t('res.threads'))}</th>
        <th>${esc(t('res.tcp'))}</th><th>${esc(t('res.net'))}</th><th>${esc(t('res.restarts'))}</th><th></th></tr></thead>
      <tbody>${rows}</tbody></table></div>`;
}

// Pod listesi ağa (cluster) göre gruplanır (Netflix'in bölüm listesi gibi):
//   up          -> bildirim gönderiyor
//   unconfirmed -> bildirimi gecikti (alarm değil)
//   missing     -> MissingAfterCycles test aralığı boyunca bildirim yok (kırmızı, alarm)
function episodesHtml(s) {
  if (!s.pods?.length) return '';
  const builds = buildsOf(s);
  const main = mainBuildOf(s);
  const groups = groupByCluster(s.pods);

  let n = 0;
  return `<h3 class="m-sec">${esc(t('m.pods'))} <span>${esc(t('m.podsSummary', { n: s.pods.length, c: groups.length }))}</span></h3>
    ${groups.map((g) => `
      ${clusterHead(g, `<span>${esc(t('m.podsUp', { up: g.pods.filter((p) => p.state === 'up').length, all: g.pods.length }))}</span>`)}
      <div class="episodes">${g.pods.map((p) => {
        n++;
        const odd = builds.length > 1 && p.state === 'up' && versionLabel(p) !== main;
        const desc = p.state === 'up'
          ? esc((p.addresses || []).join(', ')) + ' · ' + esc(t('pod.lastReport', { t: agoOf(p.lastSeenUtc) }))
          : p.state === 'missing'
            ? esc(t('pod.missingDesc', { d: durationOf(p.lastSeenUtc), t: timeOf(p.lastSeenUtc) }))
            : esc(t('pod.lateDesc', { t: timeOf(p.lastSeenUtc) }));
        const kvs = [
          ['version', t('kv.version'), versionLabel(p)],
          ...(p.primaryAddress ? [['ip', t('kv.ip'), p.primaryAddress]] : []),
          ...(p.buildDateUtc ? [['build', t('kv.build'), dateTimeOf(p.buildDateUtc)]] : []),
          ...(p.namespace ? [['ns', t('kv.namespace'), p.namespace]] : []),
          ['probe', 'ConnectivityProbe', p.probeVersion || '?'],
          ...Object.entries(p.details || {}).filter(([k]) => k !== 'POD_NAME' && k !== 'HOSTNAME').slice(0, 4).map(([k, v]) => [k, k, v]),
        ].map(([id, k, v]) => `<span class="kv ${id === 'version' && odd ? 'odd' : ''}"><b>${esc(k)}</b> ${esc(v)}</span>`).join('');
        return `
          <div class="ep ${p.state}">
            <div class="ep-num">${n}</div>
            <div class="ep-thumb" style="--h:${hueOf(p.instanceId)}">${n}</div>
            <div>
              <div class="ep-title">${esc(podName(p))}<span class="pid">${shortId(p.instanceId)}</span><span class="ep-state ${p.state}">${esc(t('pod.' + p.state))}</span>
                ${odd ? `<span class="ep-state odd">${esc(t('pod.odd'))}</span>` : ''}${alertBadges(p)}</div>
              <div class="ep-desc">${desc}</div>
              <div class="kvs">${kvs}</div>
            </div>
            <div class="ep-time">${p.startedAtUtc ? esc(t('pod.started', { t: dateTimeOf(p.startedAtUtc) })) : ''}</div>
          </div>`;
      }).join('')}</div>`).join('')}`;
}

// Hücredeki ek bilgiler: DNS süresi, TLS sonucu (sertifikanın kalan günü) ve "yavaş" işareti.
function cellExtras(cell) {
  const parts = [];
  if (cell.dnsMs != null) parts.push(`<span class="dns">${esc(t('mx.dns', { n: cell.dnsMs }))}</span>`);
  // Sertifika durumu sade bir cümleyle: "Sertifika 66 gün geçerli", "Sertifika 9 gün sonra bitiyor", "Sertifika geçersiz"...
  // TLS sürümü ipucunda gösterilir; yalnızca eski ve güvensiz sürümlerde (TLS 1.0 / 1.1) hücrede de uyarılır.
  const tls = cell.tls;
  if (tls) {
    const days = tls.notAfterUtc ? Math.floor((new Date(tls.notAfterUtc) - Date.now()) / 86400000) : null;
    const expiring = days != null && days <= (snap?.certificateDays ?? 14);
    const cls = !tls.success ? 'bad' : expiring ? 'warn' : 'ok';
    const label = !tls.handshake ? t('mx.tlsFailed')
      : days != null && days < 0 ? t('mx.certExpired')
      : !tls.success ? t('mx.certInvalid')
      : days == null ? t('mx.certOk')
      : expiring ? t('mx.certExpiring', { n: days })
      : t('mx.certValid', { n: days });
    parts.push(`<span class="tls ${cls}">🔒 ${esc(label)}</span>`);
    if (/^(Tls|Tls11|Ssl\d)$/.test(tls.protocol || '')) parts.push(`<span class="tls warn">${esc(t('mx.tlsOld', { p: tlsVersion(tls.protocol) }))}</span>`);
  }
  if (cell.slow) parts.push(`<span class="slow-tag">${esc(t('mx.slow'))}</span>`);
  return parts.join('');
}

// "Tls13" -> "TLS 1.3", "Tls" -> "TLS 1.0".
const tlsVersion = (p) => (p === 'Tls' ? 'TLS 1.0' : String(p || '').replace(/^Tls(\d)(\d)?$/, (m, a, b) => 'TLS ' + a + '.' + (b || '0')));

// Hücre ipucundaki sertifika ayrıntıları.
function tlsTip(tls) {
  if (!tls) return [];
  return [
    tls.protocol ? t('mx.tlsProto', { p: tlsVersion(tls.protocol) }) : '',
    tls.subject ? t('mx.cert', { subject: tls.subject }) : '',
    tls.issuer ? t('mx.certIssuer', { issuer: tls.issuer }) : '',
    tls.notAfterUtc ? t('mx.certEnd', { t: new Date(tls.notAfterUtc).toLocaleString(LOCALE()) }) : '',
    tls.certificateErrors && tls.certificateErrors !== 'None' ? t('mx.certErrors', { e: tls.certificateErrors }) : '',
    !tls.handshake && tls.error ? 'TLS: ' + tls.error : '',
  ];
}

// Satırlar: atanmış bağlantılar, sütunlar: ağlara (cluster) göre gruplanmış pod'lar. Hücre: o pod'un bağlantıyı kendi
// içinden TCP ile test ettiği son sonuç. Böylece bir hedefe yalnızca bir ağdaki pod'ların erişemediği hemen görülür.
function matrixHtml(s) {
  const def = defs.apps.find((a) => a.id === s.appId);
  const hasConns = (def?.connectionIds?.length ?? 0) > 0;
  if (!hasConns) return `<p class="msg">${esc(t('mx.noConns'))}</p>`;
  if (!s.connections?.length) return s.state === 'down' ? '' : `<p class="msg">${esc(t('mx.waiting'))}</p>`;

  const groups = groupByCluster(s.pods || []);
  const pods = groups.flatMap((g) => g.pods);
  const builds = buildsOf(s);
  const main = mainBuildOf(s);

  // İki satırlı başlık: üstte ağ (cluster) adı, altında o ağın pod'ları (ad, sürüm, kimlik).
  const groupHead = groups.map((g) => `<th class="grp" colspan="${g.pods.length}">${esc(g.name)}${clusterSub(g.key) ? `<span class="pid">${esc(clusterSub(g.key))}</span>` : ''}</th>`).join('');
  const podHead = pods.map((p, i) => {
    const first = i === 0 || pods[i - 1].clusterKey !== p.clusterKey;
    const odd = builds.length > 1 && p.state === 'up' && versionLabel(p) !== main;
    return `<th class="${p.state === 'missing' ? 'missing' : ''} ${first ? 'grp-start' : ''}">${esc(podName(p))}
      <span class="pid"><span class="${odd ? 'ver-odd' : ''}">${esc(versionLabel(p))}</span> · ${shortId(p.instanceId)}${p.state === 'missing' ? ' · ' + esc(t('mx.missing')) : ''}</span></th>`;
  }).join('');

  const rows = s.connections.map((c) => {
    const ips = [...new Set(c.cells.flatMap((x) => x.ipResults.map((r) => r.address)))];
    // Hedef de Monitor'e kayıtlı bir uygulamaysa onun pod sayısı ve durumu.
    const target = c.targetAppId && (snap?.apps || []).find((a) => a.appId === c.targetAppId);
    const targetInfo = target
      ? `<span class="target-app"><i class="dot ${target.state}"></i>${esc(t('mx.target', { name: target.name, n: target.podCount, state: stateText(target.state) }))}</span>` : '';
    // Son bir saat içinde adın çözüldüğü IP'ler değiştiyse (DNS kaydı, failover...) satırda gösterilir.
    const changed = c.cells.filter((x) => x.addressesChangedUtc && Date.now() - new Date(x.addressesChangedUtc) < 3600000)
      .sort((a, b) => new Date(b.addressesChangedUtc) - new Date(a.addressesChangedUtc))[0];
    const ipChange = changed ? `<span class="ip-change">${esc(t('mx.ipChanged', { t: timeOf(changed.addressesChangedUtc),
      from: (changed.previousAddresses || []).join(', '), to: changed.ipResults.map((r) => r.address).join(', ') }))}</span>` : '';
    const rowHead = `<td class="rowhead"><span class="name">${esc(c.name)}${c.tls ? ' <span class="tls-tag" title="TLS">🔒</span>' : ''}</span><span class="target">${esc(c.target)}</span>
      ${ips.length ? `<span class="ips">${esc(t('mx.ip', { ips: ips.join(', ') }))}</span>` : ''}${ipChange}${targetInfo}</td>`;

    const cells = pods.map((p, i) => {
      const first = i === 0 || pods[i - 1].clusterKey !== p.clusterKey ? ' grp-start' : '';
      const cell = c.cells.find((x) => x.instanceId === p.instanceId);

      // Pod eksikse eski başarılı sonucu yeşil göstermek yanıltıcı olur: pod çalışmıyor, bu bağlantıyı da kullanamıyor.
      if (p.state === 'missing') {
        const lastKnown = cell
          ? t('mx.lastKnown', { t: timeOf(cell.checkedAtUtc), r: cell.success ? t('mx.okMs', { n: cell.elapsedMs }) : (cell.error || t('mx.failed')) })
          : t('mx.noPrev');
        return `<td class="cell fail${first}" title="${esc(t('mx.podSilent', { d: durationOf(p.lastSeenUtc), t: timeOf(p.lastSeenUtc) }) + '\n' + lastKnown)}">`
          + `✗<span class="reach">${esc(t('mx.podSilentShort'))}</span><span class="since">${esc(durationOf(p.lastSeenUtc))}</span></td>`;
      }

      if (!cell) return `<td class="cell none${first}" title="${esc(t('mx.noResult'))}">–</td>`;
      const tip = [
        cell.fresh ? '' : t('mx.stale', { t: timeOf(cell.checkedAtUtc) }),
        cell.reachedAddress ? t('mx.reached', { ip: cell.reachedAddress }) : '',
        ...cell.ipResults.map((r) => `${r.address}: ${r.success ? 'ok ' + r.elapsedMs + ' ms' : r.error}`),
        cell.error ? t('mx.error', { e: cell.error }) : '',
        !cell.success && cell.failingSinceUtc ? t('mx.failingSince', { t: dateTimeOf(cell.failingSinceUtc) }) : '',
        !cell.success ? (cell.lastSuccessUtc ? t('mx.lastOk', { t: dateTimeOf(cell.lastSuccessUtc) }) : t('mx.neverOk')) : '',
        cell.dnsMs != null ? t('mx.dns', { n: cell.dnsMs }) : '',
        ...tlsTip(cell.tls),
        cell.slow ? t('mx.slowTip', { n: cell.baselineMs }) : '',
      ].filter(Boolean).join('\n');
      const extras = cellExtras(cell);

      if (!cell.success) {
        return `<td class="cell fail ${cell.fresh ? '' : 'stale'}${first}" title="${esc(tip)}">✗<span class="reach">${esc(cell.error || t('mx.failed'))}</span>`
          + (cell.failingSinceUtc ? `<span class="since">${esc(t('mx.unreachableFor', { d: durationOf(cell.failingSinceUtc) }))}</span>` : '') + extras + '</td>';
      }
      return `<td class="cell ok ${cell.fresh ? '' : 'stale'}${cell.slow ? ' slow' : ''}${first}" title="${esc(tip)}">✓<span class="ms">${cell.elapsedMs} ms</span>`
        + `${cell.reachedAddress ? `<span class="reach">${esc(cell.reachedAddress)}</span>` : ''}${extras}</td>`;
    }).join('');

    return `<tr>${rowHead}${cells}</tr>`;
  }).join('');

  return `<h3 class="m-sec">${esc(t('mx.title'))}</h3>
    <div class="matrix-wrap"><table class="matrix">
      <thead><tr><th rowspan="2">${esc(t('mx.conn'))}</th>${groupHead}</tr><tr>${podHead}</tr></thead>
      <tbody>${rows}</tbody></table></div>`;
}

// Ağa elle ad verme (ör. "Prod İstanbul"); boş bırakılırsa pod ağı (ör. 10.42.0.0/16) gösterilir.
function renameCluster(key) {
  const c = clusterOf(key);
  if (!c) return;
  openForm(t('form.clusterRename'), [{ name: 'name', label: t('form.clusterName'), placeholder: t('form.clusterPh'),
    hint: t('form.clusterHint', { net: c.networks?.join(', ') || key }) }], { name: c.custom ? c.name : '' },
  async (v) => { await api('PUT', '/api/clusters/' + encodeURIComponent(key), { name: v.name }); lastSnapJson = ''; await refreshMonitor(); });
}

// ---------------------------------------------------------------------------------------------
// Başlangıç ve olaylar
// ---------------------------------------------------------------------------------------------
// Detay sayfasında sekmeler monitör sayfasına götürür (seçilen sekmeyle).
document.querySelectorAll('.tab').forEach((b) => b.addEventListener('click', () => {
  if (detailAppId) { save('tab', b.dataset.tab); location.href = '/'; } else setTab(b.dataset.tab);
}));
document.querySelectorAll('[data-lang]').forEach((b) => b.addEventListener('click', () => changeLang(b.dataset.lang)));

// Testi hemen başlatır (detay sayfasındaki düğme). Pod'lar zaten her test aralığında kendiliğinden test eder; bu yalnızca
// bir düzeltmeden (firewall, sertifika...) hemen sonra sonucu beklemeden görmek içindir.
async function runNow() {
  const buttons = document.querySelectorAll('[data-run]');
  buttons.forEach((b) => { b.disabled = true; });
  try { await api('POST', '/api/monitor/run'); } catch (err) { alert(err.message); }
  setTimeout(() => document.querySelectorAll('[data-run]').forEach((b) => { b.disabled = false; }), 1500);
  setTimeout(refreshMonitor, 1000);
}

// Monitör ekranı ve detay sayfasındaki tüm tıklamalar.
document.addEventListener('click', async (e) => {
  const el = e.target.closest('[data-open-app], [data-run], [data-reset-app], [data-close-detail], [data-scroll], [data-goto-defs], [data-rename-cluster]');
  if (!el) return;
  const d = el.dataset;

  if (d.openApp) openDetail(d.openApp);
  else if (d.run !== undefined) runNow();
  else if (d.closeDetail !== undefined) {
    // Kart tarafından açılan sekme kapatılabilir; adres doğrudan açıldıysa tarayıcı izin vermez, monitöre dönülür.
    window.close();
    setTimeout(() => { location.href = '/'; }, 150);
  }
  else if (d.gotoDefs !== undefined) setTab('defs');
  else if (d.renameCluster) renameCluster(d.renameCluster);
  else if (d.scroll) {
    const tr = el.parentElement.querySelector('.row-track');
    tr.scrollBy({ left: Number(d.scroll) * tr.clientWidth * 0.85 });
  } else if (d.resetApp) {
    // "Pod listesini sıfırla": Monitor'ün bu uygulama için hatırladığı pod'lar (eksikler dahil) silinir, mevcutlar yeniden keşfedilir.
    const app = (snap?.apps || []).find((a) => a.appId === d.resetApp);
    if (!confirm(t('confirm.reset', { name: app?.name }))) return;
    el.disabled = true;
    try { await api('POST', `/api/apps/${d.resetApp}/reset`); } catch (err) { alert(err.message); }
    lastSnapJson = '';
    setTimeout(refreshMonitor, 1500);
  }
});

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
  applyI18n();
  // Giriş zorunluysa (Monitor:AdminPassword tanımlı) çıkış düğmesini gösteriyoruz.
  try { const me = await api('GET', '/api/auth/me'); $('#logout').hidden = !me.loginRequired; } catch { /* yönlendirildi */ }
  if (detailAppId) {
    // Detay sayfası: yalnızca uygulamanın ayrıntıları; arama ve filtre burada anlamsız.
    document.body.classList.add('detail-mode');
    $('#tab-monitor').hidden = true;
    $('#tab-defs').hidden = true;
    $('#tab-detail').hidden = false;
    $('#navTools').hidden = true;
  } else {
    setTab(load('tab', 'monitor'));
  }
  await loadDefs();
  await refreshMonitor();
  setInterval(refreshMonitor, 3000);   // sonuçları 3 saniyede bir sunucudan okuyoruz
  setInterval(renderMeta, 1000);       // üst bilgiyi her saniye güncelliyoruz
})();
