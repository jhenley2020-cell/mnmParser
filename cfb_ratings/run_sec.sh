#!/bin/bash
# Build CFBD ratings, then merge PFF grades, for every SEC team except Ole Miss (already done).
cd "$(dirname "$0")"
for t in "Alabama" "Arkansas" "Auburn" "Florida" "Georgia" "Kentucky" "LSU" "Mississippi State" "Missouri" "Oklahoma" "South Carolina" "Tennessee" "Texas" "Texas A&M" "Vanderbilt"; do
  echo "=== $t"
  python build_ratings.py --team "$t" && python pff_loader.py merge --team "$t"
done
echo ALL DONE
