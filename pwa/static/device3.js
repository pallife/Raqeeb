'use strict';
/* جهازي — الجزء الثالث: شاشة تحكم كاملة (Remote Desktop) للجوال.
   شاشة الجهاز تملأ الجوال، أزرار شفافة عائمة، وضع لمس مباشر أو تراك باد (نصف الشاشة أو أقل)،
   زوم بالقرص/أزرار مع تحريك، وكيبورد ذكي لا يدفع الشاشة. يعيد استخدام قناة device.js (مشفّرة + PIN). */
(function () {
  const { h, toast } = window.CP;
  const R = { el: null, img: null, pan: null, stage: null, pad: null, grab: null, cur: null, tb: null, kb: null, status: null, keys: null,
    s: 1, tx: 0, ty: 0, mode: 'touch', tpH: 0.38, cx: 0.5, cy: 0.5, once: null, scroll: false,
    quick: false, qBusy: false, priv: false, pcolor: 0, lastTap: null, flashT: 0,
    ptr: new Map(), g: null, buf: '', bufT: 0, mvBusy: false, mvDirty: false, obs: null, open: false, X: null, send: null, timer: 0 };

  const CSS = `
  #rd{position:fixed;left:0;right:0;top:0;height:100dvh;background:#000;z-index:10000;display:flex;flex-direction:column;
      -webkit-user-select:none;user-select:none;overscroll-behavior:none;touch-action:none;color:#fff;font-size:14px}
  #rd .rd-stage{position:relative;flex:1;overflow:hidden;touch-action:none;min-height:0}
  #rd .rd-pan{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;transform-origin:50% 50%;will-change:transform}
  #rd .rd-pan img{max-width:100%;max-height:100%;display:block;-webkit-user-drag:none;pointer-events:none}
  #rd .rd-cur{position:absolute;width:12px;height:12px;margin:-6px 0 0 -6px;border:1.5px solid #fff;border-radius:50%;
      background:rgba(255,40,40,.25);box-shadow:0 0 0 1px #000,inset 0 0 0 1px #000;pointer-events:none;display:none}
  #rd .rd-cur::after{content:"";position:absolute;left:50%;top:50%;width:2px;height:2px;margin:-1px 0 0 -1px;background:#f22;border-radius:50%}
  #rd .rd-tb button.q.on{background:rgba(30,170,90,.8)}
  #rd .rd-tb button.pv.on{background:rgba(190,60,60,.8)}
  #rd .rd-tb{position:absolute;top:calc(env(safe-area-inset-top,0px) + 6px);left:6px;right:6px;display:flex;flex-wrap:wrap;gap:5px;
      justify-content:center;pointer-events:none;z-index:3}
  #rd .rd-tb button,#rd .rd-keys button{pointer-events:auto;min-width:38px;height:38px;padding:0 10px;border-radius:19px;border:1px solid rgba(255,255,255,.25);
      background:rgba(24,24,30,.42);color:#fff;font:inherit;font-size:15px;backdrop-filter:blur(8px);-webkit-backdrop-filter:blur(8px);opacity:.62}
  #rd .rd-tb button:active,#rd .rd-keys button:active{opacity:1;background:rgba(80,120,255,.7)}
  #rd .rd-tb button.on{opacity:.95;background:rgba(60,110,255,.65)}
  #rd .rd-tb .rd-st{pointer-events:none;align-self:center;font-size:11px;opacity:.65;background:rgba(0,0,0,.35);padding:2px 8px;border-radius:10px}
  #rd .rd-keys{position:absolute;left:6px;right:6px;top:calc(env(safe-area-inset-top,0px) + 92px);display:flex;flex-wrap:wrap;gap:5px;
      justify-content:center;z-index:3;max-height:40%;overflow:auto;pointer-events:none}
  #rd .rd-grab{height:22px;background:rgba(255,255,255,.08);display:flex;align-items:center;justify-content:center;touch-action:none;flex:none}
  #rd .rd-grab i{width:46px;height:5px;border-radius:3px;background:rgba(255,255,255,.45)}
  #rd .rd-pad{flex:none;background:rgba(255,255,255,.05);touch-action:none;display:flex;align-items:center;justify-content:center;
      color:rgba(255,255,255,.35);font-size:13px;border-top:1px solid rgba(255,255,255,.12);text-align:center;padding:0 12px}
  #rd .rd-kb{position:fixed;left:0;bottom:0;width:1px;height:1px;opacity:.01;font-size:16px;border:0;padding:0;resize:none;background:transparent;color:transparent;caret-color:transparent}
  `;

  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const E = (tag, props, ...kids) => h(tag, props || {}, ...kids);
  const KEYS = [['Esc', 'esc'], ['Tab', 'tab'], ['Enter', 'enter'], ['⌫', 'backspace'], ['Del', 'delete'], ['Space', 'space'],
    ['↑', 'up'], ['↓', 'down'], ['←', 'left'], ['→', 'right'], ['Home', 'home'], ['End', 'end'], ['PgUp', 'pageup'], ['PgDn', 'pagedown'],
    ['Win', 'win'], ['F5', 'f5'], ['Alt+Tab', 'tab', ['alt']], ['Ctrl+C', 'c', ['ctrl']], ['Ctrl+V', 'v', ['ctrl']], ['Ctrl+A', 'a', ['ctrl']],
    ['Ctrl+Z', 'z', ['ctrl']], ['Ctrl+S', 's', ['ctrl']], ['Alt+F4', 'f4', ['alt']], ['🔉', 'voldown'], ['🔊', 'volup'], ['🔇', 'mute'], ['⏯', 'playpause']];

  function flush() { clearTimeout(R.bufT); R.bufT = 0; if (R.buf) { const t = R.buf; R.buf = ''; R.send({ kind: 'text', text: t }); } }
  function key(name, mods) { flush(); R.send({ kind: 'key', name, mods: mods || [] }); }
  function typed(t) { R.buf += t; clearTimeout(R.bufT); R.bufT = setTimeout(flush, 45); }

  function applyT() {
    if (!R.stage) return;
    const w = R.stage.clientWidth, hh = R.stage.clientHeight, m = R.s - 1;
    R.tx = clamp(R.tx, -m * w / 2 - 40, m * w / 2 + 40); R.ty = clamp(R.ty, -m * hh / 2 - 40, m * hh / 2 + 40);
    if (R.s === 1) { R.tx = 0; R.ty = 0; }
    R.pan.style.transform = 'translate(' + R.tx + 'px,' + R.ty + 'px) scale(' + R.s + ')';
    drawCursor();
  }
  function zoomAt(fx, fy, s2) {
    s2 = clamp(s2, 1, 8);
    const w = R.stage.clientWidth, hh = R.stage.clientHeight, cx = w / 2, cy = hh / 2, k = s2 / R.s;
    R.tx = fx - cx - k * (fx - cx - R.tx); R.ty = fy - cy - k * (fy - cy - R.ty); R.s = s2; applyT();
  }
  const imgRect = () => R.img.getBoundingClientRect();
  function norm(clientX, clientY) {
    const r = imgRect(); if (!r.width || !r.height) return null;
    return { x: clamp((clientX - r.left) / r.width, 0, 1), y: clamp((clientY - r.top) / r.height, 0, 1) };
  }
  function drawCursor() {
    if (!R.cur) return;
    if (R.mode !== 'pad') { R.cur.style.display = 'none'; return; }
    const r = imgRect(), sr = R.stage.getBoundingClientRect();
    R.cur.style.display = 'block';
    R.cur.style.left = (r.left - sr.left + R.cx * r.width) + 'px';
    R.cur.style.top = (r.top - sr.top + R.cy * r.height) + 'px';
  }
  function sendMove() {
    if (R.mvBusy) { R.mvDirty = true; return; }
    R.mvBusy = true;
    R.send({ kind: 'move', x: R.cx, y: R.cy }).then(() => { R.mvBusy = false; if (R.mvDirty) { R.mvDirty = false; sendMove(); } });
  }
  const takeOnce = () => { const o = R.once; if (o) { R.once = null; syncTb(); } return o; };
  function flash(msg) {
    if (!R.status) return;
    R.status.textContent = msg; R.status.dataset.flash = '1';
    clearTimeout(R.flashT); R.flashT = setTimeout(() => { delete R.status.dataset.flash; }, 4500);
  }
  /** ضغطتان سريعتان في مكان واحد = مربّع نص غالباً: افتح الكيبورد (ويُخفى التراك باد تلقائياً). */
  function doubleTapKb(cx, cy) {
    const n = Date.now(), L = R.lastTap;
    R.lastTap = { t: n, x: cx, y: cy };
    if (L && n - L.t < 420 && Math.hypot(cx - L.x, cy - L.y) < 34) { R.lastTap = null; try { R.kb.focus(); } catch { } syncTb(); setTimeout(applyT, 60); }
  }
  function clickAt(x, y, held) {
    const o = takeOnce();
    const right = o === 'right' || held > 550;
    const p = R.send({ kind: 'click', x, y, button: right ? 'right' : 'left', count: o === 'double' ? 2 : 1 });
    if (R.quick && !right && !o) quickSend(p);
  }
  /** الإرسال السريع: بعد النقر (تحديد عنصر) ← Ctrl+C على الجهاز ← الحافظة إلى تلغرام. */
  async function quickSend(after) {
    if (R.qBusy) return;
    R.qBusy = true; flash('✈ …');
    try {
      await after; await new Promise(r => setTimeout(r, 220));
      const r = await window.CPDeviceExtra.run('quick_send', {});
      flash(r && r.sent ? '✈ ' + r.done : '— ' + ((r && r.done) || 'لم يُرسل'));
    } catch (e) { if (e.message !== 'no-pin') flash('⚠ ' + e.message); }
    R.qBusy = false;
  }
  async function togglePrivacy(x, on) {
    x = x || R.X;
    const want = on === undefined ? !R.priv : on;
    const colors = ['#000000', '#0b1b3a', '#2a2a2a', '#3a0b0b', '#0b3a1a'];
    try {
      const r = await window.CPDeviceExtra.run('privacy', { on: want, color: colors[R.pcolor % colors.length] });
      R.priv = !!(r && r.on);
      const m = !R.priv ? '🕶 أُزيل الغطاء' : (r.excluded ? '🕶 الغطاء يعمل — أنت ترى الشاشة الحقيقية' : '⚠ الغطاء يعمل لكن يظهر أسود عندك أيضاً (ويندوز قديم)');
      flash(m); toast(m);
    } catch (e) { if (e.message !== 'no-pin') { flash('⚠ ' + e.message); toast('⚠ ' + e.message); } }
    syncTb();
    try { (x || R.X).redraw(); } catch { }
  }

  // ---------- اللمس على الشاشة
  function stageGesture(el) {
    el.addEventListener('pointerdown', (e) => {
      e.preventDefault(); try { el.setPointerCapture(e.pointerId); } catch { }
      R.ptr.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (R.ptr.size === 2) {
        const [a, b] = [...R.ptr.values()], sr = R.stage.getBoundingClientRect();
        R.g = { pinch: true, d0: Math.hypot(a.x - b.x, a.y - b.y) || 1, s0: R.s, tx0: R.tx, ty0: R.ty,
          mx: (a.x + b.x) / 2 - sr.left, my: (a.y + b.y) / 2 - sr.top, moved: true };
      } else if (R.ptr.size === 1) {
        R.g = { x0: e.clientX, y0: e.clientY, t0: Date.now(), tx0: R.tx, ty0: R.ty, moved: false, lastY: e.clientY, acc: 0 };
      }
    });
    el.addEventListener('pointermove', (e) => {
      if (!R.ptr.has(e.pointerId) || !R.g) return;
      R.ptr.set(e.pointerId, { x: e.clientX, y: e.clientY });
      const g = R.g, sr = R.stage.getBoundingClientRect();
      if (g.pinch && R.ptr.size >= 2) {
        const [a, b] = [...R.ptr.values()], d = Math.hypot(a.x - b.x, a.y - b.y) || 1;
        const mx = (a.x + b.x) / 2 - sr.left, my = (a.y + b.y) / 2 - sr.top, s2 = clamp(g.s0 * d / g.d0, 1, 8);
        const cx = sr.width / 2, cy = sr.height / 2, k = s2 / g.s0;
        R.tx = mx - cx - k * (g.mx - cx - g.tx0); R.ty = my - cy - k * (g.my - cy - g.ty0); R.s = s2; applyT();
        return;
      }
      if (g.pinch) return;
      const dx = e.clientX - g.x0, dy = e.clientY - g.y0;
      if (!g.moved && Math.hypot(dx, dy) > 8) g.moved = true;
      if (!g.moved) return;
      if (R.scroll && R.mode === 'touch') {
        g.acc += g.lastY - e.clientY; g.lastY = e.clientY;
        if (Math.abs(g.acc) > 22) { R.send({ kind: 'scroll', dy: Math.sign(g.acc) * clamp(Math.round(Math.abs(g.acc) / 22), 1, 15) }); g.acc = 0; }
      } else if (R.once !== 'drag') {
        R.tx = g.tx0 + dx; R.ty = g.ty0 + dy; applyT();
      }
    });
    const up = (e) => {
      if (!R.ptr.has(e.pointerId)) return;
      R.ptr.delete(e.pointerId);
      const g = R.g;
      if (R.ptr.size === 0) R.g = null;
      if (!g || g.pinch) return;
      const held = Date.now() - g.t0;
      if (!g.moved) {
        const p = norm(e.clientX, e.clientY); if (!p) return;
        doubleTapKb(e.clientX, e.clientY);
        if (R.mode === 'pad') { R.cx = p.x; R.cy = p.y; drawCursor(); sendMove(); }
        else clickAt(p.x, p.y, held);
      } else if (R.once === 'drag') {
        const a = norm(g.x0, g.y0), b = norm(e.clientX, e.clientY); takeOnce();
        if (a && b) R.send({ kind: 'drag', x: a.x, y: a.y, x2: b.x, y2: b.y });
      }
    };
    el.addEventListener('pointerup', up); el.addEventListener('pointercancel', up);
    el.addEventListener('wheel', (e) => { e.preventDefault(); const sr = R.stage.getBoundingClientRect(); zoomAt(e.clientX - sr.left, e.clientY - sr.top, R.s * Math.exp(clamp(-e.deltaY, -300, 300) * 0.003)); }, { passive: false });
    el.addEventListener('contextmenu', (e) => e.preventDefault());
  }

  // ---------- التراك باد
  function padGesture(el) {
    // سلوك قريب من تراك باد أبل:
    //  1 إصبع: مؤشر بتسارع حسب السرعة (بطيء = دقيق، سريع = يقطع الشاشة) · نقرة خفيفة = نقر · لمسة مطوّلة = زر أيمن
    //  نقرة ثم لمسة فورية وسحب = سحب بالزر مضغوطاً (للنوافذ والتحديد)
    //  2 إصبعان: تمرير عمودي وأفقي · نقرة بإصبعين = زر أيمن
    //  3 أصابع: سحب يمين/يسار = Alt+Tab · لأعلى = عرض المهام (Win+Tab) · لأسفل = Esc
    //  4 أصابع: لأعلى/أسفل = إظهار سطح المكتب (Win+D) · يمين/يسار = تبديل أسطح المكتب الافتراضية
    const pts = new Map(); let g = null, lastTap = 0;
    const centroid = () => { let x = 0, y = 0; for (const p of pts.values()) { x += p.x; y += p.y; } const n = pts.size || 1; return { x: x / n, y: y / n }; };
    function swipe(fingers, tx, ty) {
      const horiz = Math.abs(tx) > Math.abs(ty);
      if (fingers === 3) {
        if (horiz) { key('tab', tx > 0 ? ['alt'] : ['alt', 'shift']); flash('⇥ تبديل التطبيق'); }
        else if (ty < 0) { key('tab', ['win']); flash('▦ عرض المهام'); }
        else { key('esc'); flash('⎋ Esc'); }
      } else {
        if (!horiz) { key('d', ['win']); flash('🖥 سطح المكتب'); }
        else { key(tx > 0 ? 'right' : 'left', ['ctrl', 'win']); flash('⇄ سطح مكتب افتراضي'); }
      }
    }
    el.addEventListener('pointerdown', (e) => {
      e.preventDefault(); try { el.setPointerCapture(e.pointerId); } catch { }
      pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      const c = centroid();
      if (!g) {
        g = { t0: Date.now(), fingers: 1, moved: false, cx0: c.x, cy0: c.y, lx: c.x, ly: c.y, lt: performance.now(),
          sx: R.cx, sy: R.cy, scrollY: 0, scrollX: 0, fired: false, dragging: false, checked: false, tapDrag: Date.now() - lastTap < 350 };
      } else {
        g.fingers = Math.max(g.fingers, pts.size);
        g.cx0 = c.x; g.cy0 = c.y; g.lx = c.x; g.ly = c.y; g.lt = performance.now(); g.moved = false; g.scrollY = g.scrollX = 0;
      }
    });
    el.addEventListener('pointermove', (e) => {
      const p = pts.get(e.pointerId); if (!p || !g) return;
      p.x = e.clientX; p.y = e.clientY;
      const c = centroid(), n = pts.size, tx = c.x - g.cx0, ty = c.y - g.cy0;
      if (!g.moved && Math.hypot(tx, ty) > 7) g.moved = true;
      if (!g.moved) return;
      const now = performance.now(), dt = Math.max(1, now - g.lt), dx = c.x - g.lx, dy = c.y - g.ly;
      g.lx = c.x; g.ly = c.y; g.lt = now;
      if (g.fingers >= 3) {
        if (!g.fired && n >= 3 && Math.hypot(tx, ty) > 55) { g.fired = true; swipe(Math.min(g.fingers, 4), tx, ty); }
        return;
      }
      if (n >= 2) {
        g.scrollY += -dy; g.scrollX += -dx;
        const TH = 11;
        if (Math.abs(g.scrollY) >= TH) { const k = Math.trunc(g.scrollY / TH); g.scrollY -= k * TH; R.send({ kind: 'scroll', dy: clamp(k, -15, 15) }); }
        if (Math.abs(g.scrollX) >= TH * 1.6) { const k = Math.trunc(g.scrollX / (TH * 1.6)); g.scrollX -= k * TH * 1.6; R.send({ kind: 'scroll', dx: clamp(k, -15, 15) }); }
        return;
      }
      const r = imgRect(); if (!r.width) return;
      if (!g.checked) {
        g.checked = true;
        if (g.tapDrag && R.once !== 'drag') { g.dragging = true; R.send({ kind: 'down', x: R.cx, y: R.cy, button: 'left' }); flash('✥ سحب'); }
      }
      const v = Math.hypot(dx, dy) / dt;                     // px/ms
      const gain = clamp(0.85 + v * 1.4, 0.85, 3.2);         // تسارع يشبه أبل
      R.cx = clamp(R.cx + dx * gain / r.width, 0, 1); R.cy = clamp(R.cy + dy * gain / r.height, 0, 1);
      drawCursor(); sendMove();
    });
    const up = (e) => {
      if (!pts.has(e.pointerId)) return;
      pts.delete(e.pointerId);
      if (pts.size > 0 || !g) return;
      const gg = g; g = null; const held = Date.now() - gg.t0;
      if (gg.dragging) { R.send({ kind: 'up', button: 'left' }); lastTap = 0; return; }
      if (R.once === 'drag' && gg.moved && gg.fingers === 1) { takeOnce(); R.send({ kind: 'drag', x: gg.sx, y: gg.sy, x2: R.cx, y2: R.cy }); return; }
      if (gg.moved) return;
      if (gg.fingers === 2 && held < 450) { takeOnce(); R.send({ kind: 'click', x: R.cx, y: R.cy, button: 'right', count: 1 }); return; }
      if (gg.fingers === 1) { lastTap = Date.now(); doubleTapKb(e.clientX, e.clientY); clickAt(R.cx, R.cy, held); }
    };
    el.addEventListener('pointerup', up); el.addEventListener('pointercancel', up);
  }

  // ---------- الكيبورد الذكي: حقل مخفي 1px — الصفحة لا تقفز، والشاشة تتقلّص بمقدار الكيبورد فقط
  function kbSetup() {
    const kb = E('textarea', { class: 'rd-kb', autocapitalize: 'off', autocomplete: 'off', autocorrect: 'off', spellcheck: 'false', dir: 'auto' });
    kb.addEventListener('beforeinput', (e) => {
      const t = e.inputType || '';
      if (t === 'insertText' || t === 'insertFromPaste' || t === 'insertReplacementText') { if (e.data) typed(e.data); e.preventDefault(); }
      else if (t === 'insertLineBreak' || t === 'insertParagraph') { key('enter'); e.preventDefault(); }
      else if (t.startsWith('delete')) { key('backspace'); e.preventDefault(); }
    });
    kb.addEventListener('input', () => { if (kb.value) { typed(kb.value); kb.value = ''; } });
    kb.addEventListener('keydown', (e) => {
      const m = { Enter: 'enter', Backspace: 'backspace', Tab: 'tab', Escape: 'esc', ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Delete: 'delete' }[e.key];
      if (m) { key(m); e.preventDefault(); }
    });
    kb.addEventListener('blur', () => syncTb()); kb.addEventListener('focus', () => syncTb());
    return kb;
  }
  function fitViewport() {
    if (!R.el) return;
    const vv = window.visualViewport;
    if (vv) { R.el.style.height = vv.height + 'px'; R.el.style.top = vv.offsetTop + 'px'; }
    applyT();
  }

  // ---------- الشريط الشفاف
  const btns = {};
  function syncTb() {
    if (!R.el) return;
    const set = (k, on) => btns[k] && btns[k].classList.toggle('on', !!on);
    set('kb', document.activeElement === R.kb); set('mode', R.mode === 'pad'); set('scroll', R.scroll);
    set('drag', R.once === 'drag'); set('dbl', R.once === 'double'); set('rt', R.once === 'right');
    set('keys', R.keys.style.display !== 'none');
    set('quick', R.quick); set('priv', R.priv);
    // الكيبورد مفتوح ← التراك باد يختفي ليأخذ الشاشةُ المساحة، ويعود عند إغلاقه
    const pad = R.mode === 'pad' && document.activeElement !== R.kb;
    R.pad.style.display = pad ? 'flex' : 'none'; R.grab.style.display = pad ? 'flex' : 'none';
    R.pad.style.height = Math.round(window.innerHeight * R.tpH) + 'px';
    btns.mode.textContent = R.mode === 'pad' ? '🖱' : '👆';
    drawCursor();
  }
  async function restartLive() {
    const x = R.X; if (!x) return;
    x.D.live = false;
    for (let i = 0; i < 80 && x.D.live; i++) await new Promise(r => setTimeout(r, 100));
    x.liveLoop();
  }
  function status() {
    if (!R.status || !R.X) return;
    const D = R.X.D;
    if (R.status.dataset.flash) return;
    R.status.textContent = D.liveErr ? '⚠ ' + D.liveErr : (D.live ? (D.liveInfo || '…') + ' · ' + R.s.toFixed(1) + '×' : '⏸ المعاينة متوقفة');
  }
  function build() {
    if (!document.getElementById('rdCss')) document.head.append(E('style', { id: 'rdCss' }, CSS));
    const B = (k, label, title, fn) => (btns[k] = E('button', { type: 'button', title, onclick: fn }, label));
    R.img = E('img', { alt: '' });
    R.pan = E('div', { class: 'rd-pan' }, R.img);
    R.cur = E('div', { class: 'rd-cur' });
    R.stage = E('div', { class: 'rd-stage' }, R.pan, R.cur);
    R.status = E('span', { class: 'rd-st' }, '');
    R.kb = kbSetup();
    R.keys = E('div', { class: 'rd-keys', style: 'display:none' },
      ...KEYS.map(([l, n, m]) => E('button', { type: 'button', onclick: () => key(n, m) }, l)));
    const q = ['fast', 'bal', 'good', 'hd'], ql = { fast: 'سريعة', bal: 'متوازنة', good: 'جيدة', hd: 'عالية' };
    const mid = () => [R.stage.clientWidth / 2, R.stage.clientHeight / 2];
    R.tb = E('div', { class: 'rd-tb' },
      B('kb', '⌨', 'كيبورد', () => { if (document.activeElement === R.kb) R.kb.blur(); else R.kb.focus(); syncTb(); }),
      B('mode', '👆', 'لمس مباشر / تراك باد', () => { R.mode = R.mode === 'pad' ? 'touch' : 'pad'; R.once = null; syncTb(); setTimeout(applyT, 30); }),
      B('zin', '＋', 'تكبير', () => zoomAt(...mid(), R.s * 1.4)),
      B('zout', '－', 'تصغير', () => zoomAt(...mid(), R.s / 1.4)),
      B('fit', '⤢', 'ملاءمة', () => { R.s = 1; applyT(); }),
      B('scroll', '⇅', 'وضع التمرير', () => { R.scroll = !R.scroll; syncTb(); }),
      B('drag', '✥', 'سحب (مرة واحدة)', () => { R.once = R.once === 'drag' ? null : 'drag'; syncTb(); }),
      B('dbl', '◎', 'نقر مزدوج (مرة)', () => { R.once = R.once === 'double' ? null : 'double'; syncTb(); }),
      B('rt', '⊙', 'زر أيمن (مرة)', () => { R.once = R.once === 'right' ? null : 'right'; syncTb(); }),
      B('keys', '⎋', 'مفاتيح خاصة', () => { R.keys.style.display = R.keys.style.display === 'none' ? 'flex' : 'none'; syncTb(); }),
      B('quick', '✈', 'إرسال سريع إلى تلغرام: اضغط على ملف/نص لإرساله فوراً', () => { R.quick = !R.quick; flash(R.quick ? '✈ الإرسال السريع مفعّل: كل نقرة على عنصر تُرسله إلى تلغرام' : '✈ أُوقف الإرسال السريع'); syncTb(); }),
      B('priv', '🕶', 'غطاء الشاشة على الجهاز (ضغطة للتشغيل/الإيقاف)', () => togglePrivacy()),
      B('pcol', '🎨', 'لون الغطاء', () => { R.pcolor = (R.pcolor + 1) % 5; if (R.priv) togglePrivacy(R.X, true); else flash('لون الغطاء تغيّر'); }),
      B('q', '◐', 'الجودة', () => { const D = R.X.D, i = (q.indexOf(D.liveQ) + 1) % q.length; D.liveQ = q[i]; toast('الجودة: ' + ql[q[i]]); restartLive(); }),
      B('x', '✕', 'إغلاق', close),
      R.status);
    R.grab = E('div', { class: 'rd-grab' }, E('i'));
    R.pad = E('div', { class: 'rd-pad' }, 'تراك باد — إصبع: مؤشر/نقر · نقرة ثم اسحب فوراً = سحب · إصبعان: تمرير/زر أيمن · 3 أصابع: تبديل التطبيقات · 4 أصابع: سطح المكتب');
    R.el = E('div', { id: 'rd' }, R.stage, R.grab, R.pad, R.tb, R.keys, R.kb);
    stageGesture(R.stage); padGesture(R.pad);
    let gy = false;
    R.grab.addEventListener('pointerdown', (e) => { e.preventDefault(); try { R.grab.setPointerCapture(e.pointerId); } catch { } gy = true; });
    R.grab.addEventListener('pointermove', (e) => { if (!gy) return; R.tpH = clamp((window.innerHeight - e.clientY) / window.innerHeight, 0.14, 0.62); syncTb(); applyT(); });
    R.grab.addEventListener('pointerup', () => { gy = false; });
    R.grab.addEventListener('pointercancel', () => { gy = false; });
    if (window.visualViewport) { visualViewport.addEventListener('resize', fitViewport); visualViewport.addEventListener('scroll', fitViewport); }
    window.addEventListener('resize', fitViewport);
    document.addEventListener('keydown', (e) => { if (R.open && e.key === 'Escape' && document.activeElement !== R.kb) close(); });
  }

  function open(x) {
    R.X = x;
    R.send = (a) => window.CPDeviceExtra.sendInput(a);
    if (!R.el) build();
    if (!document.body.contains(R.el)) document.body.append(R.el);
    R.open = true; R.s = 1; R.tx = R.ty = 0;
    btns.quick.classList.add('q'); btns.priv.classList.add('pv');
    document.documentElement.style.overflow = 'hidden';
    R.img.src = x.liveImg.getAttribute('src') || '';
    if (R.obs) R.obs.disconnect();
    R.obs = new MutationObserver(() => { const s = x.liveImg.getAttribute('src'); if (s && R.img.getAttribute('src') !== s) R.img.src = s; });
    R.obs.observe(x.liveImg, { attributes: true, attributeFilter: ['src'] });
    clearInterval(R.timer); R.timer = setInterval(status, 600);
    fitViewport(); syncTb();
    if (!x.D.live) x.liveLoop();
  }
  function close() {
    if (!R.open) return;
    R.open = false; clearInterval(R.timer); if (R.obs) R.obs.disconnect();
    try { R.kb.blur(); } catch { }
    flush();
    if (R.el) R.el.remove();
    document.documentElement.style.overflow = '';
    try { R.X.redraw(); } catch { }
  }

  window.CPRemote = { open, close, isOpen: () => R.open, privacy: togglePrivacy, isPrivate: () => R.priv, setPrivate: (v) => { R.priv = !!v; } };
})();
