"""비교 평가: 고정 보스(조건 A) vs 보스 디렉터(조건 C)를 규칙 봇 3단계로 돌려 가설을 확인한다.

  python evaluate.py --exe ../../_ml/build_eval/BossDirector.exe --out ../../_ml/eval/director-v1 --episodes 30
  python evaluate.py --analyze-only --out ../../_ml/eval/director-v1

실행 파일은 모델을 넣어 빌드한 것(-mlModel)이어야 조건 rl이 추론으로 돈다. 6개(조건 2 × 단계 3)를 동시에 돌린다.
보는 것
  H1 도달: 단계 간 결과 격차(승률·끝 보스 체력의 최대-최소)가 디렉터(C)에서 고정(A)보다 작은가
  H2 점수 타당성: 디렉터에서도 점수 순서가 숙련 > 중급 > 초보이고 구간(10~90%)이 겹치지 않는가
  기타: 디렉터가 만든 압박 배율 분포(보스 타격마다 기록), 같은 패턴 연속(2·3연속) 비율
"""
import argparse
import csv
import json
import os
import statistics
import subprocess
from collections import Counter, defaultdict

SKILLS = ["novice", "intermediate", "expert"]
CONDITIONS = ["fixed", "rl"]


def run(a):
    procs = []
    for cond in CONDITIONS:
        for skill in SKILLS:
            root = os.path.abspath(os.path.join(a.out, f"{cond}_{skill}"))
            os.makedirs(root, exist_ok=True)
            args = [os.path.abspath(a.exe), "-batchmode", "-nographics", "-condition", cond, "-episodes", str(a.episodes),
                    "-botSkill", skill, "-seed", str(a.seed), "-telemetryRoot", root, "-logFile", os.path.join(root, "player.log")]
            procs.append(subprocess.Popen(args))
    for p in procs:
        p.wait()


def load(root):
    attempts, events = [], []
    for dirpath, _, files in os.walk(root):
        if "attempts.csv" in files:
            with open(os.path.join(dirpath, "attempts.csv"), encoding="utf-8-sig") as f:
                attempts += [r for r in csv.DictReader(f) if r["outcome"] in ("victory", "defeat")]
        if "events.jsonl" in files:
            with open(os.path.join(dirpath, "events.jsonl"), encoding="utf-8-sig") as f:
                events += [json.loads(l) for l in f]
    return attempts, events


def pct(values, q):
    values = sorted(values)
    if not values:
        return float("nan")
    k = (len(values) - 1) * q
    lo, hi = int(k), min(int(k) + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def analyze(out):
    lines = []
    table = defaultdict(dict)
    pressure = defaultdict(list)
    repeats = {}
    for cond in CONDITIONS:
        for skill in SKILLS:
            attempts, events = load(os.path.join(out, f"{cond}_{skill}"))
            if not attempts:
                continue
            n = len(attempts)
            scores = [int(r["score_total"]) for r in attempts if r.get("score_total")]
            table[cond][skill] = {
                "n": n,
                "win": sum(r["outcome"] == "victory" for r in attempts) / n,
                "boss": statistics.mean(float(r["boss_health_ratio"]) for r in attempts),
                "player": statistics.mean(float(r["player_health_ratio"]) for r in attempts),
                "dur": statistics.mean(float(r["duration_s"]) for r in attempts),
                "score": statistics.median(scores) if scores else float("nan"),
                "p10": pct(scores, 0.1), "p90": pct(scores, 0.9),
            }
            pressure[cond] += [e["scorePressure"] for e in events if e["type"] == "boss_strike" and "scorePressure" in e]
            seq = defaultdict(list)
            for e in events:
                if e["type"] == "boss_pattern":
                    seq[e["attempt"]].append(e["patternId"])
            two = three = total = 0
            for ids in seq.values():
                for i in range(1, len(ids)):
                    total += 1
                    two += ids[i] == ids[i - 1]
                    three += i >= 2 and ids[i] == ids[i - 1] == ids[i - 2]
            repeats[(cond, skill)] = (two / max(1, total), three / max(1, total), total)

    lines.append("| 조건 | 봇 | 판 | 승률 | 끝 보스 체력 | 끝 플레이어 체력 | 평균 길이(초) | 점수 중앙값 | 점수 10~90% | 2연속·3연속 패턴 |")
    lines.append("|---|---|---|---|---|---|---|---|---|---|")
    for cond in CONDITIONS:
        for skill in SKILLS:
            t = table[cond].get(skill)
            if not t:
                continue
            r2, r3, _ = repeats[(cond, skill)]
            lines.append(f"| {cond} | {skill} | {t['n']} | {t['win']:.0%} | {t['boss']:.2f} | {t['player']:.2f} | {t['dur']:.0f} | "
                         f"{t['score']:,.0f} | {t['p10']:,.0f}~{t['p90']:,.0f} | {r2:.0%} · {r3:.0%} |")

    lines.append("")
    for cond in CONDITIONS:
        t = table[cond]
        if len(t) < 3:
            continue
        win_gap = max(v["win"] for v in t.values()) - min(v["win"] for v in t.values())
        boss_gap = max(v["boss"] for v in t.values()) - min(v["boss"] for v in t.values())
        order = t["expert"]["score"] > t["intermediate"]["score"] > t["novice"]["score"]
        overlap = not (t["novice"]["p90"] < t["intermediate"]["p10"] and t["intermediate"]["p90"] < t["expert"]["p10"])
        lines.append(f"- {cond}: H1 격차 — 승률 {win_gap:.0%}p, 끝 보스 체력 {boss_gap:.2f} / "
                     f"H2 — 점수 순서 숙련>중급>초보 {'예' if order else '아니요'}, 10~90% 구간 겹침 {'있음' if overlap else '없음'}")
    for cond in CONDITIONS:
        p = pressure[cond]
        if p:
            c = Counter(round(x, 2) for x in p)
            top = ", ".join(f"{k:.2f}:{v / len(p):.0%}" for k, v in sorted(c.items()))
            lines.append(f"- {cond} 압박 배율(보스 타격 {len(p)}개): 평균 {statistics.mean(p):.2f} · 분포 {top}")
    text = "\n".join(lines)
    print(text)
    with open(os.path.join(out, "summary.md"), "w", encoding="utf-8") as f:
        f.write(text + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe")
    ap.add_argument("--out", required=True)
    ap.add_argument("--episodes", type=int, default=30)
    ap.add_argument("--seed", type=int, default=50000)
    ap.add_argument("--analyze-only", action="store_true")
    a = ap.parse_args()
    if not a.analyze_only:
        run(a)
    analyze(a.out)


if __name__ == "__main__":
    main()
