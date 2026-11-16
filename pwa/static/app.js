'use strict';
/* رقيب 3 — واجهة الجوال الجديدة: الشاشة · التيرمينال · الداشبورد · التحكم. كل الأوامر مشفّرة AES-GCM من السرّ الرئيسي المحفوظ على هذا الجوال فقط. */
(function () {
  const $ = (id) => document.getElementById(id);
  const BASE = location.pathname.replace(/[^/]*$/, '');
  const enc = new TextEncoder(), dec = new TextDecoder();
  const LS = { get: (k) => { try { return localStorage.getItem(k); } catch { return null; } }, set: (k, v) => { try { localStorage.setItem(k, v); } catch { } }, del: (k) => { try { localStorage.removeItem(k); } catch { } } };
  const hex2u8 = (s) => Uint8Array.from(s.match(/../g).map((x) => parseInt(x, 16)));
  const u82hex = (u) => Array.from(u, (b) => b.toString(16).padStart(2, '0')).join('');
  const b64 = (u8) => { let s = ''; for (const b of u8) s += String.fromCharCode(b); return btoa(s); };
  const unb64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const fmtB = (b) => b >= 1073741824 ? (b / 1073741824).toFixed(1) + ' GB' : (b / 1048576).toFixed(0) + ' MB';
  const fmtRate = (b) => b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB/s' : (b / 1024).toFixed(0) + ' KB/s';
  function toast(m) { const t = $('toast'); t.textContent = m; t.style.display = 'block'; clearTimeout(toast._t); toast._t = setTimeout(() => { t.style.display = 'none'; }, 3200); }

  const S = { master: null, token: null, aes: null, dev: null, tab: 'screen' };

  // ---------------------------------------------------------------- التشفير والنداء
  async function hkdf(info) {
    const k = await crypto.subtle.importKey('raw', S.master, 'HKDF', false, ['deriveBits']);
    return new Uint8Array(await crypto.subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(0), info: enc.encode(info) }, k, 256));
  }
  async function initKeys() {
    S.token = u82hex(await hkdf('control-panel/web-auth/v1'));
    S.aes = await crypto.subtle.importKey('raw', await hkdf('control-panel/agent/v1'), 'AES-GCM', false, ['encrypt', 'decrypt']);
  }
  async function devices() {
    const r = await fetch(BASE + 'api/agent/devices', { headers: { Authorization: 'Bearer ' + S.token }, cache: 'no-store' });
    if (!r.ok) throw new Error('تعذّر الوصول للخادم (' + r.status + ')');
    return (await r.json()).devices;
  }
  async function call(op, args, pin) {
    const id = u82hex(crypto.getRandomValues(new Uint8Array(12)));
    const cmd = { op, args: args || {}, ts: Math.floor(Date.now() / 1000), pin: pin || '' };
    const nonce = crypto.getRandomValues(new Uint8Array(12));
    const ct = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: enc.encode('cmd:' + id), tagLength: 128 }, S.aes, enc.encode(JSON.stringify(cmd))));
    const body = new Uint8Array(12 + ct.length); body.set(nonce, 0); body.set(ct, 12);
    const r = await fetch(BASE + 'api/agent/cmd', { method: 'POST', headers: { Authorization: 'Bearer ' + S.token, 'Content-Type': 'application/json' }, body: JSON.stringify({ device: S.dev, id, ct: b64(body) }) });
    if (r.status === 404) throw Object.assign(new Error('الجهاز غير متصل'), { offline: true });
    if (r.status === 504) throw new Error('الجهاز لم يردّ');
    if (!r.ok) throw new Error('رفض الخادم (' + r.status + ')');
    const raw = unb64((await r.json()).ct);
    let plain; try { plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: raw.slice(0, 12), additionalData: enc.encode('res:' + id), tagLength: 128 }, S.aes, raw.slice(12)); } catch { throw new Error('ردّ غير موثوق'); }
    const res = JSON.parse(dec.decode(plain));
    if (!res.ok) throw new Error(res.error || 'فشل الأمر');
    return res.data;
  }

  // ---------------------------------------------------------------- PIN (في الذاكرة 10 دقائق فقط)
  const P = { v: '', at: 0 };
  const pin = () => (P.v && Date.now() - P.at < 10 * 60 * 1000 ? P.v : '');
  let pinWait = null;
  function askPin() {
    if (pinWait) return pinWait;
    const pr = new Promise((res, rej) => {
      let v = '';
      const dots = $('pinDots'), draw = () => { dots.replaceChildren(...Array.from({ length: Math.max(6, v.length) }, (_, i) => Object.assign(document.createElement('i'), { className: i < v.length ? 'f' : '' }))); };
      const pad = $('pinPad'); pad.replaceChildren();
      const add = (t, fn) => { const b = document.createElement('button'); b.textContent = t; b.onclick = fn; pad.append(b); };
      for (const d of '123456789') add(d, () => { v += d; draw(); });
      add('✕', () => { $('sheet').classList.remove('show'); rej(new Error('no-pin')); });
      add('0', () => { v += '0'; draw(); });
      add('✓', () => { if (v.length < 4) return; P.v = v; P.at = Date.now(); $('sheet').classList.remove('show'); res(v); });
      draw(); $('sheet').classList.add('show');
    });
    const clr = () => { pinWait = null; }; pr.then(clr, clr); pinWait = pr; return pr;
  }
  async function need() { return pin() || askPin(); }
  async function act(op, args, ok) {
    try { const d = await call(op, args, await need()); if (ok) toast(ok); return d; }
    catch (e) { if (/PIN/.test(e.message)) P.v = ''; if (e.message !== 'no-pin') toast('⚠ ' + e.message); throw e; }
  }

  // ---------------------------------------------------------------- التبويبات
  const ICONS = {
    screen: '<svg viewBox="0 0 24 24"><rect x="2" y="4" width="20" height="13" rx="2"/><path d="M8 21h8M12 17v4"/></svg>',
    term: '<svg viewBox="0 0 24 24"><rect x="2" y="3" width="20" height="18" rx="3"/><path d="m7 9 3 3-3 3M13 15h4"/></svg>',
    dash: '<svg viewBox="0 0 24 24"><path d="M3 12a9 9 0 1 1 18 0"/><path d="m12 12 4-4"/><circle cx="12" cy="12" r="1.5"/></svg>',
    ctl: '<svg viewBox="0 0 24 24"><path d="M12 2v10M6.4 6.4a8 8 0 1 0 11.2 0"/></svg>',
  };
  const TABS = [['screen', 'الشاشة'], ['term', 'تيرمينال'], ['dash', 'الجهاز'], ['ctl', 'تحكم']];
  function buildNav() {
    $('nav').replaceChildren(...TABS.map(([k, l]) => { const b = document.createElement('button'); b.dataset.k = k; b.innerHTML = ICONS[k] + '<span>' + l + '</span>'; b.onclick = () => go(k); return b; }));
  }
  function go(k) {
    S.tab = k;
    for (const b of $('nav').children) b.classList.toggle('act', b.dataset.k === k);
    for (const p of document.querySelectorAll('.page')) p.classList.toggle('show', p.id === 'p-' + k);
    if (k === 'screen') Screen.start(); else Screen.stop();
    if (k === 'dash') Dash.start(); else Dash.stop();
    if (k === 'term') Term.show();
  }

  // ---------------------------------------------------------------- الشاشة
  const Screen = {
    live: false, mode: 'click', q: 'bal', gen: 0, seq: Promise.resolve(), down: null,
    Q: { fast: [640, 35, 350], bal: [960, 50, 500], good: [1280, 65, 800], hd: [1920, 70, 1000] },
    init() {
      const el = $('p-screen');
      el.innerHTML = '<div id="scrWrap"><div id="scrBox"><img id="scrImg" alt=""><div id="scrInfo">—</div></div>' +
        '<div class="tools" id="scrTools"></div><div class="keys" id="scrKeys"></div>' +
        '<div class="tools"><input id="scrTxt" dir="auto" placeholder="اكتب هنا ليُكتب على الجهاز…" style="flex:1"><button class="pri" id="scrSend">إرسال</button></div></div>';
      const tools = [['click', '👆 نقر'], ['double', '✌ مزدوج'], ['right', '🖱 يمين'], ['scroll', '↕ تمرير'], ['drag', '✋ سحب'], ['move', '➤ تحريك']];
      const t = $('scrTools');
      for (const [m, l] of tools) { const b = document.createElement('button'); b.textContent = l; b.dataset.m = m; b.onclick = () => { this.mode = m; this.mark(); }; t.append(b); }
      const q = document.createElement('select'); q.innerHTML = '<option value="fast">سريع</option><option value="bal" selected>متوازن</option><option value="good">جيد</option><option value="hd">HD</option>';
      q.onchange = () => { this.q = q.value; }; t.append(q);
      const fs = document.createElement('button'); fs.textContent = '⛶ ملء الشاشة'; fs.onclick = () => this.full(); t.append(fs);
      this.mark();
      const keys = [['Esc', 'escape'], ['Tab', 'tab'], ['⌫', 'backspace'], ['⏎', 'enter'], ['←', 'left'], ['↑', 'up'], ['↓', 'down'], ['→', 'right'], ['Win', 'win'], ['Alt+Tab', 'tab', ['alt']], ['Ctrl+C', 'c', ['ctrl']], ['Ctrl+V', 'v', ['ctrl']], ['Ctrl+Z', 'z', ['ctrl']]];
      for (const [l, name, mods] of keys) { const b = document.createElement('button'); b.textContent = l; b.onclick = () => this.send({ kind: 'key', name, mods: mods || [] }); $('scrKeys').append(b); }
      $('scrSend').onclick = async () => { const v = $('scrTxt').value; if (!v) return; await this.send({ kind: 'text', text: v }); $('scrTxt').value = ''; };
      this.bind($('scrImg'));
    },
    mark() { for (const b of $('scrTools').querySelectorAll('button[data-m]')) b.classList.toggle('on', b.dataset.m === this.mode); },
    async full() {
      try { await $('scrBox').requestFullscreen(); try { await screen.orientation.lock('landscape'); } catch { } } catch { toast('المتصفح لا يدعم ملء الشاشة هنا (على آيفون: أضف التطبيق للشاشة الرئيسية وأدر الجوال)'); }
    },
    send(args) { this.seq = this.seq.then(() => act('input', args)).catch(() => { }); return this.seq; },
    bind(img) {
      img.addEventListener('pointerdown', (e) => { e.preventDefault(); try { img.setPointerCapture(e.pointerId); } catch { } this.down = { x: e.clientX, y: e.clientY, t: Date.now() }; });
      img.addEventListener('pointercancel', () => { this.down = null; });
      img.addEventListener('pointerup', (e) => {
        const d = this.down; if (!d) return; this.down = null;
        const r = img.getBoundingClientRect(); if (!r.width) return;
        const nx = (v) => Math.min(1, Math.max(0, (v - r.left) / r.width)), ny = (v) => Math.min(1, Math.max(0, (v - r.top) / r.height));
        const dist = Math.hypot(e.clientX - d.x, e.clientY - d.y), held = Date.now() - d.t, m = this.mode;
        if (m === 'scroll') { const dy = d.y - e.clientY; if (Math.abs(dy) > 8) this.send({ kind: 'scroll', dy: Math.sign(dy) * Math.min(15, Math.max(1, Math.round(Math.abs(dy) / 25))) }); return; }
        if (m === 'drag' || (dist > 14 && m !== 'move')) return void this.send({ kind: 'drag', x: nx(d.x), y: ny(d.y), x2: nx(e.clientX), y2: ny(e.clientY) });
        if (m === 'move') return void this.send({ kind: 'move', x: nx(e.clientX), y: ny(e.clientY) });
        this.send({ kind: 'click', x: nx(e.clientX), y: ny(e.clientY), button: m === 'right' || held > 550 ? 'right' : 'left', count: m === 'double' ? 2 : 1 });
      });
    },
    async start() {
      if (this.live) return; this.live = true; const gen = ++this.gen; let since = '', n = 0; const t0 = Date.now();
      try { await need(); } catch { this.live = false; return; }
      while (this.live && gen === this.gen && S.tab === 'screen') {
        if (document.hidden) { await sleep(800); continue; }
        const [w, qq, ms] = this.Q[this.q], t1 = performance.now();
        try {
          const r = await call('screenshot', { width: w, q: qq, since }, pin());
          since = r.h || since;
          if (!r.same && r.jpeg) { $('scrImg').src = 'data:image/jpeg;base64,' + r.jpeg; n++; }
          $('scrInfo').textContent = (r.locked ? '🔒 مقفلة · ' : '') + Math.round(performance.now() - t1) + 'ms';
        } catch (e) { $('scrInfo').textContent = '⚠ ' + e.message; if (/PIN/.test(e.message)) P.v = ''; await sleep(2000); }
        if (Date.now() - t0 > 10 * 60 * 1000) { $('scrInfo').textContent = 'توقّفت بعد 10 دقائق — افتح التبويب من جديد'; break; }
        await sleep(ms);
      }
      this.live = false;
    },
    stop() { this.live = false; this.gen++; },
  };

  // ---------------------------------------------------------------- التيرمينال
  const Term = {
    id: null, x: null, fit: null, since: 0, poll: false, wq: '', flushing: false,
    init() {
      $('p-term').innerHTML = '<div id="termWrap"><div class="tools"><select id="trShell"><option value="powershell">PowerShell</option><option value="claude">Claude Code</option><option value="cmd">CMD</option></select>' +
        '<button class="pri" id="trOpen">فتح جلسة</button><button id="trClose">إنهاء</button></div><div id="term"></div><div class="keys" id="trKeys"></div></div>';
      $('trOpen').onclick = () => this.open(); $('trClose').onclick = () => this.close();
      const keys = [['Esc', '\x1b'], ['Tab', '\t'], ['Ctrl+C', '\x03'], ['Ctrl+D', '\x04'], ['↑', '\x1b[A'], ['↓', '\x1b[B'], ['←', '\x1b[D'], ['→', '\x1b[C'], ['⏎', '\r'], ['Shift+Tab', '\x1b[Z'], ['/', '/'], ['|', '|'], ['~', '~']];
      for (const [l, s] of keys) { const b = document.createElement('button'); b.textContent = l; b.onclick = () => this.write(s); $('trKeys').append(b); }
    },
    mount() {
      if (this.x || !window.Terminal) return;
      this.x = new Terminal({ fontFamily: 'Consolas, "Cascadia Mono", monospace', fontSize: 13, cursorBlink: true, theme: { background: '#05080d', foreground: '#e8eef8', cursor: '#2dd4bf', selectionBackground: '#2dd4bf55' }, scrollback: 3000, allowProposedApi: true });
      this.fit = new FitAddon.FitAddon(); this.x.loadAddon(this.fit); this.x.open($('term'));
      this.x.onData((d) => this.write(d));
      new ResizeObserver(() => this.refit()).observe($('term'));
    },
    show() { this.mount(); setTimeout(() => this.refit(), 60); },
    refit() { if (!this.x || S.tab !== 'term') return; try { this.fit.fit(); } catch { return; } if (this.id) call('term_resize', { id: this.id, cols: this.x.cols, rows: this.x.rows }, pin()).catch(() => { }); },
    async open() {
      this.mount(); if (this.id) await this.close();
      try {
        this.fit.fit();
        const r = await act('term_open', { shell: $('trShell').value, cols: this.x.cols, rows: this.x.rows });
        this.id = r.id; this.since = 0; this.x.reset(); this.loop();
      } catch { }
    },
    async close() { const id = this.id; this.id = null; if (id) { try { await call('term_close', { id }, pin()); } catch { } } },
    write(s) { if (!this.id) return; this.wq += s; if (!this.flushing) this.flush(); },
    async flush() {
      this.flushing = true;
      while (this.wq && this.id) { const d = this.wq; this.wq = ''; try { await call('term_write', { id: this.id, data: d }, pin()); } catch (e) { toast('⚠ ' + e.message); } }
      this.flushing = false;
    },
    async loop() {
      if (this.poll) return; this.poll = true; let idle = 0;
      while (this.id) {
        if (document.hidden || S.tab !== 'term') { await sleep(600); continue; }
        try {
          const r = await call('term_read', { id: this.id, since: this.since }, pin());
          if (r.reset) this.x.reset();
          this.since = r.next;
          if (r.data) { this.x.write(unb64(r.data)); idle = 0; } else idle++;
          if (r.exited) { this.x.writeln('\r\n\x1b[33m[انتهت الجلسة]\x1b[0m'); this.id = null; break; }
        } catch (e) { toast('⚠ ' + e.message); await sleep(1500); }
        await sleep(idle > 8 ? 700 : 160);
      }
      this.poll = false;
    },
  };

  // ---------------------------------------------------------------- الداشبورد
  const Dash = {
    t: null, hist: { cpu: [], rx: [], tx: [] },
    init() { $('p-dash').innerHTML = '<div class="top"><h1 id="dName">الجهاز</h1><span class="dot" id="dDot"></span></div><div id="dBody"><div class="card mut">جارٍ التحميل…</div></div>'; },
    start() { if (this.t) return; const tick = async () => { if (S.tab !== 'dash') return; if (!document.hidden) await this.refresh(); this.t = setTimeout(tick, 2500); }; tick(); },
    stop() { clearTimeout(this.t); this.t = null; },
    gauge(p, label, color) {
      const R = 46, C = 2 * Math.PI * R, v = Math.max(0, Math.min(100, p || 0));
      return '<div class="gauge"><svg width="110" height="110"><circle cx="55" cy="55" r="' + R + '" stroke="var(--line)" stroke-width="9" fill="none"/><circle cx="55" cy="55" r="' + R + '" stroke="' + color + '" stroke-width="9" fill="none" stroke-linecap="round" stroke-dasharray="' + C + '" stroke-dashoffset="' + (C * (1 - v / 100)) + '" style="transition:stroke-dashoffset .6s"/></svg><b>' + Math.round(v) + '%</b></div><div style="text-align:center" class="mut">' + label + '</div>';
    },
    spark(a, color, max) {
      if (a.length < 2) return '<svg class="spark"></svg>'; const m = max || Math.max(1, ...a), w = 300, h = 60;
      const pts = a.map((v, i) => (i / (a.length - 1) * w).toFixed(1) + ',' + (h - 4 - v / m * (h - 8)).toFixed(1)).join(' ');
      return '<svg class="spark" viewBox="0 0 ' + w + ' ' + h + '" preserveAspectRatio="none"><polyline points="' + pts + '" fill="none" stroke="' + color + '" stroke-width="2.2" stroke-linejoin="round"/></svg>';
    },
    async refresh() {
      try {
        const [t, s] = await Promise.all([call('telemetry'), call('status')]);
        $('dDot').classList.add('ok'); $('dName').textContent = s.machine;
        const push = (k, v) => { if (v == null) return; this.hist[k].push(v); if (this.hist[k].length > 60) this.hist[k].shift(); };
        push('cpu', t.cpu); push('rx', t.rxBps); push('tx', t.txBps);
        const ramPct = t.ramTotalKb ? 100 * (1 - t.ramFreeKb / t.ramTotalKb) : 0;
        const temps = (t.temps || []).map((x) => x.c).filter((c) => c > 0 && c < 130), tmax = temps.length ? Math.max(...temps) : null;
        const disks = (t.disks || []).map((d) => { const used = d.total - d.free, p = 100 * used / d.total; return '<div class="row"><span>' + d.name + '</span><span class="mut">' + fmtB(used) + ' / ' + fmtB(d.total) + '</span></div><div class="bar"><i style="width:' + p.toFixed(0) + '%"></i></div>'; }).join('');
        const batt = s.battery != null && s.battery >= 0 ? '<div class="row"><span>🔋 البطارية</span><b>' + s.battery + '%' + (s.ac ? ' ⚡' : '') + '</b></div>' : '';
        $('dBody').innerHTML =
          '<div class="grid"><div class="card">' + this.gauge(t.cpu, 'المعالج', 'var(--teal)') + '</div><div class="card">' + this.gauge(ramPct, 'الذاكرة · ' + fmtB(t.ramTotalKb * 1024), 'var(--org)') + '</div></div>' +
          '<div class="card"><h3>🌡 الحرارة</h3><div class="row"><b style="font-size:26px;color:' + (tmax > 85 ? 'var(--red)' : tmax > 70 ? 'var(--org)' : 'var(--grn)') + '">' + (tmax != null ? tmax.toFixed(0) + '°C' : '—') + '</b><span class="mut">' + (tmax == null ? 'الجهاز لا يكشف الحرارة' : 'أعلى منطقة') + '</span></div></div>' +
          '<div class="card"><h3>📶 الشبكة</h3><div class="row"><span>⬇ تنزيل</span><b>' + fmtRate(t.rxBps || 0) + '</b></div>' + this.spark(this.hist.rx, 'var(--teal)') + '<div class="row"><span>⬆ رفع</span><b>' + fmtRate(t.txBps || 0) + '</b></div>' + this.spark(this.hist.tx, 'var(--org)') + '</div>' +
          '<div class="card"><h3>📈 المعالج (آخر دقيقتين)</h3>' + this.spark(this.hist.cpu, 'var(--teal)', 100) + '</div>' +
          '<div class="card"><h3>💾 التخزين</h3>' + disks + '</div>' +
          '<div class="card"><h3>ℹ النظام</h3>' + batt + '<div class="row"><span>التشغيل منذ</span><b>' + Math.floor(s.uptimeSec / 3600) + ' ساعة</b></div><div class="row"><span>النظام</span><span class="mut">' + s.os + '</span></div></div>' +
          '<div class="card"><h3>⚙ أعلى العمليات</h3><div id="dProcs" class="mut">…</div></div>';
        call('processes').then((p) => { const el = $('dProcs'); if (el) el.innerHTML = p.items.slice(0, 8).map((x) => '<div class="row"><span>' + x.name.replace(/</g, '') + '</span><span>' + fmtB(x.mem) + '</span></div>').join(''); }).catch(() => { });
      } catch (e) { $('dDot').classList.remove('ok'); $('dBody').innerHTML = '<div class="card">⚠ ' + e.message + '</div>'; }
    },
  };

  // ---------------------------------------------------------------- تحكم (سكون/قفل/طاقة)
  const Ctl = {
    init() {
      $('p-ctl').innerHTML = '<div class="top"><h1>تحكم</h1><button id="thBtn">🌓</button></div>' +
        '<div class="card"><h3>☀ إبقاء الجهاز مستيقظاً</h3><div class="row"><span class="mut" id="awInfo">…</span></div><div class="tools"><select id="awH"><option value="1">ساعة</option><option value="2">ساعتان</option><option value="4">4 ساعات</option><option value="8" selected>8 ساعات</option></select><button class="pri" id="awOn">امنع السكون</button><button id="awOff">أوقف</button></div></div>' +
        '<div class="card"><h3>⚡ الطاقة</h3><div class="tools"><button id="bLock">🔒 قفل</button><button id="bSleep">😴 سكون</button><button class="warn" id="bRestart">↻ إعادة تشغيل</button><button class="warn" id="bShut">⏻ إيقاف</button></div></div>' +
        '<div class="card"><h3>🔐 هذا الجوال</h3><div class="tools"><button id="bBio">👆 قفل بالبصمة</button><button id="bPin">نسيان PIN</button><button class="warn" id="bUnpair">إلغاء الاقتران</button></div></div>';
      $('awOn').onclick = async () => { await act('awake_on', { hours: +$('awH').value }, 'الجهاز ما رح ينام'); this.info(); };
      $('awOff').onclick = async () => { await call('awake_off'); toast('أُوقف منع السكون'); this.info(); };
      $('bLock').onclick = () => act('lock', {}, 'قُفل');
      $('bSleep').onclick = () => confirm('سكون الجهاز؟ بتنقطع السيطرة حتى يصحى.') && act('sleep', {}, 'سكون');
      $('bRestart').onclick = () => confirm('إعادة تشغيل الجهاز؟') && act('restart', {}, 'يعيد التشغيل');
      $('bShut').onclick = () => confirm('إيقاف الجهاز؟ ما بتقدر تشغّله عن بُعد.') && act('shutdown', {}, 'يُطفأ');
      $('bPin').onclick = () => { P.v = ''; toast('نُسي الـPIN'); };
      $('bUnpair').onclick = () => { if (confirm('إلغاء الاقتران ومسح السرّ من هذا الجوال؟')) { LS.del('raqeeb.secret'); LS.del('raqeeb.cred'); location.reload(); } };
      $('thBtn').onclick = () => { const l = document.documentElement.dataset.theme !== 'light'; document.documentElement.dataset.theme = l ? 'light' : 'dark'; LS.set('raqeeb.theme', l ? 'light' : 'dark'); };
      $('bBio').onclick = async () => { try { await bio.enroll(); toast('فُعّل القفل بالبصمة'); } catch { toast('تعذّر تفعيل البصمة'); } };
    },
    async info() { try { const s = await call('status'); const a = s.awake; $('awInfo').textContent = a && a.on ? 'مفعّل ✓ حتى ' + (a.until ? new Date(a.until).toLocaleTimeString('ar', { hour: '2-digit', minute: '2-digit' }) : '—') : 'غير مفعّل'; } catch (e) { $('awInfo').textContent = e.message; } },
  };

  // ---------------------------------------------------------------- القفل بالبصمة والاقتران
  const bio = {
    available: async () => !!(window.PublicKeyCredential && PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable && await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable().catch(() => false)),
    enabled: () => !!LS.get('raqeeb.cred'),
    async enroll() { const c = await navigator.credentials.create({ publicKey: { challenge: crypto.getRandomValues(new Uint8Array(32)), rp: { name: 'Raqeeb' }, user: { id: crypto.getRandomValues(new Uint8Array(16)), name: 'raqeeb', displayName: 'Raqeeb' }, pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }], authenticatorSelection: { authenticatorAttachment: 'platform', userVerification: 'required', residentKey: 'discouraged' }, timeout: 60000 } }); LS.set('raqeeb.cred', u82hex(new Uint8Array(c.rawId))); },
    async verify() { await navigator.credentials.get({ publicKey: { challenge: crypto.getRandomValues(new Uint8Array(32)), userVerification: 'required', timeout: 60000, allowCredentials: [{ type: 'public-key', id: hex2u8(LS.get('raqeeb.cred')) }] } }); },
  };
  function view(id) { for (const x of ['login', 'shell']) $(x).hidden = x !== id; }
  function pairScreen(msg) {
    view('login');
    $('loginBox').innerHTML = '<h1>رقيب</h1><p class="mut">امسح رمز QR من برنامج رقيب على الكمبيوتر بكاميرا الجوال، أو الصق السرّ هنا.</p><input id="sec" type="password" autocomplete="off" placeholder="السرّ الرئيسي (64 حرفاً)"><button class="pri" id="go" style="width:100%">اقتران</button><p class="mut" id="msg">' + (msg || '') + '</p>';
    $('go').onclick = () => { const v = $('sec').value.trim().toLowerCase(); if (!/^[0-9a-f]{64}$/.test(v)) { $('msg').textContent = 'السرّ لازم يكون 64 حرف hex'; return; } LS.set('raqeeb.secret', v); location.reload(); };
  }
  function lockScreen() {
    view('login');
    $('loginBox').innerHTML = '<h1>رقيب</h1><p class="mut">مقفل — أثبت هويتك</p><button class="pri" id="go" style="width:100%">🔓 افتح بالبصمة</button><p class="mut" id="msg"></p>';
    const run = async () => { try { await bio.verify(); await start(); } catch { $('msg').textContent = 'لم يتم التحقق'; } };
    $('go').onclick = run; run();
  }
  async function start() {
    S.master = hex2u8(LS.get('raqeeb.secret')); await initKeys(); view('shell'); buildNav();
    Screen.init(); Term.init(); Dash.init(); Ctl.init();
    try { const ds = await devices(); if (!ds.length) { toast('ما في جهاز متصل — شغّل رقيب على الكمبيوتر'); } else { S.dev = ds[0].id; } } catch (e) { toast('⚠ ' + e.message); }
    go('dash'); Ctl.info();
  }
  async function boot() {
    const th = LS.get('raqeeb.theme'); if (th) document.documentElement.dataset.theme = th;
    const m = /[#&]k=([0-9a-fA-F]{64})/.exec(location.hash);
    if (m) { LS.set('raqeeb.secret', m[1].toLowerCase()); history.replaceState(null, '', location.pathname + location.search); }
    if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => { });
    if (!LS.get('raqeeb.secret')) return pairScreen();
    if (bio.enabled()) return lockScreen();
    start();
  }
  window.addEventListener('load', boot);
})();
