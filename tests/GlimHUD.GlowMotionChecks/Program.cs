using GlimHUD.Services;

internal static class Program
{
    private static int _passed;
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1d / 30);

    private static int Main()
    {
        try
        {
            var motion = new GlowMusicMotion();
            Check(motion.Level == .72 && motion.Phase == 0 && motion.Pulse == 0 && motion.PulsePosition == 0,
                "A new controller starts with a visible baseline and no manufactured music accent.");
            for (var i = 0; i < 30; i++) motion.Update(0, true, Frame);
            Check(Near(motion.Phase, .125) && motion.Level == .72 && motion.Pulse == 0,
                "Silence advances the eight-second color wave without music flashes.");
            motion.Update(1, false, Frame);
            Check(motion.Level == .72 && motion.Pulse == 0,
                "An unavailable source cannot emit a pulse even if its stale peak is loud.");

            motion.Reset();
            motion.Update(.7, true, Frame);
            Check(motion.Pulse > .9 && motion.PulsePosition == 0 && motion.Level > .95,
                "A real loud onset produces an immediate bright accent at the start of its path.");
            var pulse = motion.Pulse;
            var phase = motion.Phase;
            motion.Update(.7, true, Frame);
            Check(motion.Pulse < pulse && motion.PulsePosition > 0 && motion.PulsePosition < 1,
                "The accent travels and releases rather than flashing in place.");
            Check(motion.Phase - phase > Frame.TotalSeconds / 8
                && motion.Phase - phase <= Frame.TotalSeconds / 8 * 1.45,
                "The musical speed accent is stronger than baseline and remains bounded.");
            for (var i = 0; i < 120; i++) motion.Update(.7, true, Frame);
            Check(motion.Pulse == 0 && motion.PulsePosition == 0 && Near(motion.Level, .72),
                "A sustained tone settles to the baseline without repeatedly inventing dynamics.");

            motion.Reset();
            for (var i = 0; i < 180; i++) motion.Update(i % 2 == 0 ? .011 : .019, true, Frame);
            Check(motion.Pulse == 0,
                "Quiet meter noise near the gate never starts traveling highlights.");

            motion.Reset();
            motion.Update(.3, true, Frame);
            var jitterOnsets = 0;
            for (var i = 0; i < 180; i++)
            {
                motion.Update(i % 2 == 0 ? .28 : .32, true, Frame);
                if (motion.Pulse > 0 && motion.PulsePosition == 0) jitterOnsets++;
            }
            Check(motion.Pulse == 0 && jitterOnsets == 0,
                "Small sustained-level fluctuations do not retrigger pulses, including during noise-estimate warmup.");

            motion.Reset();
            motion.Update(.5, true, Frame);
            motion.Update(0, true, Frame);
            motion.Update(0, true, Frame);
            motion.Update(1, true, Frame);
            Check(motion.PulsePosition > 0 && motion.Pulse < .8,
                "The refractory interval rejects a rapid second peak.");
            for (var i = 0; i < 12; i++) motion.Update(0, true, Frame);
            motion.Update(.8, true, Frame);
            Check(motion.PulsePosition == 0 && motion.Pulse >= .5,
                "A later actual onset restarts the highlight after the quiet interval.");

            motion.Reset();
            for (var i = 0; i < 30; i++) motion.Update(.12, true, Frame);
            motion.Update(.65, true, Frame);
            Check(motion.Pulse > .9 && motion.PulsePosition == 0,
                "A strong rise above an established moderate level is detected.");

            var compressed = new GlowMusicMotion();
            for (var i = 0; i < 120; i++) compressed.Update(.85, true, Frame);
            Check(compressed.Pulse == 0, "Compressed high-level audio settles without repeating accents.");
            compressed.Update(1, true, Frame);
            Check(compressed.Pulse >= .35 && compressed.Pulse <= 1 && compressed.PulsePosition == 0,
                "A real high-level transient is detected before visual gain could saturate it.");

            var lastPulse = motion.Pulse;
            var lastLevel = motion.Level;
            motion.Update(1, false, Frame);
            Check(motion.Pulse > 0 && motion.Pulse < lastPulse && motion.Level < lastLevel,
                "Missing audio fades the existing accent and wash instead of faking a new one.");
            for (var i = 0; i < 120; i++) motion.Update(1, false, Frame);
            Check(motion.Pulse == 0 && motion.Level == .72,
                "Unavailable audio settles fully back to the animated baseline.");

            motion.Update(.9, true, Frame);
            phase = motion.Phase;
            motion.ResetMusic();
            Check(motion.Phase == phase && motion.Pulse == 0 && motion.PulsePosition == 0 && motion.Level == .72,
                "Source reset clears all old music while preserving the current color phase.");
            motion.Reset();
            Check(motion.Phase == 0 && motion.Pulse == 0 && motion.Level == .72,
                "A complete reset restores the initial decorative state.");

            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -2d })
                motion.Update(invalid, true, Frame);
            Check(motion.Pulse == 0 && motion.Level == .72 && Finite(motion),
                "Invalid and negative peaks do not emit music accents.");
            phase = motion.Phase;
            motion.Update(1, true, TimeSpan.Zero);
            motion.Update(1, true, TimeSpan.FromSeconds(-1));
            Check(motion.Phase == phase && motion.Pulse == 0 && motion.Level == .72,
                "Nonpositive elapsed time cannot manufacture motion or an onset.");

            motion.Update(1, true, Frame);
            phase = motion.Phase;
            motion.Update(.9, true, TimeSpan.MaxValue);
            Check(motion.Pulse == 0 && Finite(motion) && PhaseDistance(phase, motion.Phase) <= .25 / 8 + .00001,
                "A very long scheduling gap releases stale highlights without replaying an onset or jumping the wave.");
            motion.Update(0, false, TimeSpan.MaxValue);
            Check(motion.Level == .72 && motion.Pulse == 0 && Finite(motion),
                "A long unavailable interval safely returns to baseline.");

            var random = new Random(8675309);
            for (var i = 0; i < 5000; i++)
                motion.Update(random.NextDouble() * 4 - 1, i % 13 != 0,
                    TimeSpan.FromSeconds(i % 47 == 0 ? 5 : random.NextDouble() * .1));
            Check(Finite(motion), "Varied real-world readings and scheduling gaps keep all outputs bounded and finite.");

            motion.Sensitivity = 2;
            Check(motion.Sensitivity == 1, "Sensitivity is clamped at the upper bound.");
            motion.Sensitivity = -1;
            Check(motion.Sensitivity == 0, "Sensitivity is clamped at the lower bound.");
            motion.Sensitivity = double.NaN;
            Check(motion.Sensitivity == .75, "Invalid sensitivity returns to the useful default.");

            foreach (var bpm in new[] { 70, 100, 140 })
            {
                var compressedTrace = Trace(.85, .08, bpm, .08);
                Check(compressedTrace.Onsets >= 11 && compressedTrace.Onsets <= 13,
                    $"Compressed 0.85–0.93 music with 80 ms attacks responds to nearly every real onset at {bpm} BPM.");
                Check(compressedTrace.Range > .08,
                    $"Compressed music visibly breathes instead of saturating at a flat level at {bpm} BPM.");
                var quietTrace = Trace(.006, .018, bpm, .08);
                Check(quietTrace.Onsets >= 10 && quietTrace.Onsets <= 13,
                    $"Quiet 0.006–0.024 music still produces real traveling accents at {bpm} BPM.");
            }
            foreach (var attackSeconds in new[] { .04, .12, .16 })
            {
                var smoothTrace = Trace(.35, .09, 100, attackSeconds);
                Check(smoothTrace.Onsets >= 11 && smoothTrace.Onsets <= 13,
                    $"An attack spread across {attackSeconds * 1000:0} ms accumulates into an onset instead of being lost between frames.");
            }
            var sensitiveQuiet = Trace(.006, .018, 100, .08, 1);
            var restrainedQuiet = Trace(.006, .018, 100, .08, 0);
            Check(sensitiveQuiet.Onsets >= 11 && restrainedQuiet.Onsets < sensitiveQuiet.Onsets / 2,
                "Sensitivity has a meaningful effect on subtle musical dynamics.");
            var softTrace = Trace(.005, .015, 90, .08);
            Check(softTrace.Onsets >= 10 && softTrace.Onsets <= 13,
                "Soft 0.005–0.02 playback remains usable at the default sensitivity.");
            var unevenTrace = Trace(.85, .08, 118, .1, timingJitter: true);
            Check(unevenTrace.Onsets >= 11 && unevenTrace.Onsets <= 13,
                "Slightly uneven note timing is measured from sound changes rather than predicted by a beat timer.");
            var interruptedTrace = Trace(.85, .08, 100, .08, interruptions: true);
            Check(interruptedTrace.Onsets >= 11 && interruptedTrace.Onsets <= 13,
                "Isolated unavailable samples do not inflate the local range or suppress subsequent compressed-music onsets.");

            var availability = new GlowMusicMotion();
            for (var i = 0; i < 120; i++) availability.Update(.85, true, Frame);
            availability.Update(0, false, Frame);
            availability.Update(.85, true, Frame);
            Check(availability.Pulse == 0,
                "Resuming the same steady level after a short meter gap does not invent an onset from zero.");

            motion.Reset();
            motion.Sensitivity = 1;
            for (var i = 0; i < 300; i++) motion.Update(.85 + Math.Sin(i * 1.8) * .0005, true, Frame);
            Check(motion.Pulse == 0 && Near(motion.Level, .72),
                "Maximum sensitivity does not amplify tiny steady-tone jitter into flashes.");
            motion.Reset();
            for (var i = 0; i < 300; i++) motion.Update(.001 + Math.Sin(i) * .0004, true, Frame);
            Check(motion.Pulse == 0 && motion.Level == .72,
                "Sub-gate noise remains dark even at maximum sensitivity.");

            Console.WriteLine($"{_passed} glow motion checks passed; no audio or input APIs were used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static bool Finite(GlowMusicMotion motion)
        => double.IsFinite(motion.Level) && motion.Level is >= .72 and <= 1
            && double.IsFinite(motion.Phase) && motion.Phase is >= 0 and < 1
            && double.IsFinite(motion.Pulse) && motion.Pulse is >= 0 and <= 1
            && double.IsFinite(motion.PulsePosition) && motion.PulsePosition is >= 0 and < 1;

    private static double PhaseDistance(double from, double to) => (to - from + 1) % 1;
    private static bool Near(double first, double second) => Math.Abs(first - second) < .00001;

    private static (int Onsets, double Range) Trace(double floor, double amplitude, double bpm,
        double attackSeconds, double sensitivity = .75, bool timingJitter = false, bool interruptions = false)
    {
        const int beats = 12;
        const double warmup = 1;
        var period = 60 / bpm;
        var motion = new GlowMusicMotion { Sensitivity = sensitivity };
        var onsets = 0;
        var min = 1d;
        var max = 0d;
        var starts = Enumerable.Range(0, beats).Select(beat => warmup + beat * period
            + (timingJitter ? Math.Sin(beat * 2.3) * .025 : 0)).ToArray();
        for (var frame = 0; frame < (warmup + period * beats + 1) * 30; frame++)
        {
            var time = frame / 30d;
            var shape = 0d;
            if (time >= warmup && time < warmup + period * beats)
            {
                var index = Array.FindLastIndex(starts, start => start <= time);
                var local = time - starts[index];
                shape = local < attackSeconds ? local / attackSeconds : Math.Exp(-(local - attackSeconds) / .12);
            }
            var peak = floor + amplitude * shape + Math.Sin(frame * 1.7) * .0002;
            var available = !interruptions || frame is not (69 or 123 or 177);
            motion.Update(peak, available, Frame);
            if (time >= warmup && time < warmup + period * beats + .2 && motion.Pulse > 0 && motion.PulsePosition == 0)
                onsets++;
            if (time >= warmup + period && time < warmup + period * beats)
            {
                min = Math.Min(min, motion.Level);
                max = Math.Max(max, motion.Level);
            }
        }
        Console.WriteLine($"Trace base={floor:0.###}, span={amplitude:0.###}, BPM={bpm}, attack={attackSeconds:0.###}, sensitivity={sensitivity:0.##}: {onsets} onsets, level range={max - min:0.###}");
        return (onsets, max - min);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
    }
}
