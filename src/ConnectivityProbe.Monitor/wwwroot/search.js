'use strict';

// ---------------------------------------------------------------------------------------------
// Alaka sıralı arama (arama motoru gibi): yalnızca birebir eşleşeni değil, en alakalı sonuçları üstte gösterir.
//   - Büyük/küçük harf ve Türkçe karakter duyarsız (ı=i, ş=s, ğ=g, ü=u, ö=o, ç=c).
//   - Metin kelimelere bölünür; "ELMT1EntSQL_RO" -> elmt, 1, ent, sql, ro (ve bütün hali).
//   - Her sorgu kelimesi için en iyi eşleşme puanlanır: birebir > baş tarafı > içinde geçiyor > yazım hatası (1-2 harf)
//     > harfler sırayla geçiyor. Alanların ağırlığı farklıdır (ad > host > port > uygulamalar).
//   - Tüm sorgu kelimelerini karşılayan sonuçlar, bir kısmını karşılayanlardan önce gelir.
// ---------------------------------------------------------------------------------------------
const Search = (() => {
  const TR = { ı: 'i', İ: 'i', ş: 's', Ş: 's', ğ: 'g', Ğ: 'g', ü: 'u', Ü: 'u', ö: 'o', Ö: 'o', ç: 'c', Ç: 'c' };

  // Karşılaştırma için sadeleştirir: Türkçe harfler ve aksanlar düz harfe, küçük harfe.
  function normalize(s) {
    return String(s ?? '').replace(/[ıİşŞğĞüÜöÖçÇ]/g, (c) => TR[c]).normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
  }

  // Kelimelere böler: ayraçlar (boşluk . _ - : /), küçük->büyük harf ve harf<->rakam geçişleri.
  function tokens(s) {
    const raw = String(s ?? '');
    const parts = raw
      .replace(/([a-zçğıöşü])([A-ZÇĞİÖŞÜ])/g, '$1 $2')
      .replace(/([A-ZÇĞİÖŞÜ]+)([A-ZÇĞİÖŞÜ][a-zçğıöşü])/g, '$1 $2')
      .replace(/([a-zA-ZçğıöşüÇĞİÖŞÜ])(\d)/g, '$1 $2')
      .replace(/(\d)([a-zA-ZçğıöşüÇĞİÖŞÜ])/g, '$1 $2')
      .split(/[^a-zA-Z0-9çğıöşüÇĞİÖŞÜ]+/);
    const words = raw.split(/[^a-zA-Z0-9çğıöşüÇĞİÖŞÜ]+/);
    return [...new Set([...parts, ...words].map(normalize).filter(Boolean))];
  }

  // İki kelime arasındaki düzenleme uzaklığı (harf ekleme/silme/değiştirme ve yan yana iki harfin yer değiştirmesi);
  // max'ı aşınca erken biter.
  function distance(a, b, max) {
    if (Math.abs(a.length - b.length) > max) return max + 1;
    const d = Array.from({ length: a.length + 1 }, (_, i) => [i]);
    for (let j = 1; j <= b.length; j++) d[0][j] = j;
    for (let i = 1; i <= a.length; i++) {
      let rowMin = Infinity;
      for (let j = 1; j <= b.length; j++) {
        const cost = a[i - 1] === b[j - 1] ? 0 : 1;
        d[i][j] = Math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + cost);
        if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) d[i][j] = Math.min(d[i][j], d[i - 2][j - 2] + 1);
        rowMin = Math.min(rowMin, d[i][j]);
      }
      if (rowMin > max) return max + 1;
    }
    return d[a.length][b.length];
  }

  // Harfler sırayla geçiyor mu ("elmsql" -> "elmt1entsql").
  function subsequence(q, text) {
    let i = 0;
    for (const c of text) if (c === q[i] && ++i === q.length) return true;
    return false;
  }

  // Bir sorgu kelimesinin bir alandaki en iyi puanı (0 = eşleşme yok).
  function scoreWord(q, field) {
    if (field.text.includes(q)) {
      if (field.words.includes(q)) return 10;                       // birebir kelime
      if (field.words.some((w) => w.startsWith(q))) return 7;       // kelimenin başı
      return 5;                                                     // metnin içinde
    }
    if (q.length >= 4) {
      const max = q.length >= 7 ? 2 : 1;                            // yazım hatası toleransı
      let best = max + 1;
      for (const w of field.words) {
        best = Math.min(best, distance(q, w, max));
        if (w.length > q.length) best = Math.min(best, distance(q, w.slice(0, q.length), max)); // yazım hatalı baş taraf
      }
      if (best <= max) return 4 - best;
    }
    if (q.length >= 3 && subsequence(q, field.text)) return 1.5;
    return 0;
  }

  // Aranacak kayıt: { item, fields: [{ value, weight, exact? }] }. exact: yalnızca birebir eşleşme (ör. port).
  function prepare(fields) {
    return fields.filter((f) => f.value != null && f.value !== '').map((f) => ({
      ...f, text: normalize(f.value).replace(/\s+/g, ' '), words: tokens(f.value),
    }));
  }

  /**
   * Kayıtları sorguya göre puanlar ve en alakalıdan başlayarak döner: [{ item, score, matched }].
   * entries: [{ item, fields: [{ value, weight, exact }] }]
   */
  function rank(query, entries) {
    const qWords = tokens(query);
    const qText = normalize(query).trim();
    if (!qWords.length) return entries.map((e) => ({ item: e.item, score: 0, matched: 0 }));
    const numeric = qWords.every((w) => /^\d+$/.test(w));

    const results = [];
    for (const e of entries) {
      const fields = e._prepared || (e._prepared = prepare(e.fields));
      let score = 0;
      let matched = 0;
      for (const q of qWords) {
        let best = 0;
        for (const f of fields) {
          const s = f.exact ? (f.words.includes(q) ? 10 : 0) : scoreWord(q, f);
          best = Math.max(best, s * f.weight);
        }
        if (best > 0) matched++;
        score += best;
      }
      // Sorgunun tamamı bir alanda geçiyorsa (ör. "ana veritabanı") ek puan; ad ile birebir aynıysa daha fazla.
      for (const f of fields) {
        if (qText.length >= 2 && f.text === qText) score += 15 * f.weight;
        else if (qText.length >= 2 && qWords.length > 1 && f.text.includes(qText)) score += 6 * f.weight;
      }
      // Sorgu kelimelerinin en az yarısını karşılamayan sonuçlar gösterilmez. Yalnızca sayılardan oluşan sorgularda (IP, port,
      // "10.43") her parça eşleşmeli; yoksa "10.43" aranınca 10.80... adresli her şey de çıkardı.
      const needed = numeric ? qWords.length : Math.ceil(qWords.length / 2);
      if (matched > 0 && matched >= needed) results.push({ item: e.item, score, matched });
    }
    results.sort((a, b) => (b.matched - a.matched) || (b.score - a.score));
    // En iyi sonucun beşte birinden zayıf, tesadüfi eşleşmeleri (ör. harflerin dağınık geçmesi) gösterme.
    const top = results.length ? results[0].score : 0;
    return results.filter((r) => r.score >= top * 0.2);
  }

  // Metinde sorgu kelimelerinin geçtiği yerleri işaretler (HTML; metin önceden kaçırılmaz, burada kaçırılır).
  function highlight(text, query, escape) {
    const raw = String(text ?? '');
    const qWords = tokens(query).filter((w) => w.length >= 2);
    if (!qWords.length) return escape(raw);
    const norm = normalize(raw);  // normalize harf sayısını korur (Türkçe harfler tek harfe, aksanlar silinir)
    const marks = new Array(raw.length).fill(false);
    if (norm.length === raw.length) {
      for (const q of qWords) {
        let i = norm.indexOf(q);
        while (i >= 0) { for (let k = i; k < i + q.length; k++) marks[k] = true; i = norm.indexOf(q, i + 1); }
      }
    }
    let out = '';
    let open = false;
    for (let i = 0; i < raw.length; i++) {
      if (marks[i] && !open) { out += '<mark>'; open = true; }
      if (!marks[i] && open) { out += '</mark>'; open = false; }
      out += escape(raw[i]);
    }
    return out + (open ? '</mark>' : '');
  }

  return { normalize, tokens, distance, rank, highlight };
})();

if (typeof module !== 'undefined') module.exports = Search;
