"""
Unit tests for the Oxco compare analysis core.

Run:  .venv\\Scripts\\python.exe -m unittest discover -s tests\\python -v
"""

import os
import random
import sys
import unittest

import numpy as np

OXCO_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "bridges", "vendor", "oxco"))
sys.path.insert(0, OXCO_DIR)

import compare  # noqa: E402
import oxco_analysis as oa  # noqa: E402


def legacy_keep_frames(raw_diff, buffer_frames):
    """Verbatim copy of the pre-refactor compare.py keep logic (reference for equivalence)."""
    keep_frames = []
    false_streak = 0
    for i in range(len(raw_diff)):
        if raw_diff[i]:
            keep_frames.append(i)
            false_streak = 0
        else:
            false_streak += 1
            if false_streak >= buffer_frames:
                keep_frames.append(i)
                if false_streak == buffer_frames:
                    for j in range(1, buffer_frames):
                        keep_frames.append(i - j)
    keep = set(keep_frames)
    return [i in keep for i in range(len(raw_diff))]


def legacy_seqs(metric, noise_pixel_thr, pixel_max, buffer_frames):
    raw = []
    for m in metric:
        d = m > noise_pixel_thr
        if d and pixel_max > 0 and m > pixel_max:
            d = False
        raw.append(d)
    keep = legacy_keep_frames(raw, buffer_frames)
    if not any(keep):
        return []
    seqs = []
    cur = keep[0]
    start = 0
    for i in range(1, len(keep)):
        if keep[i] != cur:
            seqs.append((start, i, cur))
            start = i
            cur = keep[i]
    seqs.append((start, len(keep), cur))
    return seqs


class LegacyEquivalenceTests(unittest.TestCase):
    def test_compute_segments_matches_legacy_with_postfilters_off(self):
        rng = random.Random(1234)
        for _ in range(300):
            n = rng.randint(1, 400)
            # Bursty metric: runs of high/low values like real footage.
            metric = []
            while len(metric) < n:
                hi = rng.random() < 0.6
                metric.extend([rng.randint(201, 5000) if hi else rng.randint(0, 200)] * rng.randint(1, 60))
            metric = metric[:n]
            buffer_frames = rng.choice([0, 1, 2, 5, 25, 50, 120])
            pixel_max = rng.choice([0, 0, 3000])
            fp = oa.FilterParams(pixel_threshold=200, pixel_max=pixel_max, buffer_frames=buffer_frames)
            seqs, _ = oa.compute_segments(metric, fp)
            expected = legacy_seqs(metric, 200, pixel_max, buffer_frames)
            if not expected:
                self.assertFalse(any(v for _a, _b, v in seqs))
            else:
                self.assertEqual(seqs, expected, msg=f"buffer={buffer_frames} max={pixel_max}")


class SharedVectorTests(unittest.TestCase):
    def test_fixture_matches_python_reference(self):
        """The C# port is verified against this file — it must stay in sync with the Python logic."""
        sys.path.insert(0, os.path.dirname(__file__))
        import json
        import make_oxco_segment_vectors as mk

        with open(mk.FIXTURE, encoding="utf-8") as f:
            stored = json.load(f)
        self.assertEqual(stored, json.loads(json.dumps(mk.build_vectors())),
                         "run tests/python/make_oxco_segment_vectors.py and the C# verify")


class PostFilterTests(unittest.TestCase):
    def test_short_diff_spike_does_not_split_long_quiet_stretch(self):
        # 50 frames swap, 30 quiet, 1 noise spike, 30 quiet, 50 swap; buffer 50 frames.
        metric = [1000] * 50 + [0] * 30 + [900] + [0] * 30 + [1000] * 50
        legacy = oa.compute_segments(metric, oa.FilterParams(buffer_frames=50))[0]
        fixed = oa.compute_segments(metric, oa.FilterParams(buffer_frames=50, min_diff_run=2))[0]
        self.assertIn((50, 80, False), legacy)  # the spike made two short stretches → both cut
        self.assertEqual(fixed, [(0, 161, True)])  # spike ignored → 61-frame stretch kept

    def test_single_frame_dropout_is_still_cut(self):
        metric = [1000] * 40 + [0] + [1000] * 40
        seqs, _ = oa.compute_segments(metric, oa.FilterParams(buffer_frames=50, min_diff_run=2))
        self.assertEqual(seqs, [(0, 40, True), (40, 41, False), (41, 81, True)])

    def test_cut_padding_grows_cuts(self):
        keep = [True] * 10 + [False] * 3 + [True] * 10
        out = oa.pad_cuts(keep, 2)
        self.assertEqual(oa.mask_to_seqs(out), [(0, 8, True), (8, 15, False), (15, 23, True)])

    def test_padding_clamps_at_edges(self):
        keep = [False] + [True] * 5
        self.assertEqual(oa.pad_cuts(keep, 3), [False] * 4 + [True] * 2)

    def test_short_kept_islands_between_cuts_are_removed(self):
        keep = [True] * 20 + [False] * 5 + [True] * 3 + [False] * 5 + [True] * 20
        out = oa.drop_short_keeps(keep, 6)
        self.assertEqual(oa.mask_to_seqs(out), [(0, 20, True), (20, 33, False), (33, 53, True)])

    def test_whole_video_kept_is_never_dropped(self):
        self.assertEqual(oa.drop_short_keeps([True] * 3, 10), [True] * 3)

    def test_pixel_max_ignores_global_changes(self):
        self.assertEqual(oa.diff_flags([100, 300, 5000], 200, 1000), [False, True, False])

    def test_shift_and_summary(self):
        seqs = [(0, 10, True), (10, 12, False), (12, 20, True)]
        self.assertEqual(oa.shift_seqs(seqs, 2), [(2, 12, True), (12, 14, False), (14, 22, True)])
        self.assertEqual(oa.segment_summary(seqs), (18, 2, 1))


class MetricTests(unittest.TestCase):
    def test_blob_metric_ignores_scattered_noise_but_sees_a_face(self):
        rng = np.random.default_rng(7)
        base = rng.integers(60, 190, size=(360, 640), dtype=np.uint8)
        noisy = base.copy()
        idx = rng.integers(0, base.size, size=3000)
        noisy.flat[idx] = np.clip(noisy.flat[idx].astype(int) + 60, 0, 255).astype(np.uint8)

        classic_noise, _ = oa.classic_metric(base, noisy, 15)
        blob_noise, _ = oa.blob_metric(base, noisy, 15, 3)
        self.assertGreater(classic_noise, 1000)  # classic counts every noisy pixel
        self.assertLess(blob_noise, 50)  # blob: no coherent region

        face = noisy.copy()
        face[100:160, 300:350] = 255
        blob_face, _ = oa.blob_metric(base, face, 15, 3)
        self.assertGreater(blob_face, 1500)

    def test_analysis_size_keeps_aspect_and_never_upscales(self):
        self.assertEqual(oa.analysis_size(1920, 1080, 640), (640, 360))
        self.assertEqual(oa.analysis_size(480, 270, 640), (480, 270))
        w, h = oa.analysis_size(1081, 1921, 640)
        self.assertEqual((w % 2, h % 2), (0, 0))

    def test_detect_sync_offset(self):
        rng = np.random.default_rng(3)
        frames = [rng.integers(0, 255, size=(90, 160), dtype=np.uint8) for _ in range(120)]
        # Deepfake lost its first 2 frames: deepfake[k] == source[k + 2].
        shift, s0, sb = oa.detect_sync_offset(frames, frames[2:], 6)
        self.assertEqual(shift, 2)
        self.assertLess(sb, s0)
        # Deepfake has 3 extra leading frames: deepfake[k] == source[k - 3].
        extra = [np.zeros((90, 160), np.uint8)] * 3 + frames
        self.assertEqual(oa.detect_sync_offset(frames, extra, 6)[0], -3)
        # Static content → no evidence → no shift.
        still = [frames[0]] * 100
        self.assertEqual(oa.detect_sync_offset(still, still, 6)[0], 0)


class CompareHelperTests(unittest.TestCase):
    def test_encoder_aliases(self):
        self.assertEqual(compare.normalize_ffmpeg_encoder("libx264"), "cpu")
        self.assertEqual(compare.normalize_ffmpeg_encoder("h264_nvenc"), "nvidia_h264")
        self.assertEqual(compare.normalize_ffmpeg_encoder("NVIDIA_HEVC"), "nvidia_hevc")
        self.assertEqual(compare.normalize_ffmpeg_encoder("bogus"), "cpu")
        self.assertEqual(compare.normalize_ffmpeg_encoder(""), "cpu")

    def test_select_filter_is_frame_exact(self):
        f = compare.build_ffmpeg_select_filter([(0, 10, True), (20, 25, True)], 25.0, with_audio=True)
        self.assertIn("between(n,0,9)+between(n,20,24)", f)
        self.assertIn("gte(t,0.800000)*lt(t,1.000000)", f)
        self.assertNotIn("[0:a", compare.build_ffmpeg_select_filter([(0, 10, True)], 25.0, with_audio=False))

    def test_cli_parsing(self):
        self.assertEqual(
            compare.parse_compare_cli_args(["a.mp4", "b.mp4", "--auto", "--retry-export-only"]),
            ("a.mp4", "b.mp4", True, False, False),
        )
        self.assertIsNone(compare.parse_compare_cli_args(["a.mp4", "--auto"]))

    def test_filename_tag_marks_blob_mode(self):
        self.assertEqual(compare.compare_export_filename_tag(2.0, 15, 200), "_b2_n15_p200")
        self.assertEqual(compare.compare_export_filename_tag(2.0, 15, 200, 0, "blob"), "_b2_n15_p200_blob")


if __name__ == "__main__":
    unittest.main()
