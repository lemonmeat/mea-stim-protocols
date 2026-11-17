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
        public double[] AmplitudesMv;       // interleaved with sites and waveforms in random order
        public Waveform[] Waveforms;
        public int TrialsPerCondition;      // per site, per waveform and per amplitude
        public bool Paired;                 // STG 1 and STG 2 on two different sites in one trial
        public double Stg2DelayMinMs;
        public double Stg2DelayMaxMs;
    }

    // Balanced, shuffled schedule. Nothing here touches the hardware.
    //
    // There are TrialsPerCondition blocks. A block holds every (site, waveform, amplitude) combination
    // once, in a new random order, so sites, waveforms and amplitudes are interleaved and each
    // combination gets the same number of trials. Amplitudes are not run in blocks of their own: slow
    // changes in the culture (active and silent periods, drift, effects of the stimulation itself) then
    // fall on every amplitude alike instead of on whichever amplitude came at that time. The highest
    // amplitude can come first.
    public static class Schedule
    {
        private sealed class Condition
        {
            public uint Site;
            public Waveform Waveform;
            public int AmplitudeUv;
        }

        public static List<List<Delivery>> Build(ScheduleSettings s, Random rng)
        {
            var trials = new List<List<Delivery>>();
            var combos = new List<Condition>();
            foreach (double mv in s.AmplitudesMv)
                foreach (uint site in s.Sites)
                    foreach (Waveform w in s.Waveforms)
                        combos.Add(new Condition { Site = site, Waveform = w, AmplitudeUv = (int)Math.Round(mv * 1000) });
            uint[] secondPool = Enumerable.Repeat(s.Sites, s.Waveforms.Length * s.AmplitudesMv.Length).SelectMany(x => x).ToArray();

            for (int block = 0; block < s.TrialsPerCondition; block++)
            {
                Condition[] order = Shuffled(combos.ToArray(), rng);
                if (!s.Paired)
                {
                    foreach (Condition c in order)
                    {
                        int stg = trials.Count % 2 == 0 ? 1 : 2;   // alternate the two outputs
                        trials.Add(new List<Delivery> { new Delivery { Stg = stg, Electrode = c.Site, DelayUs = 0, AmplitudeUv = c.AmplitudeUv, Waveform = c.Waveform } });
                    }
                    continue;
                }

                // Paired: both outputs use the trial's waveform and amplitude.
                uint[] first = order.Select(c => c.Site).ToArray();
                uint[] second = Shuffled(secondPool, rng);
                SeparatePairs(first, second, rng);
                for (int i = 0; i < order.Length; i++)
                {
                    double delayMs = s.Stg2DelayMinMs + rng.NextDouble() * (s.Stg2DelayMaxMs - s.Stg2DelayMinMs);
                    ulong delayUs = (ulong)Math.Round(delayMs * 1000 / Waveform.ResolutionUs) * Waveform.ResolutionUs;
                    trials.Add(new List<Delivery>
                    {
                        new Delivery { Stg = 1, Electrode = first[i], DelayUs = 0, AmplitudeUv = order[i].AmplitudeUv, Waveform = order[i].Waveform },
                        new Delivery { Stg = 2, Electrode = second[i], DelayUs = delayUs, AmplitudeUv = order[i].AmplitudeUv, Waveform = order[i].Waveform },
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
