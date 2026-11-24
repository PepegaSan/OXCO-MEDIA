import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


BRIDGES = Path(__file__).resolve().parents[2] / "bridges"


@unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "FFmpeg required")
class BitrateChangerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        fixture = tempfile.TemporaryDirectory()
        cls.addClassCleanup(fixture.cleanup)
        clip = Path(fixture.name) / "fixture.mp4"
        subprocess.run([
            "ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=160x96:r=12:d=1.5",
            "-c:v", "libx264", "-crf", "15", str(clip),
        ], check=True)
        cls.original = clip.read_bytes()

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.inputs = self.root / "Quellen"
        self.inputs.mkdir()
        self.clip = self.inputs / "Grüner Clip.MP4"
        self.neighbor = self.inputs / "neighbor.mp4"
        self.clip.write_bytes(self.original)
        self.neighbor.write_bytes(self.original)
        self.output = self.root / "Output"
        self.config = {
            "input_folder": str(self.inputs), "input_file": str(self.clip),
            "output_folder": str(self.output), "only_lower": False, "rule_values": {"0": "48"},
        }
        self.rows = self.root / "rows.json"

    def bridge(self, job, flag):
        config = self.root / "config.json"
        config.write_text(json.dumps(self.config), encoding="utf-8")
        return subprocess.run([
            sys.executable, str(BRIDGES / job), "--config-json", str(config), flag, str(self.rows),
        ], capture_output=True)

    def scan(self):
        result = self.bridge("bitrate_scan_job.py", "--output-json")
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(self.rows.read_text(encoding="utf-8"))["rows"]

    def convert(self):
        return self.bridge("bitrate_convert_job.py", "--rows-json")

    def test_file_picker_scope_and_custom_output(self):
        rows = self.scan()
        self.assertEqual([Path(row["path"]) for row in rows], [self.clip])
        self.assertEqual(rows[0]["effective_target_kbps"], 48)
        result = self.convert()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([p.name for p in self.output.iterdir()], ["Grüner Clip_bitrate.MP4"])
        self.assertEqual(self.clip.read_bytes(), self.original)
        self.assertEqual(self.neighbor.read_bytes(), self.original)

    def test_missing_or_unsupported_file_never_scans_parent(self):
        for name in ("missing.mp4", "notes.txt"):
            with self.subTest(name=name):
                if name.endswith(".txt"):
                    (self.inputs / name).write_text("notes")
                self.config["input_file"] = str(self.inputs / name)
                result = self.bridge("bitrate_scan_job.py", "--output-json")
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(self.rows.exists())

    def test_legacy_folder_input_and_recursion(self):
        self.config.pop("input_file")
        child = self.inputs / "Sub"
        child.mkdir()
        (child / "nested.mov").write_bytes(self.original)
        for recursive, count in [(False, 2), (True, 3)]:
            with self.subTest(recursive=recursive):
                self.config["recursive"] = recursive
                self.assertEqual(len(self.scan()), count)

    def test_stale_file_selection_refused(self):
        self.scan()
        self.config["input_file"] = str(self.neighbor)
        self.config["post_success_action"] = "delete_original"
        self.assertNotEqual(self.convert().returncode, 0)
        self.assertFalse(self.output.exists())
        self.assertEqual(self.clip.read_bytes(), self.original)

    def test_keep_original_beside_source_with_strip(self):
        self.config.update(output_folder=str(self.inputs), strip_autobitrate_suffix=True)
        self.scan()
        self.assertEqual(self.convert().returncode, 0)
        self.assertEqual(self.clip.read_bytes(), self.original)
        self.assertTrue((self.inputs / "Grüner Clip_bitrate.MP4").is_file())

    def test_backup_original_before_stripping_suffix(self):
        self.config.update(output_folder=str(self.inputs), strip_autobitrate_suffix=True,
                           post_success_action="move_to_backup")
        backup = self.inputs / "_original_backup"
        backup.mkdir()
        (backup / self.clip.name).write_bytes(b"older")
        (backup / "Grüner Clip_dup.MP4").write_bytes(b"second backup")
        self.scan()
        self.assertEqual(self.convert().returncode, 0)
        self.assertEqual((backup / "Grüner Clip_dup2.MP4").read_bytes(), self.original)
        self.assertEqual((backup / "Grüner Clip_dup.MP4").read_bytes(), b"second backup")
        self.assertNotEqual(self.clip.read_bytes(), self.original)
        self.assertEqual(self.neighbor.read_bytes(), self.original)

    def test_failed_batch_preserves_existing_output_and_reports_failure(self):
        self.config.pop("input_file")
        self.config["parallel_jobs"] = 2
        self.scan()
        self.clip.unlink()
        self.output.mkdir()
        prior = self.output / "Grüner Clip_bitrate.MP4"
        prior.write_bytes(b"previous output")
        self.assertNotEqual(self.convert().returncode, 0)
        self.assertEqual(prior.read_bytes(), b"previous output")
        self.assertTrue((self.output / "neighbor_bitrate.mp4").is_file())
        self.assertFalse(list(self.output.glob(".hm-bitrate-*")))

    def test_parallel_inputs_with_same_mp4_destination(self):
        self.config.pop("input_file")
        self.config.update(output_mp4=True, parallel_jobs=2, post_success_action="delete_original")
        other = self.clip.with_suffix(".mov")
        other.write_bytes(self.original)
        self.scan()
        self.assertNotEqual(self.convert().returncode, 0)
        self.assertEqual(self.clip.read_bytes(), self.original)
        self.assertEqual(other.read_bytes(), self.original)
        self.assertFalse((self.output / "Grüner Clip_bitrate.mp4").exists())
