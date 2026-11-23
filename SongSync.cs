using System.Diagnostics;

namespace Sada;

internal sealed class SongSync
{
    const double SearchRadius = 1.45;
    const double MinimumSampleSpacing = .022;
    const double MatchTolerance = .145;
    const double CorrectionThreshold = .16;
    const int MinimumMatches = 5;
    const int MaximumMatches = 9;

    readonly record struct Sample(double Position, double Voice, double Center, double Mid, double Bass, double High);
    readonly record struct Match(double Offset, double Score);

    readonly List<Sample> samples = new(240);
    readonly List<Match> matches = new(MaximumMatches);
    LyricsTrack? track;
    double[] cues = [];
    int nextCue;
    double? previousPosition;
    long previousSampleClock;

    public TimeSpan Correction { get; private set; }
    public bool HasEstimate { get; private set; }

    public void Observe(AudioFrame frame, LyricsTrack? lyrics, TimeSpan position, bool isPlaying)
    {
        if (!ReferenceEquals(track, lyrics))
        {
            Reset();
            track = lyrics;
            cues = SelectCues(lyrics);
        }
        if (cues.Length < MinimumMatches) return;

        if (!isPlaying)
        {
            if (previousPosition.HasValue) ClearObservations();
            return;
        }

        double seconds = position.TotalSeconds;
        long now = Stopwatch.GetTimestamp();
        if (previousPosition is { } prior)
        {
            double elapsed = Stopwatch.GetElapsedTime(previousSampleClock, now).TotalSeconds;
            double delta = seconds - prior;
            if (delta < -.16 || delta > Math.Max(.60, elapsed + .35)) ClearObservations();
            else if (delta < MinimumSampleSpacing) return;
        }

        previousPosition = seconds;
        previousSampleClock = now;
        samples.Add(Measure(frame, seconds));
        while (nextCue < cues.Length && cues[nextCue] < samples[0].Position + SearchRadius + .20) nextCue++;
        while (nextCue < cues.Length && seconds >= cues[nextCue] + SearchRadius + .24)
        {
            if (FindMatch(cues[nextCue]) is { } match) AddMatch(match);
            nextCue++;
        }

        var cutoff = seconds - 4.5;
        int old = 0;
        while (old < samples.Count && samples[old].Position < cutoff) old++;
        if (old > 0) samples.RemoveRange(0, old);
    }

    public void Reset()
    {
        track = null;
        cues = [];
        ClearObservations();
        Correction = TimeSpan.Zero;
        HasEstimate = false;
    }

    void ClearObservations()
    {
        samples.Clear();
        matches.Clear();
        nextCue = 0;
        previousPosition = null;
    }

    static double[] SelectCues(LyricsTrack? lyrics)
    {
        if (lyrics is null) return [];
        var starts = new List<double>();
        double last = double.NegativeInfinity;
        for (var i = 0; i < lyrics.Lines.Count; i++)
        {
            var words = lyrics.WordsForLine(i);
            if (words.Count == 0) continue;
            double firstWord = words[0].At.TotalSeconds;
            // Do not count overlapping search windows as independent evidence.
            if (firstWord < last + 2 * SearchRadius + .24) continue;
            starts.Add(firstWord);
            last = firstWord;
        }
        return starts.ToArray();
    }

    Match? FindMatch(double expected)
    {
        // Search for sustained centered mid-band changes, not one-frame drum hits.
        var candidates = new List<Match>();
        for (var i = 4; i < samples.Count - 4; i++)
        {
            var at = samples[i].Position;
            if (Math.Abs(at - expected) > SearchRadius) continue;
            var before = AverageWindow(i, at - .20, at - .065, backward: true);
            var after = AverageWindow(i, at + .035, at + .205, backward: false);
            if (before is not { } prior || after is not { } following) continue;

            var rise = following.Voice - prior.Voice;
            var jump = samples[i].Voice - samples[i - 1].Voice;
            var mid = following.Mid - prior.Mid;
            var bass = following.Bass - prior.Bass;
            var high = following.High - prior.High;
            var focus = mid - Math.Max(0, bass) * .45 - Math.Max(0, high) * .35;
            if (following.Center < .62 || rise < .22 || jump < .095 || mid < .012 || focus < .008) continue;
            candidates.Add(new Match(at - expected, jump + rise * .50 + focus * 1.35));
        }
        if (candidates.Count == 0) return null;

        var best = candidates.MaxBy(value => value.Score);
        // A comparable event elsewhere in the same window is ambiguous.
        if (candidates.Any(value => Math.Abs(value.Offset - best.Offset) > .24 && value.Score > best.Score * .70))
            return null;
        return best;
    }

    Sample? AverageWindow(int from, double start, double end, bool backward)
    {
        var voice = 0d;
        var center = 0d;
        var mid = 0d;
        var bass = 0d;
        var high = 0d;
        int count = 0;
        int stride = backward ? -1 : 1;
        for (int i = from; i >= 0 && i < samples.Count; i += stride)
        {
            var sample = samples[i];
            if (backward ? sample.Position < start : sample.Position > end) break;
            if (sample.Position < start || sample.Position > end) continue;
            voice += sample.Voice;
            center += sample.Center;
            mid += sample.Mid;
            bass += sample.Bass;
            high += sample.High;
            count++;
        }
        return count < 2 ? null : new Sample(0, voice / count, center / count, mid / count, bass / count, high / count);
    }

    void AddMatch(Match result)
    {
        if (matches.Count == MaximumMatches) matches.RemoveAt(0);
        matches.Add(result);
        if (matches.Count < MinimumMatches) return;

        var agreeing = matches.Select(candidate => matches.Where(item => Math.Abs(item.Offset - candidate.Offset) <= MatchTolerance).ToArray())
            .OrderByDescending(group => group.Length)
            .ThenBy(group => Math.Abs(group.Average(item => item.Offset)))
            .First();
        if (agreeing.Length < MinimumMatches || agreeing.Length * 4 < matches.Count * 3) return;

        var offsets = agreeing.Select(item => item.Offset).Order().ToArray();
        double offset = offsets[offsets.Length / 2];
        if (Math.Abs(offset) < CorrectionThreshold) offset = 0;
        // Users add this to their clock. An onset heard at a later playback
        // position than its timestamp needs a negative correction.
        Correction = TimeSpan.FromSeconds(-offset);
        HasEstimate = true;
    }

    static Sample Measure(AudioFrame frame, double seconds)
    {
        var left = frame.Left;
        var right = frame.Right;
        int size = Math.Min(left.Length, right.Length);
        double midSlow = 0, midFast = 0, sideSlow = 0, sideFast = 0;
        double midPower = 0, sidePower = 0;
        for (var i = 0; i < size; i++)
        {
            var mid = (left[i] + right[i]) * .5;
            var side = (left[i] - right[i]) * .5;
            // Band-pass the approximately 28 ms display waveform before
            // measuring the signal common to both channels.
            midSlow += .065 * (mid - midSlow);
            sideSlow += .065 * (side - sideSlow);
            midFast += .56 * (mid - midFast);
            sideFast += .56 * (side - sideFast);
            double voice = midFast - midSlow;
            double ambience = sideFast - sideSlow;
            midPower += voice * voice;
            sidePower += ambience * ambience;
        }

        double rms = Math.Sqrt(midPower / Math.Max(1, size));
        double sideRms = Math.Sqrt(sidePower / Math.Max(1, size));
        double center = rms / (rms + sideRms + .0001);
        return new Sample(seconds, Math.Log(1 + rms * 90), center, Band(frame.Spectrum, .33, .74),
            Band(frame.Spectrum, .04, .27), Band(frame.Spectrum, .78, .97));
    }

    static double Band(float[] spectrum, double start, double end)
    {
        int first = (int)(spectrum.Length * start);
        int last = (int)(spectrum.Length * end);
        var sum = 0d;
        for (var i = first; i < last; i++) sum += spectrum[i];
        return sum / Math.Max(1, last - first);
    }
}
