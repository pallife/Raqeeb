'use strict';
/* جهازي — الجزء الثاني: التحكم بالفأرة والكيبورد، الملفات، منع السكون.
   يعتمد على device.js (call/PIN/render) عبر window.CPDeviceExtra الذي يستدعيه عند بناء الصفحة.
   كل الأوامر تمرّ بنفس قناة device.js: مشفّرة طرف-لطرف، تتطلب PIN، ويتحقق الجهاز من الصلاحية في التطبيق. */
(function () {
  const { h, toast } = window.CP;
  const b64 = (u8) => { let s = ''; const n = 0x8000; for (let i = 0; i < u8.length; i += n) s += String.fromCharCode.apply(null, u8.subarray(i, i + n)); return btoa(s); };
  const unb64 = (s) => Uint8Array.from(atob(s), c => c.charCodeAt(0));
  const rid = () => Array.from(crypto.getRandomValues(new Uint8Array(10)), b => b.toString(16).padStart(2, '0')).join('');
  const fmtSize = (b) => b < 1024 ? b + ' B' : b < 1048576 ? (b / 1024).toFixed(0) + ' KB' : b < 1073741824 ? (b / 1048576).toFixed(1) + ' MB' : (b / 1073741824).toFixed(2) + ' GB';
  const fmtTime = (iso) => { try { return new Date(iso).toLocaleTimeString('ar', { hour: '2-digit', minute: '2-digit' }); } catch { return ''; } };
  const fmtDate = (sec) => { try { return sec ? new Date(sec * 1000).toLocaleDateString('ar', { dateStyle: 'medium' }) : ''; } catch { return ''; } };
  const MAX_DL = 500 * 1024 * 1024;       // لا نحمّل أكثر من 500MB في ذاكرة المتصفح
  const UP_CHUNK = 1000 * 1000;           // الجهاز يقبل حتى ~1.5MB للجزء

  let X = null;                           // سياق device.js الأحدث (يُحدَّث عند كل رسم)
  const C = { on: false, mode: 'click', text: '', enter: true, bound: false, down: null, q: Promise.resolve(), sent: 0 };
  const F = { path: '', parent: '', items: null, find: '', url: '', urlName: '', msg: '', busy: false, jobs: [], poll: false, truncated: false };

  const pin = () => X.pinVal();
  async function run(op, args) {
    const p = pin();
    if (!p) { toast('أدخل رمز PIN'); try { X.pinPad(() => X.redraw()); } catch { } throw new Error('no-pin'); }
    return X.call(X.D.dev, op, args, p);
  }

  // ===================================================== التحكم بالفأرة والكيبورد (يُضمَّن في بطاقة الشاشة)
  function sendInput(args) {
    // سلسلة متتابعة: لا يتقدّم أمر على آخر (النقر قبل السحب مثلاً)
    C.q = C.q.then(() => run('input', args)).then(() => { C.sent++; }).catch((e) => { if (e.message !== 'no-pin') toast('⚠ ' + e.message); });
    return C.q;
  }

  function bindImage(img) {
    if (C.bound === img) return;
    C.bound = img;
    img.addEventListener('pointerdown', (e) => {
      if (!C.on) return;
      e.preventDefault();
      try { img.setPointerCapture(e.pointerId); } catch { }
      C.down = { x: e.clientX, y: e.clientY, t: Date.now() };
    });
    img.addEventListener('pointerup', (e) => {
      if (!C.on || !C.down) return;
      e.preventDefault();
      const d = C.down; C.down = null;
      const r = img.getBoundingClientRect(); if (!r.width || !r.height) return;
      const nx = (v) => Math.min(1, Math.max(0, (v - r.left) / r.width)), ny = (v) => Math.min(1, Math.max(0, (v - r.top) / r.height));
      const dist = Math.hypot(e.clientX - d.x, e.clientY - d.y), held = Date.now() - d.t;
      if (C.mode === 'scroll') {
        const dy = d.y - e.clientY;                       // سحب لأعلى = تمرير للأسفل
        if (Math.abs(dy) > 8) sendInput({ kind: 'scroll', dy: Math.sign(dy) * Math.min(15, Math.max(1, Math.round(Math.abs(dy) / 25))) });
        return;
      }
      if (C.mode === 'drag' || (dist > 14 && C.mode !== 'move')) {
        sendInput({ kind: 'drag', x: nx(d.x), y: ny(d.y), x2: nx(e.clientX), y2: ny(e.clientY) });
        return;
      }
      if (C.mode === 'move') return void sendInput({ kind: 'move', x: nx(e.clientX), y: ny(e.clientY) });
      const right = C.mode === 'right' || held > 550;
      sendInput({ kind: 'click', x: nx(e.clientX), y: ny(e.clientY), button: right ? 'right' : 'left', count: C.mode === 'double' ? 2 : 1 });
    });
    img.addEventListener('pointercancel', () => { C.down = null; });
    img.addEventListener('contextmenu', (e) => { if (C.on) e.preventDefault(); });
  }

  const KEYS = [
    ['Esc', 'esc'], ['Tab', 'tab'], ['Enter', 'enter'], ['⌫', 'backspace'], ['Del', 'delete'], ['Space', 'space'],
    ['↑', 'up'], ['↓', 'down'], ['←', 'left'], ['→', 'right'], ['Home', 'home'], ['End', 'end'], ['PgUp', 'pageup'], ['PgDn', 'pagedown'],
    ['Win', 'win'], ['F5', 'f5'], ['Alt+Tab', 'tab', ['alt']], ['Ctrl+C', 'c', ['ctrl']], ['Ctrl+V', 'v', ['ctrl']], ['Ctrl+A', 'a', ['ctrl']], ['Ctrl+Z', 'z', ['ctrl']],
    ['Alt+F4', 'f4', ['alt']], ['🔉', 'voldown'], ['🔊', 'volup'], ['🔇', 'mute'], ['⏯', 'playpause'],
  ];

  function control(x) {
    X = x;
    const { caps, D, liveImg } = x;
    if (!caps.screenshot) return null;
    if (!caps.input) return h('p', { class: 'hint' }, 'التحكم بالفأرة والكيبورد معطّل. فعّله من بطاقة «صلاحيات التحكم عن بُعد» (بالـPIN).');
    bindImage(liveImg);
    liveImg.style.touchAction = C.on ? 'none' : '';
    liveImg.style.cursor = C.on ? 'crosshair' : 'zoom-in';
    liveImg.style.userSelect = 'none';
    if (!CPRemote.seen) { CPRemote.seen = true; CPRemote.setPrivate(!!caps.privacyOn); }
    const wrap = h('div', { style: 'margin-top:10px' });
    wrap.append(h('div', { class: 'bar' },
      h('button', { class: 'primary', disabled: !caps.pin, onclick: () => {
        if (!pin()) { toast('أدخل رمز PIN'); try { x.pinPad(() => x.redraw()); } catch { } return; }
        window.CPRemote.open(x);
      } }, CPIcon('zap', 18), '🖥 شاشة كاملة للتحكم'),
      h('button', { class: CPRemote.isPrivate() ? 'danger' : '', disabled: !caps.pin || !caps.privacy, title: 'تغطية شاشة اللابتوب بلون أسود وأنت تراها وتتحكم بها',
        onclick: () => { if (!pin()) { toast('أدخل رمز PIN'); try { x.pinPad(() => x.redraw()); } catch { } return; } CPRemote.privacy(x, !CPRemote.isPrivate()); } },
        '🕶 ' + (CPRemote.isPrivate() ? 'أزل غطاء الشاشة' : 'غطِّ شاشة اللابتوب'))));
    wrap.append(h('div', { class: 'bar' },
      h('button', { class: C.on ? 'danger' : 'primary', disabled: !D.live || !caps.pin, onclick: () => { C.on = !C.on; x.redraw(); } },
        CPIcon('zap', 18), C.on ? 'أوقف التحكم' : 'ابدأ التحكم باللمس'),
      !D.live ? h('span', { class: 'hint' }, 'شغّل «معاينة حية» أولاً') : null));
    if (!C.on) return wrap;
    const modes = [['click', 'نقر'], ['double', 'نقر مزدوج'], ['right', 'زر أيمن'], ['drag', 'سحب'], ['scroll', 'تمرير'], ['move', 'تحريك']];
    wrap.append(h('div', { class: 'bar' }, ...modes.map(([m, l]) => h('button', { class: C.mode === m ? 'primary' : '', onclick: () => { C.mode = m; x.redraw(); } }, l))));
    wrap.append(h('p', { class: 'hint' }, 'المس الصورة: نقرة = نقر، لمسة مطوّلة = زر أيمن، سحب = سحب. «تمرير»: اسحب لأعلى/أسفل.'));
    const txt = h('input', { type: 'text', placeholder: 'اكتب نصاً ليُكتب على الجهاز…', style: 'flex:1;min-width:160px', value: C.text, dir: 'auto' });
    txt.oninput = () => { C.text = txt.value; };
    const enter = h('input', { type: 'checkbox' }); enter.checked = C.enter; enter.onchange = () => { C.enter = enter.checked; };
    wrap.append(h('div', { class: 'bar' }, txt,
      h('button', { class: 'primary', onclick: async () => {
        const v = txt.value; if (!v) return;
        await sendInput({ kind: 'text', text: v });
        if (C.enter) await sendInput({ kind: 'key', name: 'enter' });
        txt.value = ''; C.text = '';
      } }, CPIcon('send', 18), 'اكتب'),
      h('label', { class: 'hint', style: 'display:flex;gap:6px;align-items:center' }, enter, 'ثم Enter')));
    wrap.append(h('div', { class: 'bar' }, ...KEYS.map(([l, name, mods]) => h('button', { onclick: () => sendInput({ kind: 'key', name, mods: mods || [] }) }, l))));
    wrap.append(h('p', { class: 'hint' }, 'يعمل أيضاً على شاشة القفل إن ثُبّت مساعد الفتح. كل أمر يتطلب PIN ويصلك إشعار تلغرام عند بدء التحكم.'));
    return wrap;
  }

  // ===================================================== منع السكون
  function awakeCard(x) {
    const a = x.s.awake || {};
    const hours = h('select', { style: 'width:auto' },
      ...[[1, 'ساعة'], [2, 'ساعتان'], [4, '4 ساعات'], [8, '8 ساعات'], [12, '12 ساعة'], [24, '24 ساعة']].map(([v, l]) => h('option', { value: v, selected: v === (C.hours || 4) }, l)));
    hours.onchange = () => { C.hours = parseInt(hours.value, 10); };
    const card = h('div', { class: 'xcard', style: 'margin-bottom:14px' }, h('h3', {}, 'منع السكون وإطفاء الشاشة'));
    card.append(h('div', { class: 'chips' },
      h('span', { class: 'chip ' + (a.on ? 'warn' : 'ok') }, a.on ? '☕ مفعّل منذ ' + fmtTime(a.since) + ' · ينتهي ' + fmtTime(a.until) : 'الوضع الافتراضي (يمكن للشاشة أن تنطفئ)')));
    card.append(h('div', { class: 'bar' },
      h('button', { class: 'primary', disabled: !x.caps.actions || !x.caps.pin, onclick: () => x.act('awake_on', { hours: parseInt(hours.value, 10) }, 'تم منع السكون') }, CPIcon('sun', 18), a.on ? 'جدّد المدة' : 'امنع السكون'),
      hours,
      h('button', { disabled: !a.on, onclick: async () => {
        try { const r = await x.call(x.D.dev, 'awake_off', {}, ''); toast('✓ ' + (r.done || 'أُعيد الافتراضي')); } catch (e) { toast('⚠ ' + e.message); }
        x.reload();
      } }, CPIcon('moon', 18), 'أعد الافتراضي')));
    card.append(h('p', { class: 'hint' }, `أثناء التفعيل: لا تنطفئ الشاشة ولا ينام الجهاز، وتصلك تذكيرات تلغرام كل ${a.remindMin || 30} دقيقة (مع تنبيه إن كان على البطارية). يُلغى تلقائياً بانتهاء المدة، ويعود الافتراضي بإغلاق التطبيق. زر «أعد الافتراضي» لا يحتاج PIN.`));
    return card;
  }

  // ===================================================== الملفات
  const setMsg = (t) => { F.msg = t; const e = document.getElementById('fsMsg'); if (e) e.textContent = t; };
  const pathOf = (it) => it.full || (F.path + '\\' + it.name);

  async function list(path) {
    F.busy = true; setMsg('… أقرأ المجلد');
    try {
      const r = await run('fs_list', { path: path || '' });
      F.path = r.path || ''; F.parent = r.parent || ''; F.items = r.items; F.truncated = false; setMsg('');
    } catch (e) { setMsg('⚠ ' + e.message); }
    F.busy = false; X.redraw();
  }
  async function findFiles() {
    const q = F.find.trim(); if (q.length < 2) return toast('اكتب حرفين على الأقل');
    F.busy = true; setMsg('… أبحث');
    try { const r = await run('fs_find', { q }); F.path = ''; F.parent = ''; F.items = r.items; F.truncated = r.truncated; setMsg(r.items.length ? '' : 'لا نتائج'); }
    catch (e) { setMsg('⚠ ' + e.message); }
    F.busy = false; X.redraw();
  }
  async function download(it) {
    if (it.size > MAX_DL) return toast('الملف أكبر من 500MB — أرسله لتلغرام بدلاً من ذلك');
    const path = pathOf(it), parts = []; let off = 0, got = 0;
    try {
      for (;;) {
        const r = await run('fs_read', { path, offset: off, len: 1500000 });
        const u = r.data ? unb64(r.data) : new Uint8Array(0);
        parts.push(u); off += u.length; got += u.length;
        setMsg(`⬇ ${it.name}: ${fmtSize(got)} / ${fmtSize(r.size)}`);
        if (r.eof || u.length === 0) break;
        if (got > MAX_DL) throw new Error('تجاوز 500MB');
      }
      const a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob(parts)); a.download = it.name; document.body.append(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 60000);
      setMsg('✓ نُزّل ' + it.name);
    } catch (e) { setMsg('⚠ ' + e.message); }
  }
  async function upload(files) {
    for (const f of files) {
      const id = rid(); let off = 0;
      try {
        for (;;) {
          const buf = new Uint8Array(await f.slice(off, off + UP_CHUNK).arrayBuffer());
          const done = off + buf.length >= f.size;
          await run('fs_put', { name: f.name, upload: id, offset: off, data: b64(buf), done });
          off += buf.length; setMsg(`⬆ ${f.name}: ${fmtSize(off)} / ${fmtSize(f.size)}`);
          if (done) break;
        }
        setMsg('✓ رُفع ' + f.name + ' إلى مجلد Raqeeb-Inbox في التنزيلات');
      } catch (e) { setMsg('⚠ ' + f.name + ': ' + e.message); break; }
    }
  }
  async function simple(op, args, okText) {
    try { const r = await run(op, args); setMsg('✓ ' + (r.done || okText)); return r; }
    catch (e) { setMsg('⚠ ' + e.message); }
  }
  async function pollJobs() {
    if (F.poll) return; F.poll = true;
    try {
      for (let i = 0; i < 600; i++) {
        const r = await run('fetch_status', {});
        F.jobs = r.jobs || [];
        const el = document.getElementById('fsJobs'); if (el) el.replaceChildren(...jobRows());
        if (!F.jobs.some(j => j.state === 'running')) break;
        await new Promise(r => setTimeout(r, 3000));
      }
    } catch { }
    F.poll = false;
  }
  const jobRows = () => F.jobs.map(j => h('div', { class: 'sess' },
    h('div', { class: 't' }, h('b', { class: 'mono' }, j.name), h('small', {},
      j.state === 'running' ? '⬇ ' + fmtSize(j.bytes) + (j.total ? ' / ' + fmtSize(j.total) : '') : j.state === 'done' ? '✓ اكتمل · ' + fmtSize(j.bytes) : '⚠ ' + j.error))));

  function filesCard(x) {
    const caps = x.caps;
    const card = h('div', { class: 'xcard', style: 'margin-bottom:14px' }, h('h3', {}, 'الملفات'));
    if (!caps.files) {
      card.append(h('p', { class: 'hint' }, 'معطّلة. فعّلها من بطاقة «صلاحيات التحكم عن بُعد» أعلاه (بالـPIN). تقتصر على مجلدات مسموحة ولا تُظهر الملفات الحساسة.'));
      return card;
    }
    if (F.items === null && !F.busy && caps.pin && pin()) setTimeout(() => list(''), 0);   // لا نفتح لوحة PIN من تلقاء أنفسنا
    const find = h('input', { type: 'text', placeholder: 'ابحث باسم ملف…', style: 'flex:1;min-width:140px', value: F.find, dir: 'auto' });
    find.oninput = () => { F.find = find.value; };
    const doFind = () => { F.find = find.value; return findFiles(); };
    find.onkeydown = (e) => { if (e.key === 'Enter') doFind(); };
    card.append(h('div', { class: 'bar' },
      find, h('button', { onclick: doFind }, CPIcon('search', 18), 'بحث'),
      h('button', { onclick: () => list(F.path) }, CPIcon('refresh', 18), 'تحديث')));
    card.append(h('p', { class: 'hint', id: 'fsMsg' }, F.msg));
    if (F.path) card.append(h('div', { class: 'bar' },
      h('button', { onclick: () => list(F.parent) }, '↩ للأعلى'),
      h('span', { class: 'chip mono', style: 'direction:ltr;max-width:100%;overflow:hidden;text-overflow:ellipsis' }, F.path)));
    const box = h('div', {});
    for (const it of (F.items || [])) {
      const btns = [];
      if (it.dir) {
        btns.push(h('button', { onclick: () => list(pathOf(it)) }, CPIcon('folder', 16), 'فتح'));
        if (caps.telegram) btns.push(h('button', { title: 'اضغط المجلد وأرسله إلى تلغرام', onclick: () => simple('fs_telegram', { path: pathOf(it) }, 'أُرسل') }, CPIcon('send', 16)));
      }
      else {
        btns.push(h('button', { title: 'تنزيل على الجوال', onclick: () => download(it) }, CPIcon('download', 16)));
        if (!it.exec) btns.push(h('button', { title: 'افتحه على اللابتوب', onclick: () => simple('fs_open', { path: pathOf(it) }, 'فُتح على الجهاز') }, CPIcon('eye', 16)));
        if (caps.telegram) btns.push(h('button', { title: 'أرسله إلى تلغرام', onclick: () => simple('fs_telegram', { path: pathOf(it) }, 'أُرسل') }, CPIcon('send', 16)));
      }
      box.append(h('div', { class: 'sess' },
        h('span', { class: 'ico' }, CPIcon(it.dir ? 'folder' : 'note', 20)),
        h('div', { class: 't' }, h('b', { dir: 'auto' }, it.name), h('small', {}, it.dir ? 'مجلد' : fmtSize(it.size) + (it.mtime ? ' · ' + fmtDate(it.mtime) : '') + (it.exec ? ' · تنفيذي' : ''))),
        ...btns));
    }
    if (F.items && !F.items.length) box.append(h('p', { class: 'hint' }, 'لا عناصر.'));
    if (F.truncated) box.append(h('p', { class: 'hint' }, 'اقتُطعت النتائج — حدّد البحث أكثر.'));
    card.append(box);

    // رفع من الجوال
    const file = h('input', { type: 'file', multiple: true, style: 'display:none' });
    file.onchange = async () => { const fs = [...file.files]; file.value = ''; if (fs.length) await upload(fs); };
    card.append(h('div', { class: 'bar' },
      h('button', { disabled: !caps.pin, onclick: () => file.click() }, CPIcon('upload', 18), 'ارفع ملفاً من الجوال إلى اللابتوب'), file));

    // تنزيل رابط على اللابتوب
    const url = h('input', { type: 'url', placeholder: 'https://… رابط تريد تنزيله على اللابتوب', style: 'flex:1;min-width:200px', value: F.url, dir: 'ltr' });
    url.oninput = () => { F.url = url.value; };
    card.append(h('div', { class: 'bar' }, url,
      h('button', { class: 'primary', disabled: !caps.pin, onclick: async () => {
        const v = url.value.trim(); if (!v) return;
        const r = await simple('fetch', { url: v }, 'بدأ التنزيل');
        if (r) { F.url = ''; url.value = ''; pollJobs(); }
      } }, CPIcon('download', 18), 'نزّل على اللابتوب'),
      h('button', { disabled: !caps.pin, onclick: async () => {
        const v = url.value.trim(); if (!/^https?:\/\//i.test(v)) return toast('اكتب رابطاً يبدأ بـ https://');
        await simple('fs_open', { path: v }, 'فُتح الرابط على الجهاز');
      } }, CPIcon('eye', 18), 'افتحه في متصفح اللابتوب')));
    card.append(h('div', { id: 'fsJobs' }, ...jobRows()));
    card.append(h('p', { class: 'hint' }, 'الملفات المرفوعة/المنزّلة تُحفظ في «التنزيلات\\Raqeeb-Inbox» ولا تُشغَّل تلقائياً. الملفات التنفيذية والمفاتيح والخزنة محجوبة، والروابط الداخلية (192.168…/localhost) محجوبة.'));
    return card;
  }

  // ===================================================== الصلاحيات (تُفعَّل من الجوال بالـPIN)
  function capsCard(x) {
    const c = x.caps || {};
    const card = h('div', { class: 'xcard', style: 'margin-bottom:14px' }, h('h3', {}, 'صلاحيات التحكم عن بُعد'));
    const row = (label, on, key) => h('div', { class: 'bar', style: 'align-items:center' },
      h('span', { class: 'chip ' + (on ? 'ok' : 'warn') }, (on ? '✓ ' : '✕ ') + label),
      h('button', { disabled: !c.actions || !c.pin, onclick: () => x.act('set_caps', { [key]: !on }, on ? 'عُطّل' : 'فُعّل') }, on ? 'عطّل' : 'فعّل'));
    card.append(row('الفأرة والكيبورد', !!c.input, 'input'), row('الملفات (تصفّح/تنزيل/رفع/تلغرام)', !!c.files, 'files'));
    card.append(h('p', { class: 'hint' }, 'التغيير يتطلب PIN ويصلك إشعار تلغرام. القفل ولقطة الشاشة وPowerShell تبقى من إعدادات التطبيق فقط.'));
    return card;
  }

  window.CPDeviceExtra = {
    control,
    active: () => C.on,
    cards(box, x) {
      X = x;
      box.append(capsCard(x));
      box.append(awakeCard(x));
      box.append(filesCard(x));
    },
    sendInput,
    run,
    reset() { if (window.CPRemote) CPRemote.close(); C.on = false; F.items = null; F.path = ''; F.jobs = []; F.msg = ''; },
  };
})();
