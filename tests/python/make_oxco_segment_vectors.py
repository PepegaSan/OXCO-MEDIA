"""
Regenerates tests/python/fixtures/oxco_segment_vectors.json from the Python reference (oxco_analysis.compute_segments).
The C# port (OxcoSegmentFilter) is checked against the same file in src/HailMary.Verify.

Run:  .venv\\Scripts\\python.exe tests\\python\\make_oxco_segment_vectors.py
"""

import json
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "bridges", "vendor", "oxco"))

import oxco_analysis as oa  # noqa: E402

FIXTURE = os.path.join(HERE, "fixtures", "oxco_segment_vectors.json")


def build_vectors(seed: int = 4711, count: int = 80):
    rng = random.Random(seed)
    cases = []
    for _ in range(count):
        n = rng.randint(1, 300)
        metric = []
        while len(metric) < n:
            hi = rng.random() < 0.6
            metric.extend(rng.randint(150, 4000) if hi else rng.randint(0, 220) for _ in range(rng.randint(1, 40)))
        metric = metric[:n]
        fp = oa.FilterParams(
            pixel_threshold=rng.choice([100, 200]),
            pixel_max=rng.choice([0, 0, 3000]),
            buffer_frames=rng.choice([0, 1, 5, 25, 50]),
            min_diff_run=rng.choice([1, 2, 3]),
            min_keep_frames=rng.choice([0, 3, 6]),
            cut_padding=rng.choice([0, 0, 1, 2]),
        )
        seqs, _ = oa.compute_segments(metric, fp)
        cases.append({
            "metric": metric,
            "params": {
                "pixel_threshold": fp.pixel_threshold,
                "pixel_max": fp.pixel_max,
                "buffer_frames": fp.buffer_frames,
                "min_diff_run": fp.min_diff_run,
                "min_keep_frames": fp.min_keep_frames,
                "cut_padding": fp.cut_padding,
            },
            "segments": [[a, b, keep] for a, b, keep in seqs],
        })
    return cases


if __name__ == "__main__":
    os.makedirs(os.path.dirname(FIXTURE), exist_ok=True)
    with open(FIXTURE, "w", encoding="utf-8", newline="\n") as f:
        json.dump(build_vectors(), f, separators=(",", ":"))
        f.write("\n")
    print("wrote", FIXTURE)
