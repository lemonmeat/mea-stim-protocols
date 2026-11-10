using System;
using System.Collections.Generic;
using System.Linq;

namespace MEA2100_Stimulation
{
    // One waveform sent to one electrode through one STG output. A train is one delivery with several pulses.
    public sealed class Delivery
    {
        public int Stg;             // 1 or 2
        public uint Electrode;      // API electrode index
        public ulong DelayUs;       // start of the waveform after the trial start
        public int AmplitudeUv;     // amplitude that a level of 1 stands for, in uV as the API expects
        public Waveform Waveform;
    }

    public sealed class ScheduleSettings
    {
        public uint[] Sites;
        public double[] AmplitudesMv;       // run in the order given (lowest first)
        public Waveform[] Waveforms;
        public int TrialsPerCondition;      // per site, per waveform and per amplitude
        public bool Paired;                 // STG 1 and STG 2 on two different sites in one trial
        public double Stg2DelayMinMs;
        public double Stg2DelayMaxMs;
    }

    // Balanced, shuffled schedule. Nothing here touches the hardware.
    //
    // For each amplitude (in the order given), there are TrialsPerCondition blocks. A block holds every
    // (site, waveform) combination once, in a new random order, so sites and waveforms are interleaved
    // and each combination gets the same number of trials.
    public static class Schedule
    {
        public static List<List<Delivery>> Build(ScheduleSettings s, Random rng)
        {
            var trials = new List<List<Delivery>>();
            var combos = new List<KeyValuePair<uint, Waveform>>();
            foreach (uint site in s.Sites)
                foreach (Waveform w in s.Waveforms)
                    combos.Add(new KeyValuePair<uint, Waveform>(site, w));
            uint[] secondPool = Enumerable.Repeat(s.Sites, s.Waveforms.Length).SelectMany(x => x).ToArray();

            foreach (double mv in s.AmplitudesMv)
            for (int block = 0; block < s.TrialsPerCondition; block++)
            {
                int uv = (int)Math.Round(mv * 1000);
                KeyValuePair<uint, Waveform>[] order = Shuffled(combos.ToArray(), rng);
                if (!s.Paired)
                {
                    foreach (var c in order)
                    {
                        int stg = trials.Count % 2 == 0 ? 1 : 2;   // alternate the two outputs
                        trials.Add(new List<Delivery> { new Delivery { Stg = stg, Electrode = c.Key, DelayUs = 0, AmplitudeUv = uv, Waveform = c.Value } });
                    }
                    continue;
                }

                uint[] first = order.Select(c => c.Key).ToArray();
                uint[] second = Shuffled(secondPool, rng);
                SeparatePairs(first, second, rng);
                for (int i = 0; i < order.Length; i++)
                {
                    double delayMs = s.Stg2DelayMinMs + rng.NextDouble() * (s.Stg2DelayMaxMs - s.Stg2DelayMinMs);
                    ulong delayUs = (ulong)Math.Round(delayMs * 1000 / Waveform.ResolutionUs) * Waveform.ResolutionUs;
                    trials.Add(new List<Delivery>
                    {
                        new Delivery { Stg = 1, Electrode = first[i], DelayUs = 0, AmplitudeUv = uv, Waveform = order[i].Value },
                        new Delivery { Stg = 2, Electrode = second[i], DelayUs = delayUs, AmplitudeUv = uv, Waveform = order[i].Value },
                    });
                }
            }
            return trials;
        }

        public static T[] Shuffled<T>(T[] items, Random rng)
        {
            T[] a = (T[])items.Clone();
            for (int i = a.Length - 1; i > 0; i--)   // Fisher-Yates
            {
                int j = rng.Next(i + 1);
                T t = a[i]; a[i] = a[j]; a[j] = t;
            }
            return a;
        }

        // STG 1 and STG 2 must not stimulate the same electrode in one trial: swap within the block until they differ.
        public static void SeparatePairs(uint[] first, uint[] second, Random rng)
        {
            for (int i = 0; i < second.Length; i++)
            {
                if (second[i] != first[i])
                    continue;
                foreach (int j in Enumerable.Range(0, second.Length).OrderBy(_ => rng.Next()))
                {
                    if (j != i && second[j] != first[i] && second[i] != first[j])
                    {
                        uint t = second[i]; second[i] = second[j]; second[j] = t;
                        break;
                    }
                }
                if (second[i] == first[i])
                    throw new InvalidOperationException("Could not separate STG 1 and STG 2 sites.");
            }
        }

        // What the STG has to play for one delivery: steps in uV, their durations, and whether the
        // stimulation is "active" in each step. Active covers each whole pulse, including 0 V steps
        // inside it, so one pulse gives exactly one Start and one Stop in the sideband.
        public static void Expand(Delivery d, ulong tailUs, out int[] amplitudeUv, out ulong[] durationUs, out int[] active)
        {
            var amp = new List<int>();
            var dur = new List<ulong>();
            var act = new List<int>();
            if (d.DelayUs > 0)
            {
                amp.Add(0); dur.Add(d.DelayUs); act.Add(0);
            }
            int[] steps = d.Waveform.StepsUv(d.AmplitudeUv);
            for (int k = 0; k < d.Waveform.PulseCount; k++)
            {
                for (int i = 0; i < steps.Length; i++)
                {
                    amp.Add(steps[i]); dur.Add(d.Waveform.DurationsUs[i]); act.Add(1);
                }
                if (k < d.Waveform.PulseCount - 1)
                {
                    amp.Add(0); dur.Add(d.Waveform.PeriodUs - d.Waveform.PulseUs); act.Add(0);
                }
            }
            amp.Add(0); dur.Add(tailUs); act.Add(0);
            amplitudeUv = amp.ToArray();
            durationUs = dur.ToArray();
            active = act.ToArray();
        }
    }
}
