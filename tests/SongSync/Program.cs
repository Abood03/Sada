using Sada;
using System.Globalization;

double[] chart = [3.3, 7.38, 11.9, 15.4, 20.71, 24.06, 29.2, 34.7, 39.1];
string file = string.Join("\n", chart.Select(t => string.Create(CultureInfo.InvariantCulture,
    $"[00:{t:00.000}]A phrase for the timing check")));
var track = LyricsTrack.Parse(file);

void Verify(bool passed, string text)
{
    if (!passed) throw new InvalidOperationException(text);
    Console.WriteLine($"PASS {text}");
}

SongSync Simulate(double[] actual, bool vocal = true, SongSync? sync = null, LyricsTrack? source = null)
{
    sync ??= new SongSync();
    var simulationEnd = Math.Max(43, actual.DefaultIfEmpty().Max() + 2.0);
    for (var t = 0d; t < simulationEnd; t += .024)
    {
        var gain = 0d;
        foreach (var at in actual)
        {
            var age = t - at;
            if (age is >= 0 and < .82)
                gain = Math.Max(gain, age < .05 ? age / .05 : Math.Min(1, (.82 - age) / .17));
        }
        sync.Observe(Sample(gain, vocal, t), source ?? track, TimeSpan.FromSeconds(t), true);
    }
    return sync;
}

foreach (double delta in new[] { .53, -.69, 0d })
{
    var sync = Simulate(chart.Select(t => t + delta).ToArray());
    Console.WriteLine($"Cue offset {delta:+0.00;-0.00;0}, correction {sync.Correction.TotalSeconds:F3}, estimate {sync.HasEstimate}");
    Verify(sync.HasEstimate && Math.Abs(sync.Correction.TotalSeconds + delta) < .075, $"correction at audible displacement {delta:+0.00;-0.00;0}");
    sync.Observe(Sample(0, true, 0), track, TimeSpan.FromSeconds(11), false);
    Verify(sync.HasEstimate && Math.Abs(sync.Correction.TotalSeconds + delta) < .075, "paused same song retains correction");
    sync.Observe(Sample(0, true, 0), track, TimeSpan.FromSeconds(3), true);
    Verify(sync.HasEstimate && Math.Abs(sync.Correction.TotalSeconds + delta) < .075, "seek retains established same-song correction");
    sync.Observe(Sample(0, true, 0), LyricsTrack.Parse(file), TimeSpan.Zero, true);
    Verify(!sync.HasEstimate && sync.Correction == TimeSpan.Zero, "new track clears correction");
}

var mismatched = Simulate(chart.Zip(new[] { 1.15, -.95, .48, -.27, -.78, 1.35, .62, -.47, -.1 }, (a, b) => a + b).ToArray());
Verify(!mismatched.HasEstimate && mismatched.Correction == TimeSpan.Zero, "uncorrelated vocal onsets do not move lyrics");
Verify(!Simulate(chart.SelectMany(t => new[] { t - .62, t + .54 }).ToArray()).HasEstimate, "competing onsets are ambiguous");
Verify(!Simulate(chart.Select(t => t + .53).ToArray(), vocal: false).HasEstimate, "bass accompaniment does not trigger alignment");
Verify(!Simulate(chart.Take(3).Select(t => t + .53).ToArray()).HasEstimate, "three entries cannot move timing");

double[] slowCues = [3, 10, 17.6, 26.1, 34, 43.6];
var slowSource = LyricsTrack.Parse(string.Join('\n', slowCues.Select(cue =>
    string.Create(CultureInfo.InvariantCulture, $"[00:{cue:00.000}]Long rest"))));
var slowMatch = Simulate(slowCues.Select(cue => cue - .58).ToArray(), source: slowSource);
Verify(slowMatch.HasEstimate && Math.Abs(slowMatch.Correction.TotalSeconds - .58) < .075,
    "consistent late source timing is advanced on a slow track with long rests");

static AudioFrame Sample(double gain, bool vocal, double time)
{
    var left = new float[512];
    var right = new float[512];
    var spectrum = new float[80];
    for (var i = 0; i < left.Length; i++)
    {
        double t = time + i / 18432d;
        double bass = Math.Sin(t * Math.PI * 2 * 68) * (.06 + (vocal ? 0 : gain * .18));
        double mid = vocal ? gain * (.11 * Math.Sin(t * Math.PI * 2 * 587)
                                     + .025 * Math.Sin(t * Math.PI * 2 * 902)) : 0;
        double side = .043 * Math.Sin(t * Math.PI * 2 * 413);
        left[i] = (float)(bass + mid + side);
        right[i] = (float)(bass + mid - side);
    }
    for (var i = 0; i < spectrum.Length; i++)
        spectrum[i] = (float)(i < 22 ? .3 + (vocal ? 0 : gain * .4) : i < 60 ? .15 + (vocal ? gain * .31 : 0) : .11);
    return new AudioFrame(left, right, spectrum, .4f, 0);
}
