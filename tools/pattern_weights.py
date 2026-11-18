"""패턴 가중치 추정(지도 학습): 전투 로그로 "이 공격에 맞을 확률"을 로지스틱 회귀로 배우고 패턴별 가중치를 낸다.

입력: CombatTelemetryRecorder 세션 폴더들(events.jsonl + attempts.csv, 스키마 4 이상).
      boss_strike 이벤트 한 줄 = 보스 타격 하나. 결과 hit → 1, evaded/parried/missed → 0.
특징: 패턴 ID(원-핫), 페이즈, 거리 구간(스키마 6부터, 없으면 '모름'), 실력 단계(참가자 ID: BOT_NOVICE 등, 사람은 HUMAN).
가중치: 패턴마다 "다른 특징은 데이터 그대로 두고 패턴만 바꿨을 때" 평균 맞을 확률(주변화)을 구해
        전체 평균으로 나눈 뒤 0.6~1.8로 자르고 표 평균을 1.0으로 맞춘다(ScorePatternWeights와 같은 규칙).
        L2 정규화가 표본이 적은 패턴을 평균 쪽으로 당긴다.

사용:
  python pattern_weights.py <세션폴더 또는 그 상위 폴더> [...] [--out weights_v1.csv] [--min-version 4]
결과: 화면에 표(v0 비교 포함), --out에 CSV. ScorePatternWeights.cs는 고치지 않는다(사람이 보고 옮긴다).
"""
import argparse
import csv
import json
import os
import sys
from collections import Counter, defaultdict

import numpy as np
from sklearn.linear_model import LogisticRegression

# 게임의 패턴 표 순서(페이즈 전환 300 포함, ScorePatternWeights 표와 같은 ID)
PATTERN_IDS = [101, 102, 103, 104, 105, 106, 107, 108, 109, 110,
               201, 202, 203, 204, 210, 211, 212, 213, 300, 303]
V0 = {101: 1.06, 102: 0.70, 103: 0.71, 104: 1.51, 105: 0.60, 106: 0.60, 107: 1.69, 108: 1.21,
      109: 0.87, 110: 1.21, 201: 1.06, 202: 0.70, 203: 0.71, 204: 1.51, 210: 1.21, 211: 0.85,
      212: 0.95, 213: 0.95, 300: 0.95, 303: 0.95}
SKILLS = ["BOT_NOVICE", "BOT_INTERMEDIATE", "BOT_EXPERT", "HUMAN"]
DIST_BINS = [0, 2.5, 4.5, 7.5, 12, 1e9]  # 근접 / 중간 / 돌진 거리 / 원거리
W_MIN, W_MAX = 0.6, 1.8


def session_dirs(paths):
    for root in paths:
        for dirpath, _, files in os.walk(root):
            if "events.jsonl" in files:
                yield dirpath


def skill_of(participant):
    p = (participant or "").upper()
    if p.startswith("BOT_MID"):
        return "BOT_INTERMEDIATE"
    return p if p in SKILLS else "HUMAN"


def load(paths, min_version):
    rows = []
    for d in session_dirs(paths):
        participants = {}
        attempts_csv = os.path.join(d, "attempts.csv")
        if os.path.exists(attempts_csv):
            with open(attempts_csv, encoding="utf-8-sig") as f:
                for r in csv.DictReader(f):
                    participants[int(r["attempt"])] = r["participant_id"]
        with open(os.path.join(d, "events.jsonl"), encoding="utf-8-sig") as f:
            for line in f:
                e = json.loads(line)
                if e.get("type") != "boss_strike" or e.get("schemaVersion", 0) < min_version:
                    continue
                pid = e.get("patternId", 0)
                if pid not in PATTERN_IDS:
                    continue
                rows.append({
                    "pattern": pid,
                    "phase": e.get("bossPhase", 1),
                    "distance": e.get("distance", -1.0) if e.get("schemaVersion", 0) >= 6 else -1.0,
                    "skill": skill_of(participants.get(e.get("attempt"))),
                    "hit": 1 if e.get("outcome") == "hit" else 0,
                })
    return rows


def dist_bin(d):
    if d < 0:
        return len(DIST_BINS) - 1  # 모름
    for i in range(len(DIST_BINS) - 1):
        if d < DIST_BINS[i + 1]:
            return i
    return len(DIST_BINS) - 2


def features(row, pattern=None):
    x = np.zeros(len(PATTERN_IDS) + 1 + len(DIST_BINS) + len(SKILLS))
    x[PATTERN_IDS.index(pattern if pattern is not None else row["pattern"])] = 1
    o = len(PATTERN_IDS)
    x[o] = 1.0 if row["phase"] == 2 else 0.0
    x[o + 1 + dist_bin(row["distance"])] = 1
    x[o + 1 + len(DIST_BINS) + SKILLS.index(row["skill"])] = 1
    return x


def normalize(raw):
    w = dict(raw)
    for _ in range(50):
        w = {k: min(W_MAX, max(W_MIN, v)) for k, v in w.items()}
        m = sum(w.values()) / len(w)
        w = {k: v / m for k, v in w.items()}
    return {k: round(min(W_MAX, max(W_MIN, v)), 2) for k, v in w.items()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--out", default=None)
    ap.add_argument("--min-version", type=int, default=4)
    ap.add_argument("--C", type=float, default=1.0, help="L2 정규화 세기의 역수(작을수록 평균 쪽으로 강하게 당김)")
    a = ap.parse_args()

    rows = load(a.paths, a.min_version)
    if len(rows) < 30:
        sys.exit(f"boss_strike 표본이 너무 적다: {len(rows)}")
    X = np.array([features(r) for r in rows])
    y = np.array([r["hit"] for r in rows])
    model = LogisticRegression(C=a.C, max_iter=2000)
    model.fit(X, y)

    counts = Counter(r["pattern"] for r in rows)
    hits = defaultdict(int)
    for r in rows:
        hits[r["pattern"]] += r["hit"]
    # 주변화: 모든 표본의 페이즈·거리·실력은 그대로 두고 패턴만 바꿔 평균 확률을 낸다.
    base = model.predict_proba(X)[:, 1].mean()
    raw = {}
    for pid in PATTERN_IDS:
        Xp = np.array([features(r, pattern=pid) for r in rows])
        raw[pid] = model.predict_proba(Xp)[:, 1].mean() / max(base, 1e-6)
    weights = normalize(raw)

    by_skill = Counter(r["skill"] for r in rows)
    print(f"표본 {len(rows)}타, 맞은 비율 {y.mean():.3f}, 실력 단계 {dict(by_skill)}")
    print(f"{'패턴':>5} {'타격':>5} {'맞음':>5} {'피격률':>6} {'회귀확률비':>9} {'v1후보':>7} {'v0':>5}")
    for pid in PATTERN_IDS:
        n = counts.get(pid, 0)
        rate = hits[pid] / n if n else float('nan')
        print(f"{pid:>5} {n:>5} {hits[pid]:>5} {rate:>6.2f} {raw[pid]:>9.2f} {weights[pid]:>7.2f} {V0[pid]:>5.2f}")
    print(f"v1 후보 평균 {sum(weights.values()) / len(weights):.3f}")
    coef = dict(zip(["phase2"] + [f"dist{i}" for i in range(len(DIST_BINS))] + SKILLS,
                    model.coef_[0][len(PATTERN_IDS):]))
    print("다른 특징의 계수(+면 더 잘 맞음): " + ", ".join(f"{k} {v:+.2f}" for k, v in coef.items()))

    if a.out:
        with open(a.out, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["pattern_id", "strikes", "hits", "hit_rate", "model_ratio", "weight_v1", "weight_v0"])
            for pid in PATTERN_IDS:
                n = counts.get(pid, 0)
                w.writerow([pid, n, hits[pid], f"{hits[pid] / n:.3f}" if n else "", f"{raw[pid]:.3f}", weights[pid], V0[pid]])
        print("저장:", a.out)


if __name__ == "__main__":
    main()
