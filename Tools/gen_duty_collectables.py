#!/usr/bin/env python3
"""Generate the duty -> learnable-collectable mapping for DailyDuty's
Duty Finder annotation feature.

Sources: LuminaSupplemental community drop CSVs (already downloaded to
lumisup/) intersected with TC 7.20 EXD (authoritative for what exists on
this client). Faded orchestrion copies map to the roll(s) they craft into,
since the unlock state lives on the roll, not the faded copy.

Output: duty_collectables.json  {"<cfcId>": [[itemId, type, unlockId...], ...]}
type: 1=mount 2=minion 3=orchestrion 4=faded 5=card 6=barding 7=other
unlockId list only present for faded copies (crafted roll ids).
"""
import csv
import json
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
E = Path(r"D:\ffxiv-tc-port\exd-tc\7.20")
L = Path(__file__).with_name("lumisup")

def rows(p):
    with open(p, encoding="utf-8") as f:
        r = csv.reader(f)
        hdr = next(r)
        col = {c: i for i, c in enumerate(hdr)}
        for row in r:
            if row and row[0].strip().isdigit():
                yield col, row

# --- TC EXD ---
item_name, item_action = {}, {}
for col, r in rows(E / "Item.csv"):
    rid = int(r[0])
    item_name[rid] = r[col["Name"]]
    a = r[col["ItemAction"]]
    item_action[rid] = int(a) if a.isdigit() else 0

ia_type = {}
for col, r in rows(E / "ItemAction.csv"):
    ia_type[int(r[0])] = int(r[col["Action"]])

cfc_ok = set()
for col, r in rows(E / "ContentFinderCondition.csv"):
    if r[col["Name"]].strip():
        cfc_ok.add(int(r[0]))

# faded copy -> crafted roll(s): recipes whose result is an orchestrion roll,
# indexed by each ingredient
TYPE = {1322: 1, 853: 2, 25183: 3, 3357: 5, 1013: 6, 2633: 7, 20086: 7, 29459: 7}
def itype(item_id):
    return TYPE.get(ia_type.get(item_action.get(item_id, 0), 0), 0)

faded_to_rolls = defaultdict(set)
for col, r in rows(E / "Recipe.csv"):
    result = r[col["ItemResult"]]
    if not result.isdigit():
        continue
    result = int(result)
    if itype(result) != 3:
        continue
    for k, i in col.items():
        if k.startswith("Ingredient["):
            ing = r[i]
            if ing.isdigit() and int(ing) > 0:
                faded_to_rolls[int(ing)].add(result)

# --- LuminaSupplemental drop tables -> (cfc, item) ---
pairs = set()
for col, r in rows(L / "DungeonDrop.csv"):
    pairs.add((int(r[col["ContentFinderConditionId"]]), int(r[col["ItemId"]])))
for col, r in rows(L / "DungeonBossDrop.csv"):
    pairs.add((int(r[col["ContentFinderConditionId"]]), int(r[col["ItemId"]])))
for col, r in rows(L / "DungeonBossChest.csv"):
    pairs.add((int(r[col["ContentFinderConditionId"]]), int(r[col["ItemId"]])))
chest_cfc = {}
for col, r in rows(L / "DungeonChest.csv"):
    chest_cfc[int(r[0])] = int(r[col["ContentFinderConditionId"]])
for col, r in rows(L / "DungeonChestItem.csv"):
    c = chest_cfc.get(int(r[col["ChestId"]]))
    if c:
        pairs.add((c, int(r[col["ItemId"]])))

# --- classify + filter to TC ---
out = defaultdict(list)
stats = defaultdict(int)
for cfc, item in sorted(pairs):
    if cfc not in cfc_ok or item not in item_name or not item_name[item]:
        continue
    t = itype(item)
    if t:
        out[cfc].append([item, t])
        stats[t] += 1
    elif item in faded_to_rolls:
        rolls = sorted(faded_to_rolls[item])
        out[cfc].append([item, 4, *rolls])
        stats[4] += 1

Path(__file__).with_name("duty_collectables.json").write_text(
    json.dumps({str(k): v for k, v in sorted(out.items())}, separators=(",", ":")),
    encoding="utf-8")
NAMES = {1: "坐騎", 2: "寵物", 3: "樂譜", 4: "陳舊樂譜", 5: "幻卡", 6: "鳥甲", 7: "其他"}
print(f"duties: {len(out)}, entries: {sum(len(v) for v in out.values())}")
for t in sorted(stats):
    print(f"  {NAMES[t]}: {stats[t]}")
# spot check: 天然要害沙斯塔夏溶洞 (cfc 4) and a known mount duty
for probe in (4,):
    print(f"cfc {probe}:", [(i[0], NAMES[i[1]], item_name[i[0]][:14]) for i in out.get(probe, [])][:6])
