using MEA2100_Stimulation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Simulates a session without hardware: builds a schedule, plays it into a fake data stream
// (electrode samples + digital words with sideband bits), and writes <dir>/check.h5 and
// <dir>/check.stimlog.csv with the same code the application uses. check.py then reads them.
static class Program
{
    const int Fs = 20000, Electrodes = 60, Stride = 72, WordOffset = 68, Words = 4;
    static int failures;

    static void Expect(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
        if (!ok) failures++;
    }

    static bool Throws(Action a)
    {
        try { a(); } catch (ArgumentException) { return true; }
        return false;
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        var inv = CultureInfo.InvariantCulture;

        // ---- waveforms ----
        Waveform bi = Waveform.Biphasic("bi500", 500);
        Waveform gap = Waveform.Biphasic("bi200gap", 200, negativeFirst: false, gapUs: 100);
        Waveform asym = Waveform.Asymmetric("asym", 200, 4);
        Waveform train = Waveform.Biphasic("bi200", 200).Train(3, 100);
        Expect(bi.IsChargeBalanced && gap.IsChargeBalanced && asym.IsChargeBalanced, "biphasic, gap and asymmetric waveforms are charge-balanced");
        Expect(!Waveform.Monophasic("mono", 200).IsChargeBalanced, "monophasic is not charge-balanced");
        Expect(asym.StepsUv(400000).SequenceEqual(new[] { -400000, 100000 }) && asym.DurationsUs.SequenceEqual(new ulong[] { 200, 800 }), "asymmetric steps");
        Expect(train.PulseCount == 3 && train.PeriodUs == 10000 && train.TotalUs == 20400, "train timing");
        Expect(Throws(() => Waveform.Biphasic("x", 510)), "durations off the 20 us grid are refused");
        Expect(Throws(() => Waveform.Custom("x", new[] { 1.5 }, new ulong[] { 200 })), "levels above 1 are refused");
        Expect(Throws(() => Waveform.Custom("x", new[] { 0.0, 1 }, new ulong[] { 200, 200 })), "a leading 0 step is refused");
        Expect(Throws(() => Waveform.Biphasic("x", 500).Train(3, 800)), "a train without a gap between pulses is refused");
        Expect(Throws(() => Waveform.Biphasic("a b", 500)), "names with spaces are refused");

        // ---- schedule ----
        var settings = new ScheduleSettings
        {
            Sites = new uint[] { 0, 5, 20, 59 }, AmplitudesMv = new double[] { 100, 500 }, Waveforms = new[] { bi, gap, train },
            TrialsPerCondition = 2, Paired = false, Stg2DelayMinMs = 100, Stg2DelayMaxMs = 300,
        };
        List<List<Delivery>> trials = Schedule.Build(settings, new Random(1));
        Expect(trials.Count == 4 * 2 * 3 * 2, "single mode: sites x amplitudes x waveforms x repeats trials");
        Expect(trials.SelectMany(t => t).GroupBy(d => d.Electrode + "/" + d.Waveform.Name + "/" + d.AmplitudeUv).All(g => g.Count() == 2), "every condition has the same number of trials");
        Expect(trials.Take(24).All(t => t[0].AmplitudeUv == 100000) && trials.Skip(24).All(t => t[0].AmplitudeUv == 500000), "lowest amplitude first");
        Expect(trials.Select((t, i) => t[0].Stg == i % 2 + 1).All(x => x), "outputs alternate");

        settings.Paired = true;
        List<List<Delivery>> paired = Schedule.Build(settings, new Random(2));
        Expect(paired.All(t => t.Count == 2 && t[0].Electrode != t[1].Electrode), "paired mode: two different electrodes per trial");
        Expect(paired.All(t => t[1].DelayUs % 20 == 0 && t[1].DelayUs >= 100000 && t[1].DelayUs <= 300000), "paired mode: STG 2 delay in range, on the 20 us grid");
        Expect(paired.SelectMany(t => t).GroupBy(d => d.Stg + "/" + d.Electrode).All(g => g.Count() == 12), "paired mode: every site equally often on each output");

        int[] amp, act; ulong[] dur;
        Schedule.Expand(new Delivery { Stg = 2, Electrode = 1, DelayUs = 1000, AmplitudeUv = 500000, Waveform = gap }, 1000, out amp, out dur, out act);
        Expect(amp.SequenceEqual(new[] { 0, 500000, 0, -500000, 0 }) && dur.SequenceEqual(new ulong[] { 1000, 200, 100, 200, 1000 }) && act.SequenceEqual(new[] { 0, 1, 1, 1, 0 }),
               "the gap inside a pulse stays active");
        Schedule.Expand(new Delivery { Stg = 1, Electrode = 1, AmplitudeUv = 100000, Waveform = train }, 1000, out amp, out dur, out act);
        Expect(act.SequenceEqual(new[] { 1, 1, 0, 1, 1, 0, 1, 1, 0 }) && dur[2] == 9600, "a train is inactive between its pulses");

        // ---- a simulated session (single mode + a few paired trials) ----
        var session = trials.Concat(paired.Take(6)).ToList();
        var rng = new Random(3);
        var pulseSample = new List<long>();     // per log row
        var rows = new List<string>();
        var logged = new Dictionary<int, List<double>> { { 1, new List<double>() }, { 2, new List<double>() } };
        var truth = new Dictionary<int, List<long>> { { 1, new List<long>() }, { 2, new List<long>() } };
        // sideband[stg] = list of (sample, value) changes of that output's 16 bits
        var high = new Dictionary<int, List<long[]>> { { 1, new List<long[]>() }, { 2, new List<long[]>() } };
        long at = 2 * Fs;                          // first trial 2 s into the recording
        int seq = 0;
        for (int i = 0; i < session.Count; i++)
        {
            double pcElapsed = 0.35 + at / (double)Fs * (1 + 20e-6) + (rng.NextDouble() - 0.5) * 0.004;   // offset, drift, jitter
            foreach (Delivery d in session[i])
                for (int k = 0; k < d.Waveform.PulseCount; k++)
                {
                    long start = at + (long)((d.DelayUs + d.Waveform.PulseOffsetUs(k)) * Fs / 1000000);
                    long stop = start + (long)(d.Waveform.PulseUs * Fs / 1000000);
                    truth[d.Stg].Add(start);
                    high[d.Stg].Add(new[] { start, stop });
                    double offsetMs = (d.DelayUs + d.Waveform.PulseOffsetUs(k)) / 1000.0;
                    logged[d.Stg].Add(pcElapsed + offsetMs / 1000);
                    seq++;
                    rows.Add(string.Join(",", (i + 1).ToString(inv), seq.ToString(inv), d.Stg.ToString(inv), d.Electrode.ToString(inv),
                        ElectrodeMap.IndexToLabel[d.Electrode], d.AmplitudeUv.ToString(inv), "uV", d.Waveform.DurationsUs[0].ToString(inv), d.Waveform.Name,
                        (k + 1).ToString(inv), offsetMs.ToString("F3", inv), "0.000", "x", pcElapsed.ToString("F4", inv)));
                }
            at += (long)((0.4 + rng.NextDouble() * 0.4) * Fs);
        }
        long total = at + Fs + 123;                // odd length: the last chunk is partial

        string h5 = Path.Combine(dir, "check.h5");
        File.Delete(h5);
        File.WriteAllLines(Path.Combine(dir, "check.stimlog.csv"),
            new[] { "trial,seq,stg,api_index,label,amplitude,unit,phase_us,waveform,pulse,offset_ms,interval_s,pc_time,pc_elapsed_s" }.Concat(rows),
            new UTF8Encoding(false));

        string[] labels = ElectrodeMap.IndexToLabel;
        var writer = new McsH5Writer(h5, "E-00278", "writer-check", labels, 1000000 / Fs, 8670, -12, 24, Words, 1, "simulated");
        var decoder = new SidebandDecoder(Words);
        // Sideband of an output while active: 0xD9 (bits 0, 3, 4, 6, 7); bit 3 stays 800 us longer, bits 0 and 4 40 us.
        Func<int, long, int> sideband = (stg, s) =>
        {
            int v = 0;
            foreach (long[] h in high[stg])
            {
                if (s >= h[0] && s < h[1]) v |= 0xC0;
                if (s >= h[0] && s < h[1] + 1) v |= 0x11;
                if (s >= h[0] && s < h[1] + 16) v |= 0x08;
            }
            return v;
        };
        // Precompute the sideband word per sample only around pulses (it is 0 elsewhere).
        var word1 = new Dictionary<long, int>();
        foreach (int stg in new[] { 1, 2 })
            foreach (long[] h in high[stg])
                for (long s = h[0]; s < h[1] + 16; s++)
                {
                    int v;
                    word1.TryGetValue(s, out v);
                    word1[s] = v | (sideband(stg, s) << (stg == 1 ? 0 : 16));
                }

        const int Block = 2000;
        var data = new int[Block * Stride];
        for (long done = 0; done < total; )
        {
            int n = (int)Math.Min(Block, total - done);
            for (int j = 0; j < n; j++)
            {
                long s = done + j;
                for (int c = 0; c < Electrodes; c++)
                    data[j * Stride + c] = c * 1000 + (int)(s % 977) - 400;
                for (int c = Electrodes; c < WordOffset; c++)
                    data[j * Stride + c] = (int)(s * 31 % 1009);                 // interface-board analog inputs: ignored
                int w1;
                word1.TryGetValue(s, out w1);
                data[j * Stride + WordOffset + 0] = 0x00010000;                  // digital in/out: constant
                data[j * Stride + WordOffset + 1] = w1;                          // sideband
                data[j * Stride + WordOffset + 2] = (int)(s * 2654435761L);      // checksum: changes every sample
                data[j * Stride + WordOffset + 3] = (int)(s & 1) << 6;           // a bit 6 that toggles all the time
            }
            writer.Append(data, n, Stride, WordOffset);
            decoder.Feed(data, n, Stride, WordOffset);
            done += n;
            if (done % (10 * Block) == 0) writer.Flush();
        }

        List<SidebandDecoder.Pulses> found = decoder.Assign(logged.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), Fs);
        Console.Write(decoder.Report());
        foreach (var p in found)
        {
            Expect(p.Found && p.Word == 1 && p.Bit == (p.Stg == 1 ? 6 : 22), "STG " + p.Stg + ": found in word 1, bit " + (p.Stg == 1 ? 6 : 22) +
                   " (got word " + p.Word + ", bit " + p.Bit + ", " + p.Candidates + " candidates, residual " + p.MaxResidualMs.ToString("F2", inv) + " ms)");
            Expect(p.StartSample.SequenceEqual(truth[p.Stg].OrderBy(x => x)), "STG " + p.Stg + ": start samples exact");
            Expect(p.StopSample.Length == p.StartSample.Length, "STG " + p.Stg + ": every start has a stop");
        }
        writer.WriteEvents(found.Where(p => p.Found).Select(p => new McsH5Writer.StgEvents
            { Stg = p.Stg, StartSample = p.StartSample, StopSample = p.StopSample, StartValue = p.StartValue, StopValue = p.StopValue }).ToList());
        writer.Close();
        Expect(writer.FramesWritten == total, "all " + total + " frames written");
        Expect(Throws(() => { try { new McsH5Writer(h5, "E", "v", labels, 50, 1, -12, 24, 0, 0, ""); } catch (InvalidOperationException e) { throw new ArgumentException(e.Message); } }),
               "an existing recording is never overwritten");

        // A log that does not belong to the recording must not be matched.
        var wrong = logged.ToDictionary(kv => kv.Key, kv => kv.Value.Select((x, i) => x + (i % 2) * 0.5).ToArray());
        Expect(decoder.Assign(wrong, Fs).All(p => !p.Found), "pulse times that disagree with the log are not accepted");
        var fewer = logged.ToDictionary(kv => kv.Key, kv => kv.Value.Skip(1).ToArray());
        Expect(decoder.Assign(fewer, Fs).All(p => !p.Found), "a different pulse count is not accepted");

        File.WriteAllText(Path.Combine(dir, "check.truth.txt"),
            total.ToString(inv) + "\n" + string.Join(" ", truth[1].OrderBy(x => x)) + "\n" + string.Join(" ", truth[2].OrderBy(x => x)) + "\n");
        Console.WriteLine(failures == 0 ? "ALL OK" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
