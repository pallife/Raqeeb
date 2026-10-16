"""اختبار طرف-لطرف: مرحّل cpweb حقيقي محلي + Raqeeb.exe + «جوال» وهمي يرسل أوامر مشفّرة بنفس بروتوكول PWA.

الاستعمال:  python tests/e2e_relay.py <مسار Raqeeb.exe> <مجلد مصدر المرحّل (web/server)>
يحتاج: fastapi uvicorn websockets cryptography. لا يحرّك الفأرة ولا يطفئ شيئاً (كل الاختبارات آمنة).
"""
import base64
import hashlib
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

EXE, SERVER = sys.argv[1], sys.argv[2]
TOKEN = os.urandom(32).hex()
KEY = os.urandom(32)
PIN = "246810"
DEV = "e2etest" + uuid.uuid4().hex[:6]
tmp = Path(tempfile.mkdtemp(prefix="raqeeb-e2e-"))


def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p


port = free_port()
base = f"http://127.0.0.1:{port}/cp"
relay = subprocess.Popen(
    [sys.executable, "-m", "uvicorn", "cpweb:app", "--app-dir", SERVER, "--host", "127.0.0.1", "--port", str(port), "--log-level", "warning"],
    env={**os.environ, "CPWEB_DATA": str(tmp / "d"), "CPWEB_AUTH_SHA256": hashlib.sha256(TOKEN.encode()).hexdigest(), "CPWEB_BASE_PATH": "/cp", "CPWEB_AGENT_TIMEOUT": "5"})
(tmp / "root").mkdir(); (tmp / "root" / "hello.txt").write_text("مرحبا", encoding="utf-8")
env_file = tmp / ".env"
env_file.write_text(f"""RAQEEB_RELAY_URL={base}
RAQEEB_WEB_TOKEN={TOKEN}
RAQEEB_AGENT_KEY={KEY.hex()}
RAQEEB_PIN={PIN}
RAQEEB_DEVICE_ID={DEV}
RAQEEB_DEVICE_NAME=اختبار
RAQEEB_ALLOW_INPUT=false
RAQEEB_ALLOW_SHELL=false
RAQEEB_ALLOW_FILES=true
RAQEEB_ALLOW_SCREENSHOT=true
RAQEEB_NOTIFY_TELEGRAM=false
RAQEEB_FILE_ROOTS={tmp / 'root'}
""", encoding="utf-8")
app = None
g = AESGCM(KEY)
results = []


def check(name, cond, extra=""):
    results.append(bool(cond))
    print(("PASS " if cond else "FAIL ") + name + ((" — " + str(extra)) if extra and not cond else ""))


def http(method, path, body=None):
    req = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Authorization": "Bearer " + TOKEN, "Content-Type": "application/json"}, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def cmd(op, args=None, pin=PIN):
    cid = uuid.uuid4().hex[:16]
    plain = json.dumps({"op": op, "args": args or {}, "pin": pin, "ts": int(time.time())}).encode()
    n = os.urandom(12)
    ct = base64.b64encode(n + g.encrypt(n, plain, ("cmd:" + cid).encode())).decode()
    st, r = http("POST", "/api/agent/cmd", {"device": DEV, "id": cid, "ct": ct})
    if st != 200:
        return {"ok": False, "error": f"http {st} {r}"}
    raw = base64.b64decode(r["ct"])
    return json.loads(g.decrypt(raw[:12], raw[12:], ("res:" + cid).encode()))


try:
    for _ in range(80):
        try:
            urllib.request.urlopen(base + "/api/salt", timeout=1)
        except urllib.error.HTTPError:
            break
        except Exception:
            time.sleep(0.25)
    app = subprocess.Popen([EXE, "--env", str(env_file)])
    online = False
    for _ in range(60):
        st, r = http("GET", "/api/agent/devices")
        if any(d["id"] == DEV for d in r.get("devices", [])):
            online = True; break
        time.sleep(0.5)
    check("الجهاز يظهر متصلاً في المرحّل", online)
    if online:
        r = cmd("ping"); check("ping", r.get("ok") and r["data"].get("pong"), r)
        r = cmd("status", pin=""); check("status بلا PIN (قراءة)", r.get("ok") and r["data"]["caps"]["input"] is False and r["data"]["caps"]["files"] is True, r)
        r = cmd("processes", pin=""); check("processes", r.get("ok") and r["data"]["count"] > 10, r)
        r = cmd("notify", {"text": "x"}, pin="000000"); check("PIN خاطئ يُرفض", not r.get("ok") and "PIN" in r.get("error", ""), r)
        r = cmd("input", {"kind": "move", "dx": 1, "dy": 1}); check("الإدخال معطّل بالإعداد", not r.get("ok") and "معطّل" in r.get("error", ""), r)
        r = cmd("shell", {"cmd": "echo hi"}); check("PowerShell معطّل بالإعداد", not r.get("ok") and "معطّل" in r.get("error", ""), r)
        r = cmd("screenshot", {"width": 640}); check("لقطة الشاشة (jpeg)", r.get("ok") and (r["data"].get("jpeg") or r["data"].get("error") or r["data"].get("locked")), str(r)[:200])
        r = cmd("fs_roots"); check("fs_roots", r.get("ok") and any("root" in json.dumps(x) for x in r["data"].get("roots", r["data"].get("items", []))), str(r)[:300])
        r = cmd("awake_on", {"hours": 1}); check("awake_on", r.get("ok"), r)
        r = cmd("status", pin=""); check("منع السكون يظهر في الحالة", r.get("ok") and r["data"]["awake"]["on"] is True, r)
        r = cmd("awake_off", pin=""); check("awake_off", r.get("ok"), r)
        r = cmd("privacy", {"on": True, "color": "#000000"}, pin=PIN); check("privacy يتطلب AllowInput", not r.get("ok"), r)
        # أمر قديم (ts) يُرفض
        cid = uuid.uuid4().hex[:16]
        plain = json.dumps({"op": "ping", "args": {}, "pin": "", "ts": int(time.time()) - 600}).encode(); n = os.urandom(12)
        st, rr = http("POST", "/api/agent/cmd", {"device": DEV, "id": cid, "ct": base64.b64encode(n + g.encrypt(n, plain, ("cmd:" + cid).encode())).decode()})
        raw = base64.b64decode(rr["ct"]); out = json.loads(g.decrypt(raw[:12], raw[12:], ("res:" + cid).encode()))
        check("أمر منتهي الصلاحية يُرفض", not out.get("ok"), out)
        # مفتاح خاطئ يُهمل بصمت (المرحّل يعطي timeout)
        bad = AESGCM(os.urandom(32)); cid = uuid.uuid4().hex[:16]; n = os.urandom(12)
        ct = base64.b64encode(n + bad.encrypt(n, json.dumps({"op": "ping", "ts": int(time.time())}).encode(), ("cmd:" + cid).encode())).decode()
        st, rr = http("POST", "/api/agent/cmd", {"device": DEV, "id": cid, "ct": ct})
        check("مفتاح خاطئ يُهمل (timeout)", st == 504, (st, rr))
finally:
    if app: app.kill()
    relay.kill()

print(f"\n{sum(results)}/{len(results)} passed")
sys.exit(0 if results and all(results) else 1)
