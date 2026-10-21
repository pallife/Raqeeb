'use strict';
/* شريط رقيب العلوي: أيقونات هادئة بدل أزرار نصية، ومظهر داكن (الافتراضي) / فاتح يُحفظ على هذا الجوال فقط. */
(function () {
  const $ = (id) => document.getElementById(id);
  let theme = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
  function apply(t) {
    theme = t; document.documentElement.dataset.theme = t;
    const m = document.querySelector('meta[name=theme-color]'); if (m) m.content = t === 'light' ? '#f5f7fa' : '#0a0e17';
    $('themeBtn').replaceChildren(CPIcon(t === 'dark' ? 'sun' : 'moon', 19));
    $('themeBtn').title = t === 'dark' ? 'التبديل إلى الفاتح' : 'التبديل إلى الداكن';
  }
  const icon = (id, name) => { const b = $(id); b.replaceChildren(CPIcon(name, 19)); b.setAttribute('aria-label', b.title); };
  icon('bioBtn', 'shield'); icon('lockBtn', 'lock'); icon('unpairBtn', 'power');
  apply(theme);
  $('themeBtn').addEventListener('click', () => {
    const t = theme === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem('cp-theme', t); } catch { }
    apply(t);
  });
})();
