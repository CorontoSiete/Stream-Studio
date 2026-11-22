import json
from pathlib import Path
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
from native_build_files import install_binary


class NativeBuildFileTests(unittest.TestCase):
    def test_adaptive_bundle_rejects_an_unpinned_import_library(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            scripts = root / "scripts"
            scripts.mkdir()
            for name in ("build-adaptive-replay.py", "native_build_files.py"):
                shutil.copyfile(SCRIPTS / name, scripts / name)
            native = root / "native" / "adaptive-replay"
            native.mkdir(parents=True)
            (native / "provenance.json").write_text(json.dumps({
                "sourceBundle": "source.tar.gz", "sourceBundleSha256": "0" * 64,
                "libvlccoreImportLibrarySha256": "0" * 64,
            }), encoding="utf-8")
            (native / "source.tar.gz").write_bytes(b"source fixture")
            (root / "libvlccore.a").write_bytes(b"unverified library")
            bundled = root / "src" / "StreamlinkVlcStudio.Infrastructure" / "Vlc" / "BundledAdaptiveReplay"
            bundled /= "libstudio_adaptive_plugin.dll"
            bundled.parent.mkdir(parents=True)
            bundled.write_bytes(b"last verified binary")
            command = [sys.executable, str(scripts / "build-adaptive-replay.py"),
                       "--gcc", str(root / "gcc.exe"), "--vlc-library-directory", str(root)]

            result = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 2, result.stderr)
            self.assertIn("Bundled output requires the pinned libvlccore.a", result.stderr)
            self.assertEqual(bundled.read_bytes(), b"last verified binary")

            result = subprocess.run(command + ["--output", str(root / "diagnostic.dll")],
                                    capture_output=True, text=True, timeout=10)
            self.assertIn("The bundled upstream source does not match its pinned SHA-256.", result.stderr)

    def test_partial_copy_preserves_last_binary_and_removes_stage(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "candidate.dll"
            destination = root / "libvlccore.dll"
            source.write_bytes(b"rebuilt library")
            destination.write_bytes(b"last verified library")

            def interrupted_copy(_source, staged):
                staged.write_bytes(b"partial")
                raise OSError("copy interrupted")

            with patch("native_build_files.shutil.copyfile", side_effect=interrupted_copy):
                with self.assertRaises(OSError):
                    install_binary(source, destination)
            self.assertEqual(destination.read_bytes(), b"last verified library")
            self.assertCountEqual(root.iterdir(), [source, destination])

            install_binary(source, destination)
            self.assertEqual(destination.read_bytes(), source.read_bytes())
            self.assertCountEqual(root.iterdir(), [source, destination])
