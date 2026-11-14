namespace GlimHUD.Services;

/// <summary>
/// A fast audio envelope with a short onset accent. Every pulse comes from an
/// increase in the supplied music level; there is no timer-generated beat.
/// </summary>
public sealed class GlowEnvelope
{
    private double _envelope;
    private double _pulse;
    private double _previousInput;
    private double _onsetCooldown;

    public double Level { get; private set; }

    public double Update(double peak, bool signalAvailable, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0) return Level;
        seconds = Math.Min(seconds, 0.25);

        var input = signalAvailable && double.IsFinite(peak) && peak > 0
            ? Math.Pow(Math.Clamp(peak * 2.5, 0, 1), 0.65)
            : 0;
        _onsetCooldown = Math.Max(0, _onsetCooldown - seconds);
        _pulse *= Math.Exp(-seconds / 0.15);

        // A constant tone produces one initial accent, never a synthetic pulse train.
        var rise = input - _previousInput;
        if (rise > 0.035 && _onsetCooldown <= 0)
        {
            _pulse = Math.Max(_pulse, Math.Clamp(rise * 2.6, 0, 1));
            _onsetCooldown = 0.10;
        }
        _previousInput = input;

        var timeConstant = input > _envelope ? 0.014 : 0.23;
        _envelope += (input - _envelope) * (1 - Math.Exp(-seconds / timeConstant));
        Level = Math.Clamp(_envelope * 0.60 + _pulse * 0.45, 0, 1);
        if (Level < 0.0005) Level = 0;
        return Level;
    }

    public void Reset()
    {
        _envelope = _pulse = _previousInput = _onsetCooldown = Level = 0;
    }
}
