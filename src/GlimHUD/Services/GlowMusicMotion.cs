namespace GlimHUD.Services;

/// <summary>
/// Drives decorative motion from the selected application's peak readings.
/// The color wave is continuous; flashes and traveling accents require an
/// observed increase in sound level. This is not a BPM or frequency analyzer.
/// </summary>
public sealed class GlowMusicMotion
{
    private const double Baseline = .72;
    private const double RefractorySeconds = .18;
    private const double TravelSeconds = .72;
    private double _previousPeak;
    private double _riseEnergy;
    private double _rangeLow;
    private double _rangeHigh;
    private double _meanFlux;
    private double _musicEnvelope;
    private double _cooldown;
    private double _pulseAge;
    private double _pulseStrength;
    private double _signalGap;
    private double _sensitivity = .75;
    private bool _hasReading;
    private bool _hadAvailable;
    private bool _armed = true;

    public double Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .75;
    }

    public double Level { get; private set; } = Baseline;
    public double Phase { get; private set; }
    public double Pulse { get; private set; }
    public double PulsePosition { get; private set; }

    public void Update(double peak, bool available, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0) return;

        var validSignal = available && double.IsFinite(peak);
        var input = validSignal ? Math.Clamp(peak, 0, 1) : 0;
        if (input < .002) input = 0;
        _cooldown = Math.Max(0, _cooldown - seconds);
        _pulseAge = Math.Min(60, _pulseAge + seconds);

        // Integrate a rising attack over several readings instead of requiring
        // one large frame-to-frame jump. Signed changes cancel rapid jitter.
        // The local range and ordinary meter flux set an adaptive threshold,
        // independent of the absolute loudness of compressed or quiet music.
        var firstReading = validSignal && !_hasReading;
        var contiguous = validSignal && _hadAvailable && seconds <= .5;
        if (validSignal)
        {
            _signalGap = 0;
            if (firstReading || seconds > .5)
            {
                _hasReading = true;
                _rangeLow = _rangeHigh = input;
                _meanFlux = _riseEnergy = 0;
                _armed = true;
            }
            else if (!contiguous)
            {
                // A returned meter sample is not evidence of an intervening
                // attack. Resume from it without inventing a rise from zero.
                _riseEnergy = 0;
                _armed = true;
            }
        }
        else
        {
            _signalGap = Math.Min(60, _signalGap + seconds);
            _riseEnergy = 0;
            if (_signalGap > .5) _hasReading = false;
        }
        var change = contiguous ? input - _previousPeak : 0;
        if (validSignal)
        {
            _riseEnergy = Math.Max(0, _riseEnergy * Math.Exp(-seconds / .16) + change);
            _rangeLow = input < _rangeLow ? input : _rangeLow + (input - _rangeLow) * (1 - Math.Exp(-seconds / 1.2));
            _rangeHigh = input > _rangeHigh ? input : _rangeHigh + (input - _rangeHigh) * (1 - Math.Exp(-seconds / 1.2));
            _previousPeak = input;
        }
        var span = Math.Max(0, _rangeHigh - _rangeLow);
        var threshold = Math.Max(.004 + (1 - Sensitivity) * .02,
            Math.Max(span * (.28 - .16 * Sensitivity), _meanFlux * (2.6 - 1.2 * Sensitivity)));
        if (_riseEnergy < threshold * .45) _armed = true;
        var onset = seconds <= .5 && validSignal && _cooldown <= 0 && _armed
            && (firstReading && input >= .025 || input >= .006 && _riseEnergy > threshold);
        if (onset)
        {
            _pulseStrength = firstReading ? Math.Clamp(input * 4, .5, 1)
                : Math.Clamp(.5 + (_riseEnergy / threshold - 1) * .3, .5, 1);
            _pulseAge = 0;
            _cooldown = RefractorySeconds;
            _armed = false;
        }
        // Learn ordinary back-and-forth meter jitter during the refractory
        // interval, then release that noise estimate slowly. Availability gaps
        // are not sound changes and must not inflate the estimate.
        var flux = validSignal && _hadAvailable ? Math.Abs(change) : 0;
        _meanFlux += (flux - _meanFlux) * (1 - Math.Exp(-seconds / (flux > _meanFlux ? .065 : .6)));
        _hadAvailable = validSignal;

        Pulse = _pulseStrength * Math.Exp(-_pulseAge / .36);
        if (Pulse < .001) Pulse = 0;
        PulsePosition = Pulse > 0 ? (_pulseAge / TravelSeconds) % 1 : 0;

        // Normalize dynamics against the recent range before visual mapping.
        // Multiplying absolute peaks and clipping them flattened loud music.
        var dynamics = Math.Clamp((_riseEnergy - threshold * .5) / Math.Max(threshold * 2, span * .4), 0, 1);
        var target = Math.Clamp(Pulse * .95 + dynamics * .35, 0, 1);
        var timeConstant = target > _musicEnvelope ? .014 : .23;
        _musicEnvelope += (target - _musicEnvelope) * (1 - Math.Exp(-seconds / timeConstant));
        if (_musicEnvelope < .0005) _musicEnvelope = 0;
        Level = Baseline + (1 - Baseline) * _musicEnvelope;

        // Scheduling stalls do not make the decorative wave jump across a full
        // lap. A real onset gives it a brief, bounded speed accent.
        var motionSeconds = Math.Min(seconds, .25);
        Phase = (Phase + motionSeconds / 8 * (1 + .45 * Pulse)) % 1;
    }

    /// <summary>Clear audio from an expired or changed source without jumping the color wave.</summary>
    public void ResetMusic()
    {
        _previousPeak = _riseEnergy = _rangeLow = _rangeHigh = _meanFlux = 0;
        _musicEnvelope = _cooldown = _pulseAge = _pulseStrength = _signalGap = 0;
        _hasReading = false;
        _hadAvailable = false;
        _armed = true;
        Level = Baseline;
        Pulse = PulsePosition = 0;
    }

    public void Reset()
    {
        ResetMusic();
        Phase = 0;
    }
}
