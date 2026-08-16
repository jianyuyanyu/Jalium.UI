# Image pipeline triage

How to find out why an image is blank, black, soft or static on a machine you do not have.

Everything described here is live in **Release**. That is the point of the channel: the image
pipeline's only error output used to be `Debug.WriteLine`, which is `[Conditional("DEBUG")]` and
therefore absent from the builds users run — so a failed upload rendered a black rectangle and
produced not one line of evidence anywhere. Do not reintroduce a `Debug.WriteLine` as the only
output of an image failure path.

---

## 1. Capture a trace

```powershell
$env:JALIUM_IMAGE_TRACE = "1"
$env:JALIUM_IMAGE_TRACE_FILE = "$env:TEMP\jalium-image.log"
dotnet run --project <the app>
```

| Variable | Effect | Default |
| --- | --- | --- |
| `JALIUM_IMAGE_TRACE` | `1`/`true`/`yes`/`on` writes every record through `Trace.WriteLine`, i.e. to the attached debugger / DebugView via `OutputDebugString`. Works in Release. | off |
| `JALIUM_IMAGE_TRACE_FILE` | Appends every record to this path (created if needed, shared read). **Use this one when collecting a capture from a user** — it needs no debugger attached. | off |
| `JALIUM_IMAGE_DECODE_WATCHDOG_MS` | How long a single native decode may run before `DecodeStalled` is reported and one relief worker is started | `5000`, floor `250` |
| `JALIUM_IMAGE_NOTIFY_WATCHDOG_MS` | How long a queued completion drain may sit on the main dispatcher before the notifier reports it and re-posts | `1000`, floor `50` |

An EventPipe/ETW consumer can enable the `Jalium-UI-Image` EventSource instead of the text sink;
the records are identical.

With all sinks off, a report costs a few interlocked increments and a null check — the counters and
the `ImageDiagnostics.Reported` event stay live regardless, so an application can subscribe in
production without turning any switch on.

## 2. Read the first line

The first record of any capture is the one-shot process line, emitted lazily ahead of the first real
image event:

```
[Jalium.Image] kind=CapabilitiesProbed source="process" size=0x0 attempt=0 elapsed_ms=0.000 thread_id=1 detail="configuration=Release processors=8 arch=X64 os=\"...\" runtime=\"...\" trace=1 traceFile=1"
```

`configuration` and `processors` are the two fields that decide most triage questions immediately.
A capture with no `CapabilitiesProbed` line at all means no image event was ever reported — the
element never reached the pipeline, so the problem is upstream (layout, visibility, a null `Source`).

## 3. Record format

```
[Jalium.Image] kind=<Kind> source="<uri>" size=<W>x<H> attempt=<n> elapsed_ms=<ms> thread_id=<id> detail="<text>" [error="<type>" message="<text>"]
```

`detail` is free-form and may change between versions — never parse it in tooling. `kind`, `source`,
`size` and `attempt` are stable.

| Kind | Meaning |
| --- | --- |
| `DecodeRequested` | A native decode is about to run. `detail="upgrade"` marks a re-decode for a larger display bucket. |
| `DecodeCompleted` | Pixels were published at `size`. |
| `DecodeFailed` | A decode, a header probe, a frame-count probe or an option transform threw. |
| `DecodeStalled` | One decode has been running longer than the decode watchdog. `detail` carries `queue=` and `workers=`. |
| `BucketSaturated` | A decode chain hit the hard attempt bound and stopped. Resident pixels stay on screen. |
| `UploadFailed` | The GPU upload of decoded pixels threw. `detail` names the stage. |
| `Degraded` | A lower-fidelity path was taken so something is still painted/animated. |
| `PlaceholderShown` | Reserved. No framework path raises this yet — the visible failure placeholder is not implemented. |
| `CapabilitiesProbed` | The one-shot process line above. |

## 4. Counters

`ImageDiagnostics.Snapshot()` returns the always-live totals, which is what to ask for when a user
cannot run with a trace file:

`DecodesStarted`, `DecodesCompleted`, `DecodesFailed`, `UpgradesScheduled`, `BucketSaturatedCount`,
`StalledDecodeCount`, `UploadFailureCount`, `DegradedCount`, `PlaceholdersShown`, `QueueLength`,
`ActiveWorkers`, `LongestRunningDecodeMs`, `SinkFaults`.

`SinkFaults > 0` means a diagnostics sink itself failed — a subscriber that threw, or a trace file
that could not be opened. Read it before concluding "the trace is empty, so nothing happened".

## 5. Reading a capture

### `BucketSaturatedCount > 0`

**Always a framework bug.** A correct bucket predicate stops its own chain; the attempt bound is
belt-and-braces. Seeing it means the producer (`BitmapPixelResampler.ResizeToDisplayBucket`) and the
upgrade predicate (`BitmapImage.DecodeUpgradeNeededLocked`) disagree about what bucket a request
resolves to, so the predicate keeps asking for a raster the producer will never emit.

`detail` separates the two causes:

* `decode chain stopped by the unproductive-attempt bound` — the predicate/producer disagreement.
  Reproduce with `BitmapPixelResampler.ResolveBucket(natural, request)` for the `size=` in the
  record; it is the single source of truth and both sides must agree with it exactly.
* `decode chain stopped: consecutive decodes failed` — not a bucket problem at all. Read the
  `DecodeFailed` records just above it.

This is the RC1 class: the original bug re-enqueued forever at the same source version, and on a
machine running a single decode worker one trapped image left every other image in the process
queued — a whole page blank. The bound now caps it at `1 + 3` decodes per source version.

### Many `DecodeRequested` records with `attempt` climbing

Same class as above, caught before the bound trips. A healthy source shows `attempt=1` and, at most,
one `detail="upgrade"` per crossed power-of-two bucket edge. `attempt` climbing on *every* image is
a predicate regression, not a content problem.

### `DecodeStalled` with `workers=` equal to `MaxWorkers`

Worker starvation: every decode worker is inside a native decode that has not returned. Look at the
`source=` on the stalled record — that is the payload the codec is stuck on (a huge image, a slow
network URI, a codec deadlock). The scheduler starts exactly one relief worker above the cap so the
rest of the process keeps making progress; the stall itself still has to be explained.

`MaxWorkers` is `clamp(ProcessorCount / 4, 2, 3)`. The floor of **2** is load-bearing: it used to be
1, which is what turned one non-terminating decode into a process-wide blackout.

### `UploadFailed`

The decode succeeded and the GPU rejected the pixels. `size=` is the raster that failed to upload.

**Do not expect an HRESULT here.** The native bitmap ABI collapses bad arguments, a missing backend
and any C++ exception into a null return, and the D3D12 allocation returns null on `FAILED(hr)`
without logging the code, so the managed layer can only report
`InvalidOperationException: Failed to create bitmap from raw pixel data`. An absent `hr=`/
`DXGI_ERROR_*` in `message=` therefore rules *nothing* out — in particular it does not rule out VRAM
exhaustion. Until the ABI carries a status code, separate the causes from the context:

* `size=` whose long edge exceeds the adapter's `D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION` — 4096 at
  FL9_3, 8192 at FL10_x, 16384 at FL11_0+ — is a dimension rejection. WARP, RDP sessions and old
  integrated GPUs are the realistic cases, and the `CapabilitiesProbed` line plus the user's adapter
  answer it.
* An ordinary `size=` that uploaded successfully earlier in the same capture points at pressure or a
  device-removed frame rather than at the payload.

An upload failure also reaches the application: it is routed into the source's `LoadFailed` chain
and surfaces as `Image.ImageFailed` on the UI thread, once per failure episode rather than once per
frame. It does **not** clear `Image.Source` — a transient GPU failure must not destroy the
application's data or its binding.

### `DecodeFailed`

`detail` names the stage:

* `source load` — the source could not be reached at all, before any decode: an asset that was not
  copied to the output directory (`FileNotFoundException`, and by far the most common cause), a URI
  scheme the loader does not handle such as `pack://` or `ms-appx://` (`NotSupportedException`), an
  unreadable file, or a failed HTTP fetch. Every latched load failure produces this record, whatever
  draws the image — an `ImageBrush`, a `DrawingImage` or a `Shape.Fill` has no `Image` element and
  therefore no `ImageFailed` event, so for those consumers this record is the only signal there is.
* `frame count probe` / `animated substitute` — the still image is fine; only the animation is lost.
  A GIF that renders its first frame and never advances is this record.
* an option transform — an out-of-range `SourceRect` for the natural image. The untransformed raster
  is published instead, so the symptom is "the crop stopped working", not a blank element.
* anything else — the codec could not read the payload. `error=` and `message=` carry the native
  exception; on Windows that is a WIC HRESULT.

A failed decode is terminal for that source version and is retried with backoff rather than once per
render pass, so a repeated `DecodeFailed` for one source is itself a finding.

**`DecodesFailed` counts more than decodes.** The counter behind this kind is incremented by every
site that reports one — the stages above, a load failure, a throwing diagnostics or image-loaded
subscriber, a GPU cache eviction that could not release its texture — so `DecodesFailed` may exceed
`DecodesStarted` without anything being wrong with the arithmetic. Read the records for the stage;
use the counter only as "did anything fail at all".

### `Degraded`

Everything still paints; something was worked around.

* `<Property> changed after EndInit and was ignored` — `SourceRect`, `DecodePixelWidth`,
  `DecodePixelHeight` or `Rotation` was written outside `BeginInit`/`EndInit`. Matches WPF, where
  the decode pipeline is built once when initialization ends. Move the write inside the init block.
* `metadata probe stopped after N attempts; intrinsic size unavailable` — the decoder cannot read
  this container's header. Layout falls back to the element's own constraints.
* `animated frames will not advance: this process has no main dispatcher to run the frame timer` —
  an animated payload in a host with no `Application`.
* `image notifications delivered off the UI thread` / `the main dispatcher has not run the queued
  image completion drain in Nms` — the UI thread is blocked. Images are decoded but nothing repaints
  until it runs; the cause is in the application, not the pipeline.

### Nothing at all

No records for a source that should be on screen means nothing ever asked for its pixels. Every
bitmap consumer drives the decode through `RenderTargetDrawingContext.GetNativeBitmap` (and the
software rasterizer's own `RequestDecode`, for an `ImageDrawing` and for an `ImageBrush` used as a
vector fill), so silence here means the element was never rendered: zero size, collapsed, clipped
out, or a `Source` that is still null.

One case still produces no managed record at all: a native-layer upload rejection that returns null
without throwing. See `UploadFailed` above for what that costs.

## 6. Structural facts worth knowing before you dig

* **The display bucket is not the natural size.** `PixelWidth`/`PixelHeight` report the *canonical*
  decoded size and are invariant across the ladder; `RasterPixelWidth`/`RasterPixelHeight` describe
  the raster actually resident, which is the DPI- and layout-dependent bucket. A `size=` in a record
  is always the bucket, so a `size=` smaller than `PixelWidth` is normal and not a finding.
* **An explicit `DecodePixelWidth`/`DecodePixelHeight` bypasses the ladder.** The author named the
  decode resolution, so exactly that raster is published, `PixelWidth` equals it, and the chain ends
  on the first decode — expect one `DecodeCompleted` and no `UpgradeScheduled` for such a source.
* **`CacheOption="OnLoad"` moves only the file read forward**, into `EndInit`. The decode is still
  deferred and still sized for the slot. An `IOException` out of `EndInit` for a URI source means
  that eager read failed; with the default option the same fault surfaces later as a `DecodeFailed`.
* **Buckets only ever grow.** A request that resolves to a smaller bucket than the one already
  published is free — no decode, no queue entry. This is why a window-resize drag does not cost one
  decode per mouse-move.
* **The bucket long edge is capped at 4096** (`BitmapPixelResampler.MaxBucketEdge`), and the
  resampler never upscales. Both caps mean a request can legitimately never be satisfied: a 24x24
  icon asked for at 48 device pixels, or an 8000x6000 photo asked for at 5120x3840. Neither is an
  error, and neither may cause a re-decode.
* **Decode options resolve against natural coordinates**, inside the decode, once per publication.
  A `SourceRect` therefore means the same thing at 100% and at 200% display scaling.
* **A decode completion is delivered on `Dispatcher.MainDispatcher` at `Normal` priority**, coalesced
  to one drain per dispatcher turn, and never inline on a decode worker while a live main dispatcher
  exists.
* **An intrinsic size can arrive before the pixels.** Measuring an `Image` whose source has no size
  yet arms a header probe (bounded to three attempts per source version), and its answer is
  announced on the same UI-thread channel, so the element measures correctly before the decode
  lands. It is deliberately not offered for a source carrying decode options, whose canonical size a
  header alone does not determine — those measure at zero until the first publish, as before.

## 7. Regression coverage

The behaviours above are pinned by, in `tests/Jalium.UI.Tests`:

| Area | Test class |
| --- | --- |
| Bucket policy and decode-chain termination | `ImageDpiBucketSaturationTests` |
| Every consumer drives the decode (RC2) | `ImageBrushDecodeTests` |
| Release-live failure channel, snapshot publication, dispatcher delivery | `ImagePipelineFailureContractTests` |
| Worker floor, metadata-before-pixels ordering, starvation | `BitmapDecodeSchedulerTests` |
| Deferred decode, awaiter release, decode options vs. DPI, `CacheOption` | `BitmapDeferredDecodeTests` |
| Intrinsic size vs. display bucket, header-probe lane | `ImageIntrinsicSizeTests` |
| Reclaim/restore at the currently requested size | `BitmapImageReclaimRestoreTests` |
| Animated substitute through the type-converter path | `ImageSourceLoaderTests` |
| `ImageFailed` never clearing `Source` or its binding | `ImageParityTests` |
