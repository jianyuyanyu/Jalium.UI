namespace Jalium.UI.Media;

/// <summary>
/// 把 SVG ArcSegment 展开成最多 4 段 cubic bezier,直接 append 到 commands buffer。
/// 算法:端点参数化 → 中心参数化 (SVG F.6.5-F.6.6) + 每段 ≤ π/2 用单位圆弧 4-bezier
/// 近似 (alpha = sin(da)·(√(4 + 3·tan²(da/2)) − 1) / 3,误差 &lt; 0.0003 单位长度)。
///
/// StreamGeometry.Compose 和 RenderTargetDrawingContext.DrawPathGeometry 都消费这个
/// helper —— 一份实现避免不一致。命令编码与 native API 注释一致:
///   tag 0 = LineTo   [0, x, y]
///   tag 1 = BezierTo [1, c1x, c1y, c2x, c2y, ex, ey]
/// </summary>
internal static class StreamGeometryArcHelper
{
    public static void AppendArcAsCubicBeziers(
        IList<float> cmds, Point start, ArcSegment arc, double ox, double oy)
    {
        var end = arc.Point;
        var rx = arc.Size.Width;
        var ry = arc.Size.Height;

        // 退化情况:零半径或起点终点重合,只画到 end 一条直线
        if (rx == 0 || ry == 0 || (start.X == end.X && start.Y == end.Y))
        {
            cmds.Add(0f);
            cmds.Add((float)(end.X + ox));
            cmds.Add((float)(end.Y + oy));
            return;
        }

        // 端点 → 中心参数化 (SVG spec F.6.5-F.6.6)
        var rotAngle = arc.RotationAngle * Math.PI / 180.0;
        var cosA = Math.Cos(rotAngle);
        var sinA = Math.Sin(rotAngle);

        var dx2 = (start.X - end.X) / 2.0;
        var dy2 = (start.Y - end.Y) / 2.0;
        var x1p = cosA * dx2 + sinA * dy2;
        var y1p = -sinA * dx2 + cosA * dy2;

        // 半径不足放大
        var x1pSq = x1p * x1p;
        var y1pSq = y1p * y1p;
        var rxSq = rx * rx;
        var rySq = ry * ry;
        var lambda = x1pSq / rxSq + y1pSq / rySq;
        if (lambda > 1)
        {
            var sqrtLam = Math.Sqrt(lambda);
            rx *= sqrtLam;
            ry *= sqrtLam;
            rxSq = rx * rx;
            rySq = ry * ry;
        }

        // 中心点
        var sign = (arc.IsLargeArc != (arc.SweepDirection == SweepDirection.Clockwise)) ? 1.0 : -1.0;
        var sq = Math.Max(0, (rxSq * rySq - rxSq * y1pSq - rySq * x1pSq) / (rxSq * y1pSq + rySq * x1pSq));
        var coef = sign * Math.Sqrt(sq);
        var cxp = coef * rx * y1p / ry;
        var cyp = -coef * ry * x1p / rx;
        var cx = cosA * cxp - sinA * cyp + (start.X + end.X) / 2.0;
        var cy = sinA * cxp + cosA * cyp + (start.Y + end.Y) / 2.0;

        // 起始/扫描角
        var startAngle = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
        var endAngle = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);
        var deltaAngle = endAngle - startAngle;

        if (arc.SweepDirection == SweepDirection.Clockwise && deltaAngle < 0)
            deltaAngle += 2 * Math.PI;
        else if (arc.SweepDirection == SweepDirection.Counterclockwise && deltaAngle > 0)
            deltaAngle -= 2 * Math.PI;

        // 切成最多 π/2 弧段,每段一个 cubic bezier
        int segCount = (int)Math.Ceiling(Math.Abs(deltaAngle) / (Math.PI / 2.0));
        segCount = Math.Max(1, segCount);
        var segAngle = deltaAngle / segCount;

        for (int i = 0; i < segCount; i++)
        {
            var a1 = startAngle + segAngle * i;
            var a2 = a1 + segAngle;

            var da = a2 - a1;
            var halfTan = Math.Tan(da / 2.0);
            var alpha = Math.Sin(da) * (Math.Sqrt(4 + 3 * halfTan * halfTan) - 1) / 3.0;

            var cos1 = Math.Cos(a1);
            var sin1 = Math.Sin(a1);
            var cos2 = Math.Cos(a2);
            var sin2 = Math.Sin(a2);

            var ep1x = rx * cos1;
            var ep1y = ry * sin1;
            var ep2x = rx * cos2;
            var ep2y = ry * sin2;

            var d1x = -rx * sin1;
            var d1y = ry * cos1;
            var d2x = -rx * sin2;
            var d2y = ry * cos2;

            var cp1x = ep1x + alpha * d1x;
            var cp1y = ep1y + alpha * d1y;
            var cp2x = ep2x - alpha * d2x;
            var cp2y = ep2y - alpha * d2y;

            // 旋转 + 平移
            var fcp1x = cosA * cp1x - sinA * cp1y + cx;
            var fcp1y = sinA * cp1x + cosA * cp1y + cy;
            var fcp2x = cosA * cp2x - sinA * cp2y + cx;
            var fcp2y = sinA * cp2x + cosA * cp2y + cy;
            var fep2x = cosA * ep2x - sinA * ep2y + cx;
            var fep2y = sinA * ep2x + cosA * ep2y + cy;

            cmds.Add(1f); // BezierTo
            cmds.Add((float)(fcp1x + ox));
            cmds.Add((float)(fcp1y + oy));
            cmds.Add((float)(fcp2x + ox));
            cmds.Add((float)(fcp2y + oy));
            cmds.Add((float)(fep2x + ox));
            cmds.Add((float)(fep2y + oy));
        }
    }
}
