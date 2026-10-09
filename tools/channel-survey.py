#!/usr/bin/env python3
"""Разбор Telegram-канала перед тем, как подключать его к программе и каталогу.

Скачивает всю историю t.me/s/<канал>, делит посты по виду, спрашивает у магазина Unity цены
найденных ассетов и сверяет их с каталогом аккаунта. Только чтение: аккаунт и каталог не меняются.

    python3 tools/channel-survey.py UnityAssetsTools
    python3 tools/channel-survey.py UnityAssetsTools --pages-dir /tmp/uat   # страницы сохранить/переиспользовать
    python3 tools/channel-survey.py UnityAssetsTools --no-store              # без запросов в магазин

Что показывает (по порядку):
  1. сколько постов и за какие месяцы, как менялась «первая строка» постов (шаблон);
  2. на какие сайты ведут ссылки, какие ссылки на ботов и с какими start=;
  3. по каждому виду постов: сколько ассетов магазина бесплатных/платных, сколько уже на аккаунте;
  4. бесплатные ассеты, которых ещё нет на аккаунте.

Вид «раздача через бота» (start=file_… / download_…) — платные ассеты без оплаты: в каталог не берём,
программа такие посты пропускает (ChannelCardParser). Образцы постов каждого вида выведите флагом --samples N.
"""
import argparse, collections, html, json, os, re, secrets, sys, tempfile, time, urllib.request
from urllib.parse import parse_qs, urlparse

UA = {"User-Agent": "Mozilla/5.0 (X11; Linux x86_64)"}
STORE_ID = re.compile(r"/packages/[^?#]*?[-/](\d+)/?(?:[?#]|$)")  # то же, что UnityAssetAutomationApp.ExtractPackageId


def get(url, tries=3):
    for attempt in range(tries):
        try:
            return urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=30).read().decode("utf-8")
        except Exception as ex:  # сеть бывает нестабильной
            if attempt == tries - 1:
                raise
            print(f"  повтор {url}: {ex}", file=sys.stderr)
            time.sleep(3)


def decode_href(href):
    """Telegram иногда кодирует адрес дважды (&amp;#33;): разбираем, пока меняется."""
    for _ in range(3):
        nxt = html.unescape(href)
        if nxt == href:
            break
        href = nxt
    return href.strip()


def crawl(channel, pages_dir, reuse):
    os.makedirs(pages_dir, exist_ok=True)
    before, n = None, 0
    while True:
        path = os.path.join(pages_dir, f"p_{before or 'last'}.html")
        if reuse and os.path.exists(path):
            page = open(path, encoding="utf-8").read()
        else:
            page = get(f"https://t.me/s/{channel}" + (f"?before={before}" if before else ""))
            open(path, "w", encoding="utf-8").write(page)
            time.sleep(0.7)
        ids = [int(x) for x in re.findall(r'data-post="[^"/]+/(\d+)"', page)]
        if not ids:
            if n == 0:
                sys.exit(f"Канал {channel}: постов не найдено (нет такого канала или у него нет веб-просмотра t.me/s/).")
            return
        n += 1
        yield page
        if min(ids) <= 1:
            return
        before = min(ids)


def parse_posts(pages):
    posts = {}
    for page in pages:
        for block in re.split(r'(?=<div class="tgme_widget_message_wrap)', page)[1:]:
            pid = re.search(r'data-post="[^"/]+/(\d+)"', block)
            if not pid:
                continue
            text, links = "", []
            m = re.search(r'<div class="tgme_widget_message_text[^"]*"[^>]*>', block)
            if m:
                depth, start = 1, m.end()
                end = len(block)
                for tag in re.finditer(r"<(/?)div\b", block[start:]):
                    depth += -1 if tag.group(1) else 1
                    if depth == 0:
                        end = start + tag.start()
                        break
                inner = block[start:end]
                links = [decode_href(x) for x in re.findall(r'<a\b[^>]*\shref="([^"]*)"', inner)]
                text = html.unescape(re.sub(r"<[^>]+>", "", re.sub(r"<br\s*/?>", "\n", inner))).strip()
            when = re.search(r'<time[^>]*datetime="([^"]+)"', block)
            posts[int(pid.group(1))] = {"id": int(pid.group(1)), "date": when.group(1) if when else "", "text": text, "links": links}
    return [posts[k] for k in sorted(posts)]


def bot_payload(link):
    u = urlparse(link)
    if u.netloc.lower() not in ("t.me", "telegram.me", "telegram.dog"):
        return None
    first = u.path.strip("/").split("/")[0]
    if not first.lower().endswith("bot"):
        return None
    return (parse_qs(u.query).get("start") or [""])[0]


def kind(post):
    """bot-download — раздача файла через бота; store — есть ссылка на магазин; other — остальное."""
    for link in post["links"]:
        payload = bot_payload(link)
        if payload and payload.lower().startswith(("file_", "download_")):
            return "bot-download"
    if any("assetstore.unity.com" in l and STORE_ID.search(l.split("#")[0]) for l in post["links"]):
        return "store"
    return "other"


def store_ids(post):
    return sorted({m.group(1) for l in post["links"] if "assetstore.unity.com" in l and (m := STORE_ID.search(l.split("#")[0]))})


def first_line_shape(text):
    """Грубая подпись шаблона: первые знаки первой строки (эмодзи/решётка/буква)."""
    line = next((l.strip() for l in text.split("\n") if l.strip()), "")
    if not line:
        return "(пусто)"
    ch = line[0]
    return "#хэштег" if ch == "#" else ("эмодзи " + ch if ord(ch) > 0x2000 else ("буква/цифра" if ch.isalnum() else "знак " + ch))


def fetch_prices(ids):
    csrf = secrets.token_hex(16)
    out = {}
    for i in range(0, len(ids), 50):
        batch = ids[i:i + 50]
        query = "query P {" + "".join(f' a{j}: product(id: "{b}") {{ id state originalPrice {{ isFree finalPrice currency }} }}' for j, b in enumerate(batch)) + " }"
        body = json.dumps([{"operationName": "P", "query": query}]).encode()
        req = urllib.request.Request("https://assetstore.unity.com/api/graphql/batch", data=body, headers={
            "Content-Type": "application/json", "X-Requested-With": "XMLHttpRequest", "X-Csrf-Token": csrf, "Cookie": f"_csrf={csrf}", **UA})
        for attempt in range(3):
            try:
                data = json.loads(urllib.request.urlopen(req, timeout=60).read())
                data = (data[0] if isinstance(data, list) else data)["data"]
                out.update({b: data.get(f"a{j}") for j, b in enumerate(batch)})
                break
            except Exception as ex:
                if attempt == 2:
                    print(f"  магазин не отдал пачку {i}: {ex}", file=sys.stderr)
                time.sleep(3)
    return out


def load_catalog(path):
    ids = set()
    if path and os.path.exists(path):
        for line in open(path, encoding="utf-8"):
            if line.strip():
                ids.add(json.loads(line)["id"])
    return ids


def default_catalog():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    try:
        profile = json.load(open(os.path.join(root, "data", "profiles.json"), encoding="utf-8"))["DefaultProfile"]
    except Exception:
        return None
    return os.path.join(root, "data", "profiles", profile, "catalog", "assets.jsonl")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("channel", help="имя канала: UnityAssetsTools, @имя или t.me/имя")
    ap.add_argument("--pages-dir", help="куда сохранить страницы (по умолчанию временная папка)")
    ap.add_argument("--reuse", action="store_true", help="не качать заново страницы, которые уже лежат в --pages-dir")
    ap.add_argument("--catalog", default=default_catalog(), help="assets.jsonl каталога аккаунта (по умолчанию профиль из data/profiles.json)")
    ap.add_argument("--no-store", action="store_true", help="не спрашивать цены у магазина")
    ap.add_argument("--samples", type=int, default=0, help="сколько образцов поста каждого вида показать")
    a = ap.parse_args()

    channel = re.sub(r"^(?:https?://)?(?:t\.me/(?:s/)?)?@?", "", a.channel).strip("/")
    pages_dir = a.pages_dir or tempfile.mkdtemp(prefix=f"survey-{channel}-")
    posts = [p for p in parse_posts(crawl(channel, pages_dir, a.reuse)) if p["text"] or p["links"]]
    json.dump(posts, open(os.path.join(pages_dir, "posts.json"), "w", encoding="utf-8"), ensure_ascii=False)
    print(f"Канал {channel}: {len(posts)} постов с текстом ({posts[0]['date'][:10]} … {posts[-1]['date'][:10]}). Страницы и posts.json: {pages_dir}")

    kinds = collections.Counter(kind(p) for p in posts)
    print("\n== 1. Виды постов:", dict(kinds))
    print("   по месяцам (вид / первая строка):")
    by_month = collections.defaultdict(collections.Counter)
    for p in posts:
        by_month[p["date"][:7]][f"{kind(p)} | {first_line_shape(p['text'])}"] += 1
    for month in sorted(by_month):
        print(f"   {month}: " + "; ".join(f"{k} ×{v}" for k, v in by_month[month].most_common(3)))

    print("\n== 2. Куда ведут ссылки (посты со ссылкой на сайт) и боты:")
    hosts, bots = collections.Counter(), collections.Counter()
    for p in posts:
        seen = set()
        for l in p["links"]:
            if l.startswith("?q="):
                continue
            host = urlparse(l).netloc.lower()
            payload = bot_payload(l)
            if payload is not None:
                bots[f"{urlparse(l).path.strip('/')}?start={re.sub(r'[0-9_-]+$', '…', payload)}"] += 1
            elif host and host not in seen:
                seen.add(host)
                hosts[host] += 1
    for h, n in hosts.most_common(12):
        print(f"   {n:5d} {h}")
    for b, n in bots.most_common(8):
        print(f"   {n:5d} бот {b}")

    ids_by_kind = {k: set() for k in kinds}
    for p in posts:
        ids_by_kind[kind(p)].update(store_ids(p))
    all_ids = sorted(set().union(*ids_by_kind.values()), key=int)
    print(f"\n== 3. Ассеты магазина в постах: {len(all_ids)} уникальных")
    if all_ids and not a.no_store:
        owned = load_catalog(a.catalog)
        print(f"   каталог аккаунта: {len(owned)} ассетов ({a.catalog})" if owned else "   каталог аккаунта не найден — «на аккаунте» не считается")
        prices = fetch_prices(all_ids)
        free_not_owned = {}
        for k, ids in ids_by_kind.items():
            c = collections.Counter()
            for i in ids:
                s = prices.get(i)
                if not s:
                    c["нет в магазине"] += 1
                    continue
                free = bool((s.get("originalPrice") or {}).get("isFree"))
                c[("бесплатный" if free else "платный") + (", на аккаунте" if i in owned else ", НЕ на аккаунте")] += 1
                if free and i not in owned:
                    free_not_owned[i] = k
            if ids:
                print(f"   {k}: {len(ids)} — " + "; ".join(f"{n} {name}" for name, n in sorted(c.items())))
        print(f"\n== 4. Бесплатные, которых нет на аккаунте: {len(free_not_owned)}")
        for i, k in list(free_not_owned.items())[:15]:
            print(f"   {i} ({k})")
        if len(free_not_owned) > 15:
            print(f"   … и ещё {len(free_not_owned) - 15}")

    if a.samples:
        print("\n== Образцы постов")
        for k in kinds:
            for p in [p for p in posts if kind(p) == k][-a.samples:]:
                print(f"\n--- {k} #{p['id']} {p['date'][:10]}\n{p['text'][:500]}\n{[l for l in p['links'] if not l.startswith('?q=')][:5]}")


if __name__ == "__main__":
    main()
