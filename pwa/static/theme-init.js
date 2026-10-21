'use strict';
/* يُحمَّل في <head> قبل أي رسم كي لا يظهر وميض: الافتراضي داكن، والفاتح اختيار المستخدم المحفوظ. */
(function () {
  var t = 'dark';
  try { if (localStorage.getItem('cp-theme') === 'light') t = 'light'; } catch (e) { }
  document.documentElement.dataset.theme = t;
})();
