"""Recent news about a place: a list of headlines to review, nothing more.

The headlines come from the GDELT DOC API (free, open, made for this). The app never reads or stores
the articles themselves: a headline, its outlet, its date and its link are enough to decide whether a
report names a damaged building. A person then adds it as a report (condition.py) and links it to
the building; nothing in a headline is applied to a building automatically.

GDELT limits how often it answers (about one request every few seconds, and not reliably): a request
that comes too soon gets a plain sentence instead of JSON, so the fetch waits and tries again, and
gives up with NewsUnavailable. The last good answer is kept on disk.
"""
import datetime as dt
import json
import re
import time
import urllib.parse
import urllib.request
from pathlib import Path

GDELT_URL = "https://api.gdeltproject.org/api/v2/doc/doc"
# GDELT answers an ordinary browser agent more reliably than an unknown one (observed in October 2026).
AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) BuildingExtractor/1.0"


class NewsUnavailable(Exception):
    """GDELT did not answer with data (it was busy, limiting requests or offline)."""


def _date(value):
    match = re.match(r"(\d{4})(\d{2})(\d{2})", value or "")
    return f"{match[1]}-{match[2]}-{match[3]}" if match else None


def parse(payload):
    """Headlines of a GDELT article list ({"articles": [...]}), newest first, one per link."""
    items, seen = [], set()
    for article in (payload.get("articles") if isinstance(payload, dict) else payload) or []:
        if not isinstance(article, dict):
            continue
        url = article.get("url") or ""
        title = " ".join((article.get("title") or "").split())
        if not url.startswith(("http://", "https://")) or not title or url in seen:
            continue
        seen.add(url)
        items.append({"title": title[:300], "url": url[:2000], "domain": article.get("domain", ""),
                      "language": article.get("language", ""), "country": article.get("sourcecountry", ""),
                      "seenAt": _date(article.get("seendate"))})
    return items


def fetch(query, days=14, limit=40, retries=2, wait=8.0, opener=urllib.request.urlopen, sleep=time.sleep):
    """Headlines matching `query` from the last `days` days. Raises NewsUnavailable when GDELT will not answer."""
    url = GDELT_URL + "?" + urllib.parse.urlencode({"query": query, "mode": "artlist", "format": "json", "maxrecords": limit,
                                                    "timespan": f"{days}d", "sort": "datedesc"})
    for attempt in range(retries + 1):
        try:
            with opener(urllib.request.Request(url, headers={"User-Agent": AGENT}), timeout=45) as response:
                raw = response.read().decode("utf-8", errors="replace")
        except OSError as error:
            raise NewsUnavailable(f"GDELT could not be reached: {error}") from error
        try:
            return parse(json.loads(raw))
        except ValueError:
            pass  # a request that comes too soon is answered with a sentence, not JSON
        if attempt < retries:
            sleep(wait)
    raise NewsUnavailable("GDELT limits how often it answers; try again in a minute.")


def cached(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"query": None, "fetchedAt": None, "items": []}


def refresh(path, query, **options):
    """Fetches, keeps and returns {query, fetchedAt, items}. A failure leaves the last answer in place."""
    items = fetch(query, **options)
    data = {"query": query, "fetchedAt": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
            "items": items}
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
    return data
