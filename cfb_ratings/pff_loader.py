#!/usr/bin/env python3
"""Load PFF player grades and fold them into <team>_ratings.json (40-99 scale, shrunk by snaps).

Usage:
  python pff_loader.py fetch [--year 2026]            # API -> pff_grades_<year>.json (cache)
  python pff_loader.py fetch --csv grades.csv          # or from a CSV export (cols: player,team,position,grade,snaps)
  python pff_loader.py merge [--year 2026] [--team "Ole Miss"]

The API key is read from PFF_API_KEY, else from the file named by pff_config.json "key_file".
It is only ever sent as a request header: never printed, logged, or written to disk.
Run `fetch` once, then `merge` as often as you like (merge is offline).
"""
import argparse, csv, json, math, os, re, sys, unicodedata, urllib.parse, urllib.request
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
CFG = json.load(open(os.path.join(HERE, "pff_config.json"), encoding="utf-8"))


def api_key():
    k = os.environ.get("PFF_API_KEY")
    if not k and os.path.exists(CFG["key_file"]):
        k = open(CFG["key_file"], encoding="utf-8").read().strip()
    if not k:
        sys.exit("No PFF key: set PFF_API_KEY or fix key_file in pff_config.json.")
    return k


def http_get(path, params=None):
    q = urllib.parse.urlencode(params or {})
    url = CFG["base_url"].rstrip("/") + path + ("?" + q if q else "")
    h = {"Authorization": "Bearer " + api_key(), "Accept": "application/json"}
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=h), timeout=120) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:   # message only; headers (with the key) are never printed
        if e.code == 404:
            return None
        sys.exit(f"PFF API HTTP {e.code} for {path}.")


def fetch_api(year):
    """Every NCAA team's depth-chart roster (grade + snaps per player) for the season."""
    lg = CFG["league"]
    d = http_get(f"/v2/{lg}/teams", {"season": year})
    teams = d["rows"] if "rows" in d else d
    rows = []
    for i, t in enumerate(teams, 1):
        r = http_get(f"/v2/{lg}/teams/{t['slug']}/roster", {"season": year})
        if not r:
            continue
        for p in r["rows"]:
            if p.get("grade") is None or not p.get("snapCounts"):
                continue
            rows.append({"player": p["name"], "team": t["name"], "position": p["position"],
                         "grade": float(p["grade"]), "snaps": float(p["snapCounts"])})
        print(f"\r{i}/{len(teams)} teams", end="", flush=True)
    print()
    return rows


def normalise_rows(raw):
    return raw


def fetch(a):
    if a.csv:
        with open(a.csv, newline="", encoding="utf-8-sig") as fh:
            rows = [{"player": r["player"], "team": r["team"], "position": r["position"],
                     "grade": float(r["grade"]), "snaps": float(r["snaps"] or 0)} for r in csv.DictReader(fh)]
    else:
        rows = normalise_rows(fetch_api(a.year))
    if not rows:
        sys.exit("0 usable rows - field names in pff_config.json probably don't match the API response.")
    path = os.path.join(HERE, f"pff_grades_{a.year}.json")
    json.dump(rows, open(path, "w"), indent=1)
    print(f"wrote {len(rows)} rows -> {path}")


# ------------------------------------------------------------------------ merge
def norm_name(s):
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode().lower()
    s = re.sub(r"\b(jr|sr|ii|iii|iv|v)\b\.?", "", s)
    return " ".join(re.sub(r"[^a-z ]", "", s).split())


POS_GROUP = {"HB": "RB", "FB": "RB", "RB": "RB", "WR": "WR", "TE": "TE", "QB": "QB",
             "T": "OL", "G": "OL", "C": "OL", "OT": "OL", "OG": "OL", "OL": "OL", "IOL": "OL",
             "DE": "EDGE", "ED": "EDGE", "EDGE": "EDGE", "DT": "DI", "DI": "DI", "DL": "DI", "NT": "DI",
             "LB": "LB", "ILB": "LB", "OLB": "LB", "CB": "DB", "S": "DB", "FS": "DB", "SS": "DB", "DB": "DB"}


def grp(p):
    return POS_GROUP.get((p or "").upper(), (p or "").upper())


def merge(a):
    from build_ratings import to_rating   # same 40-99 mapping as the CFBD ratings
    rows = json.load(open(os.path.join(HERE, f"pff_grades_{a.year}.json")))
    K, minsn = CFG["prior_snaps"], CFG["min_snaps_for_league_stats"]
    by = defaultdict(list)
    for r in rows:
        if r["snaps"] >= minsn:
            by[grp(r["position"])].append((r["grade"], r["snaps"]))
    stats = {}
    for g, v in by.items():
        if len(v) < 10:
            continue
        tot = sum(s for _, s in v)
        mu = sum(x * s for x, s in v) / tot
        sd = math.sqrt(sum(s * (x - mu) ** 2 for x, s in v) / tot) or 1.0
        stats[g] = (mu, sd)

    aliases = {t.lower() for t in CFG["team_aliases"].get(a.team, [a.team])}
    mine = defaultdict(list)
    for r in rows:
        if r["team"].lower() in aliases:
            mine[norm_name(r["player"])].append(r)

    path = os.path.join(HERE, f'{a.team.lower().replace(" ", "_")}_ratings.json')
    data = json.load(open(path))
    hit = 0
    for p in data["players"]:
        cands = [r for r in mine.get(norm_name(p["name"]), []) if grp(r["position"]) == grp(p["pos"])] \
                or mine.get(norm_name(p["name"]), [])
        if not cands:
            continue
        r = max(cands, key=lambda c: c["snaps"])
        g = grp(r["position"])
        if g not in stats or r["snaps"] <= 0:
            continue
        mu, sd = stats[g]
        n = r["snaps"]
        shrunk = (n * r["grade"] + K * mu) / (n + K)
        z = (shrunk - mu) / (sd * 0.8)    # shrunk grades spread less than raw ones
        p.setdefault("cfbd_rating", p.get("rating"))
        p.setdefault("cfbd_basis", p.get("basis"))
        p.update({"rating": to_rating(z), "pff_grade": r["grade"], "pff_snaps": round(n),
                  "confidence": "high" if n >= 300 else "medium" if n >= 100 else "low",
                  "basis": "PFF grade, shrunk toward position mean by snaps"})
        hit += 1
    data["players"].sort(key=lambda p: (p["rating"] is None, -(p["rating"] or 0)))
    json.dump(data, open(path, "w"), indent=2)
    print(f"matched {hit}/{len(data['players'])} roster players -> {path}")
    miss = [p["name"] for p in data["players"] if "pff_grade" not in p and p["pos"] in ("QB", "RB", "WR", "TE")]
    if miss:
        print("skill players with no PFF match:", ", ".join(miss))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["fetch", "merge"])
    ap.add_argument("--year", type=int, default=2026)
    ap.add_argument("--team", default="Ole Miss")
    ap.add_argument("--csv")
    a = ap.parse_args()
    fetch(a) if a.cmd == "fetch" else merge(a)
