# Test data

## External-link origin fixtures

`make_external_h5.py` generates `external-parent.h5`,
`tealvm-origin-target-6f9d6b19.h5` and `external-conflict.h5` with **h5py 3.15.1 /
libhdf5 1.14.6**. The parent links by a relative filename to the sibling's `values`
dataset (42, attribute `origin=sibling`). The conflict has values 99/100 and
`origin=cwd`; the test places it in the working directory to catch wrong-origin lookup.
Normal tests consume the committed fixtures and do not require Python/h5py.

Provenance: all three are project-generated reference data from the included generator,
using only the displayed numeric/string constants; no external input asset or source
code was reused. No separate third-party asset redistribution conditions attach to these
data files. Existing repository licensing terms apply; no additional license is asserted.

## Numeric reference fixture

`reference-h5py.h5` was written by the reference HDF5 implementation (h5py 3.x, libhdf5), not
by this project, so that reading it tests interoperability rather than a round trip:

```python
import h5py, numpy as np
g = h5py.File("reference-h5py.h5", "w")
g.create_dataset("f32", data=np.arange(6, dtype="float32").reshape(2, 3))
g.create_dataset("u16", data=np.array([0, 1, 65535], dtype="uint16"))
g.create_dataset("i8", data=np.array([-128, 127], dtype="int8"))
g.create_dataset("chunked", data=np.linspace(0, 1, 1000), chunks=(100,), compression="gzip")
g.create_dataset("scalar", data=3.25)
grp = g.create_group("meas"); grp.attrs["operator"] = "ada"; grp.attrs["n"] = 5
grp.create_dataset("names", data=np.array([b"x", b"yy"]))
grp.create_dataset("vl", data=["alpha", "beta"], dtype=h5py.string_dtype())
g.close()
```
