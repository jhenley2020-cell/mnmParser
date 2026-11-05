#!/usr/bin/env python3
"""Re-fit Ole Miss's opponent-adjusted rushing rating with Kewan Lacy's carries removed.

For each Ole Miss game, the rushing PPA/play from CFBD `ppa/games` is shifted by the difference
between (all Ole Miss rush plays) and (rush plays excluding Lacy), measured on raw play-level PPA.
Opponent/home adjustment is then re-fit with the same ridge model as build_ratings.py.
Usage: python refit_no_lacy.py [--year 2026] [--team "Ole Miss"] [--player "K.Lacy"]
"""
import argparse, json, os
import build_ratings as b

ap = argparse.ArgumentParser()
ap.add_argument("--year", type=int, default=2026)
ap.add_argument("--team", default="Ole Miss")
ap.add_argument("--player", default="K.Lacy", help="name as it appears in play text, e.g. K.Lacy")
a = ap.parse_args()

# play-level PPA for the team's rushes, per game
by_game = {}
for g in b.get("games", year=a.year, team=a.team, seasonType="regular"):
    for p in b.get("plays", year=a.year, week=g["week"], team=a.team, seasonType="regular"):
        if p.get("offense") == a.team and p.get("playType") in ("Rush", "Rushing Touchdown") and p.get("ppa") is not None:
            by_game.setdefault(p["gameId"], []).append((a.player in p["playText"], p["ppa"]))

delta, carries = {}, [0, 0]
for gid, pl in by_game.items():
    rest = [v for lac, v in pl if not lac]
    allv = [v for _, v in pl]
    carries[0] += len(allv) - len(rest); carries[1] += len(allv)
    if rest and len(rest) < len(allv):
        delta[gid] = sum(rest) / len(rest) - sum(allv) / len(allv)
print(f"{a.player}: {carries[0]} of {carries[1]} {a.team} rushes removed across {len(delta)} games")

orig_get = b.get
def patched(path, **kw):
    out = orig_get(path, **kw)
    if path == "ppa/games":
        for r in out:
            if r["team"] == a.team and r["gameId"] in delta and r["offense"].get("rushing") is not None:
                r["offense"]["rushing"] += delta[r["gameId"]]
    return out

def rush_row(fit, gp):
    t = a.team
    return {"off_rush": fit["rushing"][t][0], "def_rush": fit["rushing"][t][1]}

teams, fit0, gp = b.fit_team_ratings(a.year)
base = rush_row(fit0, gp)
b.get = patched
teams, fit1, gp = b.fit_team_ratings(a.year)
b.get = orig_get
new = rush_row(fit1, gp)

rank = lambda f: 1 + sum(1 for t in teams if gp[t] >= 4 and f["rushing"][t][0] > f["rushing"][a.team][0])
res = {"team": a.team, "year": a.year, "removed_player": a.player,
       "rushes_removed": carries[0], "rushes_total": carries[1],
       "off_rush_with": round(base["off_rush"], 3), "off_rush_without": round(new["off_rush"], 3),
       "off_rush_rank_with": rank(fit0), "off_rush_rank_without": rank(fit1)}
print(json.dumps(res, indent=2))
path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ole_miss_rush_refit.json")
json.dump(res, open(path, "w"), indent=2)
