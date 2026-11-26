"""Generate independent file-origin fixtures with h5py (not needed by normal tests).

Run with h5py 3.15.1. No third-party data is embedded; values are the constants below.
"""
from pathlib import Path
import h5py

root = Path(__file__).resolve().parent
if not root.is_dir():
    raise RuntimeError("test-data directory is missing")
target_name = "tealvm-origin-target-6f9d6b19.h5"
outputs = [root / target_name, root / "external-parent.h5", root / "external-conflict.h5"]
if any(path.exists() for path in outputs):
    raise RuntimeError("refusing to overwrite existing fixtures; remove only owned outputs before regeneration")
with h5py.File(outputs[0], "w") as target:
    target.create_dataset("values", data=[42.0]).attrs["origin"] = "sibling"
with h5py.File(outputs[1], "w") as parent:
    parent["linked"] = h5py.ExternalLink(target_name, "/values")
with h5py.File(outputs[2], "w") as conflict:
    conflict.create_dataset("values", data=[99.0, 100.0]).attrs["origin"] = "cwd"
print("h5py", h5py.__version__, "libhdf5", h5py.version.hdf5_version)
for output in outputs:
    print(output.name, output.stat().st_size)
