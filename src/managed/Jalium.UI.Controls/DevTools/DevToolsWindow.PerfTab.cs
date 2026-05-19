using Jalium.UI.Diagnostics;
using Jalium.UI.Interop;
using Jalium.UI.Media;
using Jalium.UI.Threading;

namespace Jalium.UI.Controls.DevTools;

public partial class DevToolsWindow
{
    private TextBlock? _perfBackendText;
    private TextBlock? _perfEngineText;
    private TextBlock? _perfFpsText;
    private TextBlock? _perfGpuText;
    private TextBlock? _perfApiStatsText;
    private Border? _perfGraphHost;
    private DispatcherTimer? _perfRefreshTimer;
    private Image? _perfGraphImage;
    private DevToolsUi.DevToolsButton? _perfEngineAuto;
    private DevToolsUi.DevToolsButton? _perfEngineVello;
    private DevToolsUi.DevToolsButton? _perfEngineImpeller;

    private UIElement BuildPerfTab()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(180) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ── Toolbar ──
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };

        _perfBackendText = DevToolsUi.Text("Backend: ?", DevToolsTheme.FontSm, DevToolsTheme.TextPrimary);
        _perfBackendText.Margin = new Thickness(0, 0, DevToolsTheme.GutterLg, 0);
        toolbar.Children.Add(_perfBackendText);

        _perfEngineText = DevToolsUi.Text("Engine: ?", DevToolsTheme.FontSm, DevToolsTheme.TextPrimary);
        _perfEngineText.Margin = new Thickness(0, 0, DevToolsTheme.GutterLg, 0);
        toolbar.Children.Add(_perfEngineText);

        _perfFpsText = DevToolsUi.Text("FPS —", DevToolsTheme.FontSm, DevToolsTheme.Accent, weight: FontWeights.SemiBold);
        _perfFpsText.Margin = new Thickness(0, 0, DevToolsTheme.GutterLg, 0);
        toolbar.Children.Add(_perfFpsText);

        toolbar.Children.Add(DevToolsUi.VerticalDivider());
        toolbar.Children.Add(DevToolsUi.Muted("Engine:"));
        _perfEngineAuto     = DevToolsUi.Toggle("Auto",     () => SwitchEngine(RenderingEngine.Auto),     false);
        _perfEngineVello    = DevToolsUi.Toggle("Vello",    () => SwitchEngine(RenderingEngine.Vello),    false);
        _perfEngineImpeller = DevToolsUi.Toggle("Impeller", () => SwitchEngine(RenderingEngine.Impeller), false);
        toolbar.Children.Add(_perfEngineAuto);
        toolbar.Children.Add(_perfEngineVello);
        toolbar.Children.Add(_perfEngineImpeller);

        var toolbarBar = DevToolsUi.Toolbar(toolbar);
        Grid.SetRow(toolbarBar, 0);
        root.Children.Add(toolbarBar);

        // ── Frame graph ──
        _perfGraphImage = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _perfGraphHost = new Border
        {
            Background = DevToolsTheme.Chrome,
            BorderBrush = DevToolsTheme.BorderSubtle,
            BorderThickness = DevToolsTheme.ThicknessHairline,
            CornerRadius = DevToolsTheme.RadiusBase,
            Margin = new Thickness(DevToolsTheme.GutterBase, DevToolsTheme.GutterBase, DevToolsTheme.GutterBase, DevToolsTheme.GutterSm),
            Child = _perfGraphImage,
            ClipToBounds = true,
        };
        Grid.SetRow(_perfGraphHost, 1);
        root.Children.Add(_perfGraphHost);

        // ── Legend ──
        var legendRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(DevToolsTheme.GutterBase, 0, DevToolsTheme.GutterBase, DevToolsTheme.GutterSm),
        };
        legendRow.Children.Add(MakeLegendChip(PerfColorLayout, "Layout"));
        legendRow.Children.Add(MakeLegendChip(PerfColorRender, "Render"));
        legendRow.Children.Add(MakeLegendChip(PerfColorPresent, "Present"));
        legendRow.Children.Add(DevToolsUi.Muted("  · red dashed = 16 ms budget", DevToolsTheme.FontXS));
        Grid.SetRow(legendRow, 2);
        root.Children.Add(legendRow);

        // ── Bottom panel: GPU snapshot (left, fixed-ish) +
        //     per-API call counters / timing (right, fills remainder) ──
        var bottomGrid = new Grid();
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _perfGpuText = new TextBlock
        {
            Text = "GPU: (no snapshot published)",
            FontSize = DevToolsTheme.FontSm,
            FontFamily = DevToolsTheme.MonoFont,
            Foreground = DevToolsTheme.TextPrimary,
        };
        var gpuCard = new Border
        {
            Background = DevToolsTheme.SurfaceAlt,
            BorderBrush = DevToolsTheme.BorderSubtle,
            BorderThickness = DevToolsTheme.ThicknessHairline,
            CornerRadius = DevToolsTheme.RadiusBase,
            Padding = new Thickness(DevToolsTheme.GutterLg, DevToolsTheme.GutterBase, DevToolsTheme.GutterLg, DevToolsTheme.GutterBase),
            Margin = new Thickness(DevToolsTheme.GutterBase, 0, DevToolsTheme.GutterSm, DevToolsTheme.GutterBase),
            Child = new ScrollViewer { Content = _perfGpuText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            ClipToBounds = true,
        };
        Grid.SetColumn(gpuCard, 0);
        bottomGrid.Children.Add(gpuCard);

        _perfApiStatsText = new TextBlock
        {
            Text = "Draw API stats: (waiting for first frame)",
            FontSize = DevToolsTheme.FontSm,
            FontFamily = DevToolsTheme.MonoFont,
            Foreground = DevToolsTheme.TextPrimary,
        };
        var apiCard = new Border
        {
            Background = DevToolsTheme.SurfaceAlt,
            BorderBrush = DevToolsTheme.BorderSubtle,
            BorderThickness = DevToolsTheme.ThicknessHairline,
            CornerRadius = DevToolsTheme.RadiusBase,
            Padding = new Thickness(DevToolsTheme.GutterLg, DevToolsTheme.GutterBase, DevToolsTheme.GutterLg, DevToolsTheme.GutterBase),
            Margin = new Thickness(DevToolsTheme.GutterSm, 0, DevToolsTheme.GutterBase, DevToolsTheme.GutterBase),
            Child = new ScrollViewer { Content = _perfApiStatsText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            ClipToBounds = true,
        };
        Grid.SetColumn(apiCard, 1);
        bottomGrid.Children.Add(apiCard);

        Grid.SetRow(bottomGrid, 3);
        root.Children.Add(bottomGrid);

        return new Border
        {
            Background = DevToolsTheme.Surface,
            Child = root,
            ClipToBounds = true,
        };
    }

    private static StackPanel MakeLegendChip(Color color, string label)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, DevToolsTheme.GutterLg, 0),
        };
        row.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, DevToolsTheme.GutterSm, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = DevToolsTheme.FontXS,
            FontFamily = DevToolsTheme.UiFont,
            Foreground = DevToolsTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    partial void OnPerfTabActivated()
    {
        // Turn on per-frame draw-API counters while the Perf tab is visible.
        // Window.RenderFrame's end-of-frame hook publishes them, RefreshPerfStats
        // renders the snapshot. The flag is the only thing gating the recording
        // overhead — outside DevTools we pay nothing.
        RenderDiagnostics.ApiStatsEnabled = true;
        RefreshPerfStats();
        if (_perfRefreshTimer == null)
        {
            _perfRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _perfRefreshTimer.Tick += (_, _) => RefreshPerfStats();
            _perfRefreshTimer.Start();
        }
    }

    private void SwitchEngine(RenderingEngine engine)
    {
        try
        {
            _targetWindow.SetRenderingEngineOverride(engine);
            RefreshPerfStats();
        }
        catch (Exception ex)
        {
            if (_perfEngineText != null)
            {
                _perfEngineText.Text = $"Engine: switch failed — {ex.Message}";
                _perfEngineText.Foreground = DevToolsTheme.Error;
            }
        }
    }

    private void RefreshPerfStats()
    {
        if (_perfBackendText == null) return;
        _perfBackendText.Text = $"Backend  {_targetWindow.CurrentRenderBackend}";
        if (_perfEngineText != null)
        {
            _perfEngineText.Text = $"Engine  {_targetWindow.CurrentRenderingEngine}";
            _perfEngineText.Foreground = DevToolsTheme.TextPrimary;
        }

        var currentEngine = _targetWindow.CurrentRenderingEngine;
        if (_perfEngineAuto != null)     _perfEngineAuto.IsActive     = currentEngine == RenderingEngine.Auto;
        if (_perfEngineVello != null)    _perfEngineVello.IsActive    = currentEngine == RenderingEngine.Vello;
        if (_perfEngineImpeller != null) _perfEngineImpeller.IsActive = currentEngine == RenderingEngine.Impeller;

        var history = _targetWindow.FrameHistory;
        var buffer = new FrameHistory.Sample[FrameHistory.Capacity];
        int count = history.CopyTo(buffer);
        if (count > 0 && _perfFpsText != null)
        {
            double totalMs = 0;
            double worst = 0;
            for (int i = 0; i < count; i++)
            {
                totalMs += buffer[i].TotalMs;
                if (buffer[i].TotalMs > worst) worst = buffer[i].TotalMs;
            }
            double avg = totalMs / count;
            double fps = avg > 0 ? 1000.0 / avg : 0;
            _perfFpsText.Text = $"FPS {fps:F1}   avg {avg:F1} ms   worst {worst:F1} ms";
            _perfFpsText.Foreground = worst > 16
                ? DevToolsTheme.Warning
                : fps >= 55 ? DevToolsTheme.Success : DevToolsTheme.Accent;
        }
        else if (_perfFpsText != null)
        {
            _perfFpsText.Text = "FPS — (enable F3 HUD to collect samples)";
            _perfFpsText.Foreground = DevToolsTheme.TextMuted;
        }

        if (_perfGraphImage != null)
            _perfGraphImage.Source = RenderPerfGraph(buffer.AsSpan(0, count));

        var snap = RenderDiagnostics.LatestGpuSnapshot;
        if (_perfGpuText != null)
        {
            if (snap == null)
            {
                _perfGpuText.Text = "GPU  no snapshot published\n"
                                  + "Ask the native backend to call RenderDiagnostics.PublishGpuSnapshot(...) once per frame.";
                _perfGpuText.Foreground = DevToolsTheme.TextMuted;
            }
            else
            {
                var cache = RenderDiagnostics.LatestPathCacheStats;
                var lines = new List<string>();

                // Last-frame breakdown — Layout / Render / Present from FrameHistory
                // versus Draw API total time. The gap between RenderMs and the
                // Draw-API sum is "render-but-not-Draw-API" cost: command recording
                // in RenderTargetDrawingContext, ImageBrush tile placement, brush
                // marshaling, retained-mode cache lookups, etc. — anything between
                // BeginDraw and EndDraw that's NOT a single P/Invoke wrapped in
                // ApiStart/ApiEnd. A large gap means the bottleneck is on the
                // managed command-recording side, not the native draw calls.
                if (count > 0)
                {
                    var last = buffer[count - 1];
                    var apiSnapForGap = RenderDiagnostics.LatestDrawApiStats;
                    double apiMs = apiSnapForGap?.TotalMs ?? 0;
                    double renderGap = last.RenderMs - apiMs;
                    lines.Add($"Last frame  L {last.LayoutMs,5:F1}  R {last.RenderMs,5:F1}  P {last.PresentMs,5:F1}  total {last.TotalMs,5:F1} ms");
                    lines.Add($"            Draw API total  {apiMs,5:F1} ms    gap {renderGap,5:F1} ms (managed recording / brush marshaling)");
                    lines.Add("");
                }

                lines.Add($"GPU snapshot  {snap.Timestamp:HH:mm:ss.fff}");
                lines.Add($"  Glyph atlas   {snap.GlyphAtlasSlotsUsed}/{snap.GlyphAtlasSlotsTotal} slots    {(snap.GlyphAtlasBytes / (1024.0 * 1024.0)):F2} MB");
                lines.Add($"  Path cache    {snap.PathCacheEntries} entries           {(snap.PathCacheBytes / (1024.0 * 1024.0)):F2} MB");
                lines.Add($"  Textures      {snap.TextureCount}                        {(snap.TextureBytes / (1024.0 * 1024.0)):F2} MB");
                if (cache != null)
                {
                    long strokeTotal = cache.StrokeHits + cache.StrokeMisses;
                    long fillTotal   = cache.FillHits + cache.FillMisses;
                    long geomTotal   = cache.GeometryHits + cache.GeometryMisses;
                    double strokeHitPct = strokeTotal == 0 ? 0 : (double)cache.StrokeHits / strokeTotal * 100;
                    double fillHitPct   = fillTotal == 0 ? 0 : (double)cache.FillHits / fillTotal * 100;
                    double geomHitPct   = geomTotal == 0 ? 0 : (double)cache.GeometryHits / geomTotal * 100;
                    lines.Add("");
                    lines.Add("Path raster cache (per frame)");
                    lines.Add($"  Stroke   hit {cache.StrokeHits,4}  miss {cache.StrokeMisses,4}  ({strokeHitPct:F0}%)");
                    lines.Add($"  Fill     hit {cache.FillHits,4}  miss {cache.FillMisses,4}  ({fillHitPct:F0}%)");
                    if (geomTotal > 0)
                        lines.Add($"  Geometry hit {cache.GeometryHits,4}  miss {cache.GeometryMisses,4}  ({geomHitPct:F0}%)  evict {cache.CacheEvictions}");
                    if (cache.StrokeHits > 0)
                        lines.Add($"  Stroke rects/hit avg  {(cache.StrokeRectsTotal / (double)cache.StrokeHits):F0}");
                    if (cache.FillHits > 0)
                        lines.Add($"  Fill   rects/hit avg  {(cache.FillRectsTotal / (double)cache.FillHits):F0}");

                    long flattenCalls = strokeTotal - cache.StrokeHits + fillTotal - cache.FillHits + cache.GeometryMisses;
                    if (cache.FlattenNs > 0 || cache.TriangulateNs > 0)
                    {
                        lines.Add("");
                        lines.Add("Path CPU work (per frame)");
                        double flattenMs  = cache.FlattenNs    / 1_000_000.0;
                        double triangMs   = cache.TriangulateNs / 1_000_000.0;
                        double flattenAvgUs = flattenCalls > 0 ? cache.FlattenNs / 1000.0 / flattenCalls : 0;
                        long triangCalls = cache.TriangulateOk + cache.TriangulateFail;
                        double triangAvgUs = triangCalls > 0 ? cache.TriangulateNs / 1000.0 / triangCalls : 0;
                        lines.Add($"  Flatten     {flattenMs,6:F2} ms  avg {flattenAvgUs,5:F0} µs  verts {cache.FlattenOutputVerts}");
                        lines.Add($"  Triangulate {triangMs,6:F2} ms  avg {triangAvgUs,5:F0} µs  ok {cache.TriangulateOk}  fail {cache.TriangulateFail}");
                    }
                }
                var bmpStats = RenderDiagnostics.LatestBitmapUploadStats;
                if (bmpStats != null)
                {
                    long total = bmpStats.UploadCount + bmpStats.FastPathHits;
                    double fastPct = total == 0 ? 0 : (double)bmpStats.FastPathHits / total * 100;
                    lines.Add("");
                    lines.Add("Bitmap upload (per frame)");
                    lines.Add($"  Uploads  {bmpStats.UploadCount,4}  ({(bmpStats.UploadBytes / (1024.0 * 1024.0)):F2} MB,  {bmpStats.DynamicReuses} reuses)");
                    lines.Add($"  FastPath {bmpStats.FastPathHits,4}  ({fastPct:F0}% of DrawBitmap calls)");
                    lines.Add($"  Memcmp   {bmpStats.MemcmpShortCircuits,4}  short-circuits (Set/Update with same content)");
                    string residentSign = bmpStats.GpuResidentBytes >= 0 ? "+" : "";
                    lines.Add($"  GpuRes   {residentSign}{(bmpStats.GpuResidentBytes / (1024.0 * 1024.0)):F2} MB  net delta this frame");
                    if (bmpStats.CacheEvictions != 0)
                        lines.Add($"  Cache    {bmpStats.CacheEvictions,4} evictions");
                }
                var retCache = RenderDiagnostics.LatestRetainedCacheStats;
                if (retCache != null)
                {
                    long retTotal = retCache.Records + retCache.Replays + retCache.Bypasses;
                    double replayPct = retTotal == 0 ? 0 : (double)retCache.Replays / retTotal * 100;
                    lines.Add("");
                    lines.Add("Retained-mode cache (per frame)");
                    lines.Add($"  Records  {retCache.Records,5}  (cache miss / dirty / first time)");
                    lines.Add($"  Replays  {retCache.Replays,5}  ({replayPct:F0}% — the win)");
                    lines.Add($"  Bypass   {retCache.Bypasses,5}  (host off / opted out / non-cacheable DC)");
                }
                _perfGpuText.Text = string.Join('\n', lines);
                _perfGpuText.Foreground = DevToolsTheme.TextPrimary;
            }
        }

        // ── Draw-API per-frame counters ──
        var apiSnap = RenderDiagnostics.LatestDrawApiStats;
        if (_perfApiStatsText != null)
        {
            if (apiSnap == null || apiSnap.Entries.Count == 0)
            {
                _perfApiStatsText.Text = "Draw API stats: (waiting for next frame…)";
                _perfApiStatsText.Foreground = DevToolsTheme.TextMuted;
            }
            else
            {
                // Header + sorted entries (hot-first). One row per native draw API,
                // showing call count and total wall-clock time spent inside the
                // managed wrapper + native call. Column widths chosen so the
                // values line up under a fixed-width font in the bottom panel.
                var sb = new System.Text.StringBuilder(256 + apiSnap.Entries.Count * 80);
                sb.Append("Draw APIs  ").Append(apiSnap.Timestamp.ToString("HH:mm:ss.fff"))
                  .Append("    last frame total ")
                  .AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", apiSnap.TotalMs)
                  .Append(" ms\n");
                sb.Append("  ").Append("API".PadRight(28))
                  .Append("calls".PadLeft(8))
                  .Append("total ms".PadLeft(12))
                  .Append("avg µs".PadLeft(10))
                  .Append('\n');
                sb.Append("  ").Append(new string('-', 58)).Append('\n');
                foreach (var e in apiSnap.Entries)
                {
                    sb.Append("  ").Append(e.Name.PadRight(28))
                      .Append(e.Count.ToString().PadLeft(8))
                      .AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0,12:F2}", e.TotalMs)
                      .AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0,10:F1}", e.AvgUs)
                      .Append('\n');
                }
                _perfApiStatsText.Text = sb.ToString();
                _perfApiStatsText.Foreground = DevToolsTheme.TextPrimary;
            }
        }
    }

    private static readonly Color PerfColorLayout  = Color.FromRgb(0x5A, 0xB0, 0xFF);
    private static readonly Color PerfColorRender  = Color.FromRgb(0xE5, 0xB0, 0x5A);
    private static readonly Color PerfColorPresent = Color.FromRgb(0x6C, 0xC3, 0x78);
    private static readonly Color PerfColorBudget  = Color.FromArgb(0xA0, 0xF0, 0x6C, 0x6C);

    // Reused across refreshes — ContentRevision bookkeeping (WriteableBitmap.cs)
    // triggers a native re-upload whenever we write, so we don't need to throw
    // away and re-allocate a new bitmap + native texture each tick.
    private WriteableBitmap? _perfGraphBitmap;
    private byte[]? _perfGraphPixels;

    private ImageSource? RenderPerfGraph(ReadOnlySpan<FrameHistory.Sample> samples)
    {
        // Size bitmap to the control's physical pixel dimensions — Image.Stretch.Fill
        // then becomes a 1:1 blit instead of a ~3× upscale, which is what was
        // turning our single-pixel-wide bars into a soft orange smear.
        double dipW = _perfGraphImage?.RenderSize.Width ?? 0;
        double dipH = _perfGraphImage?.RenderSize.Height ?? 0;
        // First layout pass may not have placed the image yet — fall back to the
        // host border's size.
        if (dipW <= 1 || dipH <= 1)
        {
            dipW = _perfGraphHost?.RenderSize.Width ?? 0;
            dipH = _perfGraphHost?.RenderSize.Height ?? 0;
        }
        // Scale to physical pixels so high-DPI monitors also render sharply.
        double dpiScale = GetDevicePixelScale();
        int width = Math.Max(160, (int)Math.Round(dipW * dpiScale));
        int height = Math.Max(80, (int)Math.Round(dipH * dpiScale));

        if (_perfGraphBitmap == null || _perfGraphBitmap.PixelWidth != width || _perfGraphBitmap.PixelHeight != height)
        {
            _perfGraphBitmap = new WriteableBitmap(width, height, 96, 96, Jalium.UI.Media.PixelFormats.Pbgra32, null);
            _perfGraphPixels = new byte[width * height * 4];
        }
        var bitmap = _perfGraphBitmap!;
        var pixels = _perfGraphPixels!;

        // Background uses Chrome color for consistency with toolbars/cards.
        byte bgB = DevToolsTheme.ChromeColor.B;
        byte bgG = DevToolsTheme.ChromeColor.G;
        byte bgR = DevToolsTheme.ChromeColor.R;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = bgB;
            pixels[i + 1] = bgG;
            pixels[i + 2] = bgR;
            pixels[i + 3] = 255;
        }

        // Horizontal gridlines every quarter of the max range.
        byte gridLum = DevToolsTheme.BorderSubtleColor.R;
        for (int gy = 1; gy <= 3; gy++)
        {
            int y = height * gy / 4;
            for (int x = 0; x < width; x += 2)
                WritePixel(pixels, width, x, y, Color.FromArgb(0x60, gridLum, gridLum, gridLum));
        }

        if (samples.Length > 0)
        {
            double maxMs = 16.0;
            foreach (var s in samples)
                if (s.TotalMs > maxMs) maxMs = s.TotalMs;
            maxMs = Math.Max(maxMs, 1.0);

            // Map each sample to a contiguous column range so a 300-sample buffer
            // still covers the full physical width instead of leaving most of it
            // as background.
            double colsPerSample = (double)width / Math.Max(samples.Length, 1);
            for (int i = 0; i < samples.Length; i++)
            {
                int xStart = (int)Math.Round(i * colsPerSample);
                int xEnd = (int)Math.Round((i + 1) * colsPerSample);
                if (xEnd <= xStart) xEnd = xStart + 1;
                if (xEnd > width) xEnd = width;

                var s = samples[i];
                int lY = height - 1 - (int)((s.LayoutMs / maxMs) * (height - 2));
                int rY = lY - (int)((s.RenderMs / maxMs) * (height - 2));
                int pY = rY - (int)((s.PresentMs / maxMs) * (height - 2));
                lY = Math.Clamp(lY, 1, height - 1);
                rY = Math.Clamp(rY, 1, height - 1);
                pY = Math.Clamp(pY, 1, height - 1);

                for (int x = xStart; x < xEnd; x++)
                {
                    for (int y = height - 1; y >= lY; y--) WritePixel(pixels, width, x, y, PerfColorLayout);
                    for (int y = lY - 1; y >= rY; y--) WritePixel(pixels, width, x, y, PerfColorRender);
                    for (int y = rY - 1; y >= pY; y--) WritePixel(pixels, width, x, y, PerfColorPresent);
                }
            }

            // 16 ms budget line (dashed) — scale dash gap with pixel width so it
            // remains visually dashed at all zoom levels.
            int budgetY = height - 1 - (int)((16.0 / maxMs) * (height - 2));
            budgetY = Math.Clamp(budgetY, 1, height - 1);
            int dashStride = Math.Max(4, (int)(6 * dpiScale));
            for (int x = 0; x < width; x += dashStride)
                WritePixel(pixels, width, x, budgetY, PerfColorBudget);
        }

        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        return bitmap;
    }

    /// <summary>
    /// Device-to-DIP scale factor for the DevTools window. Falls back to 1.0
    /// when the window hasn't reported DPI yet.
    /// </summary>
    private double GetDevicePixelScale()
    {
        // DevToolsWindow inherits DPI state from the target window host. Use
        // the dispatcher-visible DpiScale if available, otherwise 1.0.
        try
        {
            double s = DpiScale;
            return s > 0.1 ? s : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private static void WritePixel(byte[] pixels, int width, int x, int y, Color c)
    {
        if ((uint)x >= (uint)width) return;
        int idx = (y * width + x) * 4;
        if ((uint)idx >= (uint)pixels.Length) return;
        pixels[idx + 0] = c.B;
        pixels[idx + 1] = c.G;
        pixels[idx + 2] = c.R;
        pixels[idx + 3] = c.A == 0 ? (byte)255 : c.A;
    }
}
