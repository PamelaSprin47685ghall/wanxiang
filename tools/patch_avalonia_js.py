#!/usr/bin/env python3
"""Patch Avalonia Browser avalonia.js for reliable WebGL on AMD/ANGLE Chrome.

- failIfMajorPerformanceCaveat: false — caveat=true can yield a context that never presents Skia frames
- preserveDrawingBuffer: true — keeps frames readable for diagnostics; cheap on desktop GPUs
Does NOT enable Software2D.
"""
from __future__ import annotations
import pathlib, sys
path = pathlib.Path(sys.argv[1])
t = path.read_text()
old = "alpha:!0,depth:!0,stencil:!0,antialias:!1,premultipliedAlpha:!0,preserveDrawingBuffer:!1,failIfMajorPerformanceCaveat:!0"
new = "alpha:!0,depth:!0,stencil:!0,antialias:!1,premultipliedAlpha:!0,preserveDrawingBuffer:!0,failIfMajorPerformanceCaveat:!1"
# also handle already partially patched
old2 = "alpha:!1,depth:!0,stencil:!0,antialias:!1,premultipliedAlpha:!0,preserveDrawingBuffer:!0,failIfMajorPerformanceCaveat:!1"
new2 = "alpha:!0,depth:!0,stencil:!0,antialias:!1,premultipliedAlpha:!0,preserveDrawingBuffer:!0,failIfMajorPerformanceCaveat:!1"
if old in t:
    t = t.replace(old, new)
    print(f"patched defaults -> {path}")
elif old2 in t:
    t = t.replace(old2, new2)
    print(f"normalized alpha+caveat -> {path}")
elif "failIfMajorPerformanceCaveat:!1" in t and "preserveDrawingBuffer:!0" in t:
    print(f"already patched {path}")
else:
    raise SystemExit(f"avalonia.js attrs pattern not found in {path}")
path.write_text(t)
