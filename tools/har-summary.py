#!/usr/bin/env python3
"""Безопасная сводка HAR: что запрашивала страница, пока вы получали ассет на Fab.

Файл HAR содержит cookies, токены и ваши данные — его нельзя никому отправлять. Эта сводка нужна, чтобы
показать только устройство запросов. Принцип: всё скрыто, пока не доказано, что безопасно.
  * печатаются хост (без логина и пароля из адреса), метод, статус и путь; в пути идентификаторы, почта,
    длинные и смешанные буквенно-цифровые куски и параметры «;…» заменены на <uuid>/<id>/<email>;
  * у запроса и ответа — имена полей (и они проверяются: почта, uuid, токены в роли имени скрыты); значения —
    только из короткого белого списка (статус, валюта, сумма, код ошибки вида epic.error.xxx) и только если
    значение похоже на код, а не на текст: любой текст с пробелами, почта, токен, длинное число — скрыты;
  * заголовки, cookies, тела `data:`-адресов, фрагменты адресов, значения параметров запроса вне белого списка
    не печатаются никогда.
Сам HAR остаётся на сервере.

Запуск:  python3 tools/har-summary.py fab.har [--limit 150]
"""
import json
import os
import re
import sys
from urllib.parse import unquote, urlsplit, parse_qsl

# На сервере терминал бывает не в UTF-8 (LANG не задан): без этого русский текст не напечатается.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

MAX_FILE_MB = 150  # на тесном сервере HAR больше этого размера в память не читаем (нужно ~3× размера файла)

# Значения этих полей можно показывать (после проверки по маске ниже).
SAFE_KEYS = {
    "status", "success", "ok", "type", "currency", "currencycode", "isfree", "free", "quantity", "count", "step",
    "result", "errorcode", "numericerrorcode", "errorname", "errorstatus", "orderstatus", "ordertype",
    "istotalpricezero", "isdiscounttofree", "isfreetofree", "acquired", "mode", "plan_name", "offerid", "offer_id",
    "license", "price", "total", "totalprice", "discount", "discountedprice", "amount", "error", "errors",
    "message", "detail", "reason",
}
# Эти числовые поля можно показывать числом.
NUMERIC_KEYS = {
    "status", "price", "total", "totalprice", "discount", "discountedprice", "amount", "quantity", "count", "step",
    "numericerrorcode",
}
# Значения таких полей — адреса: показываем только путь, без параметров.
URL_KEYS = {"url", "next", "redirect", "redirecturl", "location", "returnurl", "href"}
# Короткие фразы, которые безопасно показать целиком (стандартные ответы Fab).
ALLOWED_TEXT = {"Not free.", "Not found.", "CSRF Failed", "Authentication credentials were not provided."}

STATIC_EXT = (".js", ".mjs", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif", ".ico",
              ".woff", ".woff2", ".ttf", ".otf", ".map", ".mp4", ".webm")
SKIP_TYPES = {"image", "stylesheet", "script", "font", "media", "manifest", "texttrack", "ping"}
CAPTCHA_HOSTS = ("arkoselabs", "funcaptcha", "hcaptcha", "recaptcha", "challenges.cloudflare", "turnstile", "geetest")
NOISE_HOSTS = ("google-analytics", "googletagmanager", "doubleclick", "sentry", "datadog", "facebook", "segment",
               "hotjar", "newrelic", "nr-data", "clarity.ms", "tiktok", "twitter", "licdn", "amplitude", "fullstory")

UUID = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
EMAIL = re.compile(r"[^\s/@;:]+@[\w-]+\.[\w.-]+")
TOKENISH = re.compile(r"^[A-Za-z0-9_\-\.=+/:%]{24,}$")
LONG_ID = re.compile(r"^[0-9a-fA-F]{20,}$|^\d{7,}$")
DOTTED_CODE = re.compile(r"[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*){1,10}")  # epic.error.captcha.challenge.failed
ENUM_WORD = re.compile(r"[A-Za-z][A-Za-z0-9_\-]{0,40}")
SIMPLE_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_.\-]{0,50}")


def looks_secret(text):
    """Токен, идентификатор или другое, что нельзя показывать: длинное, смешанное буквы+цифры, шестнадцатеричное."""
    if TOKENISH.match(text) or LONG_ID.match(text):
        return True
    return len(text) >= 12 and " " not in text and bool(re.search(r"[A-Za-z]", text)) and bool(re.search(r"\d", text))


def mask_text(value):
    """Значение, которое можно напечатать: код ошибки, слово-статус, короткое число; всё остальное скрыто."""
    text = str(value)
    if text in ALLOWED_TEXT:
        return text
    if EMAIL.search(text):
        return "<email>"
    if UUID.fullmatch(text):
        return text[:8] + "…"
    if any(ch.isspace() for ch in text):
        return "<текст>"
    if DOTTED_CODE.fullmatch(text) and len(text) <= 80:
        return text
    if looks_secret(text):
        return "<token>"
    if ENUM_WORD.fullmatch(text) or re.fullmatch(r"\d{1,6}", text):
        return text
    return "<?>"


def mask_key(key):
    """Имя поля: в роли имени бывают почта, uuid и токены — их тоже скрываем."""
    text = str(key)
    if EMAIL.search(text):
        return "<email>"
    if UUID.search(text):
        return "<uuid>" if UUID.fullmatch(text) else "<id>"
    if looks_secret(text):
        return "<id>"
    return text if SIMPLE_NAME.fullmatch(text) else "<имя>"


def mask_segment(segment):
    raw = segment.split(";")[0]  # «;jsessionid=…» и другие параметры пути
    seg = unquote(raw)
    if EMAIL.search(seg):
        return "<email>"
    if UUID.fullmatch(seg):
        return "<uuid>"
    if "%" in raw or len(seg) > 40 or looks_secret(seg) or re.fullmatch(r"\d{5,}", seg):
        return "<id>"
    return seg


def mask_path(path):
    return "/".join(mask_segment(s) for s in path.split("/"))


def short_url(url):
    """Только путь, без адреса, параметров и фрагмента."""
    try:
        parsed = urlsplit(url)
    except ValueError:
        return "<адрес>"
    if parsed.scheme in ("data", "blob", "javascript"):
        return "<%s:>" % parsed.scheme
    return mask_path(parsed.path or "/")


def host_of(url):
    """Хост без логина и пароля из адреса."""
    try:
        parsed = urlsplit(url)
        host = parsed.hostname or ""
        port = parsed.port
    except ValueError:
        return "<адрес>"
    if parsed.scheme in ("data", "blob", "javascript"):
        return "<%s:>" % parsed.scheme
    if EMAIL.search(host):
        host = "<email>"
    return "%s:%s" % (host, port) if port else host


def describe_url(url):
    try:
        parsed = urlsplit(url)
        pairs = parse_qsl(parsed.query, keep_blank_values=True)
    except ValueError:
        return "<адрес>"
    if parsed.scheme in ("data", "blob", "javascript"):
        return "<%s:>" % parsed.scheme
    names = []
    for key, value in pairs:
        low = key.lower()
        names.append("%s=%s" % (mask_key(key), mask_text(value)) if low in SAFE_KEYS else mask_key(key))
    query = ("?" + "&".join(names)) if names else ""
    return "%s%s%s" % (host_of(url), mask_path(parsed.path or "/"), query)


def shape(value, depth=0, key=""):
    """Устройство JSON: имена полей и типы; значения — только из белого списка и только «похожие на код»."""
    low = key.lower()
    if depth > 4:
        return "…"
    if isinstance(value, dict):
        items = list(value.items())[:30]
        inner = ", ".join("%s: %s" % (mask_key(k), shape(v, depth + 1, str(k))) for k, v in items)
        more = ", …" if len(value) > 30 else ""
        return "{" + inner + more + "}"
    if isinstance(value, list):
        if not value:
            return "[]"
        return "[%d× %s]" % (len(value), shape(value[0], depth + 1, key))
    if value is None:
        return "null"
    if isinstance(value, bool):
        return str(value).lower() if low in SAFE_KEYS else "bool"
    if isinstance(value, (int, float)):
        return str(value) if low in NUMERIC_KEYS and abs(value) < 10 ** 8 else "num"
    if isinstance(value, str):
        if low in URL_KEYS:
            return "url:" + short_url(value)
        if low in SAFE_KEYS:
            return '"' + mask_text(value) + '"'
        return "str"
    return type(value).__name__


def body_summary(mime, text, encoding=None):
    if not text or not isinstance(text, str):
        return None
    if encoding == "base64":
        return "<бинарное тело, %d знаков>" % len(text)
    mime = (mime or "").lower()
    try:
        if "json" in mime or text.lstrip().startswith(("{", "[")):
            return "json " + shape(json.loads(text))
        if "x-www-form-urlencoded" in mime:
            names = [mask_key(k) if k.lower() not in SAFE_KEYS else "%s=%s" % (mask_key(k), mask_text(v))
                     for k, v in parse_qsl(text, keep_blank_values=True)]
            return "form {" + ", ".join(names) + "}"
        if "multipart" in mime or "Content-Disposition" in text[:400]:
            # только имена полей формы; имена файлов (filename="…") не берём
            names = [mask_key(n) for n in re.findall(r'(?<![A-Za-z])name="([^"]+)"', text)]
            return "multipart {" + ", ".join(names) + "}"
    except Exception:  # битый JSON, слишком глубокая вложенность и т. п. — тело просто не разбираем
        return "<тело не разобрано, %d знаков>" % len(text)
    return "<%s, %d знаков>" % (mime or "тело", len(text))


def is_static(entry):
    rtype = str(entry.get("_resourceType", "")).lower()
    if rtype in SKIP_TYPES:
        return True
    url = str((entry.get("request") or {}).get("url", "")).split("?")[0].lower()
    if url.endswith(STATIC_EXT):
        return True
    mime = str(((entry.get("response") or {}).get("content") or {}).get("mimeType") or "").lower()
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
    size_mb = os.path.getsize(args[0]) / 1048576
    if size_mb > MAX_FILE_MB:
        print("Файл %.0f МБ больше %d МБ: на тесном сервере его не читаем (нужно ~3× размера в памяти). "
              "Сохраните HAR короче (только нужный запрос) или запустите сводку на другом компьютере." % (size_mb, MAX_FILE_MB))
        return 2
    with open(args[0], encoding="utf-8-sig") as f:
        data = json.load(f)
    entries = (data.get("log") or {}).get("entries") if isinstance(data, dict) else None
    if not isinstance(entries, list):
        print("В файле нет log.entries — это не HAR.")
        return 2

    hosts = {}
    for e in entries:
        try:
            host = host_of(str(e["request"]["url"]))
        except Exception:
            host = "<адрес>"
        hosts[host] = hosts.get(host, 0) + 1
    print("Записей в HAR: %d. Хосты:" % len(entries))
    for host, n in sorted(hosts.items(), key=lambda x: -x[1]):
        flag = "  ⚠ КАПЧА/ПРОВЕРКА" if any(c in host for c in CAPTCHA_HOSTS) else ""
        print("  %4d  %s%s" % (n, host, flag))

    print("\nЗапросы (без статики и аналитики):")
    shown = 0
    for i, e in enumerate(entries, 1):
        try:
            if is_static(e):
                continue
            req, resp = e["request"], e.get("response") or {}
            if any(n in host_of(str(req["url"])) for n in NOISE_HOSTS):
                continue
            shown += 1
            if shown > limit:
                print("… и ещё записи (показано %d; --limit для большего)" % limit)
                break
            line = "#%d %s %s %s" % (i, req.get("method"), resp.get("status"), describe_url(str(req["url"])))
            post = req.get("postData") or {}
            body = body_summary(post.get("mimeType"), post.get("text"))
            if body:
                line += "\n      запрос: " + body
            content = resp.get("content") or {}
            answer = body_summary(content.get("mimeType"), content.get("text"), content.get("encoding"))
            if answer and "html" not in str(content.get("mimeType") or "").lower():
                line += "\n      ответ:  " + answer
            location = resp.get("redirectURL")
            if location:
                line += "\n      перенаправление → %s%s" % (host_of(str(location)), short_url(str(location)))
            print(line)
        except Exception as exc:  # одна странная запись не должна ронять всю сводку
            print("#%d <запись не разобрана: %s>" % (i, type(exc).__name__))
    return 0


if __name__ == "__main__":
    sys.exit(main())
