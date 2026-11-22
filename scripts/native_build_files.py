"""File operations shared by the VLC native builders."""

import hashlib
from pathlib import Path
import shutil
from tempfile import TemporaryDirectory


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def install_binary(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    # Stage next to the destination so the replacement stays on the same filesystem.
    # Each build owns its stage, even when two builds share an output path.
    with TemporaryDirectory(prefix=f".{destination.name}-", dir=destination.parent) as directory:
        staged = Path(directory) / destination.name
        shutil.copyfile(source, staged)
        staged.replace(destination)
