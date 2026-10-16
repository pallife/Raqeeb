'use strict';
/* غلاف رقيب للجوال: اقتران بالسرّ الرئيسي، قفل بالبصمة (WebAuthn محلي)، ثم واجهة «جهازي» (device*.js).
   السرّ الرئيسي يُحفظ في هذا المتصفح فقط؛ يُشتق منه مفتاح التشفير (HKDF) ورمز الدخول، ولا يغادر الجوال. */
(function () {
  const $ = (id) => document.getElementById(id);
  const BASE = location.pathname.replace(/[^/]*$/, '');
  const enc = new TextEncoder();
  const LS = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { } },
    del(k) { try { localStorage.removeItem(k); } catch { } },
  };

  function h(tag, props, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(props || {})) {
      if (k === 'class') e.className = v; else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
      else if (v === true) e.setAttribute(k, ''); else if (v !== false && v != null) e.setAttribute(k, v);
    }
    for (const k of kids.flat()) if (k != null) e.append(k.nodeType ? k : document.createTextNode(String(k)));
    return e;
  }
  function toast(msg) { const t = $('toast'); t.textContent = msg; t.hidden = false; clearTimeout(toast._t); toast._t = setTimeout(() => { t.hidden = true; }, 3500); }

  const S = { key: null, token: null, rev: 0, data: { Prefs: { Agent: {} } }, group: 'device' };
  const hex2u8 = (s) => Uint8Array.from(s.match(/../g).map((x) => parseInt(x, 16)));
  const u82hex = (u) => Array.from(u, (b) => b.toString(16).padStart(2, '0')).join('');

  async function deriveToken(master) {
    const k = await crypto.subtle.importKey('raw', master, 'HKDF', false, ['deriveBits']);
    const bits = await crypto.subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(0), info: enc.encode('control-panel/web-auth/v1') }, k, 256);
    return u82hex(new Uint8Array(bits));
  }

  // ------------------------------------------------------------- عرض الصفحة (يشبه CPX.render في لوحة التحكم)
  const X = { force: false, last: '' };
  function render() {
    const view = $('view');
    const ae = document.activeElement;
    if (!X.force && ae && view.contains(ae) && /^(INPUT|TEXTAREA|SELECT)$/.test(ae.tagName)) return;
    X.force = false;
    const box = h('div', {});
    (window.CPX._views.device)(box);
    view.replaceChildren(box);
  }
  window.CPX = { render, _state: X, _views: {} };
  window.CP = { S, h, $, toast, render, BASE };

  // ------------------------------------------------------------- القفل بالبصمة (WebAuthn، قفل محلي للتطبيق)
  const bio = {
    available: async () => !!(window.PublicKeyCredential && PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable && await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable().catch(() => false)),
    enabled: () => !!LS.get('raqeeb.cred'),
    async enroll() {
      const cred = await navigator.credentials.create({ publicKey: {
        challenge: crypto.getRandomValues(new Uint8Array(32)), rp: { name: 'Raqeeb' },
        user: { id: crypto.getRandomValues(new Uint8Array(16)), name: 'raqeeb', displayName: 'Raqeeb' },
        pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }],
        authenticatorSelection: { authenticatorAttachment: 'platform', userVerification: 'required', residentKey: 'discouraged' }, timeout: 60000 } });
      LS.set('raqeeb.cred', u82hex(new Uint8Array(cred.rawId)));
    },
    async verify() {
      await navigator.credentials.get({ publicKey: {
        challenge: crypto.getRandomValues(new Uint8Array(32)), userVerification: 'required', timeout: 60000,
        allowCredentials: [{ type: 'public-key', id: hex2u8(LS.get('raqeeb.cred')) }] } });
    },
  };

  // ------------------------------------------------------------- الشاشات
  function showOnly(id) { for (const x of ['login', 'app']) $(x).hidden = x !== id; }

  function pairScreen(msg) {
    showOnly('login');
    const inp = h('input', { type: 'password', autocomplete: 'off', placeholder: 'الصق السرّ الرئيسي (64 حرفاً) أو افتح رابط الاقتران' });
    const m = h('p', { class: 'msg' }, msg || '');
    $('loginBox').replaceChildren(
      h('h1', {}, 'رقيب'),
      h('p', { class: 'hint' }, 'اقتران هذا الجوال بجهازك. من Raqeeb.exe على اللابتوب: أيقونة الصينية ← «اقتران الجوال (رمز QR)» ثم امسح الرمز بكاميرا الجوال، أو الصق السرّ هنا.'),
      h('label', {}, 'السرّ الرئيسي', inp),
      h('button', { class: 'primary', onclick: async () => {
        const v = inp.value.trim().toLowerCase();
        if (!/^[0-9a-f]{64}$/.test(v)) { m.textContent = 'السرّ يجب أن يكون 64 حرف hex.'; return; }
        LS.set('raqeeb.secret', v); location.reload();
      } }, 'اقتران'),
      m);
  }

  async function lockScreen() {
    showOnly('login');
    const m = h('p', { class: 'msg' }, '');
    const go = async () => {
      m.textContent = '';
      try { await bio.verify(); await start(); } catch (e) { m.textContent = 'لم يتم التحقق من البصمة.'; }
    };
    $('loginBox').replaceChildren(h('h1', {}, 'رقيب'), h('p', { class: 'hint' }, 'مقفل. أثبت هويتك بالبصمة/الوجه.'),
      h('button', { class: 'primary', onclick: go }, '🔓 افتح بالبصمة'), m,
      h('button', { class: 'ghost', style: 'width:100%;margin-top:8px', onclick: () => { if (confirm('إلغاء الاقتران ومسح السرّ من هذا الجوال؟')) { unpair(); } } }, 'إلغاء الاقتران'));
    go();
  }

  function unpair() { LS.del('raqeeb.secret'); LS.del('raqeeb.cred'); location.reload(); }

  async function start() {
    const master = hex2u8(LS.get('raqeeb.secret'));
    S.key = master; S.token = await deriveToken(master);
    showOnly('app');
    // أزرار الرأس
    const bioOn = bio.enabled();
    $('bioBtn').hidden = bioOn || !(await bio.available());
    $('bioBtn').onclick = async () => { try { await bio.enroll(); toast('فُعّل القفل بالبصمة'); $('bioBtn').hidden = true; } catch { toast('تعذّر تفعيل البصمة'); } };
    $('lockBtn').hidden = !bioOn;
    $('lockBtn').onclick = () => { try { window.CPDevice.reset(); } catch { } S.key = S.token = null; location.reload(); };
    $('unpairBtn').onclick = () => { if (confirm('إلغاء الاقتران ومسح السرّ من هذا الجوال؟')) unpair(); };
    X.force = true; render();
  }

  async function boot() {
    // رابط الاقتران: #k=<hex> — يُقرأ ثم يُمسح من العنوان
    const m = /[#&]k=([0-9a-fA-F]{64})/.exec(location.hash);
    if (m) { LS.set('raqeeb.secret', m[1].toLowerCase()); history.replaceState(null, '', location.pathname + location.search); }
    if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => { });
    if (!LS.get('raqeeb.secret')) return pairScreen();
    if (bio.enabled()) return lockScreen();
    start();
  }
  window.addEventListener('DOMContentLoaded', () => {
    // يُحمَّل device*.js بعد هذا الملف، فنؤجّل الإقلاع إلى نهاية التحميل
    window.addEventListener('load', boot);
  });
})();
