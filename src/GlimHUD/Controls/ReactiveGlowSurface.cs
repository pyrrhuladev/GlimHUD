using System.Windows;
using System.Windows.Media;
using GlimHUD.Models;

namespace GlimHUD.Controls;

public enum GlowLayer
{
    Outer,
    Inner,
    Both
}

/// <summary>
/// An RGB wave traveling by distance around the entire capsule perimeter. The
/// outer layer belongs in the click-through companion; the clipped inner layer
/// belongs underneath the panel background and content. It owns no animation.
/// Allow 23 local DIPs around an outer layer so its halo is never clipped.
/// </summary>
public sealed class ReactiveGlowSurface : FrameworkElement
{
    private const int PaletteSize = 768;
    private static readonly Brush[] Rainbow = CreatePalette();
    private static readonly Brush[] Neon = CreateNeonPalette();
    private static readonly LinearGradientBrush[] WashPalette = CreateWashPalette(Rainbow);
    private static readonly LinearGradientBrush[] NeonWashPalette = CreateWashPalette(Neon);
    private static readonly double[] BloomPositions = [0, .065, .14, .26, .43, .65, .84, 1];
    private static readonly double[] BloomAlpha = [.9, .83, .52, .28, .13, .045, .012, 0];
    private static readonly double[] InsidePositions = [0, .06, .14, .28, .48, .72, 1];
    private static readonly double[] InsideAlpha = [.85, .60, .36, .18, .08, .02, 0];
    private static readonly double[] OuterDistances = [0, 1.8, 3.2, 5, 7.5, 10.5, 14, 18, 22];
    private static readonly double[] OuterAlpha = [1, .54, .36, .245, .16, .095, .05, .018];
    private static readonly double[] InnerFractions = [0, .055, .12, .22, .36, .53, .75, 1];
    private static readonly double[] InnerAlpha = [.95, .70, .50, .35, .23, .13, .035];
    private Geometry[][]? _outerRings;
    private Geometry[][]? _innerRings;
    private Geometry? _outerClip;
    private Geometry? _innerClip;
    private Geometry[]? _outerStrips;
    private Geometry[]? _innerStrips;
    private Geometry[]? _outerCore;
    private Geometry[]? _innerCore;
    private MatrixTransform[]? _outerTransforms;
    private MatrixTransform[]? _innerTransforms;
    private DrawingBrush[]? _outerMasks;
    private DrawingBrush? _innerMask;
    private Size _geometrySize;
    private Rect _capsule;
    private double _radius;
    private double _perimeter;
    private int _segments;

    // Kept for callers that also style the panel's accent. Rainbow emission has
    // its own full spectrum, independent of the chosen UI accent color.
    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor), typeof(Color), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(Color.FromRgb(101, 221, 242)));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, Normalize));

    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
        nameof(Intensity), typeof(double), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(.65d, FrameworkPropertyMetadataOptions.AffectsRender, null, Normalize));

    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(double), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, NormalizePhase));

    public static readonly DependencyProperty LayerProperty = DependencyProperty.Register(
        nameof(Layer), typeof(GlowLayer), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(GlowLayer.Outer, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is GlowLayer layer && Enum.IsDefined(layer));

    public static readonly DependencyProperty EffectStyleProperty = DependencyProperty.Register(
        nameof(EffectStyle), typeof(string), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(GlowStyles.RainbowWave, FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => GlowStyles.Normalize(value as string)));

    public static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(double), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, Normalize));

    public static readonly DependencyProperty PulsePositionProperty = DependencyProperty.Register(
        nameof(PulsePosition), typeof(double), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, NormalizePhase));

    public Color GlowColor { get => (Color)GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Intensity { get => (double)GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public GlowLayer Layer { get => (GlowLayer)GetValue(LayerProperty); set => SetValue(LayerProperty, value); }
    public string EffectStyle { get => (string)GetValue(EffectStyleProperty); set => SetValue(EffectStyleProperty, value); }
    public double Pulse { get => (double)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }
    public double PulsePosition { get => (double)GetValue(PulsePositionProperty); set => SetValue(PulsePositionProperty, value); }

    public ReactiveGlowSurface()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
    }

    private static object Normalize(DependencyObject owner, object value)
        => value is double number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : 0d;

    private static object NormalizePhase(DependencyObject owner, object value)
    {
        if (value is not double number || !double.IsFinite(number)) return 0d;
        var phase = number - Math.Floor(number);
        // Subtracting a tiny negative value from -1 can round up to exactly one.
        return phase >= 1 ? 0d : phase;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var strength = Level * Intensity;
        if (strength <= 0 || ActualWidth < 2 || ActualHeight < 2) return;
        EnsureGeometry();
        var phaseOffset = (int)(Phase * PaletteSize);
        if (EffectStyle == GlowStyles.NeonFlow)
        {
            EnsureSmoothGeometry();
            RenderNeon(drawing, phaseOffset, strength);
            return;
        }
        if (Layer is GlowLayer.Inner or GlowLayer.Both)
        {
            // A continuous wash bridges opposite perimeter hues at the medial
            // line instead of drawing a sharp top/bottom color boundary.
            drawing.PushOpacity(strength * .24);
            drawing.DrawGeometry(WashPalette[phaseOffset], null, _innerClip!);
            drawing.Pop();
            DrawRings(drawing, _innerRings!, InnerAlpha, _innerClip!, phaseOffset, strength);
        }
        if (Layer is GlowLayer.Outer or GlowLayer.Both)
        {
            DrawRings(drawing, _outerRings!, OuterAlpha, _outerClip!, phaseOffset, strength, true);
            if (Pulse > 0)
            {
                EnsureSmoothGeometry();
                DrawSmooth(drawing, false, null, phaseOffset, strength, true);
                DrawCore(drawing, false, strength, false);
            }
        }
    }

    private void DrawRings(DrawingContext drawing, Geometry[][] rings, double[] alpha,
        Geometry clip, int phaseOffset, double strength, bool exterior = false)
    {
        var pulse = Math.Sqrt(Pulse);
        drawing.PushClip(clip);
        for (var ring = rings.Length - 1; ring >= 0; ring--)
        {
            drawing.PushOpacity(Math.Min(1, strength * alpha[ring]
                * (1 + (exterior ? pulse * 1.5 * ring / (rings.Length - 1) : 0))));
            for (var segment = 0; segment < _segments; segment++)
            {
                var colorIndex = (phaseOffset + segment * PaletteSize / _segments) % PaletteSize;
                drawing.DrawGeometry(Rainbow[colorIndex], null, rings[ring][segment]);
            }
            drawing.Pop();
        }
        drawing.Pop();
    }

    private void RenderNeon(DrawingContext drawing, int phaseOffset, double strength)
    {
        if (Layer is GlowLayer.Inner or GlowLayer.Both)
        {
            drawing.PushOpacity(strength * .25);
            drawing.DrawGeometry(NeonWashPalette[phaseOffset], null, _innerClip!);
            drawing.Pop();
            DrawSmooth(drawing, true, Neon, phaseOffset, strength);
            DrawCore(drawing, true, strength, true);
        }
        if (Layer is GlowLayer.Outer or GlowLayer.Both)
        {
            DrawSmooth(drawing, false, Neon, phaseOffset, strength);
            DrawCore(drawing, false, strength, true);
        }
    }

    private void DrawSmooth(DrawingContext drawing, bool inner, Brush[]? palette,
        int phaseOffset, double strength, bool highlightsOnly = false)
    {
        var strips = inner ? _innerStrips! : _outerStrips!;
        var transforms = inner ? _innerTransforms! : _outerTransforms!;
        // Typical detected hits are below one. A perceptual response makes
        // those hits readable while leaving zero-pulse styling unchanged.
        var pulse = Math.Sqrt(Pulse);
        drawing.PushClip(inner ? _innerClip! : _outerClip!);
        drawing.PushOpacity(Math.Min(1, strength * (1 + pulse * .20)));
        // Composite the color field before applying one shared capsule falloff.
        // Segment joins cannot accumulate translucent alpha into radial spikes.
        drawing.PushOpacityMask(inner ? _innerMask! : _outerMasks![(int)Math.Round(pulse * 32)]);
        for (var segment = 0; segment < _segments; segment++)
        {
            drawing.PushTransform(transforms[segment]);
            if (!highlightsOnly && palette is not null)
                drawing.DrawGeometry(palette[(phaseOffset + segment * PaletteSize / _segments) % PaletteSize],
                    null, strips[segment]);
            if (Pulse > 0)
            {
                var highlight = pulse * CometWeight((segment + .5) / _segments);
                drawing.PushOpacity(highlight * (inner ? .42 : 1));
                drawing.DrawGeometry(Brushes.White, null, strips[segment]);
                drawing.Pop();
            }
            drawing.Pop();
        }
        drawing.Pop();
        drawing.Pop();
        drawing.Pop();
    }

    private void DrawCore(DrawingContext drawing, bool inner, double strength, bool continuous)
    {
        var core = inner ? _innerCore! : _outerCore!;
        var baseOpacity = continuous ? Math.Pow(strength, .30) * (inner ? .62 : .98) : 0;
        var pulse = Math.Sqrt(Pulse);
        drawing.PushClip(inner ? _innerClip! : _outerClip!);
        for (var segment = 0; segment < _segments; segment++)
        {
            var flash = pulse * Math.Sqrt(strength)
                * (.08 + .86 * CometWeight((segment + .5) / _segments));
            drawing.PushOpacity(Math.Min(1, baseOpacity + flash));
            drawing.DrawGeometry(Brushes.White, null, core[segment]);
            drawing.Pop();
        }
        drawing.Pop();
    }

    private double CometWeight(double position)
    {
        var first = Weight(position - PulsePosition);
        var opposite = Weight(position - PulsePosition - .5);
        return Math.Min(1, Math.Max(first, opposite));

        static double Weight(double delta)
        {
            delta -= Math.Floor(delta + .5);
            var head = Math.Exp(-Math.Pow(delta / .035, 2));
            var tail = delta <= 0 ? .7 * Math.Exp(delta / .09) : 0;
            return Math.Max(head, tail);
        }
    }

    private void EnsureSmoothGeometry()
    {
        if (_outerStrips is not null) return;
        _outerStrips = new Geometry[_segments];
        _innerStrips = new Geometry[_segments];
        _innerTransforms = new MatrixTransform[_segments];
        _outerTransforms = new MatrixTransform[_segments];
        _outerMasks = new DrawingBrush[33];
        for (var width = 0; width < _outerMasks.Length; width++)
            _outerMasks[width] = CreateCapsuleMask(16 + 6 * width / 32d, false);
        _innerMask = CreateCapsuleMask(_radius, true);
        var step = _perimeter / _segments;
        for (var segment = 0; segment < _segments; segment++)
        {
            var middle = OffsetPoint(_capsule, _radius, _perimeter, (segment + .5) * step, 0);
            var normal = OffsetPoint(_capsule, _radius, _perimeter, (segment + .5) * step, 1) - middle;
            var tangent = new Vector(-normal.Y, normal.X);
            _outerStrips[segment] = BuildSmoothStrip(segment, step, 22, middle, normal, tangent);
            _innerStrips[segment] = BuildSmoothStrip(segment, step, -_radius, middle, normal, tangent);
            _innerTransforms[segment] = MakeTransform(-_radius);
            _outerTransforms[segment] = MakeTransform(22);

            MatrixTransform MakeTransform(double depth)
            {
                var transform = new MatrixTransform(tangent.X, tangent.Y,
                    normal.X * depth, normal.Y * depth, middle.X, middle.Y);
                transform.Freeze();
                return transform;
            }
        }
        _outerCore = BuildRings(_capsule, _radius, _perimeter, [0, 1.4])[0];
        _innerCore = BuildRings(_capsule, _radius, _perimeter, [0, -.8])[0];
    }

    private Geometry BuildSmoothStrip(int segment, double step, double depth,
        Point middle, Vector normal, Vector tangent)
    {
        var start = segment * step - .25;
        var end = (segment + 1) * step + .25;
        var subdivisions = Math.Max(2, (int)Math.Ceiling((end - start) / 2));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(Local(start, 0), true, true);
            for (var index = 1; index <= subdivisions; index++)
                path.LineTo(Local(start + (end - start) * index / subdivisions, 0), true, false);
            for (var index = subdivisions; index >= 0; index--)
                path.LineTo(Local(start + (end - start) * index / subdivisions, depth), true, false);
        }
        geometry.Freeze();
        return geometry;

        Point Local(double distance, double offset)
        {
            var vector = OffsetPoint(_capsule, _radius, _perimeter, distance, offset) - middle;
            return new Point(Vector.Multiply(vector, tangent), Vector.Multiply(vector, normal) / depth);
        }
    }

    private void EnsureGeometry()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (_outerRings is not null && _geometrySize == size) return;
        _geometrySize = size;
        var capsule = new Rect(.5, .5, ActualWidth - 1, ActualHeight - 1);
        var radius = Math.Min(capsule.Width, capsule.Height) / 2;
        var perimeter = 2 * (capsule.Width + capsule.Height - 4 * radius) + 2 * Math.PI * radius;
        _capsule = capsule;
        _radius = radius;
        _perimeter = perimeter;
        _outerStrips = _innerStrips = _outerCore = _innerCore = null;
        _outerTransforms = null;
        _innerTransforms = null;
        _outerMasks = null;
        _innerMask = null;
        _segments = Math.Clamp((int)Math.Ceiling(perimeter / 8), 96, 192);
        var inner = new RectangleGeometry(capsule, radius, radius);
        inner.Freeze();
        _innerClip = inner;
        var outer = new RectangleGeometry(new Rect(-23, -23, ActualWidth + 46, ActualHeight + 46));
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        outside.Freeze();
        _outerClip = outside;
        _outerRings = BuildRings(capsule, radius, perimeter, OuterDistances);
        var innerDistances = InnerFractions.Select(fraction => -fraction * radius).ToArray();
        _innerRings = BuildRings(capsule, radius, perimeter, innerDistances);
    }

    private Geometry[][] BuildRings(Rect capsule, double radius, double perimeter, double[] distances)
    {
        var rings = new Geometry[distances.Length - 1][];
        var step = perimeter / _segments;
        for (var ring = 0; ring < rings.Length; ring++)
        {
            rings[ring] = new Geometry[_segments];
            for (var segment = 0; segment < _segments; segment++)
            {
                // A subpixel overlap removes raster seams between cached strips.
                // Colors differ by only a few degrees, so the join stays smooth.
                var start = segment * step - .18;
                var end = (segment + 1) * step + .18;
                var subdivisions = Math.Max(2, (int)Math.Ceiling((end - start) / 2));
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(OffsetPoint(capsule, radius, perimeter, start, distances[ring]), true, true);
                    for (var index = 1; index <= subdivisions; index++)
                        path.LineTo(OffsetPoint(capsule, radius, perimeter,
                            start + (end - start) * index / subdivisions, distances[ring]), true, false);
                    for (var index = subdivisions; index >= 0; index--)
                        path.LineTo(OffsetPoint(capsule, radius, perimeter,
                            start + (end - start) * index / subdivisions, distances[ring + 1]), true, false);
                }
                geometry.Freeze();
                rings[ring][segment] = geometry;
            }
        }
        return rings;
    }

    private static Point OffsetPoint(Rect rect, double radius, double perimeter, double distance, double offset)
    {
        distance = (distance % perimeter + perimeter) % perimeter;
        var horizontal = rect.Width - 2 * radius;
        var vertical = rect.Height - 2 * radius;
        var quarter = Math.PI * radius / 2;
        if (distance < horizontal) return new Point(rect.Left + radius + distance, rect.Top - offset);
        distance -= horizontal;
        if (distance < quarter) return Arc(rect.Right - radius, rect.Top + radius, -Math.PI / 2 + distance / radius);
        distance -= quarter;
        if (distance < vertical) return new Point(rect.Right + offset, rect.Top + radius + distance);
        distance -= vertical;
        if (distance < quarter) return Arc(rect.Right - radius, rect.Bottom - radius, distance / radius);
        distance -= quarter;
        if (distance < horizontal) return new Point(rect.Right - radius - distance, rect.Bottom + offset);
        distance -= horizontal;
        if (distance < quarter) return Arc(rect.Left + radius, rect.Bottom - radius, Math.PI / 2 + distance / radius);
        distance -= quarter;
        if (distance < vertical) return new Point(rect.Left - offset, rect.Bottom - radius - distance);
        distance -= vertical;
        return Arc(rect.Left + radius, rect.Top + radius, Math.PI + distance / radius);

        Point Arc(double centerX, double centerY, double angle)
            => new(centerX + (radius + offset) * Math.Cos(angle), centerY + (radius + offset) * Math.Sin(angle));
    }

    private static LinearGradientBrush[] CreateWashPalette(Brush[] palette)
    {
        var washes = new LinearGradientBrush[PaletteSize];
        for (var phase = 0; phase < washes.Length; phase++)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, .8), EndPoint = new Point(1, .2) };
            for (var stop = 0; stop <= 12; stop++)
                brush.GradientStops.Add(new GradientStop(((SolidColorBrush)palette[(phase + stop * PaletteSize / 12) % PaletteSize]).Color,
                    stop / 12d));
            brush.Freeze();
            washes[phase] = brush;
        }
        return washes;
    }

    private DrawingBrush CreateCapsuleMask(double depth, bool inner)
    {
        var group = new DrawingGroup();
        // Only the rectangular mask partitions use aliased edges. Their shared
        // boundaries then have neither gaps nor doubled alpha; the radial and
        // linear gradient samples still provide the continuous soft falloff.
        RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
        var rect = _capsule;
        var radius = _radius;
        var horizontal = Math.Max(0, rect.Width - 2 * radius);
        var vertical = Math.Max(0, rect.Height - 2 * radius);
        var extent = inner ? radius : radius + depth;
        using (var drawing = group.Open())
        {
            if (horizontal > 0)
            {
                DrawBand(new Rect(rect.Left + radius, inner ? rect.Top : rect.Top - depth, horizontal, depth),
                    new Point(0, rect.Top), new Point(0, rect.Top + (inner ? depth : -depth)));
                DrawBand(new Rect(rect.Left + radius, inner ? rect.Bottom - depth : rect.Bottom, horizontal, depth),
                    new Point(0, rect.Bottom), new Point(0, rect.Bottom + (inner ? -depth : depth)));
            }
            if (vertical > 0)
            {
                DrawBand(new Rect(inner ? rect.Left : rect.Left - depth, rect.Top + radius, depth, vertical),
                    new Point(rect.Left, 0), new Point(rect.Left + (inner ? depth : -depth), 0));
                DrawBand(new Rect(inner ? rect.Right - depth : rect.Right, rect.Top + radius, depth, vertical),
                    new Point(rect.Right, 0), new Point(rect.Right + (inner ? -depth : depth), 0));
            }
            DrawCorner(new Point(rect.Left + radius, rect.Top + radius), -1, -1);
            DrawCorner(new Point(rect.Right - radius, rect.Top + radius), 1, -1);
            DrawCorner(new Point(rect.Right - radius, rect.Bottom - radius), 1, 1);
            DrawCorner(new Point(rect.Left + radius, rect.Bottom - radius), -1, 1);

            void DrawBand(Rect bounds, Point start, Point end)
            {
                var gradient = new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute, StartPoint = start, EndPoint = end
                };
                AddStops(gradient.GradientStops, inner, 0, 1);
                gradient.Freeze();
                drawing.DrawRectangle(gradient, null, bounds);
            }

            void DrawCorner(Point center, int x, int y)
            {
                var gradient = new RadialGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute, Center = center, GradientOrigin = center,
                    RadiusX = extent, RadiusY = extent
                };
                if (inner) AddStops(gradient.GradientStops, true, 1, -1);
                else
                {
                    gradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(BloomAlpha[0] * 255), 255, 255, 255), 0));
                    AddStops(gradient.GradientStops, false, radius / extent, depth / extent);
                }
                gradient.Freeze();
                drawing.DrawRectangle(gradient, null, new Rect(center.X + (x < 0 ? -extent : 0),
                    center.Y + (y < 0 ? -extent : 0), extent, extent));
            }
        }
        group.Freeze();
        var bounds = inner ? rect : new Rect(rect.Left - depth, rect.Top - depth,
            rect.Width + 2 * depth, rect.Height + 2 * depth);
        var brush = new DrawingBrush(group)
        {
            ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = bounds, Viewport = bounds, Stretch = Stretch.Fill, TileMode = TileMode.None
        };
        brush.Freeze();
        return brush;

        static void AddStops(GradientStopCollection stops, bool inside, double start, double span)
        {
            var positions = inside ? InsidePositions : BloomPositions;
            var alpha = inside ? InsideAlpha : BloomAlpha;
            for (var index = 0; index < positions.Length; index++)
                stops.Add(new GradientStop(Color.FromArgb((byte)(alpha[index] * 255), 255, 255, 255),
                    start + positions[index] * span));
        }
    }

    private static Brush[] CreateNeonPalette()
    {
        Color[] anchors = [Color.FromRgb(0, 225, 255), Color.FromRgb(50, 113, 255),
            Color.FromRgb(255, 35, 225), Color.FromRgb(155, 58, 255), Color.FromRgb(0, 225, 255)];
        var palette = new Brush[PaletteSize];
        for (var index = 0; index < palette.Length; index++)
        {
            var position = index * (anchors.Length - 1d) / palette.Length;
            var anchor = (int)position;
            var amount = position - anchor;
            var from = anchors[anchor];
            var to = anchors[anchor + 1];
            var brush = new SolidColorBrush(Color.FromRgb((byte)(from.R + (to.R - from.R) * amount),
                (byte)(from.G + (to.G - from.G) * amount), (byte)(from.B + (to.B - from.B) * amount)));
            brush.Freeze();
            palette[index] = brush;
        }
        return palette;
    }

    private static Brush[] CreatePalette()
    {
        var palette = new Brush[PaletteSize];
        for (var index = 0; index < palette.Length; index++)
        {
            var hue = index * 6d / palette.Length;
            var sector = (int)hue;
            var fraction = hue - sector;
            const double low = .035;
            var rising = low + (1 - low) * fraction;
            var falling = 1 - (1 - low) * fraction;
            var (red, green, blue) = sector switch
            {
                0 => (1d, rising, low), 1 => (falling, 1d, low),
                2 => (low, 1d, rising), 3 => (low, falling, 1d),
                4 => (rising, low, 1d), _ => (1d, low, falling)
            };
            var brush = new SolidColorBrush(Color.FromRgb((byte)(red * 255), (byte)(green * 255), (byte)(blue * 255)));
            brush.Freeze();
            palette[index] = brush;
        }
        return palette;
    }
}

