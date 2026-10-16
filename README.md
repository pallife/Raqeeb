# رقيب — Raqeeb

تحكّم كامل بجهاز Windows من الجوال (PWA) عبر خادم وسيط، بتشفير طرف-لطرف: الخادم يمرّر بايتات مشفّرة ولا يستطيع قراءتها أو تزويرها.

Raqeeb lets you control a Windows PC from your phone through a blind relay. Everything is in this repo: the Windows agent, the relay server, and the phone PWA. Commands are AES-256-GCM encrypted end-to-end; the relay never sees the key.

## الميزات
- معاينة الشاشة حيّة (حتى شاشة القفل مع المساعد)
- فأرة وكيبورد من الجوال (تراك باد باللمس)
- فتح قفل ويندوز من الجوال (اختياري، يحتاج «مساعد رقيب»)
- منع السكون وإطفاء الشاشة مع تذكير دوري وحد أقصى للمدة
- غطاء خصوصية للشاشة (المتحكّم يرى الحقيقية والجالس بجانبك يرى الأسود؛ إزالة محلية: `Ctrl+Alt+Shift+P`)
- ملفات: تصفح، بحث، تنزيل، رفع، فتح، تنزيل رابط إلى الجهاز، وإرسال إلى تلغرام (المجلدات الكبيرة تُقسَّم)
- صندوق إرسال: ما يُلقى في `Documents\Raqeeb-Outbox` يُرسل لتلغرام
- قفل، سكون، سبات، إيقاف، إعادة تشغيل (مع إلغاء)، العمليات والاتصالات، إنهاء برنامج
- PowerShell (معطّل افتراضيًا)
- قفل تطبيق الجوال بالبصمة/الوجه

## المكوّنات
| المجلد | ما هو |
|---|---|
| `src/Raqeeb` | برنامج الويندوز (صينية النظام): الوكيل الذي ينفّذ الأوامر |
| `src/Raqeeb.Helper` | خدمة اختيارية بصلاحية النظام للتحكم بشاشة القفل |
| `relay/` | الخادم الوسيط (FastAPI، ملف واحد): أنبوب أعمى + يقدّم واجهة الجوال |
| `pwa/` | تطبيق الجوال (PWA): اقتران، قفل بالبصمة، معاينة/تحكم/ملفات/إجراءات |

```
الجوال (PWA) ──HTTPS──▶ الخادم الوسيط ◀──WebSocket صادر── Raqeeb.exe (اللابتوب)
        └──────── أوامر مشفّرة AES-GCM، المفتاح لا يعرفه الخادم ────────┘
```

## التشغيل (3 خطوات)
**1) اللابتوب:** حمّل `Raqeeb.exe` من Releases وشغّله مرة؛ ينشئ `%APPDATA%\Raqeeb\.env` بسرّ رئيسي عشوائي. من أيقونة الصينية ← «اقتران الجوال» انسخ `RAQEEB_RELAY_AUTH_SHA256` (أو شغّل `Raqeeb.exe --pair`).

**2) الخادم الوسيط** (أي VPS خلف HTTPS):
```bash
pip install -r relay/requirements.txt
RAQEEB_RELAY_AUTH_SHA256=<القيمة> uvicorn raqeeb_relay:app --app-dir relay --host 0.0.0.0 --port 8080 --no-access-log
# أو: RAQEEB_RELAY_AUTH_SHA256=<القيمة> docker compose up -d --build
```
اختياري: `RAQEEB_TG_BOT_TOKEN` و`RAQEEB_TG_CHAT_ID` لإشعارات وملفات تلغرام. ضعه خلف Caddy/nginx/Cloudflare Tunnel (لا تعرضه بلا HTTPS).

**3) الربط:** عدّل `.env`: `RAQEEB_RELAY_URL=https://عنوان-خادمك` و`RAQEEB_PIN=` ثم «إعادة تحميل الإعدادات». من «اقتران الجوال» امسح رمز QR بكاميرا الجوال فتُفتح الصفحة مقترنة. ثبّتها على الشاشة الرئيسية وفعّل «القفل بالبصمة».

الرابط يحوي السرّ في الجزء بعد `#` فلا يصل إلى أي خادم، ويُحفظ في متصفح الجوال فقط. لإلغاء الاقتران: «إلغاء الاقتران». قفل البصمة (WebAuthn) قفل محلي للتطبيق؛ الحماية المشفّرة الحقيقية هي السرّ الرئيسي + PIN الإجراءات.

## الأمان
- كل شيء مغلق افتراضيًا عدا القراءة؛ كل إجراء يتطلب **PIN** (PBKDF2)، و5 أخطاء تقفل 15 دقيقة.
- كل أمر مشفّر بـ AES-GCM مرتبط بمعرّفه، مع نافذة زمنية 120 ثانية ومنع إعادة التشغيل.
- الجهاز يتصل «للخارج» فقط: لا منافذ مفتوحة ولا IP عام.
- الملفات محصورة بمجلدات مسموحة، والمفاتيح/الكوكيز/التنفيذيات محجوبة.
- إشعار تلغرام عند الأوامر الحساسة. PowerShell وفتح القفل معطّلان افتراضيًا.

## بروتوكول الخادم الوسيط
- السرّ الرئيسي `M` (32 بايت). مفتاح التشفير = `HKDF-SHA256(M, info="control-panel/agent/v1")`، ورمز الدخول = `hex(HKDF-SHA256(M, info="control-panel/web-auth/v1"))`. الخادم يحفظ `sha256(token)` فقط.
- الجهاز: `GET {relay}/agent/ws?t=<token>&d=<deviceId>&n=<name>` (WebSocket صادر).
- الجوال: `POST {relay}/api/agent/cmd` بجسم `{device, id, ct}` والرد `{ct}`؛ و`GET /api/agent/devices`.
- `ct = nonce(12) ‖ AES-GCM(key, JSON{op,args,pin,ts}) ‖ tag(16)`، و AAD = `"cmd:"+id` للأمر و`"res:"+id` للرد.
- تلغرام: `POST /api/ql/notify` و`/api/ql/sendfile` بنفس الرمز (Bearer).

## البناء والاختبار
```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1       # dist\Raqeeb.exe + RaqeebHelper.exe
pip install fastapi "uvicorn[standard]" cryptography
python tests/e2e_relay.py dist\Raqeeb.exe                   # relay + exe + جوال وهمي، طرف-لطرف
```
المتطلبات: .NET 8 SDK، Windows 10 2004+.

## الترخيص
MIT
