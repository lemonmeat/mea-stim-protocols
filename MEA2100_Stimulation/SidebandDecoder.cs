using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MEA2100_Stimulation
{
    // Finds the pulse times of each STG output in the digital words of the data stream.
    //
    // The MEA2100 puts "sideband" words into the data stream; some of their bits are high while a
    // stimulator output is active (Multi Channel Experimenter derives its "STG n Single Pulse
    // Start/Stop" events from them). Which word and bit belongs to which output is not documented for
    // the MEA2100-Mini, so it is found from the data: the bit of STG n is the one that rises exactly
    // once per logged pulse of STG n, at times that agree with the log (the random intervals between
    // pulses act as a fingerprint). If no bit qualifies, no events are written for that output and the
    // report says so; the raw words are kept in the file either way.
    //
    // Nothing here touches the hardware.
    public sealed class SidebandDecoder
    {
        public const int PreferredBit = 6;          // 0x40, the "stimulus active" bit the script sets
        private const int MaxChanges = 2000000;     // a word that changes more often is not a sideband word

        private readonly int nWords;
        private readonly int[] last;
        private readonly List<long>[] changeSample;
        private readonly List<int>[] changeValue;
        private readonly bool[] busy;
        private readonly int[] initial;
        private long nSamples;

        public SidebandDecoder(int nWords)
        {
            this.nWords = nWords;
            last = new int[nWords];
            initial = new int[nWords];
            busy = new bool[nWords];
            changeSample = new List<long>[nWords];
            changeValue = new List<int>[nWords];
            for (int w = 0; w < nWords; w++)
            {
                changeSample[w] = new List<long>();
                changeValue[w] = new List<int>();
            }
        }

        public long SampleCount { get { return nSamples; } }

        // Feed the next frames. `data` holds `stride` words per frame; the digital words are at
        // [offset, offset + nWords) within each frame.
        public void Feed(int[] data, int nFrames, int stride, int offset)
        {
            for (int j = 0; j < nFrames; j++)
            {
                int at = j * stride + offset;
                for (int w = 0; w < nWords; w++)
                {
                    int v = data[at + w];
                    if (nSamples == 0)
                    {
                        initial[w] = v;
                        last[w] = v;
                    }
                    else if (v != last[w])
                    {
                        last[w] = v;
                        if (busy[w])
                            continue;
                        if (changeSample[w].Count >= MaxChanges)
                        {
                            busy[w] = true;
                            changeSample[w].Clear();
                            changeValue[w].Clear();
                            continue;
                        }
                        changeSample[w].Add(nSamples);
                        changeValue[w].Add(v);
                    }
                }
                nSamples++;
            }
        }

        public sealed class Pulses
        {
            public int Stg;
            public int Word = -1, Bit = -1;         // where the output was found; -1 = not found
            public long[] StartSample = new long[0];
            public long[] StopSample = new long[0]; // empty if the stops could not be paired
            public int[] StartValue = new int[0];   // the whole word at each start and stop
            public int[] StopValue = new int[0];
            public double MaxResidualMs;            // largest disagreement with the logged times
            public int Candidates;                  // how many bits qualified
            public bool Found { get { return Word >= 0; } }
        }

        private sealed class BitTrace
        {
            public int Word, Bit;
            public List<long> Rise = new List<long>(), Fall = new List<long>();
            public List<int> RiseValue = new List<int>(), FallValue = new List<int>();
            public double MeanHighSamples
            {
                get
                {
                    if (Rise.Count == 0 || Fall.Count != Rise.Count) return double.MaxValue;
                    double s = 0;
                    for (int i = 0; i < Rise.Count; i++) s += Fall[i] - Rise[i];
                    return s / Rise.Count;
                }
            }
        }

        private List<BitTrace> Traces()
        {
            var traces = new List<BitTrace>();
            for (int w = 0; w < nWords; w++)
            {
                if (busy[w] || changeSample[w].Count == 0)
                    continue;
                for (int bit = 0; bit < 32; bit++)
                {
                    int mask = 1 << bit;
                    var t = new BitTrace { Word = w, Bit = bit };
                    int prev = initial[w];
                    for (int i = 0; i < changeSample[w].Count; i++)
                    {
                        int v = changeValue[w][i];
                        if (((v ^ prev) & mask) != 0)
                        {
                            if ((v & mask) != 0) { t.Rise.Add(changeSample[w][i]); t.RiseValue.Add(v); }
                            else { t.Fall.Add(changeSample[w][i]); t.FallValue.Add(v); }
                        }
                        prev = v;
                    }
                    if (t.Rise.Count > 0 || t.Fall.Count > 0)
                        traces.Add(t);
                }
            }
            return traces;
        }

        // Largest distance (s) between the edge times and a straight line through (logged, edge) times.
        // The line absorbs the unknown offset between the PC clock and the sample clock, and their drift.
        private static double Residual(double[] logged, double[] edges)
        {
            int n = logged.Length;
            if (n == 1)
                return 0;
            double mx = logged.Average(), my = edges.Average(), sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                sxx += (logged[i] - mx) * (logged[i] - mx);
                sxy += (logged[i] - mx) * (edges[i] - my);
            }
            double slope = sxx > 0 ? sxy / sxx : 1;
            if (Math.Abs(slope - 1) > 0.01)     // clocks do not drift by 1 %: not the same pulse train
                return double.MaxValue;
            double worst = 0;
            for (int i = 0; i < n; i++)
                worst = Math.Max(worst, Math.Abs(edges[i] - (my + slope * (logged[i] - mx))));
            return worst;
        }

        // loggedS[stg] = PC times (s) of the pulses sent on that output, in order.
        public List<Pulses> Assign(IDictionary<int, double[]> loggedS, double samplingRateHz, double toleranceS = 0.1)
        {
            List<BitTrace> traces = Traces();
            var result = new List<Pulses>();
            foreach (int stg in loggedS.Keys.OrderBy(k => k))
            {
                double[] logged = loggedS[stg];
                var p = new Pulses { Stg = stg };
                result.Add(p);
                if (logged.Length == 0)
                    continue;
                BitTrace best = null;
                double bestResidual = 0;
                foreach (BitTrace t in traces)
                {
                    if (t.Rise.Count != logged.Length)
                        continue;
                    double r = Residual(logged, t.Rise.Select(x => x / samplingRateHz).ToArray());
                    if (r > toleranceS)
                        continue;
                    p.Candidates++;
                    bool better = best == null
                        || (t.Bit == PreferredBit && best.Bit != PreferredBit)
                        || ((t.Bit == PreferredBit) == (best.Bit == PreferredBit) && t.MeanHighSamples < best.MeanHighSamples);
                    if (better)
                    {
                        best = t;
                        bestResidual = r;
                    }
                }
                if (best == null)
                    continue;
                p.Word = best.Word;
                p.Bit = best.Bit;
                p.MaxResidualMs = bestResidual * 1000;
                p.StartSample = best.Rise.ToArray();
                p.StartValue = best.RiseValue.ToArray();
                bool paired = best.Fall.Count == best.Rise.Count;
                for (int i = 0; paired && i < best.Rise.Count; i++)
                    paired = best.Fall[i] > best.Rise[i] && (i + 1 == best.Rise.Count || best.Fall[i] <= best.Rise[i + 1]);
                if (paired)
                {
                    p.StopSample = best.Fall.ToArray();
                    p.StopValue = best.FallValue.ToArray();
                }
            }
            return result;
        }

        // Every bit that changed, with its number of rising edges: the evidence for (or against) the assignment.
        public string Report()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            for (int w = 0; w < nWords; w++)
            {
                if (busy[w])
                    sb.AppendLine("  word " + w.ToString(inv) + ": changes almost every sample (not a sideband word)");
                else if (changeSample[w].Count == 0)
                    sb.AppendLine("  word " + w.ToString(inv) + ": constant 0x" + initial[w].ToString("X8", inv));
            }
            foreach (var g in Traces().GroupBy(t => t.Word))
                sb.AppendLine("  word " + g.Key.ToString(inv) + ": rising edges per bit: " +
                              string.Join(", ", g.Select(t => "bit " + t.Bit.ToString(inv) + " = " + t.Rise.Count.ToString(inv))));
            return sb.ToString();
        }
    }
}
