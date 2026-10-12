#!/usr/bin/env python3
"""Patch Avalonia Browser avalonia.js for reliable WebGL on AMD/ANGLE Chrome.

- failIfMajorPerformanceCaveat: false — caveat=true can yield a context that never presents Skia frames
- preserveDrawingBuffer: true — keeps frames readable for diagnostics; cheap on desktop GPUs
Does NOT enable Software2D.
- ResizeObserver DPR guard (UI 决策 213): Avalonia trusts devicePixelContentBoxSize unconditionally.
  Some engines (Chrome device emulation, some WebViews/zoom states) report it in CSS px while
  devicePixelRatio > 1, so the canvas backing store stays CSS-sized and the whole UI renders
  at DPR x scale (model chip overflow, half-width layout). Only trust it when it agrees with
  contentBoxSize * devicePixelRatio (±2px); otherwise fall back to the CSS size * DPR path.
- Non-composition text bridge (UI 决策 390): upstream only turns keydown (key.length==1) and
  compositionend into TextInput; `beforeinput` insertText / insertReplacementText / insertFromPaste /
  insertFromDrop (CDP Input.insertText, iOS autocorrect, voice dictation, long-press paste) is dropped.
  We forward those exactly once via InputHelper.OnCompositionEnd(data) (-> RawTextEvent), first
  mapping the replaced range into the Avalonia selection via the existing deleteContentBackward
  selection sync, and preventDefault so the hidden <input> never drifts from Avalonia's text.
  Only insertReplacementText remaps the selection (others trust Avalonia's own). Skipped while composing and for paste right after a Ctrl/Cmd+V keydown (Avalonia pastes that itself).
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
DPR_OLD = "r.devicePixelContentBoxSize?(s=r.devicePixelContentBoxSize[0].inlineSize"
DPR_MARK = "/*wx-dpr-guard*/"
DPR_NEW = (DPR_MARK + "r.devicePixelContentBoxSize&&Math.abs(r.devicePixelContentBoxSize[0].inlineSize-"
           "(r.contentBoxSize&&r.contentBoxSize[0]?r.contentBoxSize[0].inlineSize:r.contentRect.width)*i)<=2"
           "?(s=r.devicePixelContentBoxSize[0].inlineSize")
if DPR_MARK in t:
    print(f"dpr guard already present {path}")
elif DPR_OLD in t:
    t = t.replace(DPR_OLD, DPR_NEW)
    print(f"patched dpr guard -> {path}")
else:
    raise SystemExit(f"avalonia.js ResizeObserver pattern not found in {path}")
BRIDGE_OLD = 'e.addEventListener("beforeinput",n);'
BRIDGE_MARK = "/*wx-insert-bridge*/"
BRIDGE_NEW = BRIDGE_OLD + BRIDGE_MARK + (
    'e.addEventListener("compositionstart",()=>{e.__wxComp=!0});'
    'e.addEventListener("compositionend",()=>{e.__wxComp=!1});'
    'globalThis.__wxPasteHook||(globalThis.__wxPasteHook=1,globalThis.document&&globalThis.document.addEventListener("keydown",'
    'k=>{(k.ctrlKey||k.metaKey)&&(k.key==="v"||k.key==="V")&&(globalThis.__wxPasteKey=Date.now())},!0));'
    'e.addEventListener("beforeinput",o=>{let y=o.inputType;'
    'if(o.isComposing||e.__wxComp||!/^(insertText|insertReplacementText|insertFromPaste|insertFromPasteAsQuotation|insertFromDrop|insertFromDictation|insertFromYank)$/.test(y))return;'
    'if(y.startsWith("insertFromPaste")&&Date.now()-(globalThis.__wxPasteKey||0)<1e3)return;'
    'let d=o.data,x=o.target&&o.target.setSelectionRange?o.target:e;d==null&&o.dataTransfer&&(d=o.dataTransfer.getData("text/plain"));o.preventDefault();'
    'if(y==="insertReplacementText"){let g=o.getTargetRanges&&o.getTargetRanges();'
    'g&&g.length&&g[0].startOffset!==g[0].endOffset&&x.setSelectionRange&&x.setSelectionRange(g[0].startOffset,g[0].endOffset);'
    'x.selectionStart!=null&&x.selectionStart!==x.selectionEnd&&l.InputHelper.OnBeforeInput(t,"deleteContentBackward",-1,-1)}'
    'd&&l.InputHelper.OnCompositionEnd(t,d.replace(/\\r\\n?/g,"\\n"))});')
if BRIDGE_MARK in t:
    print(f"insert bridge already present {path}")
elif BRIDGE_OLD in t:
    t = t.replace(BRIDGE_OLD, BRIDGE_NEW, 1)
    print(f"patched insert bridge -> {path}")
else:
    raise SystemExit(f"avalonia.js beforeinput pattern not found in {path}")
path.write_text(t)
