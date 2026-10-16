'use strict';
/* جهازي: التحكم بلابتوب ويندوز من الجوال. الأوامر والردود مشفّرة (AES-GCM) بمفتاح مشتق من السرّ الرئيسي؛ الخادم يمرّر بايتات معتمة فقط.
   الوكيل (تطبيق ويندوز) يتحقق من الأمر ويطلب PIN للإجراءات. */
(function () {
  const { S, h, $, toast, BASE } = window.CP;
  const enc = new TextEncoder(), dec = new TextDecoder();
  const b64 = (u8) => { let s = ''; for (const b of u8) s += String.fromCharCode(b); return btoa(s); };
  const unb64 = (s) => Uint8Array.from(atob(s), c => c.charCodeAt(0));
  const rid = () => Array.from(crypto.getRandomValues(new Uint8Array(12)), b => b.toString(16).padStart(2, '0')).join('');
  const fmtSize = (b) => b < 1073741824 ? (b / 1048576).toFixed(0) + ' MB' : (b / 1073741824).toFixed(1) + ' GB';
  const fmtUp = (s) => s >= 86400 ? Math.floor(s / 86400) + ' يوم' : s >= 3600 ? Math.floor(s / 3600) + ' ساعة' : Math.floor(s / 60) + ' دقيقة';

  let keyFor = null, aes = null;
  async function key() {
    if (aes && keyFor === S.key) return aes;
    const k = await crypto.subtle.importKey('raw', S.key, 'HKDF', false, ['deriveBits']);
    const bits = await crypto.subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(0), info: enc.encode('control-panel/agent/v1') }, k, 256);
    aes = await crypto.subtle.importKey('raw', bits, 'AES-GCM', false, ['encrypt', 'decrypt']);
    keyFor = S.key;
    return aes;
  }

  async function devices() {
    const r = await fetch(BASE + 'api/agent/devices', { headers: { Authorization: 'Bearer ' + S.token }, cache: 'no-store' });
    if (!r.ok) throw new Error('تعذّر قراءة الأجهزة (' + r.status + ')');
    return (await r.json()).devices;
  }

  /** يرسل أمراً مشفّراً ويعيد data، أو يرمي خطأ بمعناه. */
  async function call(device, op, args, pin) {
    const id = rid(), k = await key();
    const cmd = { op, args: args || {}, ts: Math.floor(Date.now() / 1000), pin: pin || '' };
    const nonce = crypto.getRandomValues(new Uint8Array(12));
    const ct = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: enc.encode('cmd:' + id), tagLength: 128 }, k, enc.encode(JSON.stringify(cmd))));
    const body = new Uint8Array(12 + ct.length); body.set(nonce, 0); body.set(ct, 12);
    const r = await fetch(BASE + 'api/agent/cmd', {
      method: 'POST', headers: { Authorization: 'Bearer ' + S.token, 'Content-Type': 'application/json' },
      body: JSON.stringify({ device, id, ct: b64(body) }),
    });
    if (r.status === 423) throw Object.assign(new Error('التحكم بالجهاز موقوف.'), { paused: true, offline: true });
    if (r.status === 404) throw Object.assign(new Error('الجهاز غير متصل بالخادم'), { offline: true });
    if (r.status === 504) throw new Error('الجهاز لم يردّ في الوقت المحدد (مشغول أو التطبيق مقفل)');
    if (!r.ok) throw new Error('رفض الخادم (' + r.status + ')');
    const raw = unb64((await r.json()).ct);
    let plain;
    try { plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: raw.slice(0, 12), additionalData: enc.encode('res:' + id), tagLength: 128 }, k, raw.slice(12)); }
    catch { throw new Error('ردّ غير موثوق (فشل التحقق)'); }
    const res = JSON.parse(dec.decode(plain));
    if (!res.ok) throw new Error(res.error || 'فشل الأمر');
    return res.data;
  }

  /** ملخص خفيف للرئيسية: أول جهاز متصل + حالته. */
  async function summary() {
    const ds = await devices();
    if (!ds.length) return { online: false };
    const d = ds[0];
    try {
      const s = await call(d.id, 'status');
      const used = s.ramTotal - s.ramAvailable;
      return { online: true, id: d.id, name: d.label || s.machine, cpu: s.cpu, ramPct: 100 * used / s.ramTotal, ramText: fmtSize(used) + ' / ' + fmtSize(s.ramTotal), advice: s.advice || [] };
    } catch (e) { return { online: false, error: e.message }; }
  }

  // ===================================================== الواجهة
  const D = { live: false, liveQ: 'bal', liveErr: '', liveInfo: '', unlockMsg: '', dev: '', devs: null, status: null, procs: null, conns: null, shot: null, out: '', pin: '', pinAt: 0, timer: null, gen: 0, busy: false, err: '', shellCmd: '', delay: 30, notice: '', o: {} };
  const fold = (key, ...kids) => { const d = h('details', { class: 'fold' }, ...kids); if (D.o[key]) d.open = true; d.addEventListener('toggle', () => { D.o[key] = d.open; }); return d; };

  /** المستخدم يكتب في حقل (PIN/نص): لا نُعيد رسم الصفحة وإلا ضاع التركيز وأُغلقت لوحة المفاتيح. */
  const typing = () => { const a = document.activeElement; return !!a && /^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName) && a.type !== 'checkbox' && a.type !== 'radio' && !!a.closest('#view'); };

  function stopTimer() { clearInterval(D.timer); D.timer = null; }

  // ---------- المعاينة الحية: إطار ← انتظار ← إطار. الإطار المطابق لا يُرسَل (since=hash)
  const LIVE = { fast: { width: 640, q: 35, ms: 350 }, bal: { width: 960, q: 50, ms: 500 }, good: { width: 1280, q: 65, ms: 800 }, hd: { width: 1920, q: 70, ms: 1000 } };
  const liveImg = h('img', { class: 'shot', alt: 'معاينة حية', style: 'width:100%;height:auto;cursor:zoom-in' });
  liveImg.addEventListener('click', () => {
    if (window.CPDeviceExtra && CPDeviceExtra.active()) return;   // أثناء التحكم باللمس: اللمسة نقرة على الجهاز لا ملء شاشة
    try { liveImg.requestFullscreen && liveImg.requestFullscreen(); } catch { }
  });
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  const redraw = () => { CPX._state.force = true; CP.render(); };
  async function liveLoop() {
    if (D.live) return;
    const pin0 = pinVal(); if (!pin0) { (toast('أدخل رمز PIN'), pinPad()); return; }
    D.live = true; D.liveErr = ''; let since = '', frames = 0; const t0 = Date.now(), cfg = LIVE[D.liveQ];
    redraw();
    while (D.live && S.key && S.group === 'device') {
      if (document.hidden) { await sleep(800); continue; }
      const p = pinVal(); if (!p) { D.liveErr = 'انتهت صلاحية PIN (5 دقائق) — اكتبه وأعد التشغيل'; break; }
      const t1 = performance.now();
      try {
        const r = await call(D.dev, 'screenshot', { width: cfg.width, q: cfg.q, since }, p);
        since = r.h || since;
        if (!r.same && r.jpeg) { liveImg.src = 'data:image/jpeg;base64,' + r.jpeg; frames++; }
        D.liveInfo = (r.locked ? '🔒 الشاشة مقفلة · ' : '') + frames + ' إطار · ' + Math.round(performance.now() - t1) + 'ms';
        const el = document.getElementById('liveInfo'); if (el) el.textContent = D.liveInfo;
      } catch (e) { D.liveErr = e.message; break; }
      if (Date.now() - t0 > 10 * 60 * 1000) { D.liveErr = 'توقّفت المعاينة بعد 10 دقائق (حفاظاً على الجهاز) — شغّلها من جديد'; break; }
      await sleep(cfg.ms);
    }
    D.live = false; redraw();
  }

  async function doUnlock(secret) {
    const pin = pinVal(); if (!pin) { (toast('أدخل رمز PIN'), pinPad()); return; }
    D.unlockMsg = '… أفتح القفل (قد يستغرق ثوانيَ)'; redraw();
    try {
      const r = await call(D.dev, 'unlock', { secret }, pin);
      D.unlockMsg = (r.unlocked ? '🔓 ' : '⚠ ') + (r.done || '');
      if (r.jpeg) D.shot = 'data:image/jpeg;base64,' + r.jpeg;
    } catch (e) { D.unlockMsg = '⚠ ' + e.message; }
    toast(D.unlockMsg); reload();
  }

  async function reload(quiet) {
    if (D.busy) return;
    D.busy = true; const gen = ++D.gen;
    try {
      D.devs = await devices();
      if (!D.devs.length) { D.status = null; D.err = ''; return; }
      if (!D.devs.find(d => d.id === D.dev)) D.dev = D.devs[0].id;
      D.status = await call(D.dev, 'status');
      D.err = '';
    } catch (e) { D.err = e.message; if (e.offline) D.status = null; }
    finally { D.busy = false; if (S.group === 'device' && S.key && gen === D.gen && !(quiet && typing())) { CPX._state.force = true; CP.render(); } }
  }

  /** نافذة أرقام: تكتب الـPIN بأزرار الشاشة نفسها دون لوحة مفاتيح النظام. */
  function pinPad(done) {
    if (document.getElementById('pinPad')) return;
    let v = '';
    const dots = h('div', { style: 'font-size:28px;letter-spacing:10px;text-align:center;min-height:42px;direction:ltr' }, '');
    const upd = () => { dots.textContent = '●'.repeat(v.length) || ' '; };
    const close = () => ov.remove();
    const key = (t, fn) => h('button', { type: 'button', style: 'height:56px;font-size:22px;justify-content:center', onclick: fn }, t);
    const grid = h('div', { style: 'display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin-top:12px;direction:ltr' },
      ...['1', '2', '3', '4', '5', '6', '7', '8', '9'].map(n => key(n, () => { if (v.length < 12) { v += n; upd(); } })),
      key('⌫', () => { v = v.slice(0, -1); upd(); }),
      key('0', () => { if (v.length < 12) { v += '0'; upd(); } }),
      key('✓', () => { D.pin = v; D.pinAt = Date.now(); close(); if (done) done(); else redraw(); }));
    const ov = h('div', { id: 'pinPad', style: 'position:fixed;inset:0;z-index:9999;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;padding:16px' },
      h('div', { style: 'background:var(--card);color:var(--ink);border:1px solid var(--line);border-radius:18px;padding:18px;width:min(320px,100%)' },
        h('h3', { style: 'text-align:center;margin:0 0 6px' }, 'رمز PIN'), dots, grid,
        h('button', { type: 'button', class: 'ghost', style: 'width:100%;margin-top:10px;justify-content:center', onclick: close }, 'إلغاء')));
    document.body.append(ov);
  }

  const pinVal = () => { if (Date.now() - D.pinAt > 5 * 60 * 1000) D.pin = ''; return D.pin; };

  async function act(op, args, label, confirmMsg) {
    if (confirmMsg && !confirm(confirmMsg)) return;
    const pin = pinVal();
    if (!pin) { (toast('أدخل رمز PIN'), pinPad()); return; }
    try { const r = await call(D.dev, op, args, pin); D.notice = '✓ ' + (r && r.done ? r.done : label); toast(D.notice); }
    catch (e) { D.notice = '⚠ ' + e.message; toast(D.notice); }
    CPX._state.force = true; CP.render();
  }

  function meter(name, pct, text) {
    return h('div', {}, h('div', { class: 'xhead', style: 'margin:0' }, h('b', { style: 'flex:1' }, name), h('small', { class: 'hint' }, text)),
      h('div', { class: 'meter' }, h('i', { class: pct >= 90 ? 'bad' : pct >= 75 ? 'warn' : '', style: 'width:' + Math.min(100, Math.max(1, pct)) + '%' })));
  }
  const tile = (icon, value, lbl, cls) => h('div', { class: 'stat' }, h('span', { class: 'ico ' + (cls || '') }, CPIcon(icon, 22)), h('div', {}, h('b', {}, value), h('span', {}, lbl)));

  function view(box) {
    if (!D.timer) D.timer = setInterval(() => { if (S.group === 'device' && S.key && !document.hidden && !D.live && !typing()) reload(true); }, 8000);
    if (D.devs === null && !D.busy) reload();

    box.append(h('div', { class: 'xhead' }, h('h2', {}, 'جهازي'),
      D.devs && D.devs.length > 1 ? h('select', { style: 'width:auto', onchange: (e) => { D.dev = e.target.value; D.status = null; reload(); } },
        ...D.devs.map(d => h('option', { value: d.id, selected: d.id === D.dev }, d.label || d.id))) : null,
      h('button', { class: 'ghost', onclick: () => reload() }, CPIcon('refresh', 18), 'تحديث')));

    if (D.devs === null) { box.append(h('p', { class: 'hint' }, '… أبحث عن جهازك')); return; }
    if (!D.devs.length || !D.status) {
      box.append(h('div', { class: 'xcard' }, h('h3', {}, /موقوف/.test(D.err || '') ? 'التحكم موقوف' : 'الجهاز غير متصل'),
        h('p', { class: 'hint' }, D.err || 'لا جهاز متصل بالخادم الآن.'),
        h('details', { class: 'fold', open: true }, h('summary', {}, 'كيف أوصله؟'),
          h('div', {}, h('p', { class: 'hint', style: 'white-space:pre-wrap' },
            '1) افتح تطبيق ويندوز على لابتوبك وافتح الخزنة.\n' +
            '2) من القائمة: «التحكم من الجوال» ← فعّل التحكم واضبط رمز PIN.\n' +
            '3) يتصل الجهاز «للخارج» بخادمك فيظهر هنا تلقائياً — بلا فتح منافذ ولا IP عام.\n\n' +
            'لا يمكن الوصول إليه إن كان مطفأً أو في سكون عميق أو التطبيق مغلقاً (الإيقاظ عن بُعد يحتاج Wake-on-LAN من شبكتك المحلية).')))));
      return;
    }

    const s = D.status, used = s.ramTotal - s.ramAvailable, ramPct = 100 * used / s.ramTotal;
    const dv = D.devs.find(d => d.id === D.dev) || {};
    box.append(h('div', { class: 'chips' },
      h('span', { class: 'chip ok' }, CPIcon('check', 14), 'متصل'),
      s.locked ? h('span', { class: 'chip warn' }, CPIcon('lock', 14), 'الشاشة مقفلة') : null,
      h('span', { class: 'chip' }, (dv.label || s.machine) + ' · ' + s.user),
      h('span', { class: 'chip' }, s.os),
      h('span', { class: 'chip' }, 'التطبيق ' + s.version),
      h('span', { class: 'chip' }, 'يعمل منذ ' + fmtUp(s.uptimeSec)),
      s.battery != null ? h('span', { class: 'chip ' + (s.battery <= 20 && !s.ac ? 'bad' : '') }, '🔋 ' + s.battery + '%' + (s.ac ? ' ⚡' : '')) : null,
      h('span', { class: 'chip' }, s.deviceClass === 'Light' ? 'جهاز خفيف' : s.deviceClass === 'Strong' ? 'جهاز قوي' : 'جهاز متوسط')));

    // تنبيهات
    if ((s.advice || []).length) {
      const a = h('div', { class: 'xcard', style: 'margin-bottom:14px' }, h('h3', {}, 'يحتاج انتباهك'));
      for (const x of s.advice) a.append(h('div', { class: 'alertrow' }, h('span', { class: 'ico ' + x.sev }, CPIcon('alert', 18)), h('div', { class: 'grow' }, h('b', {}, x.title), h('small', {}, x.detail))));
      box.append(a);
    }

    // الموارد
    const res = h('div', { class: 'xcard', style: 'margin-bottom:14px' }, h('h3', {}, 'الموارد'),
      meter('المعالج', s.cpu, s.cpu.toFixed(0) + '% · ' + s.cores + ' أنوية'), meter('الذاكرة', ramPct, fmtSize(used) + ' / ' + fmtSize(s.ramTotal)));
    for (const d of s.disks.slice(0, 4)) res.append(meter('القرص ' + d.name, 100 * (d.total - d.free) / d.total, 'متبقٍ ' + fmtSize(d.free) + ' من ' + fmtSize(d.total)));
    box.append(res);

    // حالة التطبيق
    const app = s.app || {};
    if (app.backup || app.mirror) box.append(h('div', { class: 'stats' },
      tile('archive', app.backup && app.backup.last ? fmtDate(app.backup.last) : 'لم تُنفَّذ', 'آخر نسخة احتياطية', app.backup && app.backup.last ? 'ok' : 'warn'),
      tile('folder', app.mirror ? app.mirror.folders + ' مجلد' : '—', app.mirror && app.mirror.lastSync ? 'آخر مزامنة ' + fmtDate(app.mirror.lastSync) : 'المرآة', app.mirror && app.mirror.enabled ? 'ok' : 'warn'),
      tile('ticket', app.tickets != null ? app.tickets : '—', 'تذكرة مفتوحة')));

    // إجراءات
    const caps = s.caps || {};
    // لوحة أرقام داخلية (بدل لوحة مفاتيح النظام): تعمل دائماً على iPhone داخل PWA
    const pin = h('button', { type: 'button', class: 'primary', style: 'min-width:220px;justify-content:center', onclick: () => pinPad(() => redraw()) });
    const paint = () => { const v = pinVal(); pin.replaceChildren(CPIcon('shield', 18), v ? 'PIN: ' + '●'.repeat(v.length) + '  (غيّر)' : 'اضغط لإدخال رمز PIN'); };
    paint();
    const A = (icon, text, fn, cls) => h('button', { class: cls || '', disabled: !caps.actions || !caps.pin, onclick: fn }, CPIcon(icon, 18), text);
    const delay = h('select', { style: 'width:auto', onchange: (e) => { D.delay = parseInt(e.target.value, 10); } },
      ...[[10, '10 ثوانٍ'], [30, '30 ثانية'], [60, 'دقيقة'], [300, '5 دقائق']].map(([v, l]) => h('option', { value: v, selected: v === D.delay }, l)));
    const note = h('input', { type: 'text', placeholder: 'رسالة تظهر على شاشته…', style: 'flex:1;min-width:180px' });
    box.append(h('div', { class: 'xcard', style: 'margin-bottom:14px' }, h('h3', {}, 'إجراءات'),
      h('p', { class: 'hint' }, caps.pin ? 'تتطلب رمز PIN الذي ضبطته في التطبيق. الرمز لا يُحفظ هنا (يُنسى بعد 5 دقائق).' : '⚠ لم يُضبط PIN في التطبيق — الإجراءات معطّلة.'),
      pin,
      h('div', { class: 'bar' },
        A('lock', 'قفل الشاشة', () => act('lock', {}, 'قُفلت الشاشة')),
        A('moon', 'سكون', () => act('sleep', {}, 'سكون', 'إدخال الجهاز في السكون؟ سيصبح غير متاح حتى يُوقَظ.')),
        A('power', 'إيقاف التشغيل', () => act('shutdown', { delay: D.delay }, 'سيُطفأ', 'إطفاء الجهاز؟ لن تستطيع الوصول إليه حتى تشغّله بيدك.'), 'danger'),
        A('refresh', 'إعادة تشغيل', () => act('restart', { delay: D.delay }, 'سيعاد تشغيله', 'إعادة تشغيل الجهاز؟'), 'danger'),
        A('x', 'إلغاء الإيقاف', () => act('cancel_shutdown', {}, 'أُلغي الإيقاف'))),
      h('div', { class: 'bar' }, h('span', { class: 'hint' }, 'مهلة الإيقاف:'), delay),
      h('div', { class: 'bar' }, note, A('bell', 'أرسل رسالة', () => act('notify', { text: note.value }, 'وصلت الرسالة'))),
      D.notice ? h('p', { class: 'hint' }, D.notice) : null));

    // فتح القفل
    if (caps.unlock) {
      const saved = ((S.data.Prefs || {}).Agent || {}).UnlockSecret || '';
      const sec = h('input', { type: 'password', autocomplete: 'off', placeholder: 'كلمة سر ويندوز / PIN الدخول', style: 'max-width:260px' });
      const card = h('div', { class: 'xcard', style: 'margin-bottom:14px' }, h('h3', {}, s.locked ? '🔒 الشاشة مقفلة — افتحها' : 'فتح قفل الشاشة'));
      if (!caps.unlockReady) card.append(h('p', { class: 'hint' }, '⚠ مساعد الفتح غير مثبّت على اللابتوب. من أيقونة رقيب في الصينية: «تثبيت مساعد القفل» (يطلب صلاحية مسؤول مرة واحدة).'));
      else if (!s.locked) card.append(h('p', { class: 'hint' }, 'الجهاز يُبلغ أن الشاشة مفتوحة. إن كانت مطفأة/مقفلة فعلاً فجرّب الأزرار أدناه — سيوقظها ويحاول الفتح.'));
      const row = h('div', { class: 'bar' });
      const can = caps.pin && caps.unlockReady;
      if (saved) row.append(h('button', { class: 'primary', disabled: !can, onclick: () => doUnlock(saved) }, CPIcon('lock', 18), 'افتح بضغطة واحدة'));
      row.append(sec, h('button', { disabled: !can, onclick: () => { const v = sec.value; sec.value = ''; if (!v) return toast('اكتب السرّ'); doUnlock(v); } }, 'افتح بالسرّ المكتوب'));
      card.append(row);
      if (D.unlockMsg) card.append(h('p', { class: 'hint' }, D.unlockMsg));
      card.append(h('p', { class: 'hint' }, 'يتطلب PIN أعلاه. تُكتب كلمة السر على شاشة القفل عبر مساعد محلي؛ لا تُحفظ على الخادم. 3 إخفاقات ← إيقاف 10 دقائق. يصلك إشعار تلغرام عن كل محاولة.'));
      box.append(card);
    }

    // منع السكون + الملفات (device2.js)
    const ctx = { s, caps, D, call, pinVal, pinPad, redraw, liveImg, liveLoop, act, reload };
    if (window.CPDeviceExtra) window.CPDeviceExtra.cards(box, ctx);

    // الشاشة
    if (caps.screenshot) {
      const shotBox = h('div', {});
      if (D.shot) shotBox.append(h('img', { class: 'shot', src: D.shot, alt: 'لقطة الشاشة' }));
      const q = h('select', { style: 'width:auto', onchange: (e) => { D.liveQ = e.target.value; } },
        ...[['fast', 'سريعة (شبكة ضعيفة)'], ['bal', 'متوازنة'], ['good', 'جودة أعلى'], ['hd', 'عالية جداً (للتكبير)']].map(([v, l]) => h('option', { value: v, selected: v === D.liveQ }, l)));
      const live = h('div', {});
      if (D.live || liveImg.getAttribute('src')) live.append(liveImg);
      box.append(h('div', { class: 'xcard', style: 'margin-bottom:14px' }, h('h3', {}, 'الشاشة'),
        h('div', { class: 'bar' },
          D.live ? h('button', { class: 'danger', onclick: () => { D.live = false; } }, CPIcon('stop', 18), 'أوقف المعاينة')
            : h('button', { class: 'primary', disabled: !caps.pin, onclick: liveLoop }, CPIcon('play', 18), 'معاينة حية'),
          q,
          A('camera', 'التقط الشاشة الآن', async () => {
            const p = pinVal(); if (!p) return (toast('أدخل رمز PIN'), pinPad());
            try { const r = await call(D.dev, 'screenshot', { width: 1280 }, p); D.shot = 'data:image/jpeg;base64,' + r.jpeg; } catch (e) { toast('⚠ ' + e.message); }
            CPX._state.force = true; CP.render();
          })),
        h('p', { class: 'hint', id: 'liveInfo' }, D.live ? D.liveInfo : ''),
        D.liveErr ? h('p', { class: 'hint' }, '⚠ ' + D.liveErr) : null,
        live, shotBox,
        window.CPDeviceExtra ? window.CPDeviceExtra.control(ctx) : null,
        h('p', { class: 'hint' }, 'المعاينة تُرسل إطاراً بعد آخر (تتوقف تلقائياً بعد 10 دقائق أو بمغادرة الصفحة)، وتعمل على شاشة القفل أيضاً إن ثُبّت مساعد الفتح. يصلك إشعار تلغرام عند بدئها.')));
    }

    // العمليات
    const procBox = h('div', {});
    const loadProcs = async () => {
      try {
        D.procs = (await call(D.dev, 'processes')).items;
      } catch (e) { toast('⚠ ' + e.message); }
      CPX._state.force = true; CP.render();
    };
    if (D.procs) for (const p of D.procs) procBox.append(h('div', { class: 'sess' },
      h('div', { class: 't' }, h('b', { class: 'mono' }, p.name), h('small', {}, 'PID ' + p.pid + ' · ' + fmtSize(p.mem))),
      caps.actions && caps.pin ? h('button', { class: 'danger', onclick: () => act('kill', { pid: p.pid, name: p.name }, 'أُنهي ' + p.name, 'إنهاء «' + p.name + '»؟ قد يُفقد عمل غير محفوظ.') }, CPIcon('x', 16), 'إنهاء') : null));
    box.append(fold('procs', h('summary', {}, CPIcon('activity', 18), 'العمليات الأثقل ذاكرة'),
      h('div', {}, h('div', { class: 'bar' }, h('button', { onclick: loadProcs }, CPIcon('refresh', 16), D.procs ? 'تحديث' : 'اعرض')), procBox)));

    // الاتصالات
    const connBox = h('div', {});
    const loadConns = async () => {
      try { D.conns = await call(D.dev, 'connections'); } catch (e) { toast('⚠ ' + e.message); }
      CPX._state.force = true; CP.render();
    };
    if (D.conns) {
      connBox.append(h('div', { class: 'chips' },
        h('span', { class: 'chip' }, D.conns.established + ' نشط'), h('span', { class: 'chip' }, D.conns.listening + ' منفذ مفتوح'),
        h('span', { class: 'chip ' + (D.conns.high ? 'bad' : 'ok') }, D.conns.high + ' عالي الخطورة'), h('span', { class: 'chip ' + (D.conns.medium ? 'warn' : 'ok') }, D.conns.medium + ' غير معتاد')));
      for (const f of D.conns.flagged) connBox.append(h('div', { class: 'alertrow' }, h('span', { class: 'ico ' + (f.risk === 'high' ? 'bad' : 'warn') }, CPIcon('alert', 18)),
        h('div', { class: 'grow' }, h('b', {}, f.process + ' — ' + f.remote), h('small', {}, f.advice))));
    }
    box.append(fold('conns', h('summary', {}, CPIcon('globe', 18), 'الاتصالات والأمان'),
      h('div', {}, h('div', { class: 'bar' }, h('button', { onclick: loadConns }, CPIcon('refresh', 16), D.conns ? 'تحديث' : 'افحص')), connBox)));

    // PowerShell
    const sh = fold('shell', h('summary', {}, CPIcon('terminal', 18), 'PowerShell عن بُعد'));
    if (!caps.shell) sh.append(h('div', {}, h('p', { class: 'hint' }, 'معطّل. فعّله في ملف .env: RAQEEB_ALLOW_SHELL=true (الأخطر، يتطلب PIN وكل أمر يُسجَّل).')));
    else {
      const cmd = h('textarea', { class: 'mono', style: 'min-height:90px', placeholder: 'Get-Process | Sort WS -Desc | Select -First 5' }); cmd.value = D.shellCmd;
      cmd.oninput = () => { D.shellCmd = cmd.value; };
      sh.append(h('div', {}, cmd, h('div', { class: 'bar' }, A('play', 'نفّذ', async () => {
        const p = pinVal(); if (!p) return (toast('أدخل رمز PIN'), pinPad());
        if (!cmd.value.trim()) return;
        D.out = '… ينفَّذ';
        CPX._state.force = true; CP.render();
        try { const r = await call(D.dev, 'shell', { cmd: cmd.value, timeout: 60 }, p); D.out = `[exit ${r.exit}]\n` + r.output; } catch (e) { D.out = '⚠ ' + e.message; }
        CPX._state.force = true; CP.render();
      }, 'primary')), D.out ? h('div', { class: 'log' }, D.out) : null));
    }
    box.append(sh);

    box.append(h('details', { class: 'fold' }, h('summary', {}, CPIcon('shield', 18), 'ما مدى أمان هذا؟'),
      h('div', {}, h('p', { class: 'hint', style: 'white-space:pre-wrap' },
        '• الأوامر والردود مشفّرة بمفتاح مشتق من السرّ الرئيسي (RAQEEB_SECRET)؛ الخادم لا يقرأها ولا يستطيع تزويرها.\n' +
        '• الجهاز يتحقق من كل أمر: صلاحيته الزمنية، عدم تكراره، ثم PIN للإجراءات. 5 أخطاء ← قفل 15 دقيقة.\n' +
        '• الإيقاف/الإعادة بمهلة قابلة للإلغاء. لقطة الشاشة وPowerShell مغلقان افتراضياً ويُفعَّلان من التطبيق فقط.\n' +
        '• كل إجراء يُسجَّل في سجل التدقيق على الجهاز.'))));
  }

  const fmtDate = (s) => { try { return new Date(s).toLocaleString('ar', { dateStyle: 'medium', timeStyle: 'short' }); } catch { return ''; } };

  window.CPX._views = window.CPX._views || {};
  window.CPX._views.device = view;
  window.CPDevice = { summary, call, devices, reset: () => { if (window.CPDeviceExtra) window.CPDeviceExtra.reset(); D.live = false; aes = null; keyFor = null; stopTimer(); D.devs = null; D.status = null; D.procs = D.conns = D.shot = null; D.out = ''; D.pin = ''; } };
})();
