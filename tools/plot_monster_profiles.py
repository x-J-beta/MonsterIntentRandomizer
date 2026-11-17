"""Plot saved Monster Intent Randomizer profiles outside the game."""

import argparse
import json
import math
import os
import random
from dataclasses import dataclass
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager
from matplotlib.patches import Patch
from matplotlib.ticker import MaxNLocator


ROOT = Path(__file__).resolve().parents[1]
COLORS = {"普通": "#367A95", "精英": "#A26822", "首领": "#A7465C",
          "事件": "#6B628E", "未分类": "#6D7783"}
BACKGROUND = "#F4F6F8"
INK = "#182B3A"
MUTED = "#466071"


@dataclass(frozen=True)
class Profile:
    id: str
    phase: str | None
    name: str
    kind: str
    action: float
    hp: float
    hits: float
    enabled: bool


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def default_profiles():
    appdata = os.environ.get("APPDATA")
    if appdata:
        saved = Path(appdata) / "SlayTheSpire2/mod_data/MonsterIntentRandomizer/config.json"
        if saved.is_file():
            return saved
    return ROOT / "data/monster-profiles.json"


def load_profiles(path, kinds):
    document = read_json(path)
    document = document.get("Profiles", document)
    kind_by_id = {monster_id: label for label, ids in kinds.items() for monster_id in ids}
    rows = []
    for monster_id, monster in document["monsters"].items():
        phases = monster.get("phases")
        for phase_id, phase in (phases or {None: {}}).items():
            name = monster.get("name", monster_id)
            if phase_id is not None:
                name += " · " + phase.get("name", phase_id)
            rows.append(Profile(
                monster_id, phase_id, name, kind_by_id.get(monster_id, "未分类"),
                phase.get("actionValue", monster["actionValue"]),
                phase.get("hp", monster["hp"]),
                phase.get("attackFrequency", monster["attackFrequency"]),
                monster.get("enabled", True) and phase.get("enabled", True),
            ))
    return rows


def configure_font(path):
    if path is None and (windows := os.environ.get("WINDIR")):
        candidate = Path(windows) / "Fonts/msyh.ttc"
        if candidate.is_file():
            path = candidate
    if path:
        font_manager.fontManager.addfont(path)
        family = font_manager.FontProperties(fname=path).get_name()
    else:
        families = ("Noto Sans CJK SC", "PingFang SC", "Microsoft YaHei",
                    "WenQuanYi Zen Hei", "SimHei")
        installed = {font.name for font in font_manager.fontManager.ttflist}
        family = next((font for font in families if font in installed), None)
        if family is None:
            raise SystemExit("找不到简体中文字体。请通过 --font 指定字体文件。")
    plt.rcParams.update({"font.family": family, "font.size": 10, "text.color": INK,
                         "axes.labelcolor": INK, "xtick.color": MUTED, "ytick.color": INK,
                         "axes.unicode_minus": False})


def style_axis(ax):
    ax.set_facecolor("white")
    for spine in ax.spines.values():
        spine.set_visible(False)
    ax.tick_params(length=0)
    ax.set_axisbelow(True)
    ax.grid(axis="x", color="#DCE4EA", linewidth=0.8)


def draw_overview(rows, kinds, maps, upper, output):
    fig, axes = plt.subplots(1, 2, figsize=(13, 4.4), facecolor=BACKGROUND)
    fig.subplots_adjust(left=0.13, right=0.975, top=0.76, bottom=0.22, wspace=0.44)
    fig.text(0.055, 0.91, "怪物画像行动力分布", fontsize=22, weight="bold")
    fig.text(0.055, 0.835, "圆点代表一个画像或一个阶段，竖线是组内中位数。", color=MUTED)

    rng = random.Random(42)
    for ax, groups, title in zip(axes, (kinds, maps), ("按怪物种类", "按出现地图")):
        style_axis(ax)
        ax.set_title(title, loc="left", pad=8, weight="bold")
        labels = []
        for y, (label, group) in enumerate(groups.items()):
            labels.append(f"{label}  ·  {len(group)}")
            box = ax.boxplot([row.action for row in group], positions=[y], vert=False,
                             widths=0.42, patch_artist=True, showfliers=False,
                             boxprops={"facecolor": "#E5ECF1", "edgecolor": "#9AAEBC"},
                             medianprops={"color": INK, "linewidth": 1.4},
                             whiskerprops={"color": "#9AAEBC"},
                             capprops={"color": "#9AAEBC"})
            box["medians"][0].set_zorder(4)
            for row in group:
                jit_y = y + rng.uniform(-0.16, 0.16)
                ax.scatter(row.action, jit_y, s=14, facecolor=COLORS[row.kind] if row.enabled else "white",
                           edgecolors=COLORS[row.kind], alpha=0.8, linewidths=0.6, zorder=3)
        ax.set_yticks(range(len(labels)), labels)
        ax.set_ylim(len(labels) - 0.5, -0.5)
        ax.set_xlim(0, upper)
        ax.set_xlabel("基础行动力")
        ax.xaxis.set_major_locator(MaxNLocator(nbins=7))

    fig.text(0.055, 0.07, f"{len({row.id for row in rows})} 种怪物 · {len(rows)} 个画像条目"
             "    跨地图条目会重复出现；空心点为关闭的画像。", color=MUTED)
    fig.savefig(output, dpi=175, facecolor=fig.get_facecolor())
    plt.close(fig)


def draw_details(maps, upper, output):
    grouped = list(maps.items())
    heights = [max(len(group) for _, group in grouped[i:i + 2]) + 3
               for i in range(0, len(grouped), 2)]
    fig, axes = plt.subplots(len(heights), 2, figsize=(16.8, sum(heights) * 0.25 + 2.7),
                             facecolor=BACKGROUND, squeeze=False, gridspec_kw={"height_ratios": heights})
    fig.subplots_adjust(left=0.14, right=0.985, top=0.91, bottom=0.055, hspace=0.22, wspace=0.48)
    fig.text(0.045, 0.968, "各怪物基础行动力", fontsize=24, weight="bold")
    fig.text(0.045, 0.941, "各地图内按基础行动力降序排列，HP 与目标攻击段数取对应画像。", color=MUTED)
    legend = [Patch(facecolor=color, label=kind) for kind, color in COLORS.items() if kind != "未分类"]
    legend.append(Patch(facecolor="white", edgecolor=MUTED, hatch="//", label="画像关闭"))
    fig.legend(handles=legend, loc="upper right", bbox_to_anchor=(0.985, 0.965), ncol=len(legend), frameon=False)

    for ax, (label, group) in zip(axes.flat, grouped):
        style_axis(ax)
        ordered = sorted(group, key=lambda row: (-row.action, row.name))
        ax.set_title(f"{label}   ·   {len(ordered)}", loc="left", pad=17, weight="bold")
        ax.set_xlim(0, upper * 1.38)
        ax.set_xticks(MaxNLocator(nbins=6).tick_values(0, upper))
        ax.set_yticks(range(len(ordered)), [row.name for row in ordered], fontsize=9)
        ax.set_ylim(len(ordered) - 0.47, -0.6)
        for x, text in ((1.16, "HP"), (1.30, "目标段数")):
            ax.text(upper * x, 1.012, text, transform=ax.get_xaxis_transform(),
                    ha="center", color=MUTED, fontsize=9)
        for y, row in enumerate(ordered):
            color = COLORS[row.kind]
            ax.barh(y, row.action, height=0.65, color=color if row.enabled else "white",
                    edgecolor=color, linewidth=0.7, hatch=None if row.enabled else "//")
            ax.text(row.action + upper * 0.02, y, f"{row.action:g}", va="center", fontsize=9)
            ax.text(upper * 1.16, y, f"{row.hp:g}", ha="center", va="center", color=MUTED, fontsize=9)
            ax.text(upper * 1.30, y, f"{row.hits:g}", ha="center", va="center", color=MUTED, fontsize=9)
        ax.set_xlabel("基础行动力")

    for ax in list(axes.flat)[len(grouped):]:
        ax.axis("off")
    fig.savefig(output, dpi=165, facecolor=fig.get_facecolor())
    plt.close(fig)


def draw_changes(rows, calibration, output):
    changes = []
    for row in rows:
        result = calibration["monsters"].get(row.id, {})
        if row.phase is not None:
            result = result.get("phases", {}).get(row.phase, {})
        before = result.get("before")
        if before is not None and abs(row.action - before) >= 0.005:
            changes.append((row, before, round(row.action - before, 2)))
    if not changes:
        return

    ordered = sorted(changes, key=lambda item: item[2])
    largest = ordered[:8] + ordered[max(8, len(ordered) - 8):]
    largest.sort(key=lambda item: item[2])
    fig, ax = plt.subplots(figsize=(11, 7.5), facecolor=BACKGROUND)
    fig.subplots_adjust(left=0.26, right=0.96, top=0.80, bottom=0.14)
    fig.text(0.07, 0.93, "基础行动力重算前后", fontsize=23, weight="bold")
    fig.text(0.07, 0.87, f"晚招逐回合除以 {calibration['lateActionDivisor']:g}   ·   "
             f"{len(changes)} 个画像发生变化   ·   展示两端差值", color=MUTED)

    style_axis(ax)
    ax.axvline(0, color="#8295A3", linewidth=1)
    ax.set_yticks(range(len(largest)), [row.name for row, _, _ in largest])
    ax.set_ylim(len(largest) - 0.5, -0.5)
    span = max(abs(change) for _, _, change in largest)
    ax.set_xlim(min(0, largest[0][2]) - 0.47 * span, max(0, largest[-1][2]) + 0.47 * span)
    for y, (row, before, change) in enumerate(largest):
        ax.barh(y, change, height=0.61, color="#367A95" if change < 0 else "#A26822")
        ax.text(change + math.copysign(0.04 * span, change), y, f"{before:g} → {row.action:g}",
                ha="left" if change > 0 else "right", va="center", fontsize=9)
    ax.set_xlabel("新行动力 − 原行动力")
    fig.savefig(output, dpi=175, facecolor=fig.get_facecolor())
    plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description="生成怪物画像的基础行动力对比图与逐怪图。")
    parser.add_argument("--profiles", type=Path, default=default_profiles(),
                        help="画像 JSON 或游戏保存的 config.json，默认优先使用游戏配置")
    parser.add_argument("--output", type=Path, default=ROOT / "dist/monster-profile-charts", help="图表输出目录")
    parser.add_argument("--calibration", type=Path, help="重算报告，指定后额外绘制前后对比")
    parser.add_argument("--font", type=Path, help="用于中文标注的字体文件路径")
    args = parser.parse_args()

    categories = read_json(ROOT / "data/monster-categories.json")
    rows = load_profiles(args.profiles, categories["kinds"])
    if not rows:
        parser.error("画像文件里没有怪物。")
    kinds = {kind: [row for row in rows if row.kind == kind] for kind in COLORS}
    maps = {label: [row for row in rows if row.id in ids] for label, ids in categories["maps"].items()}
    mapped = {row.id for group in maps.values() for row in group}
    maps["未分类"] = [row for row in rows if row.id not in mapped]
    kinds = {label: group for label, group in kinds.items() if group}
    maps = {label: group for label, group in maps.items() if group}
    upper = max(5, 5 * math.ceil(max(row.action for row in rows) * 1.05 / 5))

    configure_font(args.font)
    args.output.mkdir(parents=True, exist_ok=True)
    draw_overview(rows, kinds, maps, upper, args.output / "action-value-overview.png")
    draw_details(maps, upper, args.output / "action-value-by-monster.png")
    if args.calibration:
        draw_changes(rows, read_json(args.calibration), args.output / "action-value-changes.png")
    print(f"画像来源：{args.profiles}\n图表目录：{args.output}")


if __name__ == "__main__":
    main()
