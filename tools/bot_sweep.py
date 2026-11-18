"""규칙 봇 보정: 학습 실행 파일을 여러 설정으로 동시에 돌려 실력 단계별 승률·끝 체력을 잰다.

  python bot_sweep.py --exe ../../_ml/build/BossDirector.exe --out ../../_ml/sweep \
      --episodes 15 --skills expert --set "dodgeLead=0.04" --set "dodgeLead=0.1;parryLead=0.0"

각 --set이 실행 하나(세미콜론으로 여러 인자). 조건은 fixed(디렉터 없음, 비교 조건 A와 같은 보스).
"""
import argparse
import csv
import os
import subprocess
import sys
from collections import defaultdict


def run_all(a):
    procs = []
    for i, setting in enumerate(a.set or [""]):
        for skill in a.skills.split(","):
            label = setting.replace("=", "").replace(";", "_").replace(",", "-") or "default"
            name = f"{i:02d}_{skill}_{label}"
            root = os.path.abspath(os.path.join(a.out, name))
            os.makedirs(root, exist_ok=True)
            args = [os.path.abspath(a.exe), "-batchmode", "-nographics", "-condition", a.condition, "-episodes", str(a.episodes),
                    "-botSkill", skill, "-telemetryRoot", root, "-logFile", os.path.join(root, "player.log")]
            for kv in filter(None, setting.split(";")):
                k, v = kv.split("=", 1)
                args += ["-" + k, v]
            procs.append((name, root, subprocess.Popen(args)))
    for _, _, p in procs:
        p.wait()
    return procs


def summarize(name, root):
    rows = []
    for dirpath, _, files in os.walk(root):
        if "attempts.csv" in files:
            with open(os.path.join(dirpath, "attempts.csv"), encoding="utf-8-sig") as f:
                rows += [r for r in csv.DictReader(f) if r["outcome"] in ("victory", "defeat")]
    if not rows:
        return f"{name}: 기록 없음"
    n = len(rows)
    wins = sum(r["outcome"] == "victory" for r in rows)
    boss = sum(float(r["boss_health_ratio"]) for r in rows) / n
    player = sum(float(r["player_health_ratio"]) for r in rows) / n
    strikes = sum(int(r["boss_strikes"]) for r in rows)
    hit = sum(int(r["boss_strikes_hit"]) for r in rows)
    ev = sum(int(r["boss_strikes_evaded"]) for r in rows)
    par = sum(int(r["boss_strikes_parried"]) for r in rows)
    dur = sum(float(r["duration_s"]) for r in rows) / n
    return (f"{name}: {n}판 승률 {wins / n:.0%} · 끝 보스 체력 {boss:.2f} · 끝 플레이어 체력 {player:.2f} · "
            f"피격률 {hit / max(1, strikes):.2f} 회피 {ev / max(1, strikes):.2f} 패링 {par / max(1, strikes):.2f} · 평균 {dur:.0f}초")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--episodes", type=int, default=15)
    ap.add_argument("--skills", default="novice,intermediate,expert")
    ap.add_argument("--condition", default="fixed")
    ap.add_argument("--set", action="append")
    a = ap.parse_args()
    for name, root, _ in run_all(a):
        print(summarize(name, root))


if __name__ == "__main__":
    sys.exit(main())
