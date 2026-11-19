using XDesigner.Core.Editing;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Layout;

/// <summary>A guide line to draw while snapping (frame DIPs). Vertical when <see cref="X1"/> == <see cref="X2"/>.</summary>
/// <param name="IsSpacing">True for the standard-gap guides (drawn dashed), false for edge/centre alignment.</param>
public sealed record SnapLine(double X1, double Y1, double X2, double Y2, bool IsSpacing);

/// <summary>A snapped rectangle and the guides that explain it.</summary>
public sealed record SnapResult(RectDto Bounds, IReadOnlyList<SnapLine> Lines);

/// <summary>
/// Snaplines (§7.3): while moving or resizing, the moving edges snap to sibling edges and centres, the container's
/// content edges, and a standard 8-DIP gap from siblings and the container, within a small threshold. X and Y snap
/// independently; the nearest candidate wins.
/// </summary>
public static class SnapEngine
{
    /// <summary>Standard spacing between controls, and between a control and its container's edge.</summary>
    public const double Spacing = 8;

    /// <summary>Snap distance in screen units; divide by zoom to get DIPs.</summary>
    public const double ThresholdScreen = 6;

    /// <param name="moving">Proposed bounds of the element being moved/resized.</param>
    /// <param name="edges">For a resize, the edges being dragged; <see cref="ResizeEdges.None"/> means a move (all edges and centres snap together).</param>
    /// <param name="siblings">Bounds of the other elements in the same container.</param>
    /// <param name="container">The container's content box (bounds minus padding).</param>
    /// <param name="threshold">Snap distance in DIPs.</param>
    public static SnapResult Snap(RectDto moving, ResizeEdges edges, IReadOnlyList<RectDto> siblings, RectDto container, double threshold)
    {
        var lines = new List<SnapLine>();
        var isMove = edges == ResizeEdges.None;

        // ---- X ----
        var xCandidates = new List<(double Value, bool Spacing, RectDto? Other)>
        {
            (container.X, false, null), (container.Right, false, null), (container.X + container.Width / 2, false, null),
            (container.X + Spacing, true, null), (container.Right - Spacing, true, null),
        };
        foreach (var s in siblings)
        {
            xCandidates.AddRange([(s.X, false, s), (s.Right, false, s), (s.X + s.Width / 2, false, s), (s.Right + Spacing, true, s), (s.X - Spacing, true, s)]);
        }

        var xEdges = new List<(double Value, Func<double, RectDto> Apply)>();
        if (isMove || edges.HasFlag(ResizeEdges.Left))
        {
            xEdges.Add((moving.X, v => isMove ? moving with { X = v } : moving with { X = v, Width = moving.Right - v }));
        }

        if (isMove || edges.HasFlag(ResizeEdges.Right))
        {
            xEdges.Add((moving.Right, v => isMove ? moving with { X = v - moving.Width } : moving with { Width = v - moving.X }));
        }

        if (isMove)
        {
            xEdges.Add((moving.X + moving.Width / 2, v => moving with { X = v - moving.Width / 2 }));
        }

        var bestX = Best(xEdges, xCandidates, threshold);
        if (bestX is { } bx)
        {
            moving = bx.Apply(bx.Candidate.Value);
        }

        // ---- Y ----
        var yCandidates = new List<(double Value, bool Spacing, RectDto? Other)>
        {
            (container.Y, false, null), (container.Bottom, false, null), (container.Y + container.Height / 2, false, null),
            (container.Y + Spacing, true, null), (container.Bottom - Spacing, true, null),
        };
        foreach (var s in siblings)
        {
            yCandidates.AddRange([(s.Y, false, s), (s.Bottom, false, s), (s.Y + s.Height / 2, false, s), (s.Bottom + Spacing, true, s), (s.Y - Spacing, true, s)]);
        }

        var yEdges = new List<(double Value, Func<double, RectDto> Apply)>();
        if (isMove || edges.HasFlag(ResizeEdges.Top))
        {
            yEdges.Add((moving.Y, v => isMove ? moving with { Y = v } : moving with { Y = v, Height = moving.Bottom - v }));
        }

        if (isMove || edges.HasFlag(ResizeEdges.Bottom))
        {
            yEdges.Add((moving.Bottom, v => isMove ? moving with { Y = v - moving.Height } : moving with { Height = v - moving.Y }));
        }

        if (isMove)
        {
            yEdges.Add((moving.Y + moving.Height / 2, v => moving with { Y = v - moving.Height / 2 }));
        }

        var bestY = Best(yEdges, yCandidates, threshold);
        if (bestY is { } by)
        {
            moving = by.Apply(by.Candidate.Value);
        }

        // Guides, drawn against the final rectangle so they line up with what the user sees.
        if (bestX is { } gx)
        {
            var v = gx.Candidate.Value;
            var other = gx.Candidate.Other ?? container;
            var top = Math.Min(moving.Y, other.Y);
            var bottom = Math.Max(moving.Bottom, other.Bottom);
            lines.Add(new SnapLine(v, top, v, bottom, gx.Candidate.Spacing));
        }

        if (bestY is { } gy)
        {
            var v = gy.Candidate.Value;
            var other = gy.Candidate.Other ?? container;
            var left = Math.Min(moving.X, other.X);
            var right = Math.Max(moving.Right, other.Right);
            lines.Add(new SnapLine(left, v, right, v, gy.Candidate.Spacing));
        }

        return new SnapResult(moving, lines);
    }

    /// <summary>
    /// Grid snapping (§7.3, optional): a move puts the top-left corner on the grid; a resize puts the dragged edges on it.
    /// The grid starts at the container's content corner. Sizes never drop below one grid step.
    /// </summary>
    public static RectDto SnapToGrid(RectDto moving, ResizeEdges edges, RectDto container, double size)
    {
        if (size <= 0)
        {
            return moving;
        }

        double RoundX(double x) => container.X + (Math.Round((x - container.X) / size) * size);
        double RoundY(double y) => container.Y + (Math.Round((y - container.Y) / size) * size);

        if (edges == ResizeEdges.None)
        {
            return moving with { X = RoundX(moving.X), Y = RoundY(moving.Y) };
        }

        var left = edges.HasFlag(ResizeEdges.Left) ? Math.Min(RoundX(moving.X), moving.Right - size) : moving.X;
        var right = edges.HasFlag(ResizeEdges.Right) ? Math.Max(RoundX(moving.Right), left + size) : moving.Right;
        var top = edges.HasFlag(ResizeEdges.Top) ? Math.Min(RoundY(moving.Y), moving.Bottom - size) : moving.Y;
        var bottom = edges.HasFlag(ResizeEdges.Bottom) ? Math.Max(RoundY(moving.Bottom), top + size) : moving.Bottom;
        return new RectDto(left, top, right - left, bottom - top);
    }

    private static ((double Value, bool Spacing, RectDto? Other) Candidate, Func<double, RectDto> Apply)? Best(
        List<(double Value, Func<double, RectDto> Apply)> edges,
        List<(double Value, bool Spacing, RectDto? Other)> candidates,
        double threshold)
    {
        ((double, bool, RectDto?) Candidate, Func<double, RectDto> Apply)? best = null;
        var bestDistance = threshold;
        foreach (var (edge, apply) in edges)
        {
            foreach (var candidate in candidates)
            {
                var distance = Math.Abs(edge - candidate.Value);
                // Strictly nearer wins; on a tie the earlier candidate stays (alignment candidates are listed before spacing ones).
                if (distance < bestDistance || (best is null && distance <= bestDistance))
                {
                    bestDistance = distance;
                    best = (candidate, apply);
                }
            }
        }

        return best;
    }
}
