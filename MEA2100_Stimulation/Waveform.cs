using System;
using System.Globalization;
using System.Linq;

namespace MEA2100_Stimulation
{
    // A stimulation waveform: a list of voltage steps, each a fraction of the session amplitude,
    // optionally repeated as a train. Nothing here touches the hardware.
    //
    //   Waveform.Biphasic("bi500", 500)                         -1 then +1, 500 us each
    //   Waveform.Biphasic("bi500pos", 500, negativeFirst: false)
    //   Waveform.Biphasic("bi200gap", 200, gapUs: 100)          100 us at 0 V between the phases
    //   Waveform.Monophasic("mono200", 200)                     NOT charge-balanced
    //   Waveform.Asymmetric("asym", 200, ratio: 4)              -1 for 200 us, then +0.25 for 800 us
    //   Waveform.Custom("tri", new[] { -1, 0.5, 0.5 }, new ulong[] { 200, 200, 200 })
    //   Waveform.Biphasic("bi500", 500).Train(3, 100)           3 pulses at 100 Hz; each pulse is logged
    public sealed class Waveform
    {
        public const ulong ResolutionUs = 20;       // STG time resolution (McsUsbNet documentation)
        public const ulong MinTrainGapUs = 1000;    // the stimulation switch stays closed 800 us after a pulse

        public string Name { get; }
        public double[] Levels { get; }             // per step, as a fraction of the amplitude, in [-1, 1]
        public ulong[] DurationsUs { get; }         // per step
        public int PulseCount { get; }              // pulses per trial; 1 = a single pulse
        public ulong PeriodUs { get; }              // train: start of one pulse to the start of the next

        private Waveform(string name, double[] levels, ulong[] durationsUs, int pulseCount, ulong periodUs)
        {
            if (string.IsNullOrEmpty(name) || name.Any(c => char.IsWhiteSpace(c) || c == ',' || c == '"' || c == ':'))
                throw new ArgumentException("Waveform name '" + name + "' must be non-empty, without spaces, commas, quotes or colons.");
            if (levels == null || durationsUs == null || levels.Length == 0 || levels.Length != durationsUs.Length)
                throw new ArgumentException(name + ": levels and durations must be non-empty and of equal length.");
            if (levels.Any(l => double.IsNaN(l) || Math.Abs(l) > 1))
                throw new ArgumentException(name + ": levels are fractions of the amplitude and must lie in [-1, 1].");
            if (levels[0] == 0 || levels[levels.Length - 1] == 0)
                throw new ArgumentException(name + ": the first and last step must not be 0 (zero steps are allowed only between them).");
            if (durationsUs.Any(d => d == 0 || d % ResolutionUs != 0))
                throw new ArgumentException(name + ": every duration must be a positive multiple of " + ResolutionUs + " us.");
            Name = name;
            Levels = (double[])levels.Clone();
            DurationsUs = (ulong[])durationsUs.Clone();
            PulseCount = pulseCount;
            PeriodUs = periodUs;
            if (pulseCount < 1)
                throw new ArgumentException(name + ": a train needs at least 1 pulse.");
            if (pulseCount > 1 && (periodUs % ResolutionUs != 0 || periodUs < PulseUs + MinTrainGapUs))
                throw new ArgumentException(name + ": the train period must be a multiple of " + ResolutionUs + " us and leave at least " +
                                            MinTrainGapUs + " us between pulses (pulse = " + PulseUs + " us, period = " + periodUs + " us).");
        }

        public static Waveform Custom(string name, double[] levels, ulong[] durationsUs)
        {
            return new Waveform(name, levels, durationsUs, 1, 0);
        }

        public static Waveform Biphasic(string name, ulong phaseUs, bool negativeFirst = true, ulong gapUs = 0)
        {
            double first = negativeFirst ? -1 : 1;
            return gapUs == 0
                ? Custom(name, new[] { first, -first }, new[] { phaseUs, phaseUs })
                : Custom(name, new[] { first, 0, -first }, new[] { phaseUs, gapUs, phaseUs });
        }

        public static Waveform Monophasic(string name, ulong phaseUs, bool negative = true)
        {
            return Custom(name, new[] { negative ? -1.0 : 1.0 }, new[] { phaseUs });
        }

        // Charge-balanced with unequal phases: the second phase is `ratio` times longer and `ratio` times smaller.
        public static Waveform Asymmetric(string name, ulong firstPhaseUs, double ratio, bool negativeFirst = true, ulong gapUs = 0)
        {
            if (ratio < 1)
                throw new ArgumentException(name + ": ratio must be at least 1.");
            double second = firstPhaseUs * ratio;
            if (Math.Abs(second - Math.Round(second)) > 1e-9)
                throw new ArgumentException(name + ": firstPhaseUs x ratio must be a whole number of us.");
            double first = negativeFirst ? -1 : 1;
            return gapUs == 0
                ? Custom(name, new[] { first, -first / ratio }, new[] { firstPhaseUs, (ulong)Math.Round(second) })
                : Custom(name, new[] { first, 0, -first / ratio }, new[] { firstPhaseUs, gapUs, (ulong)Math.Round(second) });
        }

        // This waveform repeated `count` times at `frequencyHz` (start to start).
        public Waveform Train(int count, double frequencyHz, string name = null)
        {
            if (PulseCount != 1)
                throw new ArgumentException(Name + " is already a train.");
            if (frequencyHz <= 0)
                throw new ArgumentException(Name + ": the train frequency must be positive.");
            ulong period = (ulong)Math.Round(1e6 / frequencyHz / ResolutionUs) * ResolutionUs;
            string n = name ?? Name + "_x" + count.ToString(CultureInfo.InvariantCulture) + "at" +
                       frequencyHz.ToString("0.###", CultureInfo.InvariantCulture) + "Hz";
            return new Waveform(n, Levels, DurationsUs, count, period);
        }

        public ulong PulseUs { get { ulong s = 0; foreach (ulong d in DurationsUs) s += d; return s; } }

        // From the start of the first pulse to the end of the last one.
        public ulong TotalUs { get { return (ulong)(PulseCount - 1) * PeriodUs + PulseUs; } }

        // Start of pulse k after the start of the waveform, in us.
        public ulong PulseOffsetUs(int k) { return (ulong)k * PeriodUs; }

        // Net charge of one pulse relative to its total charge: 0 = balanced, +-1 = monophasic.
        public double ChargeImbalance
        {
            get
            {
                double net = 0, total = 0;
                for (int i = 0; i < Levels.Length; i++)
                {
                    net += Levels[i] * DurationsUs[i];
                    total += Math.Abs(Levels[i]) * DurationsUs[i];
                }
                return net / total;
            }
        }

        public bool IsChargeBalanced { get { return Math.Abs(ChargeImbalance) < 1e-6; } }

        // The steps of one pulse in uV, as the STG expects, for a per-phase amplitude in uV.
        public int[] StepsUv(int amplitudeUv)
        {
            return Levels.Select(l => (int)Math.Round(l * amplitudeUv)).ToArray();
        }

        // One line of YAML for the notes file.
        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            string s = "{name: " + Name +
                       ", levels: [" + string.Join(", ", Levels.Select(l => l.ToString("0.######", inv))) + "]" +
                       ", durations_us: [" + string.Join(", ", DurationsUs.Select(d => d.ToString(inv))) + "]" +
                       ", charge_balanced: " + (IsChargeBalanced ? "true" : "false");
            if (PulseCount > 1)
                s += ", pulses: " + PulseCount.ToString(inv) + ", period_us: " + PeriodUs.ToString(inv);
            return s + "}";
        }
    }
}
