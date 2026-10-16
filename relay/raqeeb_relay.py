"""الخادم الوسيط لرقيب: أنبوب أعمى بين الجوال (PWA) والجهاز. لا يفك أي تشفير ولا يعرف المفتاح.

الإعداد بمتغيرات بيئة:
  RAQEEB_RELAY_AUTH_SHA256   sha256 (hex) لرمز الدخول — أو عدة قيم مفصولة بفاصلة. يطبعه `Raqeeb.exe --pair`. إلزامي.
  RAQEEB_RELAY_BASE_PATH     مسار خلف وكيل عكسي مثل /raqeeb (افتراضي: الجذر)
  RAQEEB_RELAY_TIMEOUT       مهلة ردّ الجهاز بالثواني (افتراضي 40)
  RAQEEB_RELAY_STATIC        مجلد واجهة الجوال (افتراضي ../pwa)
  RAQEEB_TG_BOT_TOKEN, RAQEEB_TG_CHAT_ID   اختياري: بوت تلغرام لاستقبال الإشعارات والملفات
تشغيل:  uvicorn raqeeb_relay:app --host 0.0.0.0 --port 8080 --no-access-log   (خلف HTTPS دائماً)
"""
from __future__ import annotations

import asyncio
import hashlib
import hmac
import json
import os
import re
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

from fastapi import APIRouter, FastAPI, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, JSONResponse

HERE = Path(__file__).resolve().parent
BASE = os.environ.get("RAQEEB_RELAY_BASE_PATH", "").rstrip("/")
STATIC = Path(os.environ.get("RAQEEB_RELAY_STATIC", str(HERE.parent / "pwa")))
AUTH_HASHES = {h.strip().lower() for h in os.environ.get("RAQEEB_RELAY_AUTH_SHA256", "").split(",") if h.strip()}
AGENT_TIMEOUT = float(os.environ.get("RAQEEB_RELAY_TIMEOUT", "40"))
TG_TOKEN = os.environ.get("RAQEEB_TG_BOT_TOKEN", "")
TG_CHAT = os.environ.get("RAQEEB_TG_CHAT_ID", "")
TG_FILE_MAX = 49 * 1024 * 1024
CT_MAX = 6 * 1024 * 1024
RE_DEV = re.compile(r"^[A-Za-z0-9_-]{4,32}$")
RE_CID = re.compile(r"^[A-Za-z0-9]{8,64}$")

_fails: dict[str, list[float]] = {}
_agents: dict[str, dict] = {}
_pending: dict[str, asyncio.Future] = {}
_tg_times: list[float] = []

router = APIRouter()


def _check_token(token: str) -> bool:
    if not AUTH_HASHES or not token:
        return False
    got = hashlib.sha256(token.encode()).hexdigest()
    return any(hmac.compare_digest(got, h) for h in AUTH_HASHES)


def _throttle(ip: str) -> None:
    now = time.time()
    hist = [t for t in _fails.get(ip, []) if now - t < 300]
    _fails[ip] = hist
    if len(hist) >= 10:
        raise HTTPException(429, "محاولات كثيرة، انتظر 5 دقائق")


def _auth(request: Request) -> None:
    ip = request.client.host if request.client else "?"
    _throttle(ip)
    h = request.headers.get("authorization", "")
    token = h[7:] if h.lower().startswith("bearer ") else ""
    if not _check_token(token):
        _fails.setdefault(ip, []).append(time.time())
        raise HTTPException(401, "غير مصرّح")


# ------------------------------------------------------------- الجهاز (WebSocket صادر من Raqeeb.exe)
@router.websocket("/agent/ws")
async def agent_ws(socket: WebSocket) -> None:
    q = socket.query_params
    ip = socket.client.host if socket.client else "?"
    try:
        _throttle(ip)
    except HTTPException:
        await socket.close(code=4429)
        return
    if not _check_token(q.get("t", "")):
        _fails.setdefault(ip, []).append(time.time())
        await socket.close(code=4401)
        return
    dev = q.get("d", "")
    if not RE_DEV.match(dev):
        await socket.close(code=4400)
        return
    await socket.accept()
    old = _agents.get(dev)
    if old:
        try:
            await old["ws"].close(code=4000)
        except Exception:
            pass
    _agents[dev] = {"ws": socket, "label": q.get("n", "")[:60], "since": time.time(), "last": time.time()}
    try:
        while True:
            msg = await socket.receive_text()
            if dev in _agents and _agents[dev]["ws"] is socket:
                _agents[dev]["last"] = time.time()
            try:
                d = json.loads(msg)
            except Exception:
                continue
            if isinstance(d, dict) and "id" in d and "ct" in d:
                fut = _pending.pop(str(d["id"]), None)
                if fut and not fut.done():
                    fut.set_result(str(d["ct"]))
    except WebSocketDisconnect:
        pass
    finally:
        if dev in _agents and _agents[dev]["ws"] is socket:
            _agents.pop(dev, None)


# ------------------------------------------------------------- الجوال (PWA)
@router.get("/api/agent/devices")
async def agent_devices(request: Request) -> dict:
    _auth(request)
    return {"devices": [{"id": k, "label": v["label"], "since": v["since"], "last": v["last"]} for k, v in _agents.items()]}


@router.post("/api/agent/cmd")
async def agent_cmd(request: Request) -> JSONResponse:
    _auth(request)
    try:
        b = await request.json()
        dev, cid, ct = str(b["device"]), str(b["id"]), str(b["ct"])
    except Exception:
        raise HTTPException(400, "طلب غير صالح")
    if not RE_DEV.match(dev) or not RE_CID.match(cid) or len(ct) > CT_MAX:
        raise HTTPException(400, "طلب غير صالح")
    a = _agents.get(dev)
    if not a:
        return JSONResponse({"error": "offline"}, status_code=404)
    if cid in _pending:
        raise HTTPException(409, "معرّف مكرر")
    fut = asyncio.get_running_loop().create_future()
    _pending[cid] = fut
    try:
        await a["ws"].send_text(json.dumps({"id": cid, "ct": ct}))
        res = await asyncio.wait_for(fut, AGENT_TIMEOUT)
        return JSONResponse({"ct": res}, headers={"Cache-Control": "no-store"})
    except asyncio.TimeoutError:
        return JSONResponse({"error": "timeout"}, status_code=504)
    except Exception:
        return JSONResponse({"error": "offline"}, status_code=404)
    finally:
        _pending.pop(cid, None)


# ------------------------------------------------------------- تلغرام (اختياري)
def _tg_configured() -> bool:
    return bool(TG_TOKEN and TG_CHAT)


def _tg_rate(limit: int) -> bool:
    now = time.time()
    _tg_times[:] = [t for t in _tg_times if now - t < 60]
    if len(_tg_times) >= limit:
        return False
    _tg_times.append(now)
    return True


def _tg_call(method: str, data: bytes, ctype: str, timeout: int) -> tuple[bool, str]:
    req = urllib.request.Request(f"https://api.telegram.org/bot{TG_TOKEN}/{method}", data=data, headers={"Content-Type": ctype})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            j = json.loads(r.read())
            return bool(j.get("ok")), "" if j.get("ok") else str(j.get("description", ""))
    except urllib.error.HTTPError as e:
        try:
            return False, str(json.loads(e.read()).get("description", e.code))
        except Exception:
            return False, f"HTTP {e.code}"
    except Exception as e:  # noqa: BLE001
        return False, type(e).__name__


@router.post("/api/ql/notify")
async def notify(request: Request) -> dict:
    _auth(request)
    text = str((await request.json()).get("text", ""))[:1500]
    if not text:
        raise HTTPException(400, "نص فارغ")
    if not _tg_configured() or not _tg_rate(20):
        return {"sent": False}
    body = json.dumps({"chat_id": TG_CHAT, "text": text[:3500], "disable_web_page_preview": True}).encode()
    ok, _ = await asyncio.to_thread(_tg_call, "sendMessage", body, "application/json", 15)
    return {"sent": ok}


@router.post("/api/ql/sendfile")
async def sendfile(request: Request, name: str = "file", caption: str = "") -> dict:
    _auth(request)
    if not _tg_configured():
        raise HTTPException(409, "بوت تلغرام غير مربوط على الخادم الوسيط")
    try:
        declared = int(request.headers.get("content-length", "0"))
    except ValueError:
        declared = 0
    if declared > TG_FILE_MAX:
        raise HTTPException(413, "حد تلغرام للبوتات 50MB")
    chunks, total = [], 0
    async for c in request.stream():
        total += len(c)
        if total > TG_FILE_MAX:
            raise HTTPException(413, "حد تلغرام للبوتات 50MB")
        chunks.append(c)
    if total == 0:
        raise HTTPException(400, "ملف فارغ")
    if not _tg_rate(15):
        raise HTTPException(429, "كثرة إرسال ملفات — انتظر دقيقة")
    boundary = "rqb" + uuid.uuid4().hex
    safe = re.sub(r'[\r\n"\\]', "_", name)[:120] or "file"
    parts = [f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode()
             for k, v in (("chat_id", TG_CHAT), ("caption", caption[:200]))]
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="document"; filename="{safe}"\r\n'
                 f'Content-Type: application/octet-stream\r\n\r\n'.encode())
    body = b"".join(parts) + b"".join(chunks) + f"\r\n--{boundary}--\r\n".encode()
    ok, why = await asyncio.to_thread(_tg_call, "sendDocument", body, f"multipart/form-data; boundary={boundary}", 300)
    if not ok:
        raise HTTPException(502, why or "فشل الإرسال إلى تلغرام")
    return {"sent": True, "bytes": total}


# ------------------------------------------------------------- واجهة الجوال
@router.get("/healthz")
async def healthz() -> dict:
    return {"ok": True, "devices": len(_agents), "telegram": _tg_configured()}


@router.get("/")
async def index() -> FileResponse:
    return FileResponse(STATIC / "index.html", headers={"Cache-Control": "no-store"})


@router.get("/sw.js")
async def service_worker() -> FileResponse:
    return FileResponse(STATIC / "sw.js", media_type="text/javascript", headers={"Cache-Control": "no-cache"})


@router.get("/manifest.webmanifest")
async def manifest() -> FileResponse:
    return FileResponse(STATIC / "manifest.webmanifest", media_type="application/manifest+json", headers={"Cache-Control": "no-cache"})


@router.get("/static/{name}")
async def static_file(name: str) -> FileResponse:
    p = (STATIC / "static" / name).resolve()
    if p.parent != (STATIC / "static").resolve() or not p.is_file():
        raise HTTPException(404)
    return FileResponse(p, headers={"Cache-Control": "no-cache"})


app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
app.include_router(router, prefix=BASE)


@app.middleware("http")
async def _headers(request: Request, call_next):
    r = await call_next(request)
    r.headers["X-Content-Type-Options"] = "nosniff"
    r.headers["Referrer-Policy"] = "no-referrer"
    if not request.url.path.startswith(BASE + "/static/"):
        r.headers["X-Frame-Options"] = "DENY"
    return r
