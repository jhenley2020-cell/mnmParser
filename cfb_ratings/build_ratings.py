#!/usr/bin/env python3
"""Build opponent-adjusted team ratings and shrunk player ratings from CollegeFootballData.

Usage:  CFBD_API_KEY=... python3 build_ratings.py [--year 2026] [--team "Ole Miss"]
Writes: <team>_ratings.json next to this script.
The API key is read from the environment only; it is never written to disk.
"""
import argparse, json, math, os, sys, urllib.parse, urllib.request
from collections import defaultdict
import numpy as np

BASE = "https://api.collegefootballdata.com/"


def get(path, **params):
    key = os.environ.get("CFBD_API_KEY")
    if not key:
        sys.exit("Set CFBD_API_KEY in the environment.")
    q = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})
    req = urllib.request.Request(f"{BASE}{path}?{q}", headers={"Authorization": f"Bearer {key}"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.load(r)


# ---------------------------------------------------------------- team ratings
def fit_team_ratings(year, ridge=8.0):
    """y(team offense in game) = mu + O_team - D_opp + hfa*home.  Ridge-shrunk so teams with
    few games (and FCS opponents) are pulled toward average. Higher D = better defense."""
    rows = get("ppa/games", year=year, excludeGarbageTime="true")
    games = {g["id"]: g for g in get("games", year=year, seasonType="regular")}
    teams = sorted({r["team"] for r in rows} | {r["opponent"] for r in rows})
    idx = {t: i for i, t in enumerate(teams)}
    n = len(teams)
    out = {}
    for metric in ("overall", "passing", "rushing"):
        X, y = [], []
        for r in rows:
            v = r["offense"].get(metric)
            if v is None:
                continue
            g = games.get(r["gameId"], {})
            home = 0.0
            if not g.get("neutralSite"):
                home = 1.0 if g.get("homeTeam") == r["team"] else -1.0 if g.get("awayTeam") == r["team"] else 0.0
            x = np.zeros(2 * n + 2)
            x[idx[r["team"]]] = 1
            x[n + idx[r["opponent"]]] = -1
            x[2 * n] = 1          # intercept
            x[2 * n + 1] = home   # home field
            X.append(x); y.append(v)
        X, y = np.array(X), np.array(y)
        pen = np.eye(2 * n + 2) * ridge
        pen[2 * n, 2 * n] = pen[2 * n + 1, 2 * n + 1] = 1e-6   # don't shrink intercept / hfa
        beta = np.linalg.solve(X.T @ X + pen, X.T @ y)
        out[metric] = {t: (beta[idx[t]], beta[n + idx[t]]) for t in teams}
        out[metric + "_hfa"] = float(beta[2 * n + 1])
    games_played = defaultdict(int)
    for r in rows:
        games_played[r["team"]] += 1
    return teams, out, games_played


def team_table(teams, fit, gp):
    res = {}
    for t in teams:
        res[t] = {
            "games": gp[t],
            "off_ppa": fit["overall"][t][0], "def_ppa": fit["overall"][t][1],
            "off_pass": fit["passing"][t][0], "def_pass": fit["passing"][t][1],
            "off_rush": fit["rushing"][t][0], "def_rush": fit["rushing"][t][1],
        }
        res[t]["net_ppa"] = res[t]["off_ppa"] + res[t]["def_ppa"]
    # Restrict ranking to teams with >=4 games (avoids sparse FCS noise)
    ranked = sorted((t for t in res if res[t]["games"] >= 4), key=lambda t: -res[t]["net_ppa"])
    for i, t in enumerate(ranked, 1):
        res[t]["rank"] = i
    return res, len(ranked)


# --------------------------------------------------------------- player ratings
def phi(z):
    return 0.5 * (1 + math.erf(z / math.sqrt(2)))


def to_rating(z):
    """z-score within position -> 40..99 scale (average player ~ 70)."""
    return int(round(40 + 59 * phi(z)))


def skill_ratings(year, team):
    """QB/RB/WR/TE from league-wide PPA, empirical-Bayes shrunk toward the position mean."""
    league = get("ppa/players/season", year=year, excludeGarbageTime="true")
    K = 40  # prior strength in plays
    by_pos = defaultdict(list)
    for p in league:
        a, tot = p["averagePPA"].get("all"), p["totalPPA"].get("all")
        if a in (None, 0) or tot is None:
            continue
        n = abs(tot / a)
        if n >= 15:
            by_pos[p["position"]].append((a, n))
    stats = {}
    for pos, v in by_pos.items():
        a = np.array([x[0] for x in v]); w = np.array([x[1] for x in v])
        mu = float(np.average(a, weights=w))
        sd = float(math.sqrt(np.average((a - mu) ** 2, weights=w)))
        stats[pos] = (mu, max(sd, 1e-6))
    out = {}
    for p in league:
        if p["team"] != team:
            continue
        a, tot = p["averagePPA"].get("all"), p["totalPPA"].get("all")
        if a in (None, 0) or tot is None or p["position"] not in stats:
            continue
        n = abs(tot / a)
        mu, sd = stats[p["position"]]
        shrunk = (n * a + K * mu) / (n + K)
        z = (shrunk - mu) / (sd * 0.8)   # 0.8: shrunk values spread less than raw ones
        out[p["id"]] = {"rating": to_rating(z), "avg_ppa": a, "plays": round(n),
                        "confidence": "medium" if n >= 60 else "low", "basis": "league-wide PPA, shrunk"}
    return out


DEF_CATS = {"TFL": 1.0, "SACKS": 2.0, "PD": 1.0, "INT": 2.0, "TOT": 0.25}


def defense_production(year, team):
    """Defenders: per-position z-score of a weighted production index. Low confidence by design
    (box-score counting stats cannot isolate coverage or run-fit quality)."""
    stats = get("stats/player/season", year=year, category="defensive")
    per = defaultdict(lambda: defaultdict(float))
    meta = {}
    for s in stats:
        if s["statType"] in DEF_CATS:
            try:
                per[s["playerId"]][s["statType"]] += float(s["stat"])
            except ValueError:
                pass
        meta[s["playerId"]] = (s["position"], s["team"], s["player"])
    idx = {pid: sum(DEF_CATS[k] * v for k, v in d.items()) for pid, d in per.items()}
    by_pos = defaultdict(list)
    for pid, v in idx.items():
        pos = meta[pid][0]
        if per[pid].get("TOT", 0) >= 5:
            by_pos[pos].append(v)
    out = {}
    for pid, v in idx.items():
        pos, tm, _ = meta[pid]
        if tm != team or pos not in by_pos or len(by_pos[pos]) < 10:
            continue
        arr = np.array(by_pos[pos]); mu, sd = arr.mean(), arr.std() or 1.0
        z = 0.7 * (v - mu) / sd   # damped: counting stats are noisy
        out[pid] = {"rating": to_rating(z), "production_index": round(v, 1),
                    "confidence": "low", "basis": "box-score defensive production vs position peers"}
    return out


def build_players(year, team):
    roster = get("roster", year=year, team=team)
    skill = skill_ratings(year, team)
    defense = defense_production(year, team)
    usage = {u["id"]: u["usage"]["overall"] for u in get("player/usage", year=year, team=team, excludeGarbageTime="true")}
    avail_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "availability.json")
    overrides = {}
    if os.path.exists(avail_path):
        with open(avail_path) as f:
            overrides = json.load(f).get(team, {})
    players = []
    for r in roster:
        pid = r["id"]
        entry = {"id": pid, "name": f'{r["firstName"]} {r["lastName"]}', "pos": r["position"],
                 "jersey": r.get("jersey"), "class_year": r.get("year"),
                 "height_in": r.get("height"), "weight_lb": r.get("weight"),
                 "usage_share": usage.get(pid)}
        info = skill.get(pid) or defense.get(pid)
        if info:
            entry.update(info)
        else:
            entry.update({"rating": None, "confidence": "none",
                          "basis": "no snap/grade data in CFBD for this player (typical for OL and reserves)"})
        o = overrides.get(entry["name"])
        entry["availability"] = o["status"] if o else "available"
        if o:
            entry["availability_note"] = o.get("note")
        players.append(entry)
    players.sort(key=lambda p: (p["rating"] is None, -(p["rating"] or 0)))
    return players


# ------------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--year", type=int, default=2026)
    ap.add_argument("--team", default="Ole Miss")
    a = ap.parse_args()

    teams, fit, gp = fit_team_ratings(a.year)
    table, nranked = team_table(teams, fit, gp)
    me = table[a.team]
    sp = get("ratings/sp", year=a.year, team=a.team)
    elo = get("ratings/elo", year=a.year, team=a.team)
    players = build_players(a.year, a.team)

    result = {
        "team": a.team, "year": a.year,
        "team_rating": {**{k: round(v, 3) if isinstance(v, float) else v for k, v in me.items()},
                        "ranked_among": nranked,
                        "note": "PPA units (points/play); defense value is positive = better. Garbage time excluded.",
                        "reference": {"sp_plus": sp[0]["rating"] if sp else None, "elo": elo[0]["elo"] if elo else None}},
        "players": players,
    }
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), f'{a.team.lower().replace(" ", "_")}_ratings.json')
    with open(path, "w") as f:
        json.dump(result, f, indent=2)
    print("wrote", path)


if __name__ == "__main__":
    main()
