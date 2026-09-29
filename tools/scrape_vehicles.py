"""
从官方 wiki 抓取载具性能参数，产出 VehicleData.json。

数据链路（已实测验证）：
  /aviation 页  → 1324 个真实 slug
  每个 unit 页   → <textarea id="game-unit-initial"> 里的 "gameId"
                 → 这个 gameId 正是游戏 indicators.type 返回的代号
  Limits 区块    → Max Speed Limit (IAS) / Mach Number Limit / G limit
                   / Flap Speed Limit (IAS) / Gear Speed Limit (IAS)

为什么必须两步：wiki 的 slug 命名极不规则（spitfire_ix、hurricane_mk1、
bf-109f-4、a6m2_zero），无法从游戏代号推断，只能先取全量 slug 再逐页读 gameId。

用法：
    python scrape_vehicles.py --index-only   # 只建 slug 列表（快）
    python scrape_vehicles.py                # 全量抓取（约 1300 页，慢）
    python scrape_vehicles.py --limit 20     # 调试
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
import urllib.error
import urllib.request
from html import unescape
from pathlib import Path

BASE = "https://wiki.warthunder.com"
HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/120.0 Safari/537.36"
    ),
    "Accept-Language": "en-US,en;q=0.9",
}

NUM = re.compile(r"-?\d+(?:\.\d+)?")

ROLE_MAP = {
    "fighter": "fighter",
    "attacker": "attacker",
    "bomber": "bomber",
    "helicopter": "helicopter",
    "tank": "tank",
    "bomber/attacker": "attacker",
    "utility helicopter": "helicopter",
    "attack helicopter": "helicopter",
}


# ------------------------------------------------------------------ HTTP


def fetch(url: str, retries: int = 3, timeout: int = 30) -> str | None:
    """取页面。404 直接返回 None，其余错误重试后放弃。"""
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers=HEADERS)
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.read().decode("utf-8", errors="replace")
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                return None
            time.sleep(1.0 * (attempt + 1))
        except Exception:
            time.sleep(1.0 * (attempt + 1))
    return None


# ------------------------------------------------------------------ 解析


def list_all_slugs() -> list[str]:
    """从 /aviation 页取全部载具 slug。"""
    html = fetch(f"{BASE}/aviation", timeout=60)
    if not html:
        raise RuntimeError("无法加载 /aviation 页")

    return sorted({m for m in re.findall(r'href="/unit/([^"#?]+)"', html)})


def extract_game_id(html: str) -> str | None:
    """
    取页面内嵌 JSON 里的 gameId。

    这是唯一能与游戏 indicators.type 对上号的字段，必须从这里拿，
    不能靠 slug 或标题猜。
    """
    m = re.search(
        r'<textarea id="game-unit-initial"[^>]*>(.*?)</textarea>', html, re.S)
    if not m:
        return None

    try:
        data = json.loads(unescape(m.group(1)))
    except json.JSONDecodeError:
        return None

    game_id = data.get("gameId")
    return game_id.strip() if isinstance(game_id, str) and game_id.strip() else None


def extract_display_name(html: str) -> str | None:
    m = re.search(r'class="game-unit_name">(.*?)</div>', html, re.S)
    if m:
        # 节点里可能夹着国旗等图标，剥掉标签只留文本。
        text = unescape(re.sub(r"<[^>]+>", "", m.group(1)))

        # 国旗有两种残留形态：Unicode 私用区字符，以及没渲染出来的占位方块
        # （如 U+2584 ▄）。统一在开头把「非字母数字」的杂符剃掉。
        text = "".join(c for c in text if not (0xE000 <= ord(c) <= 0xF8FF))
        text = text.lstrip("▄▀█▓▒░■□▪▫*※·•").strip()

        if text:
            return text

    m = re.search(r"<title>([^<|]+)", html)
    return unescape(m.group(1)).strip() if m else None


def extract_chars(html: str) -> dict[str, str]:
    """
    抽出 header → value 配对表。

    wiki 同一行有三种形态，必须都覆盖：
    1. 单值：   <header>X</header><span class="value"> 13,000 m </span>
    2. 多档：   <header>X</header><span class="value"><span class="show-char-rb-mod-ref">21</span>... s</span>
    3. info+值：<header>X</header><span class="info">L / T / C</span>
                <span class="value">290 / 451 / 490 km/h</span>  ← info 与 value 是同级兄弟

    所以不能写「header 紧跟 value」的简单正则，要按行块切分后整块取文本。
    """
    pairs: dict[str, str] = {}

    blocks = re.findall(
        r'<div class="game-unit_chars-line">(.*?)</div>\s*'
        r'(?=<div class="game-unit_chars-line">|</div>)',
        html,
        re.S,
    )

    for block in blocks:
        # header 有时纯文本，有时套了一层 tooltip span（如 G limit），故连标签一起抓。
        header = re.search(
            r'class="game-unit_chars-header"[^>]*>(.*?)</span>', block, re.S)
        if not header:
            continue

        header_text = re.sub(r"<[^>]+>", "", header.group(1)).strip()
        if not header_text:
            continue

        values = re.findall(
            r'class="game-unit_chars-value"[^>]*>(.*?)</span>', block, re.S)
        if not values:
            continue

        text = " ".join(re.sub(r"<[^>]+>", " ", v) for v in values)
        text = re.sub(r"\s+", " ", text).strip()

        pairs.setdefault(header_text, text)

    return pairs


def first_number(text: str) -> float | None:
    if not text:
        return None
    m = NUM.search(text.replace(",", ""))
    if not m:
        return None
    try:
        return float(m.group())
    except ValueError:
        return None


def parse_g_limit(text: str) -> tuple[float | None, float | None]:
    """解析 G limit，格式 '≈ -8/13 G' → (正向 13, 负向绝对值 8)。"""
    if not text:
        return None, None
    m = re.search(r"(-?\d+(?:\.\d+)?)\s*/\s*(-?\d+(?:\.\d+)?)", text)
    if m:
        return abs(float(m.group(2))), abs(float(m.group(1)))
    return None, None


def parse_flap(text: str) -> float | None:
    """襟翼限速 '290 / 451 / 490 km/h'（L/T/C 三档），取战斗档（最后一个）。"""
    if not text:
        return None
    nums = NUM.findall(text.replace(",", ""))
    if not nums:
        return None
    try:
        return float(nums[-1])
    except ValueError:
        return None


def extract_collection(html: str, kind: str) -> tuple[str, str] | None:
    """
    取 Collections 区块里某一类别的 (slug 尾段, 显示名)。

    标记结构：
        <a href="/collections/game_roles/assault" class="game-unit_collection">
            <div class="sub-cat">Game roles</div>
            <div class="name">Assault</div>
        </a>
    """
    pattern = re.compile(
        r'<a href="/collections/' + kind + r'/([\w-]+)"[^>]*>(.*?)</a>',
        re.S,
    )
    m = pattern.search(html)
    if not m:
        return None

    name = re.search(r'class="name">([^<]*)<', m.group(2))
    return m.group(1), (name.group(1).strip() if name else "")


def extract_role(html: str) -> str:
    """从 Collections 的 Game roles 读主定位。"""
    hit = extract_collection(html, "game_roles")
    raw = (hit[1] if hit else "").strip().lower()
    return ROLE_MAP.get(raw, "fighter")


def extract_nation(html: str) -> str | None:
    """从 Collections 的 Operator 读所属国家。"""
    hit = extract_collection(html, "operator")
    if not hit:
        return None

    slug, name = hit
    key = slug.removeprefix("country_").lower()
    return {
        "ussr": "USSR", "usa": "USA", "germany": "Germany",
        "britain": "Britain", "japan": "Japan", "china": "China",
        "italy": "Italy", "france": "France", "sweden": "Sweden",
        "israel": "Israel",
    }.get(key, name or None)


def extract_army(html: str) -> str:
    """
    判兵种。

    wiki 页首有一段面包屑/分类文本，航空器标 Aviation，
    地面标 Ground vehicles，直升机归在 Aviation 下。
    """
    head = html[:6000]
    if "Ground vehicles" in head:
        return "tank"

    return "air"


def build_entry(html: str, slug: str) -> dict | None:
    chars = extract_chars(html)
    if not chars:
        return None

    pos, neg = parse_g_limit(chars.get("G limit", ""))

    return {
        "displayName": extract_display_name(html) or slug,
        "army": extract_army(html),
        "role": extract_role(html),
        "nation": extract_nation(html),
        "maxSpeedIas": first_number(chars.get("Max Speed Limit (IAS)", "")),
        "machLimit": first_number(chars.get("Mach Number Limit", "")),
        "gLimitPositive": pos,
        "gLimitNegative": neg,
        "stallSpeedIas": None,
        "gearSpeedLimit": first_number(chars.get("Gear Speed Limit (IAS)", "")),
        "flapSpeedLimit": parse_flap(chars.get("Flap Speed Limit (IAS)", "")),
        "rateOfClimb": first_number(chars.get("Rate of Climb", "")),
        "turnTime": first_number(chars.get("Turn time", "")),
        "maxAltitude": first_number(chars.get("Max altitude", "")),
        "wikiSlug": slug,
        "source": "wiki-web",
    }


# ------------------------------------------------------------------ 主流程


def cmd_index(out: Path) -> int:
    slugs = list_all_slugs()
    out.write_text(json.dumps(slugs, indent=2), encoding="utf-8")
    print(f"共 {len(slugs)} 个 slug → {out}", file=sys.stderr)
    return 0


def cmd_scrape(out: Path, state: Path, delay: float, limit: int) -> int:
    """全量抓取，支持断点续抓。"""
    slug_data: dict[str, dict] = {}
    if state.exists():
        slug_data = json.loads(state.read_text(encoding="utf-8"))
        print(f"从断点恢复：已有 {len(slug_data)} 条", file=sys.stderr)

    slugs = list_all_slugs()
    if limit:
        slugs = slugs[:limit]

    todo = [s for s in slugs if s not in slug_data]
    print(f"共 {len(slugs)} 个 slug，待抓 {len(todo)} 个", file=sys.stderr)

    for i, slug in enumerate(todo, 1):
        html = fetch(f"{BASE}/unit/{slug}")
        if html is None:
            slug_data[slug] = {"_error": "404"}
            continue

        game_id = extract_game_id(html)
        entry = build_entry(html, slug)
        if entry is None or game_id is None:
            slug_data[slug] = {"_error": "no-gameId-or-no-chars"}
            continue

        entry["gameId"] = game_id
        slug_data[slug] = entry

        if i % 25 == 0:
            state.write_text(
                json.dumps(slug_data, ensure_ascii=False, indent=2),
                encoding="utf-8")
            print(f"  [{i}/{len(todo)}] 已存盘", file=sys.stderr)

        time.sleep(delay)

    state.write_text(
        json.dumps(slug_data, ensure_ascii=False, indent=2), encoding="utf-8")

    # 按 gameId 展平：游戏接口给的是 gameId，不是 slug
    vehicles: dict[str, dict] = {}
    for entry in slug_data.values():
        if "_error" in entry:
            continue
        record = dict(entry)
        game_id = record.pop("gameId", None)
        if game_id:
            vehicles[game_id] = record

    out.write_text(
        json.dumps(vehicles, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"\n完成：{len(vehicles)} 架载具 → {out}", file=sys.stderr)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--index-only", action="store_true", help="只输出 slug 列表")
    ap.add_argument("--out", default="VehicleData.json")
    ap.add_argument("--state", default="scrape-state.json", help="断点续抓状态")
    ap.add_argument("--delay", type=float, default=0.4, help="每页间隔秒数")
    ap.add_argument("--limit", type=int, default=0, help="只抓前 N 个（调试）")
    args = ap.parse_args()

    if args.index_only:
        return cmd_index(Path(args.out))

    return cmd_scrape(Path(args.out), Path(args.state), args.delay, args.limit)


if __name__ == "__main__":
    raise SystemExit(main())
