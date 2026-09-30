"""从《WT导弹与设备性能表》提取导弹参数，生成供 C# 内置的 JSON。

表格布局是「列为导弹、行为参数」，且每个参数分区会重复一遍导弹名行；
不同分区的同一枚导弹列名写法可能略有出入（如 AIM-9B/RB24 与 AIM-9B），
所以按「分组（表头行）+ 列位置」对齐，再跨分区合并。

用法：
    python tools/extract-missiles.py
输出：
    src/WarThunderTelemetry.Core/Weapons/missiles.json
"""

from __future__ import annotations

import json
import re
from pathlib import Path

from python_calamine import CalamineWorkbook

SRC = Path(r"C:/Users/1/Desktop/WT导弹与设备性能表(1).xlsx")
OUT = Path(__file__).resolve().parent.parent / "src" / "WarThunderTelemetry.Core" / "Weapons" / "missiles.json"

# 参与提取的工作表 -> (MissileKind, MissileGuidance)
# 按用户要求：目前只做空空弹（红外弹 + 雷达弹），其他表不提取。
SHEETS = {
    "红外弹": ("AirToAir", "Ir"),
    "雷达弹": ("AirToAir", "Sarh"),
}

NOISE = {"可", "否", "是", "无", "有", "√", "—", "-", "–", "/", "×", "?",
         "comp_a", "comp_b", "comp_h6", "octol", "hmx", "torpex"}

NUM_RE = re.compile(r"^-?\d+(\.\d+)?$")


def clean(v) -> str:
    """单元格文本化：去换行、压空白。"""
    if v is None:
        return ""
    return re.sub(r"\s+", " ", str(v).strip())


def to_num(v) -> float | None:
    """尽力把单元格转成数字；返回 None 表示不是纯数字。"""
    if v is None:
        return None
    if isinstance(v, (int, float)):
        return float(v)
    t = str(v).strip().replace(",", "")
    if not t or t in {"-", "—"}:
        return None
    if NUM_RE.match(t):
        return float(t)
    m = re.match(r"^(-?\d+(?:\.\d+)?)\s*(千克|kg|m|米|km|千米|s|秒|mm|毫米|N|牛|g|G|度|°|rad|弧度)?", t)
    if m and m.group(1):
        return float(m.group(1))
    return None


def is_name_cell(text: str) -> bool:
    """判断一个单元格是不是「导弹名」：非纯数字、短、不在噪声集。"""
    t = clean(text)
    if not t or len(t) > 40 or t.lower() in NOISE:
        return False
    if NUM_RE.match(t):
        return False
    return True


def find_groups(rows: list[list]) -> list[int]:
    """找导弹名表头行：名称格足够多的行。

    阈值必须够高 —— 否则「动态推力」这类整行长文本的**参数行**
    （如「否 海拔0米时: 31593N …」）会被误判成表头，把分区切碎，
    后面的参数全部错位。
    """
    groups = []
    for i, row in enumerate(rows):
        cells = [clean(c) for c in row[1:] if clean(c)]
        if len(cells) < 4:
            continue
        names = sum(1 for c in cells if is_name_cell(c))
        if names >= max(10, len(cells) * 0.55):
            groups.append(i)
    return groups


def norm_name(text: str) -> str:
    """名称归一化（用于跨分区合并）：小写、去分隔符。"""
    t = clean(text).lower()
    t = re.sub(r"[/\\_\- ]+", "", t)
    return t


# ---- 原始标签 -> 规范字段名 ----
# 顺序即优先级：先匹配的生效（二级/force1 必须先于普通推力判断）。
FIELD_MAP: list[tuple[str, str]] = [
    ("二级发动机 推力", "thrust2_n"),
    ("二级 推力", "thrust2_n"),
    ("二级发动机 基础推力", "thrust2_n"),
    ("二级发动机 动力段", "burn2_time_s"),
    ("二级 动力段", "burn2_time_s"),
    ("二级发动机", "thrust2_n"),          # 兜底（个别表只写“二级发动机”）
    ("空重，2级", "burnout2_mass_kg"),
    ("空重，1级", "burnout_mass_kg"),
    ("空重,2级", "burnout2_mass_kg"),
    ("空重,1级", "burnout_mass_kg"),
    ("质量", "mass_kg"),
    ("口径", "caliber_mm"),
    ("长度", "length_m"),
    ("推力", "thrust_n"),
    ("动力段", "burn_time_s"),
    ("初速", "start_speed_ms"),
    ("最高速度", "max_speed_ms"),
    ("阻力系数", "drag_cxk"),
    ("滞空", "life_time_s"),
    ("最大飞行距离", "max_distance_km"),
    ("最大过载", "max_g"),
    ("propNavAccelMax", "max_g"),
    ("reqAccelMax", "max_g"),
    ("飞控允许的最大过载", "max_g"),
    ("尾翼攻角", "fin_aoa"),
    ("1级增速", "boost_dv1_ms"),
    ("2级增速", "boost_dv2_ms"),
    ("一级增速", "boost_dv1_ms"),
    ("二级增速", "boost_dv2_ms"),
    ("预热", "warmup_s"),
    ("工作时间", "work_time_s"),
    ("离轴锁定角", "offboresight_deg"),
    ("rangeBand0", "seeker_rear_km"),
    ("rangeBand1", "seeker_all_km"),
    ("rangeBand2", "seeker_flare_km"),
    ("rangeBand3", "seeker_irccm_km"),
    ("rangeBand7", "seeker_ab_km"),
    ("rangeMax", "seeker_max_km"),
    ("极限锁定", "seeker_max_km"),
    ("爆炸物当量", "explosive_mass_kg"),
    ("等效TNT", "tnt_equivalent_kg"),
    ("延迟", "maneuver_delay_s"),
    ("自爆", "self_destruct_s"),
    ("上抛", "loft"),
]

# 保留原文的文本型参数（用于细化制导/展示）
TEXT_FIELDS = ("制导类型", "滤波类型", "引导头波段", "导引头波段", "IRCCM", "抗干扰手段", "战斗部", "爆炸物种类")


def map_label(label: str) -> str | None:
    low = label.lower()
    for pat, field in FIELD_MAP:
        if pat.lower() in low:
            return field
    return None


def refine_guidance(current: str, texts: dict[str, str]) -> str:
    """用「制导类型」原文细化制导分类。"""
    t = texts.get("制导类型", "")
    if not t:
        return current
    if "反辐射" in t:
        return "AntiRadiation"
    if "主动雷达" in t or "主动雷达导引头" in t:
        return "Arh"
    if "半主动" in t:
        return "Sarh"
    if "红外" in t and "成像" not in t:
        return "Ir"
    if "图像" in t or "电视" in t or "红外成像" in t:
        return "ElectroOptical"
    if "架束" in t or "指令" in t or "三点法" in t:
        return "Command"
    return current


def rows_of_section(rows: list[list], start: int, end: int):
    """一个分区内的参数行：label -> 每列取值。"""
    for i in range(start + 1, end):
        row = rows[i]
        if not row:
            continue
        label = clean(row[0]) if row else ""
        if not label or len(label) > 60:
            continue
        yield i, label, row


def name_fuzzy_eq(a: str, b: str) -> bool:
    """宽松匹配：归一化相等，或一方是另一方的前缀（AIM-9B/RB24 ≈ AIM-9B）。"""
    na, nb = norm_name(a), norm_name(b)
    if not na or not nb:
        return False
    if na == nb:
        return True
    return na.startswith(nb) or nb.startswith(na)


def build_groups(rows: list[list], candidates: list[int]):
    """把候选表头行划成若干「导弹组」。

    组内各分区是同一批导弹的列位置重复（个别列名写法略有出入）；
    与当前组按位置宽松比对不上的，视为新的一组导弹。
    返回 [{"names": [主表头列名], "master": 行号, "sections": [各行号]}, ...]
    """
    groups: list[dict] = []
    for cand in candidates:
        cells = [clean(c) for c in rows[cand][1:]]
        placed = False
        for g in groups:
            master = g["names"]
            n = min(len(master), len(cells))
            if n < 4:
                continue
            pair = [(a, b) for a, b in zip(master[:n], cells[:n]) if a or b]
            if not pair:
                continue
            same = sum(1 for a, b in pair if name_fuzzy_eq(a, b))
            if same / len(pair) >= 0.6 and abs(len(cells) - len(master)) <= 3:
                g["sections"].append(cand)
                placed = True
                break
        if not placed:
            groups.append({"names": cells, "master": cand, "sections": [cand]})
    return groups


def main() -> None:
    wb = CalamineWorkbook.from_path(str(SRC))
    missiles: dict[str, dict] = {}
    stats = []

    for sheet_name, (kind, guidance) in SHEETS.items():
        rows = wb.get_sheet_by_name(sheet_name).to_python()
        candidates = find_groups(rows)
        if not candidates:
            stats.append((sheet_name, 0, "无表头"))
            continue

        groups = build_groups(rows, candidates)
        section_bounds = sorted(
            [s for g in groups for s in g["sections"]] + [len(rows)]
        )

        for g in groups:
            master = g["names"]
            for section_row in g["sections"]:
                # 本分区参数行的范围：到下一个「任何组的分区表头」为止
                end = min(
                    (b for b in section_bounds if b > section_row),
                    default=len(rows),
                )
                header = rows[section_row]
                sec_names = [clean(c) for c in header[1:]]

                for ci, raw_name in enumerate(master):
                    if not raw_name or not is_name_cell(raw_name):
                        continue

                    # 在本分区表头里找这枚导弹的列：优先同位置，位置对不上时
                    # 在 ±2 列内按名称搜（分区之间偶尔有列增删）。
                    # 找不到就跳过 —— 宁可少取数也不能把别弹的参数串进来。
                    col_in_section = None
                    if ci < len(sec_names) and sec_names[ci] and \
                       name_fuzzy_eq(raw_name, sec_names[ci]):
                        col_in_section = ci
                    else:
                        best = None
                        for j, sn in enumerate(sec_names):
                            if not sn or abs(j - ci) > 2:
                                continue
                            if name_fuzzy_eq(raw_name, sn):
                                if best is None or abs(j - ci) < abs(best - ci):
                                    best = j
                        col_in_section = best

                    if col_in_section is None:
                        continue
                    ci = col_in_section

                    mkey = f"{sheet_name}|g{g['master']}|c{ci}"
                    missile = missiles.setdefault(mkey, {
                        "id": "", "displayName": raw_name, "kind": kind,
                        "guidance": guidance, "sheet": sheet_name,
                        "params": {}, "texts": {}, "aliases": set(),
                    })
                    missile["aliases"].add(raw_name)

                    for _, label, row in rows_of_section(rows, section_row, end):
                        # master 名称从 row[1:] 枚举，数据行的 row[0] 是标签列，
                        # 所以真正的数据列是 ci + 1 —— 差一位整表左移，全部串列。
                        col = ci + 1
                        if col >= len(row):
                            continue
                        cell = row[col]
                        if cell is None:
                            continue

                        mapped = map_label(label)
                        if mapped:
                            val = to_num(cell)
                            if val is not None:
                                missile["params"].setdefault(mapped, val)
                            continue

                        for tf in TEXT_FIELDS:
                            if tf in label:
                                txt = clean(cell)
                                if txt:
                                    missile["texts"].setdefault(tf, txt)
                                break

        stats.append((sheet_name,
                      sum(1 for k in missiles if k.startswith(sheet_name + "|")),
                      f"{len(groups)} 组"))

    # 生成 id 与别名，单位归一（km -> m）
    result = []
    seen_ids: set[str] = set()
    for mkey, m in missiles.items():
        # 斜杠复合名（R-60M/MK、AIM-9L/Rb74）拆出的每一段、
        # 以及去掉括号注记后的名字，都登记为别名 —— 游戏挂载名才能查到。
        expanded = set()
        for a in m["aliases"]:
            expanded.add(a)
            base = re.sub(r"（[^）]*）|\([^)]*\)", "", a).strip()
            if base and base != a:
                expanded.add(base)
            for part in re.split(r"[/\\]", base):
                part = part.strip()
                if len(part) >= 3:
                    expanded.add(part)
        m["aliases"] = expanded
        p = m["params"]
        if "max_distance_km" in p:
            p["max_distance_m"] = p.pop("max_distance_km") * 1000
        for k in ("seeker_rear_km", "seeker_all_km", "seeker_flare_km",
                  "seeker_irccm_km", "seeker_ab_km", "seeker_max_km"):
            if k in p:
                p[k.replace("_km", "_m")] = p.pop(k) * 1000

        m["guidance"] = refine_guidance(m["guidance"], m["texts"])

        base = re.sub(r"[^a-z0-9]+", "-", norm_name(m["displayName"])).strip("-")
        mid = base or f"missile-{len(result)}"
        n = 2
        while mid in seen_ids:
            mid = f"{base}-{n}"
            n += 1
        seen_ids.add(mid)
        m["id"] = mid
        result.append(m)

    # 同名多列（不同参数分区组）时，参数最全的拿无后缀 id。
    result.sort(key=lambda m: -len(m["params"]))
    seen_ids.clear()
    for m in result:
        base = re.sub(r"[^a-z0-9]+", "-", norm_name(m["displayName"])).strip("-")
        mid = base or f"missile-{len(seen_ids)}"
        n = 2
        while mid in seen_ids:
            mid = f"{base}-{n}"
            n += 1
        seen_ids.add(mid)
        m["id"] = mid

    # 用户要求：没参数的不要。只保留对包线计算有用的导弹 ——
    # 核心飞行参数（推力/燃烧/极速/最大距离/滞空/增速）至少占 3 个。
    CORE = ("thrust_n", "burn_time_s", "max_speed_ms",
            "max_distance_m", "life_time_s", "boost_dv1_ms", "drag_cxk")
    before = len(result)
    result = [m for m in result if sum(1 for k in CORE if k in m["params"]) >= 3]
    dropped = before - len(result)

    # 有飞行参数的排前面，纯标志参数的排后面
    result.sort(key=lambda m: (
        0 if "thrust_n" in m["params"] or "max_distance_m" in m["params"] else 1,
        m["sheet"], m["id"],
    ))

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps({
        "source": "WT导弹与设备性能表 2.44.0.23（B站@库撒的幽灵 @苍之古叶 整理，数据挖掘值）",
        "count": len(result),
        "missiles": [
            {**m, "aliases": sorted(m["aliases"])} for m in result
        ],
    }, ensure_ascii=False), encoding="utf-8")

    for name, count, note in stats:
        print(f"{name:<20} {count:>4} 枚  ({note})")
    print(f"有效 {len(result)} 枚（剔除无参数 {dropped} 枚）-> {OUT}")


if __name__ == "__main__":
    main()
