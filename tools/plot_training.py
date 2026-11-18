"""학습 곡선(텐서보드 기록)을 PNG로 저장한다: 평균 보상, 판(에피소드) 길이, 정책 엔트로피.

  python plot_training.py <results 폴더>/<RunId> [<RunId 폴더> ...] --out <그림.png>
"""
import argparse
import glob
import os

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from tensorboard.backend.event_processing.event_accumulator import EventAccumulator

plt.rcParams["font.family"] = ["Malgun Gothic", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False
TAGS = [("Environment/Cumulative Reward", "평균 보상(판당)"),
        ("Environment/Episode Length", "판 길이(결정 수)"),
        ("Policy/Entropy", "정책 엔트로피(높을수록 고르게 고름)")]


def load(run_dir):
    files = glob.glob(os.path.join(run_dir, "**", "events.out.tfevents.*"), recursive=True)
    data = {}
    for f in files:
        acc = EventAccumulator(f, size_guidance={"scalars": 0})
        acc.Reload()
        for tag, _ in TAGS:
            if tag in acc.Tags().get("scalars", []):
                data.setdefault(tag, []).extend((e.step, e.value) for e in acc.Scalars(tag))
    return {k: sorted(v) for k, v in data.items()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    fig, axes = plt.subplots(1, 3, figsize=(15, 4))
    for run in a.runs:
        data = load(run)
        for ax, (tag, title) in zip(axes, TAGS):
            if tag in data:
                steps, values = zip(*data[tag])
                ax.plot(steps, values, label=os.path.basename(run.rstrip("/\\")))
                ax.set_title(title)
                ax.set_xlabel("결정 수(스텝)")
    for ax in axes:
        ax.legend()
    fig.tight_layout()
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    fig.savefig(a.out, dpi=150)
    print("저장:", a.out)


if __name__ == "__main__":
    main()
