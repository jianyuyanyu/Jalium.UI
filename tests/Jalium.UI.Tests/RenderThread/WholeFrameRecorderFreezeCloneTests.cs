using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Media.Rendering;
using Jalium.UI.Rendering;
using Xunit;
using Drawing = Jalium.UI.Media.Rendering.RecordedDrawing;

namespace Jalium.UI.Tests.RenderThread;

/// <summary>
/// Increment-3 (freeze-clone) verification: on the whole-frame capture path,
/// mutable live draw inputs are snapshotted at record time, so a later
/// UI-thread mutation cannot corrupt the captured Drawing replayed on the
/// render thread. Immutable / pooled inputs pass through untouched.
/// </summary>
public sealed class WholeFrameRecorderFreezeCloneTests : System.IDisposable
{
    // Guard against a mid-record assertion leaking s_wholeFrameRecording=true into a
    // sibling test (FinishRecord exits the scope on the happy path).
    public void Dispose() => DrawingContext.EndWholeFrameRecordingScope((false, false));

    [Fact]
    public void Transform_IsSnapshotted_ImmuneToLaterMutation()
    {
        var scale = new ScaleTransform { ScaleX = 2, ScaleY = 2 };

        var host = new MediaRenderCacheHost();
        var recorder = host.CreateFrameRecorder()!;
        recorder.PushTransform(scale);
        recorder.Pop();
        var drawing = (Drawing)host.FinishRecord(recorder);

        scale.ScaleX = 99;   // mutate AFTER record — must not leak into the snapshot
        scale.ScaleY = 99;

        var sink = new RecordingRenderSink();
        host.Replay(drawing, sink);

        var push = Assert.Single(sink.Events, e => e.StartsWith("PushTransform"));
        Assert.Contains("[2,", push);          // record-time scale preserved
        Assert.DoesNotContain("99", push);     // mutation did not leak
    }

    [Fact]
    public void GradientBrush_IsSnapshotted_ImmuneToLaterStopMutation()
    {
        var grad = new LinearGradientBrush(
            Color.FromArgb(255, 255, 0, 0),
            Color.FromArgb(255, 0, 0, 255), 0);

        var host = new MediaRenderCacheHost();
        var recorder = host.CreateFrameRecorder()!;
        recorder.DrawRectangle(grad, null, new Rect(0, 0, 10, 10));
        var drawing = (Drawing)host.FinishRecord(recorder);

        grad.GradientStops[0].Color = Color.FromArgb(255, 0, 255, 0);  // red → green AFTER record

        var sink = new RecordingRenderSink();
        host.Replay(drawing, sink);

        var draw = Assert.Single(sink.Events, e => e.StartsWith("DrawRectangle"));
        Assert.Contains("FFFF0000", draw);        // original red stop preserved
        Assert.DoesNotContain("FF00FF00", draw);  // mutated green did not leak
    }

    [Fact]
    public void NestedVisualDrawing_GradientIsSnapshottedBeforeCrossThreadPublication()
    {
        MediaRenderCacheHost.Bootstrap();
        var gradient = new LinearGradientBrush(
            Color.FromArgb(255, 255, 0, 0),
            Color.FromArgb(255, 0, 0, 255),
            0);
        var visual = new Border
        {
            Width = 40,
            Height = 20,
            Background = gradient,
        };
        visual.Measure(new Size(40, 20));
        visual.Arrange(new Rect(0, 0, 40, 20));

        var host = new MediaRenderCacheHost();
        var recorder = host.CreateFrameRecorder()!;
        visual.Render(recorder);
        var drawing = (Drawing)host.FinishRecord(recorder);

        gradient.GradientStops[0].Color = Color.FromArgb(255, 0, 255, 0);

        var sink = new RecordingRenderSink();
        host.Replay(drawing, sink);

        var draw = Assert.Single(sink.Events, entry => entry.StartsWith("DrawRoundedRectangle"));
        Assert.Contains("FFFF0000", draw);
        Assert.DoesNotContain("FF00FF00", draw);
    }

    [Fact]
    public async Task NestedVisualDrawing_PathGeometryIsFrozenBeforeCrossThreadPublication()
    {
        MediaRenderCacheHost.Bootstrap();
        var source = (PathGeometry)Geometry.Parse("M0,0 L20,0 L20,10 Z");
        var visual = new GeometryRenderElement(source)
        {
            Width = 20,
            Height = 10,
        };
        visual.Measure(new Size(20, 10));
        visual.Arrange(new Rect(0, 0, 20, 10));

        // Prime the retained drawing through the inline/cacheable path first,
        // matching Window's pre-show frame before the D3D12 worker starts.
        visual.Render(new InlineCacheSink());

        var host = new MediaRenderCacheHost();
        var recorder = host.CreateFrameRecorder()!;
        visual.Render(recorder);
        var drawing = (Drawing)host.FinishRecord(recorder);

        var node = Assert.Single(
            drawing.Commands,
            command => command.Kind == DrawCommandKind.DrawRecordedDrawing);
        var nested = (Drawing)node.A!;
        var geometryCommand = Assert.Single(
            nested.Commands,
            command => command.Kind == DrawCommandKind.DrawGeometry);
        var snapshot = (Geometry)geometryCommand.C!;

        Assert.NotSame(source, snapshot);
        Assert.True(snapshot.IsFrozen);

        // Mutate the dispatcher-owned source after publication. Replaying on a
        // worker must read only the frozen snapshot and therefore neither throw
        // nor observe the mutation.
        source.Clear();
        var sink = new RecordingRenderSink();
        await Task.Run(() => host.Replay(drawing, sink));

        Assert.Single(sink.Events, entry => entry.StartsWith("DrawGeometry"));
    }

    [Fact]
    public void GradientSnapshot_IsStable_AcrossUnchangedFrames()
    {
        // Native brush cache is keyed by managed identity, so an UNCHANGED
        // gradient must yield the SAME snapshot instance across frames (else
        // every frame would miss the native cache and rebuild the gradient).
        var grad = new LinearGradientBrush(
            Color.FromArgb(255, 1, 2, 3),
            Color.FromArgb(255, 4, 5, 6), 0);

        var host = new MediaRenderCacheHost();

        var r1 = host.CreateFrameRecorder()!;
        r1.DrawRectangle(grad, null, new Rect(0, 0, 10, 10));
        var d1 = (Drawing)host.FinishRecord(r1);

        var r2 = host.CreateFrameRecorder()!;
        r2.DrawRectangle(grad, null, new Rect(0, 0, 10, 10));
        var d2 = (Drawing)host.FinishRecord(r2);

        Assert.Same(d1.Commands[0].A, d2.Commands[0].A);  // same memoised snapshot
    }

    [Fact]
    public void SolidColorBrush_PassesThrough_NotOverCloned()
    {
        // Solids are already pooled value copies; the snapshot must return them
        // unchanged so the native brush identity cache keeps hitting.
        var host = new MediaRenderCacheHost();
        var recorder = host.CreateFrameRecorder()!;
        var solid = new SolidColorBrush { Color = Color.FromArgb(255, 10, 20, 30) };
        recorder.DrawRectangle(solid, null, new Rect(0, 0, 10, 10));
        var drawing = (Drawing)host.FinishRecord(recorder);

        var sink = new RecordingRenderSink();
        host.Replay(drawing, sink);

        Assert.Contains(sink.Events, e => e.Contains("#FF0A141E"));  // color preserved
    }

    [Fact]
    public void GradientScalarAnimation_NotFrozen_ReSnapshotsAcrossFrames()
    {
        // Regression for the memo-stale bug (reviewer3#3): gradient scalar setters
        // (StartPoint/EndPoint/Center/Radius/Opacity) do NOT call InvalidateContentHash,
        // so a CACHED ComputeContentHash would freeze an animated gradient on the
        // render-thread path. SnapshotGradient now keys on the UNCACHED
        // ComputeContentHashCore, so an animated StartPoint must re-snapshot each frame.
        var grad = new LinearGradientBrush(
            Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 0, 0, 255), 0)
        { StartPoint = new Point(0, 0) };
        var host = new MediaRenderCacheHost();

        var r1 = host.CreateFrameRecorder()!;
        r1.DrawRectangle(grad, null, new Rect(0, 0, 10, 10));
        var d1 = (Drawing)host.FinishRecord(r1);
        var snap1 = d1.Commands[0].A;

        grad.StartPoint = new Point(1, 1);   // scalar animation (no GradientStops.Changed)

        var r2 = host.CreateFrameRecorder()!;
        r2.DrawRectangle(grad, null, new Rect(0, 0, 10, 10));
        var d2 = (Drawing)host.FinishRecord(r2);
        var snap2 = d2.Commands[0].A;

        Assert.NotSame(snap1, snap2);   // re-snapshotted (Same == frozen — the pre-fix bug)
        Assert.Equal(new Point(1, 1), ((LinearGradientBrush)snap2!).StartPoint);
    }

    private sealed class GeometryRenderElement(PathGeometry geometry) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext)
            => drawingContext.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(255, 20, 40, 60)),
                null,
                geometry);
    }

    private sealed class InlineCacheSink : RecordingRenderSink, ICacheableDrawingContext
    {
    }
}
