"""
End-to-end test: synthetic original + "deepfake" (a face-sized box that drops out at known frames),
analysed by both modes and exported through compare.py with FFmpeg.

Needs ffmpeg/ffprobe on PATH; skipped otherwise.
Run:  .venv\\Scripts\\python.exe -m unittest tests.python.test_oxco_compare_e2e -v   (from repo root)
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

OXCO_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "bridges", "vendor", "oxco"))
sys.path.insert(0, OXCO_DIR)

import oxco_analysis as oa  # noqa: E402

FFMPEG = shutil.which("ffmpeg")
FFPROBE = shutil.which("ffprobe")
FPS = 25
SECONDS = 20
# Swap missing (box hidden) in these frame ranges → expected cuts.
DROPOUTS = [(100, 110), (200, 205), (425, 426)]
NO_FACE = (300, 400)  # 4 s without face: longer than the 2 s buffer → must be kept


def _run(cmd):
    subprocess.run(cmd, check=True, capture_output=True)


def _hidden_expr():
    parts = [f"between(n,{a},{b - 1})" for a, b in DROPOUTS + [NO_FACE]]
    return "+".join(parts)


def _probe_frames(path):
    out = subprocess.run(
        [FFPROBE, "-v", "error", "-count_frames", "-select_streams", "v:0",
         "-show_entries", "stream=nb_read_frames", "-of", "json", path],
        check=True, capture_output=True, text=True,
    ).stdout
    return int(json.loads(out)["streams"][0]["nb_read_frames"])


def _has_audio(path):
    out = subprocess.run(
        [FFPROBE, "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "json", path],
        check=True, capture_output=True, text=True,
    ).stdout
    return bool(json.loads(out).get("streams"))


@unittest.skipUnless(FFMPEG and FFPROBE, "ffmpeg/ffprobe not on PATH")
class CompareEndToEnd(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="oxco_e2e_")
        cls.src = os.path.join(cls.tmp, "orig.mp4")
        cls.df = os.path.join(cls.tmp, "fake.mp4")
        cls.df_shift = os.path.join(cls.tmp, "fake_shift_noaudio.mp4")
        _run([
            FFMPEG, "-y", "-v", "error",
            "-f", "lavfi", "-i", f"testsrc2=s=1280x720:r={FPS}:d={SECONDS}",
            "-f", "lavfi", "-i", f"sine=f=440:d={SECONDS}",
            "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", cls.src,
        ])
        box = (
            f"drawbox=x=560:y=200:w=160:h=200:color=0xE0B090@1:t=fill:enable='not({_hidden_expr()})',"
            f"drawbox=x=600:y=260:w=30:h=20:color=black@1:t=fill:enable='not({_hidden_expr()})'"
        )
        # Different CRF → real codec noise between the two files, like an actual deepfake render.
        _run([FFMPEG, "-y", "-v", "error", "-i", cls.src, "-vf", box,
              "-c:v", "libx264", "-crf", "26", "-pix_fmt", "yuv420p", "-c:a", "copy", cls.df])
        # Deepfake that lost its first 2 frames and has no audio track.
        _run([FFMPEG, "-y", "-v", "error", "-i", cls.df, "-vf", "trim=start_frame=2,setpts=PTS-STARTPTS",
              "-an", "-c:v", "libx264", "-crf", "20", "-pix_fmt", "yuv420p", cls.df_shift])

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def _segments(self, mode, df, **filter_kw):
        params = oa.AnalysisParams(mode=mode, noise=15, auto_sync=True, max_sync_offset=6)
        res = oa.analyze_pair(self.src, df, params, src_wh=(1280, 720), ffmpeg_exe=FFMPEG, total_hint=FPS * SECONDS)
        fp = oa.FilterParams(
            pixel_threshold=filter_kw.get("pixel", 200 if mode == "classic" else 120),
            buffer_frames=2 * FPS,
            min_diff_run=2,
            min_keep_frames=int(0.25 * FPS),
        )
        seqs, _ = oa.compute_segments(res.metric, fp)
        return res, seqs

    def test_blob_mode_finds_exactly_the_dropouts(self):
        res, seqs = self._segments("blob", self.df)
        self.assertEqual(res.reader, "ffmpeg")
        self.assertEqual(res.frames, FPS * SECONDS)
        cuts = [(a, b) for a, b, keep in seqs if not keep]
        self.assertEqual(cuts, DROPOUTS)
        print(f"\n[blob] {res.frames} frames in {res.seconds:.2f}s ({res.frames / res.seconds:.0f} fps)")

    def test_classic_mode_runs_and_reports(self):
        res, seqs = self._segments("classic", self.df)
        self.assertEqual(res.reader, "opencv")
        self.assertEqual(res.frames, FPS * SECONDS)
        cuts = [(a, b) for a, b, keep in seqs if not keep]
        quiet = [m for i, m in enumerate(res.metric) if NO_FACE[0] <= i < NO_FACE[1]]
        print(f"\n[classic] {res.frames / res.seconds:.0f} fps | codec-noise pixels in face-free stretch: "
              f"median {sorted(quiet)[len(quiet) // 2]} | cuts {cuts}")

    def test_offset_is_detected_and_compensated(self):
        res, seqs = self._segments("blob", self.df_shift)
        self.assertEqual((res.src_offset, res.df_offset), (2, 0))
        # Analysis index k ↔ source frame k+2, so dropouts appear 2 earlier in analysis space.
        cuts = [(a, b) for a, b, keep in seqs if not keep]
        self.assertEqual(cuts, [(a - 2, b - 2) for a, b in DROPOUTS])

    def test_compare_cli_ffmpeg_export_and_metrics_reuse(self):
        out_dir = os.path.join(self.tmp, "export")
        os.makedirs(out_dir, exist_ok=True)
        ini = os.path.join(self.tmp, "job.ini")
        with open(ini, "w", encoding="utf-8") as f:
            f.write(
                "[SETTINGS]\nlanguage = de\nbuffer_seconds = 2.0\npixel_noise_threshold = 15\n"
                "changed_pixels_threshold = 120\nchanged_pixels_max_threshold = 0\n"
                "enable_ffmpeg_export = 1\nffmpeg_export_target = both\nffmpeg_encoder = libx264\n"
                "enable_fullcheck_edl = 0\nenable_autodelete_edl = 0\nenable_davinci_export = 0\n"
                "analysis_mode = blob\n"
                f"[PATHS]\nfinal_export_dir = {out_dir}\n"
            )
        env = dict(os.environ)
        env["OXCO_SETTINGS_INI"] = ini
        env["OXCO_METRICS_DIR"] = os.path.join(self.tmp, "metrics")
        env["OXCO_CHECKPOINT_PATH"] = os.path.join(self.tmp, "ck.json")

        def run_compare(df):
            return subprocess.run(
                [sys.executable, os.path.join(OXCO_DIR, "compare.py"), self.src, df, "--auto"],
                capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, cwd=OXCO_DIR,
            )

        first = run_compare(self.df_shift)
        self.assertEqual(first.returncode, 0, first.stdout + first.stderr)
        self.assertIn("HM_METRICS:", first.stdout)
        kept = FPS * SECONDS - 2 - sum(b - a for a, b in DROPOUTS)

        out_df = os.path.join(out_dir, "fake_shift_noaudio_AutoCut.mp4")
        out_src = os.path.join(out_dir, "orig_AutoCut.mp4")
        self.assertEqual(_probe_frames(out_df), kept)
        self.assertEqual(_probe_frames(out_src), kept)
        self.assertFalse(_has_audio(out_df))  # no audio track → video-only export instead of failure
        self.assertTrue(_has_audio(out_src))

        second = run_compare(self.df_shift)
        self.assertEqual(second.returncode, 0, second.stdout + second.stderr)
        self.assertIn("wiederverwendet", second.stdout)


if __name__ == "__main__":
    unittest.main()
