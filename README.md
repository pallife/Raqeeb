# رقيب — Raqeeb

تحكّم كامل بجهاز Windows من الجوال (PWA) عبر خادم وسيط، بتشفير طرف-لطرف: الخادم يمرّر بايتات مشفّرة ولا يستطيع قراءتها أو تزويرها.

Raqeeb is a small Windows tray app that lets you control your PC from your phone through a blind relay server. Commands are AES-256-GCM encrypted end-to-end; the relay never sees the key.

## الميزات
- معاينة الشاشة حيّة (حتى شاشة القفل مع المساعد)
- فأرة وكيبورد من الجوال
- فتح قفل ويندوز من الجوال (اختياري، يحتاج «مساعد رقيب»)
- منع السكون وإطفاء الشاشة مع تذكير دوري وحد أقصى للمدة
- غطاء خصوصية للشاشة (المتحكّم يرى الحقيقية والجالس بجانبك يرى الأسود؛ إزالة محلية: `Ctrl+Alt+Shift+P`)
- ملفات: تصفح، بحث، تنزيل، رفع، فتح، تنزيل رابط إلى الجهاز، وإرسال إلى تلغرام (مجلدات كبيرة تُقسَّم تلقائيًا)
- صندوق إرسال: ما يُلقى في `Documents\Raqeeb-Outbox` يُرسل لتلغرام
- قفل، سكون، سبات، إيقاف، إعادة تشغيل (مع إلغاء)، العمليات والاتصالات، إنهاء برنامج
- PowerShell (معطّل افتراضيًا)

## الأمان
- كل شيء مغلق افتراضيًا عدا القراءة؛ كل إجراء يتطلب **PIN** (PBKDF2)، و5 أخطاء تقفل 15 دقيقة.
- كل أمر موقّع بـ AES-GCM مرتبط بمعرّفه، مع نافذة زمنية 120 ثانية ومنع إعادة التشغيل.
- الجهاز يتصل «للخارج» فقط: لا منافذ مفتوحة ولا IP عام.
- الملفات محصورة بمجلدات مسموحة، والمفاتيح/الكوكيز/التنفيذيات محجوبة.
- إشعار تلغرام عند الأوامر الحساسة.

## التشغيل
1. حمّل `Raqeeb.exe` (و`RaqeebHelper.exe` إن أردت التحكم بشاشة القفل) من Releases، أو ابنِه بـ `publish.ps1`.
2. شغّل `Raqeeb.exe` مرة: ينشئ `%APPDATA%\Raqeeb\.env` بمفتاح ورمز عشوائيين.
3. عدّل `.env`: `RAQEEB_RELAY_URL` و`RAQEEB_PIN` والصلاحيات (انظر `.env.example`)، ثم «إعادة تحميل الإعدادات» من الصينية.
4. ملف `.env` فيه أسرار — لا تشاركه.

## بروتوكول الخادم الوسيط
- WebSocket: `GET {relay}/agent/ws?t=<token>&d=<deviceId>&n=<name>`؛ الخادم يحفظ `sha256(token)` فقط.
- الجوال: `POST {relay}/api/agent/cmd` بجسم `{device, id, ct}`، والرد `{ct}`.
- `ct = nonce(12) ‖ AES-GCM(key, JSON{op,args,pin,ts}) ‖ tag(16)`، و AAD = `"cmd:"+id` للأمر و`"res:"+id` للرد.
- المفتاح: 32 بايت (`RAQEEB_AGENT_KEY`)، ويُشتق في تطبيق لوحة التحكم من الخزنة بـ HKDF-SHA256 بـ info=`control-panel/agent/v1`.
- إشعارات/ملفات تلغرام: `POST /api/ql/notify` و`/api/ql/sendfile` بنفس الرمز.

> يحتاج خادمًا وسيطًا متوافقًا مع هذا البروتوكول. واجهة PWA وخادم الوسيط الحاليان جزء من «لوحة التحكم» وليسا في هذا المستودع بعد.

## البناء والاختبار
```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1      # dist\Raqeeb.exe + RaqeebHelper.exe
python tests/e2e_relay.py dist\Raqeeb.exe <path-to-relay>/server   # اختبار طرف-لطرف
```
المتطلبات: .NET 8 SDK، Windows 10 2004+.

## الترخيص
MIT
