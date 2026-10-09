#!/usr/bin/env python3
"""Безопасная сводка HAR: что запрашивала страница, пока вы получали ассет на Fab.

Файл HAR содержит cookies, токены и ваши данные — его нельзя никому отправлять. Эта сводка нужна, чтобы
показать только устройство запросов:
  * печатаются хост, метод, статус, путь (идентификаторы заменены на <uuid>/<id>);
  * у запроса и ответа — имена полей; значения — только из белого списка (статус, сумма, валюта,
    идентификатор предложения и т. п.), остальное — тип поля;
  * заголовки, cookies, адреса электронной почты и длинные токены не печатаются никогда.
Сам HAR остаётся на сервере.

Запуск:  python3 tools/har-summary.py fab.har [--limit 150]
"""
import json
import re
import sys
from urllib.parse import urlsplit, parse_qsl

# На сервере терминал бывает не в UTF-8 (LANG не задан): без этого русский текст не напечатается.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# Значения этих полей безопасно показывать (после маскирования почты и токенов).
SAFE_KEYS = {
    "status", "state", "success", "ok", "code", "type", "currency", "currencycode", "price", "total",
    "totalprice", "discount", "discountedprice", "amount", "offerid", "offer_id", "license", "slug",
    "error", "errors", "detail", "message", "reason", "isfree", "free", "quantity", "count", "step",
    "result", "action", "method", "format", "kind",
    "errorcode", "numericerrorcode", "errorname", "errorstatus", "orderstatus", "ordertype", "istotalpricezero",
    "isdiscounttofree", "isfreetofree", "acquired", "mode", "plan_name",
}
# Значения таких полей — адреса: показываем только путь, без параметров.
URL_KEYS = {"url", "next", "redirect", "redirecturl", "location", "returnurl", "href"}

STATIC_EXT = (".js", ".mjs", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif", ".ico",
              ".woff", ".woff2", ".ttf", ".otf", ".map", ".mp4", ".webm")
SKIP_TYPES = {"image", "stylesheet", "script", "font", "media", "manifest", "texttrack", "ping"}
CAPTCHA_HOSTS = ("arkoselabs", "funcaptcha", "hcaptcha", "recaptcha", "challenges.cloudflare", "turnstile", "geetest")
NOISE_HOSTS = ("google-analytics", "googletagmanager", "doubleclick", "sentry", "datadog", "facebook", "segment",
               "hotjar", "newrelic", "nr-data", "clarity.ms", "tiktok", "twitter", "licdn", "amplitude", "fullstory")

UUID = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
EMAIL = re.compile(r"[\w.+-]+@[\w-]+\.[\w.-]+")
TOKENISH = re.compile(r"^[A-Za-z0-9_\-\.=+/]{24,}$")
LONG_ID = re.compile(r"^[0-9a-fA-F]{20,}$|^\d{7,}$")


def mask_text(value):
    """Маскирует почту, длинные токены и идентификаторы; обрезает длинное."""
    text = str(value)
    if re.fullmatch(r"errors\.com\.[\w.]+", text):
        return text  # код ошибки Epic: не секрет, а по нему понятна причина отказа
    text = EMAIL.sub("<email>", text)
    text = UUID.sub(lambda m: m.group(0)[:8] + "…", text)
    if TOKENISH.match(text) and not text.isalpha():
        return "<token>"
    return text if len(text) <= 80 else text[:80] + "…"


def mask_path(path):
    parts = []
    for seg in path.split("/"):
        if UUID.fullmatch(seg):
            parts.append("<uuid>")
        elif LONG_ID.match(seg) or (len(seg) >= 24 and TOKENISH.match(seg)):
            parts.append("<id>")
        else:
            parts.append(seg)
    return "/".join(parts)


def short_url(url):
    try:
        parsed = urlsplit(url)
    except ValueError:
        return "<url>"
    return mask_path(parsed.path or "/")


def describe_url(url):
    parsed = urlsplit(url)
    names = []
    for key, value in parse_qsl(parsed.query, keep_blank_values=True):
        low = key.lower()
        names.append(f"{key}={mask_text(value)}" if low in SAFE_KEYS else key)
    query = ("?" + "&".join(names)) if names else ""
    return f"{parsed.netloc}{mask_path(parsed.path or '/')}{query}"


def shape(value, depth=0, key=""):
    """Устройство JSON: имена полей и типы; значения — только из белого списка."""
    low = key.lower()
    if depth > 4:
        return "…"
    if isinstance(value, dict):
        items = list(value.items())[:30]
        inner = ", ".join(f"{k}: {shape(v, depth + 1, k)}" for k, v in items)
        more = ", …" if len(value) > 30 else ""
        return "{" + inner + more + "}"
    if isinstance(value, list):
        if not value:
            return "[]"
        return f"[{len(value)}× {shape(value[0], depth + 1, key)}]"
    if value is None:
        return "null"
    if isinstance(value, bool):
        return str(value).lower() if low in SAFE_KEYS else "bool"
    if isinstance(value, (int, float)):
        return str(value) if low in SAFE_KEYS else "num"
    if isinstance(value, str):
        if low in URL_KEYS:
            return "url:" + short_url(value)
        if low in SAFE_KEYS:
            return '"' + mask_text(value) + '"'
        return "str"
    return type(value).__name__


def body_summary(mime, text, encoding=None):
    if not text:
        return None
    if encoding == "base64":
        return f"<бинарное тело, {len(text)} знаков>"
    mime = (mime or "").lower()
    try:
        if "json" in mime or text.lstrip().startswith(("{", "[")):
            return "json " + shape(json.loads(text))
        if "x-www-form-urlencoded" in mime:
            names = [k if k.lower() not in SAFE_KEYS else f"{k}={mask_text(v)}"
                     for k, v in parse_qsl(text, keep_blank_values=True)]
            return "form {" + ", ".join(names) + "}"
        if "multipart" in mime or 'Content-Disposition' in text[:400]:
            names = re.findall(r'name="([^"]+)"', text)
            return "multipart {" + ", ".join(names) + "}"
    except (ValueError, TypeError):
        pass
    return f"<{mime or 'тело'}, {len(text)} знаков>"


def is_static(entry):
    rtype = str(entry.get("_resourceType", "")).lower()
    if rtype in SKIP_TYPES:
        return True
    url = entry["request"]["url"].split("?")[0].lower()
    if url.endswith(STATIC_EXT):
        return True
    mime = (entry["response"]["content"].get("mimeType") or "").lower()
    return mime.startswith(("image/", "font/", "text/css", "video/", "audio/")) or "javascript" in mime


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    limit = 150
    if "--limit" in sys.argv:
        limit = int(sys.argv[sys.argv.index("--limit") + 1])
        args = [a for a in args if a != str(limit)]
    if not args:
        print(__doc__)
        return 2
    with open(args[0], encoding="utf-8") as f:
        entries = json.load(f)["log"]["entries"]

    hosts = {}
    for e in entries:
        host = urlsplit(e["request"]["url"]).netloc
        hosts[host] = hosts.get(host, 0) + 1
    print(f"Записей в HAR: {len(entries)}. Хосты:")
    for host, n in sorted(hosts.items(), key=lambda x: -x[1]):
        flag = "  ⚠ КАПЧА/ПРОВЕРКА" if any(c in host for c in CAPTCHA_HOSTS) else ""
        print(f"  {n:4d}  {host}{flag}")

    print("\nЗапросы (без статики и аналитики):")
    shown = 0
    for i, e in enumerate(entries, 1):
        if is_static(e):
            continue
        host = urlsplit(e["request"]["url"]).netloc
        if any(n in host for n in NOISE_HOSTS):
            continue
        shown += 1
        if shown > limit:
            print(f"… и ещё записи (показано {limit}; --limit для большего)")
            break
        req, resp = e["request"], e["response"]
        line = f"#{i} {req['method']} {resp.get('status')} {describe_url(req['url'])}"
        post = req.get("postData") or {}
        body = body_summary(post.get("mimeType"), post.get("text"))
        if body:
            line += f"\n      запрос: {body}"
        content = resp.get("content") or {}
        answer = body_summary(content.get("mimeType"), content.get("text"), content.get("encoding"))
        if answer and "html" not in (content.get("mimeType") or "").lower():
            line += f"\n      ответ:  {answer}"
        location = resp.get("redirectURL")
        if location:
            line += f"\n      перенаправление → {urlsplit(location).netloc}{short_url(location)}"
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
