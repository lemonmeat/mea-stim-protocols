using Mcs.Usb;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;


namespace MEA2100_Stimulation
{
    // Random-site, random-timing stimulation with a per-pulse log (see README.md).
    //
    // Each trial stimulates one electrode (single mode, STG 1 and STG 2 alternating) or two different
    // electrodes (paired mode, STG 1 then STG 2 after a random delay). Sites come from a balanced
    // schedule: every site is used once per block, in a new random order each block. Trials are
    // separated by random intervals. Every pulse is written to <name>.stimlog.csv as it is sent, so the
    // k-th logged pulse on an STG output matches the k-th "STG n Single Pulse Start" event that
    // Multi Channel Experimenter records.
    public partial class Form1 : Form
    {
        // ---------------- Session settings: edit before each session ----------------

        // Sites to stimulate, as MCS electrode labels. null = every label in IndexToLabel except ExcludedLabels.
        private static readonly string[] SiteLabels = null;   // e.g. new[] { "47", "12", "33", "78" }
        private static readonly string[] ExcludedLabels = { "15" };   // 15 is the reference electrode on E-00303

        private const int PulsesPerSite = 50;          // single mode: pulses per site; paired mode: per site and per STG output
        private static readonly bool PairedTrials = false;   // false: one site per trial; true: STG 1 and STG 2 on two different sites
        private const double Stg2DelayMinMs = 100;     // paired mode: STG 2 pulse starts this long after STG 1 (random in [min, max])
        private const double Stg2DelayMaxMs = 300;
        private const double IntervalMinS = 2.0;       // random wait between the starts of consecutive trials, in [min, max]
        private const double IntervalMaxS = 4.0;

        // Pulse amplitude per phase, in mV (voltage mode). Default: Kumar et al. 2026 used 1 V peak-to-peak
        // biphasic pulses of 1 ms, i.e. +-500 mV and 500 us per phase. CONFIRM for your culture (an amplitude
        // sweep, below, finds the lowest amplitude that evokes responses). The API itself counts in uV:
        // the first random-site session used 1000, which is only +-1 mV.
        private const double AmplitudeMv = 500;
        private const double MaxAmplitudeMv = 1000;    // safety cap: refuse to start above this
        private const ulong PhaseUs = 500;             // duration of each phase of the biphasic pulse (negative first)
        private const ulong TailUs = 1000;             // zero output after the pulse, before the trial's data ends

        // Amplitude sweep: null = off. Otherwise every site gets PulsesPerSite pulses at each amplitude,
        // lowest first, e.g. new double[] { 100, 250, 500, 1000 } with a few sites and PulsesPerSite = 10.
        private static readonly double[] AmplitudeSweepMv = null;

        // Hardware settings, applied to ALL electrodes at the start, so the recording behaves the same
        // throughout. (The first session set them only on electrodes as they were first stimulated, so the
        // artifact changed during the session and the one electrode never stimulated, 57, kept a large artifact.)
        // Blanking holds the amplifier inputs during each pulse: fewer saturated channels, but the
        // stimulated site no longer shows in the artifact. Turn it off for a session where the sites must be
        // confirmed from the recording.
        private static readonly bool HardwareBlanking = true;
        private static readonly bool AmplifierProtection = true;
        private const uint ElectrodeCount = 60;        // API electrode indices 0 .. ElectrodeCount - 1

        private static readonly int? FixedSeed = null; // null: a new seed each session (it is logged); set a number to repeat a schedule
        private const string DeviceId = "E-00278";     // device used; E-00278, E-00281 and E-00303 share the wiring
        private const string ScriptVersion = "random-sites-2";

        // API electrode index -> MCS electrode label (measured on E-00281). Index 59 is not listed yet;
        // it is probably label 57, the only 60-electrode label missing here. Confirm before adding it.
        private static readonly string[] IndexToLabel =
        {
            "47", "48", "46", "45", "38", "37", "28", "36", "27", "17",
            "26", "16", "35", "25", "15", "14", "24", "34", "13", "23",
            "12", "22", "33", "21", "32", "31", "44", "43", "41", "42",
            "52", "51", "53", "54", "61", "62", "71", "63", "72", "82",
            "73", "83", "64", "74", "84", "85", "75", "65", "86", "76",
            "87", "77", "66", "78", "67", "68", "55", "56", "58",
        };

        // -----------------------------------------------------------------------------

        private CancellationTokenSource stimCancelToken;

        public Form1()
        {
            InitializeComponent();
        }

        // One stimulation pulse of a trial.
        private class Pulse
        {
            public int Stg;          // 1 or 2
            public uint Electrode;   // API electrode index
            public double OffsetMs;  // start of the pulse relative to the trial start
            public int AmplitudeUv;  // per phase, in uV as the API expects
        }

        private async void BtnStartClick(object sender, EventArgs e)
        {
            uint[] sites;
            try
            {
                sites = ResolveSites();
                CheckSettings(sites);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Settings problem:\n\n" + ex.Message);
                return;
            }

            string logPath = AskLogPath();
            if (logPath == null)
                return;

            int nTrials = sites.Length * PulsesPerSite * Amplitudes().Length;
            double minutes = nTrials * (IntervalMinS + IntervalMaxS) / 2 / 60;
            var answer = MessageBox.Show(
                "Is Multi Channel Experimenter already recording?\n\n" +
                sites.Length + " sites, " + nTrials + " trials (" + (PairedTrials ? "paired" : "single") + "), about " +
                minutes.ToString("F0", CultureInfo.InvariantCulture) + " min.\n\n" +
                "Amplitude: " + string.Join(", ", Amplitudes().Select(a => a.ToString(CultureInfo.InvariantCulture))) + " mV per phase, " +
                PhaseUs + " us phases; hardware blanking " + (HardwareBlanking ? "on" : "off") + ".\n\n" +
                "Keep the .stimlog.csv and .notes.yaml together with the recording.\nLog: " + logPath,
                "Start stimulation", MessageBoxButtons.YesNo);
            if (answer != DialogResult.Yes)
                return;

            stimCancelToken = new CancellationTokenSource();
            CancellationToken token = stimCancelToken.Token;
            btnStart.Enabled = false;

            string summary = await Task.Run(() => RunSession(sites, logPath, token));

            btnStart.Enabled = true;
            MessageBox.Show(summary);
        }

        private void button1_Click(object sender, EventArgs e)
        {
            stimCancelToken?.Cancel();
        }

        private static uint[] ResolveSites()
        {
            var labels = SiteLabels ?? IndexToLabel.Where(l => !ExcludedLabels.Contains(l)).ToArray();
            var sites = new List<uint>();
            foreach (string label in labels)
            {
                int index = Array.IndexOf(IndexToLabel, label);
                if (index < 0)
                    throw new ArgumentException("Electrode label " + label + " is not in IndexToLabel.");
                if (ExcludedLabels.Contains(label))
                    throw new ArgumentException("Electrode label " + label + " is excluded (reference electrode?).");
                sites.Add((uint)index);
            }
            return sites.Distinct().ToArray();
        }

        private static void CheckSettings(uint[] sites)
        {
            if (sites.Length < (PairedTrials ? 3 : 1))
                throw new ArgumentException(PairedTrials
                    ? "Paired trials need at least 3 sites, so STG 1 and STG 2 never get the same electrode."
                    : "No sites to stimulate.");
            if (IntervalMaxS < IntervalMinS || Stg2DelayMaxMs < Stg2DelayMinMs)
                throw new ArgumentException("A maximum is smaller than its minimum.");
            foreach (double mv in Amplitudes())
                if (mv <= 0 || mv > MaxAmplitudeMv)
                    throw new ArgumentException("Amplitude " + mv.ToString(CultureInfo.InvariantCulture) + " mV is outside (0, " +
                                                MaxAmplitudeMv.ToString(CultureInfo.InvariantCulture) + "] mV.");
            double trialMs = (PairedTrials ? Stg2DelayMaxMs : 0) + (2 * PhaseUs + TailUs) / 1000.0;
            if (IntervalMinS * 1000 < trialMs + 50)
                throw new ArgumentException("IntervalMinS is shorter than one trial (" + trialMs.ToString("F0", CultureInfo.InvariantCulture) + " ms) plus 50 ms.");
        }

        // The amplitudes of this session, in mV: the sweep if set, else the one amplitude.
        private static double[] Amplitudes()
        {
            return AmplitudeSweepMv != null ? AmplitudeSweepMv.OrderBy(a => a).ToArray() : new[] { AmplitudeMv };
        }

        private static string AskLogPath()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Stimulation log (use the recording's base name)";
                dialog.Filter = "Stimulation log (*.stimlog.csv)|*.stimlog.csv";
                dialog.FileName = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture) + ".stimlog.csv";
                if (dialog.ShowDialog() != DialogResult.OK)
                    return null;
                string path = dialog.FileName;
                if (!path.EndsWith(".stimlog.csv", StringComparison.OrdinalIgnoreCase))
                    path = Path.ChangeExtension(path, null) + ".stimlog.csv";
                return path;
            }
        }

        // Balanced, shuffled schedule: list of trials, each a list of pulses.
        private static List<List<Pulse>> BuildSchedule(uint[] sites, Random rng)
        {
            var trials = new List<List<Pulse>>();
            foreach (double mv in Amplitudes())
            for (int block = 0; block < PulsesPerSite; block++)
            {
                int uv = (int)Math.Round(mv * 1000);
                uint[] first = Shuffled(sites, rng);
                if (!PairedTrials)
                {
                    foreach (uint site in first)
                    {
                        int stg = trials.Count % 2 == 0 ? 1 : 2;   // alternate the two outputs
                        trials.Add(new List<Pulse> { new Pulse { Stg = stg, Electrode = site, OffsetMs = 0, AmplitudeUv = uv } });
                    }
                    continue;
                }

                uint[] second = Shuffled(sites, rng);
                SeparatePairs(first, second, rng);
                for (int i = 0; i < first.Length; i++)
                {
                    double delay = Stg2DelayMinMs + rng.NextDouble() * (Stg2DelayMaxMs - Stg2DelayMinMs);
                    trials.Add(new List<Pulse>
                    {
                        new Pulse { Stg = 1, Electrode = first[i], OffsetMs = 0, AmplitudeUv = uv },
                        new Pulse { Stg = 2, Electrode = second[i], OffsetMs = delay, AmplitudeUv = uv },
                    });
                }
            }
            return trials;
        }

        private static uint[] Shuffled(uint[] items, Random rng)
        {
            uint[] a = (uint[])items.Clone();
            for (int i = a.Length - 1; i > 0; i--)   // Fisher-Yates
            {
                int j = rng.Next(i + 1);
                uint t = a[i]; a[i] = a[j]; a[j] = t;
            }
            return a;
        }

        // STG 1 and STG 2 must not stimulate the same electrode in one trial: swap within the block until they differ.
        private static void SeparatePairs(uint[] first, uint[] second, Random rng)
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

        private string RunSession(uint[] sites, string logPath, CancellationToken token)
        {
            int seed = FixedSeed ?? Environment.TickCount;
            var rng = new Random(seed);
            List<List<Pulse>> trials = BuildSchedule(sites, rng);
            string notesPath = logPath.Substring(0, logPath.Length - ".stimlog.csv".Length) + ".notes.yaml";
            int done = 0;
            string error = null;
            var clock = Stopwatch.StartNew();
            var active = new List<uint>();

            CMcsUsbListNet cDeviceList = new CMcsUsbListNet(DeviceEnumNet.MCS_MEAUSB_DEVICE);
            if (cDeviceList.Count == 0)
                return "No MEA USB device connected.";
            var deviceEntry = cDeviceList.GetUsbListEntry(0);
            CStg200xDownloadNet cStgDevice = new CStg200xDownloadNet();

            WriteNotes(notesPath, seed, sites, trials.Count);
            using (var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true })
            {
                log.WriteLine("trial,seq,stg,api_index,label,amplitude,unit,phase_us,offset_ms,interval_s,pc_time,pc_elapsed_s");
                try
                {
                    cStgDevice.Connect(deviceEntry, 0);
                    cStgDevice.SetVoltageMode();
                    SetUpAllElectrodes(cStgDevice);   // same hardware settings on every electrode, all switched off

                    int seq = 0;
                    double interval = 0;
                    for (int t = 0; t < trials.Count && !token.IsCancellationRequested; t++)
                    {
                        // Switch off the previous trial's electrodes, so they are not stimulated again.
                        foreach (uint e in active)
                            DisableElectrode(cStgDevice, e);
                        active = trials[t].Select(p => p.Electrode).ToList();

                        double trialStart = clock.Elapsed.TotalSeconds;
                        SendTrial(cStgDevice, trials[t]);
                        string pcTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

                        foreach (Pulse p in trials[t])
                        {
                            seq++;
                            log.WriteLine(string.Join(",",
                                (t + 1).ToString(CultureInfo.InvariantCulture),
                                seq.ToString(CultureInfo.InvariantCulture),
                                p.Stg.ToString(CultureInfo.InvariantCulture),
                                p.Electrode.ToString(CultureInfo.InvariantCulture),
                                IndexToLabel[p.Electrode],
                                p.AmplitudeUv.ToString(CultureInfo.InvariantCulture),
                                "uV",
                                PhaseUs.ToString(CultureInfo.InvariantCulture),
                                p.OffsetMs.ToString("F3", CultureInfo.InvariantCulture),
                                interval.ToString("F3", CultureInfo.InvariantCulture),
                                pcTime,
                                trialStart.ToString("F4", CultureInfo.InvariantCulture)));
                        }
                        done = t + 1;

                        // Random wait before the next trial, measured from this trial's start.
                        interval = IntervalMinS + rng.NextDouble() * (IntervalMaxS - IntervalMinS);
                        double remaining = trialStart + interval - clock.Elapsed.TotalSeconds;
                        if (remaining > 0 && token.WaitHandle.WaitOne(TimeSpan.FromSeconds(remaining)))
                            break;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    try
                    {
                        for (uint e = 0; e < ElectrodeCount; e++)
                            DisableElectrode(cStgDevice, e);
                    }
                    catch { }
                    try { cStgDevice.Disconnect(); } catch { }
                }
            }

            AppendNotesEnd(notesPath, done, error ?? (token.IsCancellationRequested ? "stopped by user" : "completed"));
            string status = error != null ? "Error during stimulation:\n\n" + error + "\n\n" : "Stimulation finished.\n\n";
            return status + done + " of " + trials.Count + " trials sent.\nLog: " + logPath;
        }

        // Upload one trial to the STG and start it. Each output gets its own waveform and sideband;
        // only the outputs used in this trial are triggered.
        private void SendTrial(CStg200xDownloadNet cStgDevice, List<Pulse> pulses)
        {
            uint channelMap = 0;
            foreach (Pulse p in pulses)
            {
                uint channel = (uint)(p.Stg - 1);
                ConfigureElectrode(cStgDevice, p.Electrode, p.Stg == 1 ? ElectrodeDacMuxEnumNet.Stg1 : ElectrodeDacMuxEnumNet.Stg2);

                ulong delayUs = (ulong)Math.Round(p.OffsetMs * 1000);
                var amplitude = new List<int>();
                var duration = new List<ulong>();
                if (delayUs > 0)
                {
                    amplitude.Add(0);
                    duration.Add(delayUs);
                }
                amplitude.AddRange(new[] { -p.AmplitudeUv, p.AmplitudeUv, 0 });
                duration.AddRange(new[] { PhaseUs, PhaseUs, TailUs });
                SendChannel(cStgDevice, channel, amplitude.ToArray(), duration.ToArray());
                channelMap |= 1u << (int)channel;
            }

            cStgDevice.SetupTrigger(0, new uint[] { channelMap }, new uint[] { channelMap }, new uint[] { 1 });
            cStgDevice.SendStart(1);
        }

        private void SendChannel(CStg200xDownloadNet cStgDevice, uint stgChannel, int[] amplitude, ulong[] duration)
        {
            uint Bit0Time = 40; // bit0 (blanking switch) activation duration prolongation in µs
            uint Bit3Time = 800; // bit3 (stimulation switch) activation duration prolongation in µs
            uint Bit4Time = 40; // bit4 (stimulus selection switch) activation duration prolongation in µs

            // specify when stimulus is active
            int[] stimulusActive = amplitude.Select(x => x == 0 ? 0 : 1).ToArray();

            // 0x40: Stimulus Active
            // 0x80: Marker (minimal length: 200us)
            int[] sideband = amplitude.Select(x => x == 0 ? 0 : 0xc0).ToArray();

            cStgDevice.PrepareAndSendData(stgChannel, amplitude, duration, STG_DestinationEnumNet.channeldata_voltage);

            var sidebandData = cStgDevice.Stimulus.CreateSideband(stimulusActive, sideband, duration, Bit0Time, Bit3Time, Bit4Time);
            cStgDevice.PrepareAndSendData(stgChannel, sidebandData.Sideband, sidebandData.Duration, STG_DestinationEnumNet.syncoutdata);
        }

        // Per trial: route the electrode to its STG output and switch it on. Mode, blanking and amplifier
        // protection were set for every electrode at the start (SetUpAllElectrodes) and are not changed here.
        private void ConfigureElectrode(
            CStg200xDownloadNet cStgDevice,
            uint electrode,
            ElectrodeDacMuxEnumNet stgMux)
        {
            cStgDevice.SetElectrodeDacMuxHsOnIfb(electrode, 0, stgMux);

            cStgDevice.SetElectrodeEnableHsOnIfb(electrode, 0, true);
        }

        private static void SetUpAllElectrodes(CStg200xDownloadNet cStgDevice)
        {
            for (uint e = 0; e < ElectrodeCount; e++)
            {
                cStgDevice.SetElectrodeModeHsOnIfb(e, ElectrodeModeEnumNet.emAutomatic);
                cStgDevice.SetBlankingEnableHsOnIfb(e, HardwareBlanking);
                cStgDevice.SetEnableAmplifierProtectionSwitchHsOnIfb(e, AmplifierProtection);
                cStgDevice.SetElectrodeEnableHsOnIfb(e, 0, false);
            }
        }

        private static void DisableElectrode(CStg200xDownloadNet cStgDevice, uint electrode)
        {
            cStgDevice.SetElectrodeEnableHsOnIfb(electrode, 0, false);
        }

        // Session description next to the log, written at the start. Culture and DIV are filled in by hand.
        private static void WriteNotes(string path, int seed, uint[] sites, int nTrials)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# Written by MEA2100_Stimulation (" + ScriptVersion + "). Fill in culture and div.");
            sb.AppendLine("culture: \"\"");
            sb.AppendLine("div: ");
            sb.AppendLine("device: " + DeviceId);
            sb.AppendLine("script_version: " + ScriptVersion);
            sb.AppendLine("started: " + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", inv));
            sb.AppendLine("seed: " + seed.ToString(inv));
            sb.AppendLine("mode: " + (PairedTrials ? "paired" : "single"));
            sb.AppendLine("stimulation: {mode: voltage, amplitude_mv: [" + string.Join(", ", Amplitudes().Select(a => a.ToString(inv))) +
                          "], phase_us: " + PhaseUs.ToString(inv) + ", shape: biphasic negative-first}");
            sb.AppendLine("hardware_blanking: " + (HardwareBlanking ? "true" : "false"));
            sb.AppendLine("amplifier_protection: " + (AmplifierProtection ? "true" : "false"));
            sb.AppendLine("interval_s: [" + IntervalMinS.ToString(inv) + ", " + IntervalMaxS.ToString(inv) + "]");
            if (PairedTrials)
                sb.AppendLine("stg2_delay_ms: [" + Stg2DelayMinMs.ToString(inv) + ", " + Stg2DelayMaxMs.ToString(inv) + "]");
            sb.AppendLine("pulses_per_site: " + PulsesPerSite.ToString(inv));
            sb.AppendLine("sites: [" + string.Join(", ", sites.Select(s => IndexToLabel[s])) + "]");
            sb.AppendLine("trials_planned: " + nTrials.ToString(inv));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // Appended at the end, so anything typed into the notes during the session is kept.
        private static void AppendNotesEnd(string path, int done, string status)
        {
            var inv = CultureInfo.InvariantCulture;
            File.AppendAllText(path,
                "trials_sent: " + done.ToString(inv) + Environment.NewLine +
                "ended: " + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", inv) + Environment.NewLine +
                "status: \"" + status.Replace("\"", "'") + "\"" + Environment.NewLine,
                new UTF8Encoding(false));
        }
    }
}
