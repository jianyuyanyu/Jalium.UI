# Jalium.UI Security, Stability, Functional, and Platform Audit

- Audit date: 2026-07-28
- Baseline commit: `f439cc77066e20ebeeeb94f891a3fe978bf82460`
- Working branch: `preview-26.10.8`
- Status: all P0/P1 findings in this report are fixed; unavailable platforms and incomplete performance/visual coverage are explicitly listed.

## Executive summary

This audit reviewed the managed framework, controls and themes, XAML/JALXAML loader, native ABI, text and media parsers, GPU backends, platform hosts, packaging, samples, and test infrastructure. It found and fixed:

- 3 P0 issues: packed-pixel integer overflow, a restrictive-XAML policy bypass, and undefined behavior in the CFF parser.
- 12 P1 issues: unsafe DLL search, GPU/ABI lifetime and bounds defects, dependency-property type corruption, binding/diagnostic retention, touch capture lifetime, UIA SAFEARRAY bounds, clipboard and media payload bounds, font/text geometry bounds, parallel-build races, package/dependency defects, and C ABI exception/encoding hazards.
- 5 P2 issues: theme cache/event lifetime, whole-file audio buffering, SHA-1 signature defaults, Linux parity gaps, and stale sample/API usage.

The audit delta was reconstructed independently from a heavily modified working tree and validated in a clean clone. Before these two reports, that isolated delta contained 157 implementation, build, and test paths (143 modified and 14 added), with 7,172 insertions and 1,329 deletions. Pre-existing user files and a separate concurrent virtualization task were excluded.

No public API was intentionally removed. Valid inputs retain their prior behavior. Deliberately observable changes are limited to rejecting malformed or unreasonably large inputs earlier, enforcing restrictive XAML consistently, using SHA-256 for newly created package signatures, and correcting package dependency/RID metadata.

## Repository and architecture inventory

The baseline workspace inventory, excluding build output directories, contained approximately:

| Asset | Count |
|---|---:|
| Files | 2,566 |
| C# files | 1,944 |
| Native C/C++ headers and sources | 219 |
| JALXAML theme/template files | 49 |
| Shader sources | 68 |
| Managed projects | 36 |
| Visual C++ projects | 18 |
| CMake roots | 15 |

The clean audit snapshot excludes a pre-existing untracked device-loss harness and therefore contains 2,555 files and 35 managed projects. It includes 9 sample projects, 9 tracked test projects, one Linux CI workflow, 135 regex-identified public control/panel/window-style declarations, approximately 905 managed native-entry declarations, and 106 `unsafe` source lines.

The architecture is organized as follows:

- `src/managed/Jalium.UI.Managed`, `.Core`, `.Controls`, `.Input`, `.Media`, `.Gpu`, `.Interop`, and `.Xaml`: dependency properties, visual/layout/input systems, controls/themes, media, renderer abstractions, interop, and runtime XAML.
- `src/managed/Jalium.UI.Build`, `.Compiler`, and `.Xaml.SourceGenerator`: MSBuild tasks, compiler support, source generation, package-time tooling, and NativeAOT/trimming integration.
- `src/native/jalium.native.core`: backend registry, common render ABI, bitmaps, brushes, contexts, render targets, and video-surface dispatch.
- `src/native/jalium.native.d3d12`, `.vulkan`, `.software`, and `.metal`: renderer backends.
- `src/native/jalium.native.text`: OpenType/CFF parsing, shaping/layout, glyph rasterization, and font providers.
- `src/native/jalium.native.media.*`: shared codec dispatch plus Windows, Linux, and Android media implementations.
- `src/native/jalium.native.platform`: Win32/X11/Wayland platform services.
- `src/packaging`: aggregate, Desktop, Linux, and Android NuGet packages.
- `tests`, `samples`, `eng`, and `.github`: unit/integration/smoke/consumer tests, demonstrations, platform build scripts, and CI.

The source scan found six remaining `TODO`/`FIXME`/`HACK` comments outside third-party code. They cover full COLR rendering, true Win32 display-mode fullscreen, a managed-to-native D3D12 parameter gap, push-constant optimization, and remaining Vello blend modes. Two reachable `NotImplementedException` sites remain in DataGrid editing internals; `XamlWriter` intentionally throws for unsupported node types. These are documented residual implementation gaps, not silently treated as complete.

## Audit scope matrix

| Area | Static review | Executed validation | Result / limitation |
|---|---|---|---|
| Dependency properties, binding, layout, visual tree | Yes | Windows and WSL managed suites; focused DP, binding, layout tests | Fixed type/lifetime defects. Broad complex-control stress remains partial. |
| Public controls, themes, templates, resources | Inventory plus changed-path review | Existing construction/theme suites and dynamic-theme regressions | Green. Manual exhaustive control-by-theme visual inspection was not performed. |
| XAML/JALXAML runtime and source generation | Yes | Restrictive-loader regressions, solution/package/consumer builds | Fixed attached-owner and registry policy bypasses. |
| Managed/native pixel and media boundaries | Yes | Boundary tests, Windows native build, Linux smoke and benchmark | Fixed overflow, truncation, and unbounded buffering. |
| D3D12 | Yes | Native build, ABI tests, real-device harness in mixed workspace | Green on RTX 4060. Clean snapshot skips harness-only tests when its optional project is absent. |
| Vulkan | Yes | Managed/native tests and Linux/Windows runtime smoke | Green on available runtime. No GPU performance claim is made. |
| Software renderer | Yes | Managed parity and native Linux CTest | Green. |
| Metal/macOS | Source review only | None | Unverified: no macOS/Metal host. |
| Font/text parser and geometry | Yes | CTest plus ASan/UBSan/leak-enabled mutation corpus | Fixed CFF UB and added hard bounds. |
| Windows host/input/UIA/clipboard | Yes | Windows tests, clipboard/UIA/touch regressions | Green. |
| X11 and Wayland | Yes | WSL native build, CTest, X11/Wayland clipboard/DND smoke | Green under WSL/Xvfb; not physical Linux hardware. |
| Android | Source review and compile graph | No NDK/runtime build | Unverified: SDK/NDK environment variables were absent. |
| NuGet, RID assets, trimming, single-file, NativeAOT | Yes | Windows/Linux package consumers and publish smokes | Green with documented linker/AOT warnings. |
| Samples | Changed-path and build review | Seven representative samples plus AotWindow | Green. No full interactive visual comparison. |
| Static analysis | Yes | `latest-recommended` analyzer build | 0 errors, 9,071 warning instances; warning debt remains. |
| Performance | Targeted | Repeated Release audio-open benchmark | One measured optimization; other requested categories remain unmeasured. |

## Environment

### Windows host

- Windows 11 Professional for Workstations Insider Preview, version/build `10.0.26300`, x64.
- AMD Ryzen 9 9950X3D, 16 cores / 32 logical processors, 61.6 GiB visible memory.
- NVIDIA GeForce RTX 4060, driver `32.0.16.1074`; AMD Radeon Graphics, driver `32.0.21043.5001`.
- .NET SDK `10.0.400-preview.0.26322.102`.
- MSBuild `18.9.1.35102`.
- Git `2.55.0.windows.3`.
- CMake `4.3.0-rc1`.
- Vulkan SDK `1.4.341.1`.

### Linux validation host

- WSL2 kernel `6.6.87.2-microsoft-standard-WSL2`, x86-64.
- Ubuntu `24.04.4 LTS`.
- .NET SDK `10.0.110`; this is older than the repository-pinned compiler and produced the documented `CS9057` compatibility warning where the Windows-built compiler was consumed.
- CMake `3.28.3`, GCC `13.3.0`, Clang `18.1.3`.

## Reproducible baseline

Baseline commands were run before audit fixes on the existing workspace at `f439cc77`:

| Command | Baseline result |
|---|---|
| `dotnet build src/packaging/Jalium.UI/Jalium.UI.csproj -c Release` | Passed in 36.99 s; 1 `CS8603`, 0 errors. |
| `dotnet test tests/Jalium.UI.Tests/Jalium.UI.Tests.csproj -c Release --no-restore` | 4,488 passed, 0 failed, 0 skipped in 52 s. |
| `msbuild src/native/Jalium.Native.sln /m /p:Configuration=Release /p:Platform=x64` | Passed. |
| `dotnet build Jalium.UI.slnx -c Release` | Failed with 2 `NU1605` errors: test projects pinned MSBuild 18.4.0 while `Jalium.UI.Build` required 18.8.2. |
| `dotnet pack src/packaging/Jalium.UI/Jalium.UI.csproj -c Release` | Produced `Jalium.UI.26.10.7.nupkg`; emitted `NU5128`. |
| Representative sample build loop | DesktopDemo, HostingDemo, BorderlessDemo, and initially TransparentBackdropDemo failed because sample code/project graphs had drifted from current APIs; Gallery and MillionScroll passed. TransparentBackdropDemo passed when rebuilt directly. |

The baseline workspace already contained unrelated uncommitted and untracked work. For that reason, final authoritative validation was also performed against a clean clone containing only the reconstructed audit delta. Test totals between the dirty baseline/mixed run and clean snapshot are not directly comparable; status is reported separately rather than hiding the difference.

## Finding summary

| ID | Severity | Title | Status |
|---|---|---|---|
| AUD-001 | P0 | Packed pixel arithmetic could wrap before allocation/copy | Fixed |
| AUD-002 | P0 | Restrictive XAML could bypass owner-type filtering | Fixed |
| AUD-003 | P0 | Malformed CFF operands triggered undefined behavior | Fixed |
| AUD-004 | P1 | Backend/WebView loader searched the current directory | Fixed |
| AUD-005 | P1 | GPU handles, descriptors, resources, and ABI inputs lacked uniform lifetime/bounds checks | Fixed |
| AUD-006 | P1 | Dependency-property values and metadata could violate the registered type | Fixed |
| AUD-007 | P1 | MultiBinding and binding diagnostics retained unbounded framework state | Fixed |
| AUD-008 | P1 | Touch capture lifetime could retain or dispatch to stale elements | Fixed |
| AUD-009 | P1 | UIA SAFEARRAY bounds and failure cleanup were unsafe | Fixed |
| AUD-010 | P1 | Clipboard and Linux transfer paths accepted unbounded/inconsistent payloads | Fixed |
| AUD-011 | P1 | Image/video/camera/audio boundaries trusted sizes and ownership across the ABI | Fixed |
| AUD-012 | P1 | Font files, em sizes, glyph data, and curve flattening lacked hard safety bounds | Fixed |
| AUD-013 | P2 | Theme switching could retain stale cache/subscription state | Fixed |
| AUD-014 | P2 | Linux audio open buffered the entire file | Fixed |
| AUD-015 | P1 | Shared build outputs raced under parallel solution builds | Fixed |
| AUD-016 | P2 | New package signatures defaulted to SHA-1 | Fixed |
| AUD-017 | P1 | Package metadata, RID assets, dependency versions, and consumer graphs had security/reproducibility defects | Fixed |
| AUD-018 | P2 | Linux native/test manifests and X11/Wayland transfer coverage were incomplete | Fixed |
| AUD-019 | P1 | C++ exceptions and invalid UTF-8 could cross or corrupt C ABI operations | Fixed |
| AUD-020 | P2 | Samples used stale namespaces and application/navigation APIs | Fixed |

## Detailed findings

### AUD-001 — Packed pixel arithmetic could wrap before allocation/copy

- **Severity / status:** P0, fixed.
- **Affected files, types, platforms:** `PixelBufferLayout`, `BitmapImage`, `DecodedImage`, `MediaFrame`, `WriteableBitmap`, `RenderTargetBitmap`, `NativeBitmap`, managed native decoder/camera/video wrappers, and native core/media backends on Windows, Linux, and Android.
- **Root cause:** widths, row bytes, strides, rectangle endpoints, plane sizes, and `stride * height` values were calculated in signed 32-bit or unchecked native arithmetic. Several call sites validated only after the value had wrapped.
- **Reproduction / evidence:** new tests pass values such as width `0x40000000`, `int.MaxValue`, undersized strides, overflowing rectangle coordinates, negative unmanaged buffer sizes, and truncated pixel arrays. Before the fix these paths could allocate/copy using a smaller wrapped length or reach native code with inconsistent dimensions.
- **Fix / actual changes:** introduced a shared checked layout validator, moved validation before allocation/copy, used wider intermediates, rejected undersized rows and truncated planes, and mirrored the checks at C ABI/backend boundaries.
- **Tests:** `PixelBufferValidationTests`, `WriteableBitmapSafetyTests`, updated imaging/clipboard tests, and native bitmap/video-surface ABI cases. The focused managed set executed 21 tests successfully.
- **Validation:** included in the clean Windows managed suite, Windows native Release build, Linux native CTest, and Android source/build-graph review.
- **Compatibility / performance:** valid buffers are unchanged. Malformed or impractically large inputs now fail deterministically with an argument/status error instead of wrapping. Checks are constant-time and occur before allocation.

### AUD-002 — Restrictive XAML could bypass owner-type filtering

- **Severity / status:** P0, fixed.
- **Affected files, types, platforms:** `src/managed/Jalium.UI.Xaml/XamlReader.cs`, runtime XAML on every managed platform.
- **Root cause:** restrictive type filtering was applied to ordinary element construction but not consistently to attached-property owners, attached-property element syntax, or a fast registry return path.
- **Reproduction / evidence:** a restrictive load using an allowed target and a disallowed attached-property owner could resolve and invoke the owner even though direct construction was denied.
- **Fix / actual changes:** every owner/type resolution path now passes through the restrictive filter before property lookup, assignment, or registry return.
- **Tests:** expanded `XamlReaderParityTests` cover attached attributes, attached-property elements, registry resolution, and allowed equivalents.
- **Validation:** focused XAML/pixel run passed 15/15; full Windows and WSL managed suites passed.
- **Compatibility / performance:** unrestricted mode is unchanged. Restrictive mode intentionally rejects inputs that previously escaped the policy.

### AUD-003 — Malformed CFF operands triggered undefined behavior

- **Severity / status:** P0, fixed.
- **Affected files, types, platforms:** `cff_charstring.cpp`, `FontFace`, font providers, glyph rasterizer/text engine, all platforms parsing OpenType CFF/CFF2.
- **Root cause:** CFF dictionary real operands were converted to unsigned offsets without finite/integer/range validation; offset addition, INDEX counts, FDSelect/variation data, recursion, and parser object reuse also lacked uniform bounds/state checks.
- **Reproduction / evidence:** a synthetic Top DICT with `CharStrings` offset `1E999` produced `inf`; UBSan reported `cff_charstring.cpp:149: runtime error: inf is outside the range of representable values of type 'unsigned int'`.
- **Fix / actual changes:** reject non-finite, fractional, negative, and out-of-range offsets; use checked offset addition and INDEX parsing; cap recursion/counts/file size; validate FDSelect/variation references; reset parser state between parses; and bound provider file loads to 512 MiB.
- **Tests:** added deterministic malformed dictionaries, truncation corpus, random mutation/fill corpus, and real-font seed mutation in `font_parser_fuzz_tests.cpp`.
- **Validation:** ASan+UBSan with leak detection and halt-on-error passed 2,562 cases using DejaVuSans.ttf and another 2,562 using NimbusRoman-Regular.otf. Native text boundary CTest also passed.
- **Compatibility / performance:** valid CFF/CFF2 remains accepted. Malformed fonts fail closed. Extra checks are linear with existing parsing and avoid pathological allocations/recursion.

### AUD-004 — Backend/WebView loader searched the current directory

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** Windows `context.cpp` backend discovery and `browser.cpp` WebView2 loader.
- **Root cause:** bare `LoadLibraryA/W` names allowed the process current working directory to participate in DLL resolution. Concurrent initialization also lacked a single serialized publication path.
- **Reproduction / evidence:** isolated child-process probes place a real-named backend or `WebView2Loader.dll` only in a hostile working directory and launch the harness from a separate application directory.
- **Fix / actual changes:** resolve sibling DLLs from the current module directory, call `LoadLibraryExW` with `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS`, serialize WebView loader initialization, and avoid repeated leaking load attempts.
- **Tests:** `BackendLoader_DoesNotLoadBackendFromCurrentWorkingDirectory` and `BrowserLoader_DoesNotLoadWebView2LoaderFromCurrentWorkingDirectory`.
- **Validation:** both probes passed in the mixed workspace with the harness built. In a clean source snapshot without that optional untracked project, all 11 harness-backed tests are reported as explicit skips with the missing-artifact reason rather than false passes.
- **Compatibility / performance:** packaged sibling DLLs and system/default safe locations continue to work; current-directory-only injection no longer works.

### AUD-005 — GPU handles, descriptors, resources, and ABI inputs lacked uniform lifetime/bounds checks

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** managed `DescriptorHeap`, `GpuResourceManager`, `TextureManager`, `D3D12ShaderBackend`; native core, D3D12, Vulkan, and software backends.
- **Root cause:** several exported operations trusted opaque pointers, descriptor indices, byte ranges, pipeline context identity, command/resource state, and allocation success. Destroy/resize/device-loss paths did not uniformly distinguish stale, foreign, and live resources.
- **Reproduction / evidence:** ABI tests exercise null/foreign handles, write-before-map, overflowing ranges, stale contexts, descriptor exhaustion, and video/bitmap dimensions. The device-loss harness exercises mid-frame removal, retained-layer generations, resize with an open command list, Vello output retirement, multi-window recovery, D3D12, and Vulkan.
- **Fix / actual changes:** added checked range helpers and ABI guards; validated context/pipeline ownership; added refcounted pipeline registry entries; guarded descriptor/resource operations; made destroy paths idempotent where the ABI requires it; hardened map/update/CBV/texture creation; and tightened device-loss/resource retirement.
- **Tests:** expanded `NativeGpuInteropAbiTests`, retained-layer/backend parity tests, and 11 child-process device-loss/loader scenarios.
- **Validation:** Windows native Release and static Release builds passed; focused ABI tests reached 14/14; the mixed full suite ran the harness scenarios successfully. Clean snapshot build/test and Linux CTest passed.
- **Compatibility / performance:** public signatures are unchanged. Invalid or stale handles now return errors instead of being dereferenced. No GPU performance improvement is claimed.

### AUD-006 — Dependency-property values and metadata could violate the registered type

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** `DependencyProperty`, `DependencyObject`, `Binding`, `DynamicResourceBinding`, `UIElement`, controls declaring dependency properties; all managed platforms.
- **Root cause:** non-null local/current/binding values and metadata defaults were not checked consistently against `PropertyType`. Failed overrides could partially publish metadata, and one collection default risked sharing mutable state.
- **Reproduction / evidence:** set an `int` into a `string` property, register/override an `int` property with a string default, or push an unconvertible binding value into `Thickness`.
- **Fix / actual changes:** centralized nullable-aware assignability checks, validate before state publication, keep failed binding updates non-destructive, and create per-element mutable transition collections.
- **Tests:** seven `DependencyPropertyTypeSafetyTests` plus existing value-type-null, transition, binding, and dynamic-resource coverage.
- **Validation:** focused DP/dynamic-resource runs passed 60/60; full Windows/WSL suites passed.
- **Compatibility / performance:** type-correct callers are unchanged. Incorrect values now fail early and cannot poison property state.

### AUD-007 — MultiBinding and binding diagnostics retained unbounded framework state

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** `MultiBindingExpression`, `BindingDiagnostics`, binding consumers on every managed platform.
- **Root cause:** each recycled `MultiBindingExpression` could register new shadow dependency properties permanently, and diagnostic tracking used strong target keys/counters that could outlive targets or become inconsistent.
- **Reproduction / evidence:** create and clear 256 two-input MultiBindings; before the fix the global registered shadow-property count grew with expressions. Diagnostic tests exercise collected targets and balanced counters.
- **Fix / actual changes:** reuse shadow dependency properties by stable slot/type keys, use weak target identity for diagnostics, and make subscription/counter cleanup deterministic.
- **Tests:** `MultiBindingResourceSafetyTests` and expanded `DiagnosticsParityTests`.
- **Validation:** focused binding resource run passed 10/10 and diagnostics passed 7/7; full suites passed.
- **Compatibility / performance:** binding results are unchanged; registration and retained-memory growth are bounded.

### AUD-008 — Touch capture lifetime could retain or dispatch to stale elements

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** `TouchDevice`, managed input `Touch`, window/input dispatch.
- **Root cause:** capture state could survive release, detach, or device transitions, and callbacks could observe stale captured elements.
- **Reproduction / evidence:** capture/release, recapture, element detach, and event ordering cases in `TouchCaptureTests`.
- **Fix / actual changes:** synchronize capture transitions, clear stale state, preserve correct lost/got-capture ordering, and prevent dispatch after release.
- **Tests:** expanded `TouchCaptureTests` (8 focused tests).
- **Validation:** 8/8 focused and full managed suites passed.
- **Compatibility / performance:** event contract is preserved for live elements; stale capture is no longer retained.

### AUD-009 — UIA SAFEARRAY bounds and failure cleanup were unsafe

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** `UiaSafeArrayMarshallers`, Windows UI Automation COM interop.
- **Root cause:** `upper - lower + 1` could overflow or request an unreasonable managed allocation; a null data pointer and conversion failure could leak or misuse a SAFEARRAY/COM reference.
- **Reproduction / evidence:** bounds such as `int.MinValue..int.MaxValue`, more than 1,048,576 elements, reversed bounds, and data-access failures.
- **Fix / actual changes:** use 64-bit count calculation, cap managed arrays at 1,048,576 elements, validate data pointers, and destroy partially initialized arrays on every failure path while preserving COM ownership rules.
- **Tests:** added SAFEARRAY overflow/unreasonable-payload cases to `UiaComWrappersTests`.
- **Validation:** focused UIA tests and the full Windows suite passed.
- **Compatibility / performance:** normal UIA arrays are unchanged. Hostile/corrupt arrays return null/error rather than allocating or indexing unsafely.

### AUD-010 — Clipboard and Linux transfer paths accepted unbounded/inconsistent payloads

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** managed `Clipboard`, X11 platform clipboard/DND implementation and tests.
- **Root cause:** bitmap metadata and transfer lengths were trusted independently; multiplication could overflow; INCR/selection transfers lacked a consistent aggregate cap and cleanup contract.
- **Reproduction / evidence:** malformed bitmap dimensions/stride, truncated BGRA data, oversized selections, and X11 incremental transfer cases.
- **Fix / actual changes:** validate layout before conversion, cap managed clipboard images at 128 MiB, reject inconsistent payloads, and bound/clean X11 selection and drag/drop buffers.
- **Tests:** expanded `ClipboardParityTests` (14 focused tests) and Linux platform transfer CTest.
- **Validation:** Windows clipboard tests passed; X11 and Wayland clipboard/DND smoke passed under WSL/Xvfb.
- **Compatibility / performance:** valid clipboard data remains compatible; oversized or inconsistent transfers fail instead of exhausting memory.

### AUD-011 — Image/video/camera/audio boundaries trusted sizes and ownership across the ABI

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** managed native image/video/camera/audio wrappers; media common code; Windows MF/WIC, Linux media, and Android camera/image/video/JNI implementations.
- **Root cause:** decoder-provided dimensions, row strides, plane sizes, time conversions, file/memory sizes, and ownership transitions were not validated uniformly. Some C ABI exports allowed allocation exceptions to escape.
- **Reproduction / evidence:** truncated/overflowing images, packed-size overflow, invalid planes, unreasonable file/memory payloads, and time boundary cases.
- **Fix / actual changes:** added checked layout/plane validation, 512 MiB buffered-media caps, safer seek conversion, explicit status mapping for allocation failure, output initialization, and consistent ownership/cleanup.
- **Tests:** managed pixel/media tests, Linux media smoke, ABI guard tests, and platform-specific build coverage.
- **Validation:** Windows managed/native suites, Linux native media CTest (3/3 focused), and complete Linux CTest passed. Android runtime remains unverified.
- **Compatibility / performance:** valid media is unchanged. Malformed/oversized input fails closed. The Linux file-open performance change is quantified in `PERFORMANCE_REPORT.md`.

### AUD-012 — Font files, em sizes, glyph data, and curve flattening lacked hard safety bounds

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** `FormattedText`, `GlyphTypeface`, control text metadata, native `FontFace`, font providers, glyph rasterizer, text engine/layout, and triangulation helpers.
- **Root cause:** non-finite/out-of-contract em sizes, very large font files, invalid glyph/table offsets, and non-finite/extreme curve inputs could reach allocation, recursion, or native math.
- **Reproduction / evidence:** NaN/infinity/out-of-WPF-range font sizes, 512 MiB+ files, non-finite Bézier controls/tolerances, extreme finite coordinates, and malformed OpenType/CFF tables.
- **Fix / actual changes:** enforce the documented WPF-compatible `0.001..35791` range, add file/table/glyph bounds, use checked UTF-8/path conversions, reject non-finite geometry, and cap flattening recursion/output.
- **Tests:** `FontSizeSafetyTests`, `geometry_safety_tests.cpp`, font parser corpus, text Linux tests, and string-boundary tests.
- **Validation:** managed font-size tests, 13/13 Linux CTest, and sanitizer corpus passed.
- **Compatibility / performance:** documented valid sizes and fonts remain accepted. Unsafe values fail at the public boundary.

### AUD-013 — Theme switching could retain stale cache/subscription state

- **Severity / status:** P2, fixed.
- **Affected files, types, platforms:** `ThemeManager` and controls/resources observing theme changes.
- **Root cause:** cache invalidation and event subscription lifetime were not fully synchronized with dynamic theme changes and disposed/unloaded consumers.
- **Reproduction / evidence:** repeated Light/Dark/High Contrast switches with resource lookup and collected/unloaded listeners.
- **Fix / actual changes:** make theme cache invalidation deterministic, avoid retaining dead listeners, and publish theme changes in a consistent order.
- **Tests:** expanded `ThemeRuntimeSwitchTests` and existing navigation/theme suites.
- **Validation:** focused and full managed suites passed.
- **Compatibility / performance:** resource keys and public events remain compatible; stale values/listeners are removed.

### AUD-014 — Linux audio open buffered the entire file

- **Severity / status:** P2, fixed.
- **Affected files, types, platforms:** `audio_decoder.cpp`, `NativeAudioDecoder`, Linux/POSIX audio file open; Windows non-ASCII fallback remains bounded.
- **Root cause:** all file opens were routed through a read-entire-file memory bridge originally needed only for Windows non-ASCII paths.
- **Reproduction / evidence:** opening a deterministic 600-second, 115,200,044-byte PCM WAV repeatedly caused baseline latency proportional to file size and 116,224 KiB maximum RSS.
- **Fix / actual changes:** use codec streaming file APIs directly on POSIX and ASCII Windows paths; retain the UTF-8-to-wide memory bridge only for non-ASCII Windows paths; cap buffered audio at 512 MiB; use exact tick-to-microsecond seek conversion.
- **Tests:** added `audio_open_benchmark.cpp`, media smoke, decoder info validation, and full suite regression.
- **Validation:** 150 measured Release opens after 10 warmups: median 28.701 ms to 0.002 ms, P95 29.900 ms to 0.002 ms; measured max RSS 116,224 KiB to 4,096 KiB. See `PERFORMANCE_REPORT.md`.
- **Compatibility / performance:** decode semantics are unchanged. Large POSIX files no longer duplicate the entire payload.

### AUD-015 — Shared build outputs raced under parallel solution builds

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** `Directory.Build.props`, `Jalium.UI.slnx`, managed/native package projects on Windows and Linux.
- **Root cause:** multiple projects restored/built the same source-generator/tool/native outputs through paths that were not project/configuration isolated; build-order edges were incomplete.
- **Reproduction / evidence:** clean parallel solution builds intermittently collided or observed incomplete artifacts; the baseline solution also exposed MSBuild package version skew.
- **Fix / actual changes:** isolate intermediate/output paths, preserve configuration/TFM/RID identity, add required ordering without forcing serial builds, and align MSBuild package versions.
- **Tests:** three consecutive clean parallel solution builds and exact packaging builds.
- **Validation:** three clean runs passed in 25.30 s, 23.32 s, and 23.95 s; final mixed solution build passed in 24.4 s, 0 errors.
- **Compatibility / performance:** artifacts move only inside build output roots. Parallelism remains enabled.

### AUD-016 — New package signatures defaulted to SHA-1

- **Severity / status:** P2, fixed.
- **Affected files, types, platforms:** `PackageDigitalSignatures`, Desktop notification UUID helper, package signature creation/verification.
- **Root cause:** new OPC signatures selected SHA-1 by default. One standards-mandated UUIDv5 SHA-1 use and legacy-signature verification made broad suppression tempting.
- **Reproduction / evidence:** static analysis identified SHA-1 call sites and package tests inspected new signature metadata.
- **Fix / actual changes:** create new package signatures with SHA-256, retain narrow legacy SHA-1 verification for compatibility, and confine the RFC/UUIDv5 SHA-1 use to a documented narrow pragma.
- **Tests:** expanded `PackagingRightsManagementParityTests`.
- **Validation:** package tests passed; `latest-recommended` analyzer build reported `CA5350=0`.
- **Compatibility / performance:** new signatures are stronger. Existing SHA-1 signed packages remain verifiable.

### AUD-017 — Package metadata, RID assets, dependency versions, and consumer graphs had security/reproducibility defects

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** aggregate/Desktop/Linux/Android packaging projects, `Jalium.UI.Managed.csproj`, Linux/Windows consumer projects and their local `NuGet.config`.
- **Root cause:** source-generator/project metadata propagated publish properties incorrectly; task dependencies and native RID assets could be omitted or stale; consumer tests used drifting versions/sources; one managed project pinned vulnerable `System.Security.Cryptography.Xml` 10.0.9; test projects pinned MSBuild 18.4.0 against a transitive 18.8.2 requirement.
- **Reproduction / evidence:** baseline `Jalium.UI.slnx` failed with `NU1605`; clean restore of 10.0.9 emitted five high-severity `NU1903` entries; package consumers exposed missing/stale assets.
- **Fix / actual changes:** update cryptography XML to 10.0.10 and MSBuild packages to 18.8.2; correct source-generator/private asset metadata; package full task dependency closure; fix RID/native asset declarations; pin consumer package sources/versions; add stale-native guards and optional harness project detection.
- **Tests:** Desktop/Android/Linux NuGet consumer builds, package content enumeration, load smoke, self-contained/single-file/NativeAOT publish smoke.
- **Validation:** produced/consumed 11 packages; Windows consumer found 9 expected native DLLs; Linux consumer found 7 native `.so` assets; Desktop isolated UI smoke and Linux X11 self-contained, single-file, and NativeAOT smoke passed.
- **Compatibility / performance:** no public API break. Restore graphs become deterministic. Publish emitted existing `IL2104`/`IL3053` warnings, documented below.

### AUD-018 — Linux native/test manifests and X11/Wayland transfer coverage were incomplete

- **Severity / status:** P2, fixed.
- **Affected files, types, platforms:** Linux media CMake, Linux test projects, platform X11 implementation/tests, native text/media test manifests.
- **Root cause:** some native tests and dependencies were not included in the full Linux graph, and selection/drag transfer boundaries were not exercised consistently under both window systems.
- **Reproduction / evidence:** the first Linux CTest run discovered only 9 tests and one platform test required an X display; later full graph exposed 13 tests and font availability requirements.
- **Fix / actual changes:** wire media/text/ABI/geometry/string tests into CMake/CTest, correct managed Linux test compile manifests, add X11 transfer caps/tests, and run X11/Wayland smokes under an explicit display environment.
- **Tests:** 13 native CTests, 4,494 managed Linux cases, clipboard/DND smoke, software/Vulkan runtime paths.
- **Validation:** native CTest passed 13/13 with explicit font data; managed Linux direct VSTest passed 4,464, failed 0, skipped 30; X11 and Wayland smokes passed.
- **Compatibility / performance:** no runtime behavior change except rejecting oversized transfers. Results are WSL-based, not physical Linux certification.

### AUD-019 — C++ exceptions and invalid UTF-8 could cross or corrupt C ABI operations

- **Severity / status:** P1, fixed.
- **Affected files, types, platforms:** native core/text/media exports and string utilities across Windows, Linux, and Android.
- **Root cause:** allocation/string/vector operations in exported functions were not uniformly guarded, and UTF-8 conversion accepted malformed sequences or relied on unchecked length arithmetic.
- **Reproduction / evidence:** ABI tests inject allocation-sensitive sizes, invalid UTF-8, truncated multibyte sequences, null outputs, and extreme lengths.
- **Fix / actual changes:** added reusable ABI exception guards/status mapping, initialize outputs before work, implement strict bounded UTF-8 conversion, and use checked size/offset helpers.
- **Tests:** `abi_guard_tests.cpp`, `string_util_tests.cpp`, managed native ABI tests, and full native suites.
- **Validation:** focused ABI/string CTests and complete Windows/Linux native builds/tests passed.
- **Compatibility / performance:** valid UTF-8 and ABI calls remain compatible. Failures return stable error codes instead of allowing exceptions across `extern "C"`.

### AUD-020 — Samples used stale namespaces and application/navigation APIs

- **Severity / status:** P2, fixed.
- **Affected files, types, platforms:** BorderlessDemo, DesktopDemo image/rotation/scroll paths, HostingDemo hosted-services/metrics pages.
- **Root cause:** samples referenced moved media types, obsolete application builder calls, and navigation events no longer exposed by the current control contract.
- **Reproduction / evidence:** baseline representative build loop failed four sample projects with `CS0246`, `CS1061`, and `CS0103`.
- **Fix / actual changes:** update namespaces/usings, current application startup API, and navigation lifecycle hooks without changing sample intent.
- **Tests:** sample Release build matrix and AotWindow build/publish smoke.
- **Validation:** seven representative sample builds plus AotWindow passed.
- **Compatibility / performance:** sample-only source updates; no runtime API change.

## Final validation ledger

### Clean isolated audit snapshot

- `dotnet build Jalium.UI.slnx -c Release --no-restore -m`: passed, 0 errors, 27 existing warnings.
- `msbuild src/native/Jalium.Native.sln /m /p:Configuration=Release /p:Platform=x64`: passed, 0 errors. The temporary clone path caused 11 `MSB8029` intermediate-directory warnings; these are path-placement warnings, not source failures.
- `dotnet test tests/Jalium.UI.Tests/Jalium.UI.Tests.csproj -c Release --no-build --no-restore`: 4,451 passed, 0 failed, 11 skipped, total 4,462, 32 s.
- The 11 skips are all `DeviceRemovalInjectionTests`; their optional harness project is not tracked at the baseline commit. The tests use a discovery-time fact attribute that skips only when the executable cannot be located and run unchanged when it exists.

### Mixed workspace / optional harness validation

- Final full Windows suite before isolation: 4,576 passed, 0 failed, 0 skipped.
- All 11 real-device/loader child-process scenarios passed when the local harness was present.
- Final solution build: 24.4 s, 0 errors.
- Exact packaging target build: 16.7 s, passed.

### Linux and native

- Managed Linux direct VSTest under X11: 4,464 passed, 0 failed, 30 skipped, total 4,494.
- Native Linux CTest: 13/13 passed after providing explicit deterministic font data.
- X11 and Wayland clipboard/drag-drop smoke: passed.
- ASan/UBSan/leak-enabled font corpus: 2,562 DejaVu cases plus 2,562 Nimbus CFF/OpenType cases, passed.
- Windows native Release and static Release: passed.

### Packaging, publish, samples, and analysis

- 11 package consumer set: passed.
- Desktop isolated consumer: 9 native DLLs found; UI/load smoke passed.
- Linux isolated consumer: 7 native `.so` files found; stale-asset guard passed.
- Linux self-contained, single-file, and NativeAOT X11 smoke: passed.
- Seven representative samples and AotWindow: passed.
- Static analysis (`EnableNETAnalyzers=true`, `AnalysisLevel=latest-recommended`): 0 errors, 9,071 warning instances, `CA5350=0`.
- Three clean parallel builds: 25.30 s, 23.32 s, 23.95 s, all passed.

## Compatibility and behavior changes

- No public member was intentionally removed or renamed by the audit.
- Oversized, truncated, non-finite, type-incompatible, stale-handle, and malformed parser/ABI inputs now fail earlier and deterministically.
- Restrictive XAML now applies the same allow/deny decision to attached owners and registry fast paths.
- New package signatures use SHA-256. Legacy SHA-1 signatures can still be verified.
- RFC/UUIDv5 SHA-1 remains only where the algorithm mandates it and is narrowly documented/suppressed.
- Package source-generator/build-task and RID assets are now visible to consumers as intended.
- `System.Security.Cryptography.Xml` is updated from 10.0.9 to 10.0.10 in the remaining lagging project.
- The optional device-loss harness is conditionally referenced; its tests become explicit skips only in source snapshots that do not contain the project.

## Known warning debt and residual risks

- The analyzer run has 9,071 warnings, dominated by broad latest-recommended quality/style diagnostics in existing source and tests. Treating this audit as authorization for a repository-wide style rewrite would obscure the security fixes, so the debt is recorded rather than mechanically changed.
- Publish produced `IL2104` and `IL3053` warnings. Runtime smoke passed, but a dedicated warning-by-warning trimming/AOT closure is still warranted.
- The WSL SDK is older than the repository-pinned compiler; a `CS9057` warning was observed. Linux runtime results were obtained by using the built artifacts directly.
- Two DataGrid editing internals still throw `NotImplementedException`; these need a product decision and feature-specific tests.
- Remaining TODOs include complete COLR layer rendering, Vello blend modes, true display-mode fullscreen, and a D3D12 managed/native parameter contract.

## Explicitly unverified

- Android NDK build, emulator/device runtime, Activity lifecycle, camera permissions, and Surface recreation: Android SDK/NDK environment was unavailable.
- macOS and Metal compilation/runtime: no macOS host.
- Physical Linux GPU/display-server/driver matrix: validation used WSL2, Xvfb, and the available Vulkan/software paths.
- ARM64, musl, and non-x86 native ABI/runtime.
- Broad screenshot/pixel-golden comparison across D3D12, Vulkan, software, and Metal.
- Cold/hot application start, window-to-first-frame, frame-time, draw-call, barrier, GPU-memory, layout-allocation, large-control virtualization, and long-running handle-leak benchmarks. No claim is made for those categories.
- Manual exhaustive matrix of every public control under every theme, locale, RTL/IME, high-DPI, multi-monitor, accessibility tool, and input device.

## Reproduction entry points

```powershell
dotnet restore Jalium.UI.slnx
dotnet build Jalium.UI.slnx -c Release --no-restore -m
dotnet build src/packaging/Jalium.UI/Jalium.UI.csproj -c Release --no-restore
dotnet test tests/Jalium.UI.Tests/Jalium.UI.Tests.csproj -c Release --no-build --no-restore
msbuild src/native/Jalium.Native.sln /m /p:Configuration=Release /p:Platform=x64
```

```bash
JALIUM_NATIVE_BUILD_TESTS=1 bash eng/linux/build-native.sh linux-x64 Release
XDG_DATA_HOME=/mnt/c/Windows xvfb-run -a \
  ctest --test-dir src/native/out/build/linux-x64 --output-on-failure
JALIUM_WINDOW_SYSTEM=x11 xvfb-run -a \
  dotnet test tests/Jalium.UI.Linux.Tests/Jalium.UI.Linux.Tests.csproj \
  -c Release
```

See `PERFORMANCE_REPORT.md` for the reproducible benchmark protocol and raw summary statistics.
