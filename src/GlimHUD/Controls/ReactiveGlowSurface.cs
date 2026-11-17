using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlimHUD.Models;

namespace GlimHUD.Controls;

public enum GlowLayer { Outer, Inner, Both }

/// <summary>
/// A cached capsule raster, animated with palette lookups. Each frame submits one
/// image instead of thousands of vector strips and full-surface opacity masks.
/// The companion owns the outer pixels; the inner image stays below HUD content.
/// Allow 23 local DIPs around an outer layer so its halo is never clipped.
/// </summary>
public sealed class ReactiveGlowSurface : FrameworkElement
{
    private const int PaletteSize = 768;
    private const int InsideSteps = 1024;
    private const int DepthSteps = 64;
    private const double Padding = 23;
    private const int MaximumPixels = 2_000_000;
    private static readonly Color[] Rainbow = CreatePalette();
    private static readonly Color[] Neon = CreateNeonPalette();
    private static readonly double[] BloomPositions = [0, .065, .14, .26, .43, .65, .84, 1];
    private static readonly double[] BloomAlpha = [.9, .83, .52, .28, .13, .045, .012, 0];
    private static readonly double[] InsidePositions = [0, .06, .14, .28, .48, .72, 1];
    private static readonly double[] InsideAlpha = [.85, .60, .36, .18, .08, .02, 0];
    private static readonly double[] OuterDistances = [0, 1.8, 3.2, 5, 7.5, 10.5, 14, 18, 22];
    private static readonly double[] OuterAlpha = [1, .54, .36, .245, .16, .095, .05, .018];
    private static readonly double[] InnerFractions = [0, .055, .12, .22, .36, .53, .75, 1];
    private static readonly double[] InnerAlpha = [.95, .70, .50, .35, .23, .13, .035];
    private static readonly float[][] OuterMasks = CreateOuterMasks();
    private static readonly float[] InnerMask = CreateInnerMask();
    private static readonly float[] Comet = CreateComet();
    private readonly FrameColor[] _colors = new FrameColor[PaletteSize];
    private readonly FrameColor[] _wash = new FrameColor[PaletteSize + 1];
    private readonly float[] _highlights = new float[PaletteSize];
    private readonly float[] _outerCore = new float[PaletteSize];
    private readonly float[] _innerCore = new float[PaletteSize];
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private RasterPixel[] _map = [];
    private Rect _rasterBounds;
    private Size _rasterSize;
    private GlowLayer _rasterLayer;
    private double _rasterScale;
    private double _rasterCornerRadius = double.NaN;
    private FrameKey? _lastFrame;

    // The wave has its own palette, independent of the UI accent.
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
    // NaN keeps the historical capsule. An explicit radius is used by data-only HUD themes.
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(ReactiveGlowSurface),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => value is double radius && double.IsFinite(radius) ? Math.Clamp(radius, 0, 1024) : double.NaN));

    public Color GlowColor { get => (Color)GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Intensity { get => (double)GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public GlowLayer Layer { get => (GlowLayer)GetValue(LayerProperty); set => SetValue(LayerProperty, value); }
    public string EffectStyle { get => (string)GetValue(EffectStyleProperty); set => SetValue(EffectStyleProperty, value); }
    public double Pulse { get => (double)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }
    public double PulsePosition { get => (double)GetValue(PulsePositionProperty); set => SetValue(PulsePositionProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public ReactiveGlowSurface()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    }

    private static object Normalize(DependencyObject owner, object value)
        => value is double number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : 0d;

    private static object NormalizePhase(DependencyObject owner, object value)
    {
        if (value is not double number || !double.IsFinite(number)) return 0d;
        var phase = number - Math.Floor(number);
        return phase >= 1 ? 0d : phase;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var strength = Level * Intensity;
        if (strength <= 0 || ActualWidth < 2 || ActualHeight < 2) return;
        EnsureRaster();
        var pulse = (int)Math.Round(Pulse * 255);
        var frame = new FrameKey((int)(Phase * PaletteSize), (int)Math.Round(strength * 65535),
            pulse, pulse == 0 ? 0 : (int)(PulsePosition * PaletteSize), EffectStyle == GlowStyles.NeonFlow);
        if (_lastFrame != frame)
        {
            Rasterize(frame);
            _bitmap!.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight),
                _pixels, _bitmap.PixelWidth * 4, 0);
            _lastFrame = frame;
        }
        drawing.DrawImage(_bitmap, _rasterBounds);
    }

    private void Rasterize(FrameKey frame)
    {
        var strength = frame.Strength / 65535f;
        var pulse = (float)Math.Sqrt(frame.Pulse / 255d);
        var pulseStrength = pulse * MathF.Sqrt(strength);
        var fieldOpacity = Math.Min(1, strength * (1 + pulse * .20f));
        var washOpacity = strength * (frame.Neon ? .25f : .24f);
        var coreBase = frame.Neon ? MathF.Pow(strength, .30f) : 0;
        var palette = frame.Neon ? Neon : Rainbow;
        for (var index = 0; index < PaletteSize; index++)
        {
            _colors[index] = new FrameColor(palette[(frame.Phase + index) % PaletteSize]);
            var delta = (index - frame.Position + PaletteSize) % PaletteSize;
            var weight = Math.Max(Comet[delta], Comet[(delta + PaletteSize / 2) % PaletteSize]);
            _highlights[index] = pulse * weight;
            _outerCore[index] = Math.Min(1, coreBase * .98f + pulseStrength * (.08f + .86f * weight));
            _innerCore[index] = frame.Neon ? Math.Min(1, coreBase * .62f + pulseStrength * (.08f + .86f * weight)) : 0;
        }
        for (var index = 0; index <= PaletteSize; index++)
        {
            var stop = Math.Min(11, index / 64);
            var amount = (index - stop * 64) / 64f;
            var from = palette[(frame.Phase + stop * 64) % PaletteSize];
            var to = palette[(frame.Phase + (stop + 1) * 64) % PaletteSize];
            _wash[index] = new FrameColor(from.R + (to.R - from.R) * amount,
                from.G + (to.G - from.G) * amount, from.B + (to.B - from.B) * amount);
        }
        var mask = OuterMasks[(int)Math.Round(pulse * 32)];
        var combined = Layer == GlowLayer.Both;
        if (combined) Array.Clear(_pixels);
        foreach (ref readonly var pixel in _map.AsSpan())
        {
            var color = _colors[pixel.Color];
            var alpha = 0f;
            var red = 0f;
            var green = 0f;
            var blue = 0f;
            if (pixel.Inner)
            {
                var wash = _wash[pixel.Wash];
                alpha = washOpacity;
                red = wash.R * alpha;
                green = wash.G * alpha;
                blue = wash.B * alpha;
            }
            if (frame.Neon)
            {
                var highlight = _highlights[pixel.Color] * (pixel.Inner ? .42f : 1);
                var field = new FrameColor(color.R + (255 - color.R) * highlight,
                    color.G + (255 - color.G) * highlight, color.B + (255 - color.B) * highlight);
                var fieldAlpha = (pixel.Inner ? InnerMask[pixel.Mask] : mask[pixel.Mask]) * fieldOpacity;
                Over(ref red, ref green, ref blue, ref alpha, field, fieldAlpha);
            }
            else
            {
                var ringAlpha = Math.Min(1, strength * (pixel.RingAlpha + pulse * pixel.RingPulse));
                Over(ref red, ref green, ref blue, ref alpha, color, ringAlpha);
                if (!pixel.Inner && frame.Pulse > 0)
                    Over(ref red, ref green, ref blue, ref alpha, FrameColor.White,
                        mask[pixel.Mask] * fieldOpacity * _highlights[pixel.Color]);
            }
            var core = (pixel.Inner ? _innerCore[pixel.Color] : _outerCore[pixel.Color]) * pixel.Core;
            if (core > 0) Over(ref red, ref green, ref blue, ref alpha, FrameColor.White, core);
            red *= pixel.Coverage;
            green *= pixel.Coverage;
            blue *= pixel.Coverage;
            alpha *= pixel.Coverage;
            if (combined)
            {
                var previous = (uint)_pixels[pixel.Offset];
                var remaining = 1 - alpha;
                red += ((previous >> 16) & 255) * remaining;
                green += ((previous >> 8) & 255) * remaining;
                blue += (previous & 255) * remaining;
                alpha += (previous >> 24) / 255f * remaining;
            }
            var a = Math.Clamp((int)(alpha * 255 + .5f), 0, 255);
            var r = Math.Clamp((int)(red + .5f), 0, a);
            var g = Math.Clamp((int)(green + .5f), 0, a);
            var b = Math.Clamp((int)(blue + .5f), 0, a);
            _pixels[pixel.Offset] = a << 24 | r << 16 | g << 8 | b;
        }
    }

    private static void Over(ref float red, ref float green, ref float blue, ref float alpha, FrameColor color, float opacity)
    {
        var remaining = 1 - opacity;
        red = color.R * opacity + red * remaining;
        green = color.G * opacity + green * remaining;
        blue = color.B * opacity + blue * remaining;
        alpha = opacity + alpha * remaining;
    }

    private void EnsureRaster()
    {
        var size = new Size(ActualWidth, ActualHeight);
        var scale = EffectiveRasterScale();
        var padding = Layer == GlowLayer.Inner ? 0 : Padding;
        var width = ActualWidth + padding * 2;
        var height = ActualHeight + padding * 2;
        scale = Math.Min(scale, Math.Sqrt(MaximumPixels / (width * height)));
        scale = Math.Min(scale, 16384 / Math.Max(width, height));
        if (_bitmap is not null && _rasterSize == size && _rasterLayer == Layer && Math.Abs(_rasterScale - scale) < .001
            && _rasterCornerRadius.Equals(CornerRadius)) return;
        _rasterSize = size;
        _rasterLayer = Layer;
        _rasterScale = scale;
        _rasterCornerRadius = CornerRadius;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
        _bitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null);
        _pixels = new int[pixelWidth * pixelHeight];
        _rasterBounds = new Rect(-padding, -padding, pixelWidth / scale, pixelHeight / scale);
        _lastFrame = null;
        var capsule = new Rect(.5, .5, ActualWidth - 1, ActualHeight - 1);
        var innerDepth = Math.Min(capsule.Width, capsule.Height) / 2;
        var radius = double.IsNaN(CornerRadius) ? innerDepth : Math.Min(CornerRadius, innerDepth);
        var horizontal = capsule.Width - 2 * radius;
        var vertical = capsule.Height - 2 * radius;
        var perimeter = 2 * (horizontal + vertical) + Math.PI * 2 * radius;
        var halfPixel = .5 / scale;
        var includeInner = Layer is GlowLayer.Inner or GlowLayer.Both;
        var includeOuter = Layer is GlowLayer.Outer or GlowLayer.Both;
        var map = new List<RasterPixel>(Math.Min(pixelWidth * pixelHeight, 200_000));
        for (var y = 0; y < pixelHeight; y++)
        {
            var py = -padding + (y + .5) / scale;
            for (var x = 0; x < pixelWidth; x++)
            {
                var px = -padding + (x + .5) / scale;
                var cx = Math.Clamp(px, capsule.Left + radius, capsule.Right - radius);
                var cy = Math.Clamp(py, capsule.Top + radius, capsule.Bottom - radius);
                var dx = px - cx;
                var dy = py - cy;
                var qx = Math.Abs(px - (capsule.Left + capsule.Width / 2)) - capsule.Width / 2 + radius;
                var qy = Math.Abs(py - (capsule.Top + capsule.Height / 2)) - capsule.Height / 2 + radius;
                var distance = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2))
                    + Math.Min(Math.Max(qx, qy), 0) - radius;
                var inside = includeInner && distance < halfPixel;
                var outside = includeOuter && distance > -halfPixel && distance < 22 + halfPixel;
                if (!inside && !outside) continue;
                var position = PerimeterPosition(px, py, dx, dy, capsule, radius, horizontal, vertical) / perimeter;
                var color = (ushort)((int)(position * PaletteSize) % PaletteSize);
                var gradientX = capsule.Width;
                var gradientY = -.6 * capsule.Height;
                var washPosition = ((px - capsule.Left) * gradientX + (py - capsule.Top - .8 * capsule.Height) * gradientY)
                    / (gradientX * gradientX + gradientY * gradientY);
                var wash = (ushort)Math.Clamp((int)Math.Round(washPosition * PaletteSize), 0, PaletteSize);
                if (inside) Add(true, -distance, Math.Clamp(.5 - distance * scale, 0, 1));
                if (outside) Add(false, distance, Math.Clamp(.5 + distance * scale, 0, 1));

                void Add(bool inner, double depth, double coverage)
                {
                    var normalized = Math.Max(0, depth);
                    var maskIndex = inner ? Math.Clamp((int)Math.Round(normalized / innerDepth * InsideSteps), 0, InsideSteps)
                        : Math.Clamp((int)Math.Round(normalized * DepthSteps), 0, 22 * DepthSteps);
                    var (ring, boost) = BandAlpha(normalized, halfPixel, inner, innerDepth);
                    var coreWidth = inner ? .8 : 1.4;
                    var core = Math.Clamp((coreWidth - normalized) * scale + .5, 0, 1);
                    map.Add(new RasterPixel(y * pixelWidth + x, color, wash, (ushort)maskIndex, inner,
                        (float)coverage, ring, boost, (float)core));
                }
            }
        }
        _map = map.ToArray();
    }

    private double EffectiveRasterScale()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Visual root = this;
        while (VisualTreeHelper.GetParent(root) is Visual parent) root = parent;
        if (!ReferenceEquals(root, this))
        {
            var transform = TransformToAncestor(root);
            var origin = transform.Transform(new Point());
            var x = transform.Transform(new Point(1, 0)) - origin;
            var y = transform.Transform(new Point(0, 1)) - origin;
            scale *= Math.Max(x.Length, y.Length);
        }
        // The halo is deliberately soft; 1.5 pixels/DIP retains its thin core
        // without a 4K/high-DPI display multiplying texture work without bound.
        return Math.Clamp(Math.Ceiling(scale * 8) / 8, .5, 1.5);
    }

    private static double PerimeterPosition(double x, double y, double dx, double dy,
        Rect rect, double radius, double horizontal, double vertical)
    {
        var quarter = Math.PI * radius / 2;
        if (horizontal > 0 && x >= rect.Left + radius && x <= rect.Right - radius)
            return y <= rect.Top + rect.Height / 2 ? x - rect.Left - radius
                : horizontal + 2 * quarter + vertical + rect.Right - radius - x;
        if (vertical > 0 && y >= rect.Top + radius && y <= rect.Bottom - radius)
            return x >= rect.Left + rect.Width / 2 ? horizontal + quarter + y - rect.Top - radius
                : 2 * horizontal + 3 * quarter + vertical + rect.Bottom - radius - y;
        var angle = Math.Atan2(dy, dx);
        if (dx >= 0) return dy < 0 ? horizontal + (angle + Math.PI / 2) * radius
            : horizontal + quarter + vertical + angle * radius;
        return dy >= 0 ? 2 * horizontal + 2 * quarter + vertical + (angle - Math.PI / 2) * radius
            : 2 * horizontal + 3 * quarter + 2 * vertical + (angle + Math.PI) * radius;
    }

    private static (float Alpha, float Boost) BandAlpha(double depth, double halfPixel, bool inner, double radius)
    {
        var boundaries = inner ? InnerFractions : OuterDistances;
        var alpha = inner ? InnerAlpha : OuterAlpha;
        var low = depth - halfPixel;
        var high = depth + halfPixel;
        var total = 0d;
        var boost = 0d;
        for (var band = 0; band < alpha.Length; band++)
        {
            var from = band == 0 ? double.NegativeInfinity : boundaries[band] * (inner ? radius : 1);
            var to = boundaries[band + 1] * (inner ? radius : 1);
            var weight = Math.Max(0, Math.Min(high, to) - Math.Max(low, from)) / (2 * halfPixel);
            total += alpha[band] * weight;
            if (!inner) boost += alpha[band] * weight * 1.5 * band / (alpha.Length - 1);
        }
        return ((float)total, (float)boost);
    }

    private static float[][] CreateOuterMasks()
    {
        var masks = new float[33][];
        for (var width = 0; width < masks.Length; width++)
        {
            masks[width] = new float[22 * DepthSteps + 1];
            var depth = 16 + 6 * width / 32d;
            for (var index = 0; index < masks[width].Length; index++)
                masks[width][index] = Falloff(index / (double)DepthSteps / depth, BloomPositions, BloomAlpha);
        }
        return masks;
    }

    private static float[] CreateInnerMask()
    {
        var mask = new float[InsideSteps + 1];
        for (var index = 0; index < mask.Length; index++)
            mask[index] = Falloff(index / (double)InsideSteps, InsidePositions, InsideAlpha);
        return mask;
    }

    private static float Falloff(double value, double[] positions, double[] alpha)
    {
        if (value >= 1) return 0;
        for (var index = 1; index < positions.Length; index++)
            if (value <= positions[index])
            {
                var amount = (value - positions[index - 1]) / (positions[index] - positions[index - 1]);
                var from = (byte)(alpha[index - 1] * 255) / 255d;
                var to = (byte)(alpha[index] * 255) / 255d;
                return (float)(from + (to - from) * amount);
            }
        return 0;
    }

    private static float[] CreateComet()
    {
        var weights = new float[PaletteSize];
        for (var index = 0; index < weights.Length; index++)
        {
            var delta = index / (double)PaletteSize;
            if (delta >= .5) delta -= 1;
            var head = Math.Exp(-Math.Pow(delta / .035, 2));
            var tail = delta <= 0 ? .7 * Math.Exp(delta / .09) : 0;
            weights[index] = (float)Math.Max(head, tail);
        }
        return weights;
    }

    private static Color[] CreateNeonPalette()
    {
        Color[] anchors = [Color.FromRgb(0, 225, 255), Color.FromRgb(50, 113, 255),
            Color.FromRgb(255, 35, 225), Color.FromRgb(155, 58, 255), Color.FromRgb(0, 225, 255)];
        var palette = new Color[PaletteSize];
        for (var index = 0; index < palette.Length; index++)
        {
            var position = index * (anchors.Length - 1d) / palette.Length;
            var anchor = (int)position;
            var amount = position - anchor;
            var from = anchors[anchor];
            var to = anchors[anchor + 1];
            palette[index] = Color.FromRgb((byte)(from.R + (to.R - from.R) * amount),
                (byte)(from.G + (to.G - from.G) * amount), (byte)(from.B + (to.B - from.B) * amount));
        }
        return palette;
    }

    private static Color[] CreatePalette()
    {
        var palette = new Color[PaletteSize];
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
            palette[index] = Color.FromRgb((byte)(red * 255), (byte)(green * 255), (byte)(blue * 255));
        }
        return palette;
    }

    private readonly record struct FrameKey(int Phase, int Strength, int Pulse, int Position, bool Neon);
    private readonly record struct RasterPixel(int Offset, ushort Color, ushort Wash, ushort Mask, bool Inner,
        float Coverage, float RingAlpha, float RingPulse, float Core);
    private readonly record struct FrameColor(float R, float G, float B)
    {
        public FrameColor(Color color) : this(color.R, color.G, color.B) { }
        public static FrameColor White => new(255, 255, 255);
    }
}
