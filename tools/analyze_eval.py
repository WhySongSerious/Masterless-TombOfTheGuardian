"""평가 분석(통계·그래프): 조건(A 고정 · B 규칙 · C PPO 디렉터 …) × 봇 3단계 결과를 신뢰구간과 가설 검정으로 정리한다.

  python analyze_eval.py --cell "A=<폴더>" --cell "B=<폴더>" --cell "C_v1=<폴더>" --figs <그림 폴더> --out <요약.md>

각 폴더 아래에 <아무 이름>_<novice|intermediate|expert>/ 세션 폴더(attempts.csv, events.jsonl)가 있다고 본다
(evaluate.py 출력과 같은 구조. 폴더 이름 끝이 단계 이름이면 된다).

지표(판 단위): 승리 여부, 끝 보스·플레이어 체력, 전투 시간, 점수. 타격 단위: 압박 배율, 패턴, 결과.
신뢰구간: 판 단위 부트스트랩 10,000회 95%.
H1 도달: 단계 간 격차(숙련 − 초보)의 끝 보스 체력·승률이 디렉터 조건에서 A보다 작은가 → 격차 차이의 부트스트랩 구간과 단측 p.
H2 점수 타당성: 각 조건에서 점수 중앙값 숙련 > 중급 > 초보, 인접 단계의 Mann-Whitney U(단측), 10~90% 구간 겹침.
H3 공정성: 패턴별 피격률이 조건 간에 같은가(패턴마다 A 대비 카이제곱/피셔 검정, 표본 20타 이상만).
"""
import argparse
import json
import os

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from scipy import stats

SKILLS = ["novice", "intermediate", "expert"]
SKILL_KO = {"novice": "초보", "intermediate": "중급", "expert": "숙련"}
RNG = np.random.default_rng(20261117)
plt.rcParams["font.family"] = ["Malgun Gothic", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False


def load_cell(name, root):
    attempts, strikes, patterns = [], [], []
    for entry in sorted(os.listdir(root)):
        path = os.path.join(root, entry)
        skill = next((s for s in SKILLS if entry.endswith(s)), None)
        if skill is None or not os.path.isdir(path):
            continue
        for dirpath, _, files in os.walk(path):
            if "attempts.csv" in files:
                a = pd.read_csv(os.path.join(dirpath, "attempts.csv"), encoding="utf-8-sig")
                a = a[a["outcome"].isin(["victory", "defeat"])].copy()
                a["session"] = dirpath
                a["skill"] = skill
                attempts.append(a)
            if "events.jsonl" in files:
                with open(os.path.join(dirpath, "events.jsonl"), encoding="utf-8-sig") as f:
                    for line in f:
                        e = json.loads(line)
                        if e["type"] == "boss_strike":
                            strikes.append({"skill": skill, "session": dirpath, "attempt": e["attempt"], "pattern": e["patternId"],
                                            "hit": e["outcome"] == "hit", "pressure": e.get("scorePressure", 1.0)})
                        elif e["type"] == "boss_pattern":
                            patterns.append({"skill": skill, "session": dirpath, "attempt": e["attempt"], "pattern": e["patternId"]})
    a = pd.concat(attempts, ignore_index=True) if attempts else pd.DataFrame()
    a["cond"] = name
    a["win"] = (a["outcome"] == "victory").astype(float)
    s = pd.DataFrame(strikes); s["cond"] = name
    p = pd.DataFrame(patterns); p["cond"] = name
    return a, s, p


def boot(values, fn=np.mean, n=10000):
    values = np.asarray(values, dtype=float)
    if len(values) == 0:
        return np.nan, np.nan, np.nan
    idx = RNG.integers(0, len(values), size=(n, len(values)))
    samples = fn(values[idx], axis=1)
    return fn(values), np.percentile(samples, 2.5), np.percentile(samples, 97.5)


def gap_boot(df, metric, n=10000):
    hi = df[df.skill == "expert"][metric].to_numpy(float)
    lo = df[df.skill == "novice"][metric].to_numpy(float)
    gi = RNG.integers(0, len(hi), size=(n, len(hi)))
    li = RNG.integers(0, len(lo), size=(n, len(lo)))
    return hi[gi].mean(1) - lo[li].mean(1)


def repeat_rates(p):
    rows = []
    for (cond, skill), g in p.groupby(["cond", "skill"]):
        two = three = total = 0
        for _, seq in g.groupby(["session", "attempt"]):
            ids = seq["pattern"].tolist()
            for i in range(1, len(ids)):
                total += 1
                two += ids[i] == ids[i - 1]
                three += i >= 2 and ids[i] == ids[i - 1] == ids[i - 2]
        rows.append({"cond": cond, "skill": skill, "repeat2": two / max(1, total), "repeat3": three / max(1, total)})
    return pd.DataFrame(rows)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cell", action="append", required=True, help="이름=폴더")
    ap.add_argument("--figs", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    os.makedirs(a.figs, exist_ok=True)

    A, S, P = [], [], []
    for cell in a.cell:
        name, root = cell.split("=", 1)
        at, st, pt = load_cell(name, root)
        A.append(at); S.append(st); P.append(pt)
    att, stk, pat = pd.concat(A, ignore_index=True), pd.concat(S, ignore_index=True), pd.concat(P, ignore_index=True)
    conds = list(dict.fromkeys(att["cond"]))
    base = conds[0]
    md = ["# 평가 결과", "", f"조건: {', '.join(conds)} (기준 {base}). 판 단위 부트스트랩 95% 신뢰구간.", ""]

    # 1) 판 단위 지표 표
    md += ["| 조건 | 봇 | 판 | 승률 | 끝 보스 체력 | 끝 플레이어 체력 | 전투 시간(초) | 점수 중앙값 | 점수 10~90% |",
           "|---|---|---|---|---|---|---|---|---|"]
    for cond in conds:
        for skill in SKILLS:
            d = att[(att.cond == cond) & (att.skill == skill)]
            if d.empty:
                continue
            w = boot(d["win"]); b = boot(d["boss_health_ratio"]); pl = boot(d["player_health_ratio"]); t = boot(d["duration_s"])
            sc = d["score_total"].dropna()
            med = boot(sc, np.median)
            md.append(f"| {cond} | {SKILL_KO[skill]} | {len(d)} | {w[0]:.0%} [{w[1]:.0%}, {w[2]:.0%}] | {b[0]:.2f} [{b[1]:.2f}, {b[2]:.2f}] | "
                      f"{pl[0]:.2f} [{pl[1]:.2f}, {pl[2]:.2f}] | {t[0]:.0f} [{t[1]:.0f}, {t[2]:.0f}] | {med[0]:,.0f} [{med[1]:,.0f}, {med[2]:,.0f}] | "
                      f"{sc.quantile(0.1):,.0f}~{sc.quantile(0.9):,.0f} |")

    # 2) H1: 격차(숙련 − 초보)
    md += ["", "## H1 도달: 실력 단계 간 격차(숙련 − 초보)", "",
           "| 조건 | 끝 보스 체력 격차 | 승률 격차 | A보다 끝 보스 체력 격차가 작을 확률(부트스트랩) |", "|---|---|---|---|"]
    base_gap = gap_boot(att[att.cond == base], "boss_health_ratio")
    for cond in conds:
        d = att[att.cond == cond]
        g = gap_boot(d, "boss_health_ratio"); gw = gap_boot(d, "win")
        # 격차는 음수(숙련이 보스를 더 깎음). 절댓값이 작을수록 격차가 작다.
        smaller = np.mean(np.abs(g) < np.abs(base_gap)) if cond != base else np.nan
        md.append(f"| {cond} | {g.mean():+.2f} [{np.percentile(g, 2.5):+.2f}, {np.percentile(g, 97.5):+.2f}] | "
                  f"{gw.mean():+.0%} [{np.percentile(gw, 2.5):+.0%}, {np.percentile(gw, 97.5):+.0%}] | "
                  f"{'' if cond == base else f'{smaller:.0%}'} |")

    # 3) H2: 점수 순서
    md += ["", "## H2 점수 타당성", "", "| 조건 | 중앙값 초보 < 중급 < 숙련 | 초보<중급 p | 중급<숙련 p | 10~90% 구간 겹침 |", "|---|---|---|---|---|"]
    for cond in conds:
        d = att[att.cond == cond]
        sc = {s: d[d.skill == s]["score_total"].dropna() for s in SKILLS}
        order = sc["novice"].median() < sc["intermediate"].median() < sc["expert"].median()
        p1 = stats.mannwhitneyu(sc["intermediate"], sc["novice"], alternative="greater").pvalue
        p2 = stats.mannwhitneyu(sc["expert"], sc["intermediate"], alternative="greater").pvalue
        overlap = not (sc["novice"].quantile(0.9) < sc["intermediate"].quantile(0.1) and sc["intermediate"].quantile(0.9) < sc["expert"].quantile(0.1))
        md.append(f"| {cond} | {'예' if order else '아니요'} | {p1:.3g} | {p2:.3g} | {'있음' if overlap else '없음'} |")

    # 4) 압박 배율·패턴 비중·반복
    md += ["", "## 디렉터 행동", "", "| 조건 | 봇 | 압박 배율 평균 | 0.7(가장 느슨) 비율 | 1.3 이상 비율 | 2연속 | 3연속 | 많이 고른 패턴 |", "|---|---|---|---|---|---|---|---|"]
    rep = repeat_rates(pat)
    for cond in conds:
        for skill in SKILLS:
            s = stk[(stk.cond == cond) & (stk.skill == skill)]
            pp = pat[(pat.cond == cond) & (pat.skill == skill)]
            if s.empty:
                continue
            r = rep[(rep.cond == cond) & (rep.skill == skill)].iloc[0]
            share = pp["pattern"].value_counts(normalize=True).head(4)
            md.append(f"| {cond} | {SKILL_KO[skill]} | {s.pressure.mean():.2f} | {(s.pressure <= 0.71).mean():.0%} | {(s.pressure >= 1.3).mean():.0%} | "
                      f"{r.repeat2:.0%} | {r.repeat3:.0%} | {', '.join(f'{k}:{v:.0%}' for k, v in share.items())} |")

    # 5) H3: 패턴별 피격률
    md += ["", f"## H3 공정성: 패턴별 피격률({base} 대비, 20타 이상인 패턴만, 피셔 정확 검정)", "", "| 패턴 | " + " | ".join(conds) + " | 다른 조건 중 p<0.05 |", "|---|" + "---|" * len(conds) + "---|"]
    for pid in sorted(stk.pattern.unique()):
        cells, flags = [], []
        b = stk[(stk.cond == base) & (stk.pattern == pid)]
        for cond in conds:
            c = stk[(stk.cond == cond) & (stk.pattern == pid)]
            cells.append(f"{c.hit.mean():.2f} ({len(c)})" if len(c) else "-")
            if cond != base and len(c) >= 20 and len(b) >= 20:
                table = [[c.hit.sum(), len(c) - c.hit.sum()], [b.hit.sum(), len(b) - b.hit.sum()]]
                if stats.fisher_exact(table).pvalue < 0.05:
                    flags.append(cond)
        md.append(f"| {pid} | " + " | ".join(cells) + f" | {', '.join(flags) or '-'} |")
    md += ["", "※ 봇이 상대이고 판마다 상황(거리·페이즈)이 달라 피격률 차이가 곧 판정 변화를 뜻하지는 않는다. 전조·판정·피해 코드는 조건 간에 같다."]

    # 그림
    fig, axes = plt.subplots(1, 2, figsize=(11, 4))
    width = 0.8 / len(conds)
    for i, cond in enumerate(conds):
        for j, (metric, ax) in enumerate([("boss_health_ratio", axes[0]), ("win", axes[1])]):
            means, lo, hi = [], [], []
            for skill in SKILLS:
                m, l, h = boot(att[(att.cond == cond) & (att.skill == skill)][metric])
                means.append(m); lo.append(m - l); hi.append(h - m)
            ax.bar(np.arange(3) + i * width, means, width, yerr=[lo, hi], capsize=3, label=cond)
    for ax, title in zip(axes, ["끝 보스 체력(낮을수록 플레이어가 더 깎음)", "승률"]):
        ax.set_xticks(np.arange(3) + width * (len(conds) - 1) / 2)
        ax.set_xticklabels([SKILL_KO[s] for s in SKILLS]); ax.set_title(title); ax.legend()
    fig.tight_layout(); fig.savefig(os.path.join(a.figs, "h1_outcomes.png"), dpi=150); plt.close(fig)

    fig, ax = plt.subplots(figsize=(8, 4))
    data = [att[(att.cond == c) & (att.skill == s)]["score_total"].dropna() for c in conds for s in SKILLS]
    labels = [f"{c}\n{SKILL_KO[s]}" for c in conds for s in SKILLS]
    ax.boxplot(data, tick_labels=labels, whis=(10, 90), showfliers=False)
    ax.set_title("점수 분포(상자 = 25~75%, 수염 = 10~90%)")
    fig.tight_layout(); fig.savefig(os.path.join(a.figs, "h2_scores.png"), dpi=150); plt.close(fig)

    fig, ax = plt.subplots(figsize=(8, 4))
    bins = np.linspace(0.65, 1.55, 19)
    for cond in conds:
        s = stk[stk.cond == cond]
        if s.pressure.nunique() > 1:
            ax.hist(s.pressure, bins=bins, alpha=0.5, density=True, label=cond)
    ax.set_title("압박 배율 분포(보스 타격마다)"); ax.legend()
    fig.tight_layout(); fig.savefig(os.path.join(a.figs, "pressure.png"), dpi=150); plt.close(fig)

    with open(a.out, "w", encoding="utf-8") as f:
        f.write("\n".join(md) + "\n")
    print("\n".join(md))


if __name__ == "__main__":
    main()
