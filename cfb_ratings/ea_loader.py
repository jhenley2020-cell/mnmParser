#!/usr/bin/env python3
"""Add EA College Football overall ratings to <team>_ratings.json as a reference only.

Usage: python ea_loader.py ea.csv [--team "Ole Miss"]
CSV columns (header names are case-insensitive): name, position (optional), overall
Adds `ea_overall` (and `ea_minus_rating` = ea_overall - rating) to each matched player.
It never changes `rating`: EA's numbers are a reference, not an input.
"""
import argparse, csv, json, os, sys
from pff_loader import norm_name, grp

HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    ap.add_argument("--team", default="Ole Miss")
    a = ap.parse_args()

    ea = {}
    with open(a.csv, newline="", encoding="utf-8-sig") as fh:
        for r in csv.DictReader(fh):
            r = {k.strip().lower(): (v or "").strip() for k, v in r.items()}
            try:
                ea.setdefault(norm_name(r["name"]), []).append((r.get("position", ""), int(float(r["overall"]))))
            except (KeyError, ValueError):
                continue
    if not ea:
        sys.exit("No usable rows: need name and overall columns.")

    path = os.path.join(HERE, f'{a.team.lower().replace(" ", "_")}_ratings.json')
    data = json.load(open(path))
    hit, miss = 0, []
    for p in data["players"]:
        c = ea.get(norm_name(p["name"]))
        if not c:
            miss.append(p["name"]) if p.get("rating") else None
            continue
        same = [o for pos, o in c if not pos or grp(pos) == grp(p["pos"])]
        p["ea_overall"] = max(same or [o for _, o in c])
        if p.get("rating") is not None:
            p["ea_minus_rating"] = p["ea_overall"] - p["rating"]
        hit += 1
    json.dump(data, open(path, "w"), indent=2)
    print(f"matched {hit}/{len(data['players'])} players -> {path}")
    if miss:
        print("rated players with no EA match:", ", ".join(miss[:20]), "..." if len(miss) > 20 else "")


if __name__ == "__main__":
    main()
