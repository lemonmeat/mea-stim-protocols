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
    // Random-site, random-timing stimulation with custom waveforms, a per-pulse log, and (optionally)
    // its own recording straight to HDF5 (see README.md).
    //
    // Each trial stimulates one electrode (single mode, STG 1 and STG 2 alternating) or two different
    // electrodes (paired mode, STG 1 then STG 2 after a random delay) with one of the Waveforms. Sites
    // and waveforms come from a balanced schedule (Schedule.cs). Trials are separated by random
    // intervals. Every pulse is written to <name>.stimlog.csv as it is sent, so the k-th logged pulse
    // on an STG output matches the k-th "STG n Single Pulse Start" event of the recording.
    //
    // With RecordToH5 the program records the session itself to <name>.h5 (Recorder.cs); Multi Channel
    // Experimenter must then be closed. Without it, Experimenter records as before.
    public partial class Form1 : Form
    {
        // ---------------- Session settings: edit before each session ----------------

        // false: record only (spontaneous activity) for SpontaneousMinutes; needs RecordToH5.
        private static readonly bool StimulationEnabled = true;

        // Sites to stimulate, as MCS electrode labels. null = every electrode except ExcludedLabels.
        private static readonly string[] SiteLabels = null;   // e.g. new[] { "47", "12", "33", "78" }
        private static readonly string[] ExcludedLabels = { "15" };   // 15 is the reference electrode

        // Waveforms to test. Every site gets TrialsPerCondition trials of each waveform at each amplitude,
        // interleaved in random order. Levels are fractions of the amplitude; see Waveform.cs for the
        // builders (Biphasic, Monophasic, Asymmetric, Custom, .Train). Examples:
        //     Waveform.Biphasic("bi500pos", 500, negativeFirst: false),
        //     Waveform.Biphasic("bi200gap", 200, gapUs: 100),
        //     Waveform.Asymmetric("asym200x4", 200, ratio: 4),
        //     Waveform.Custom("tri", new[] { -1, 0.5, 0.5 }, new ulong[] { 200, 200, 200 }),
        //     Waveform.Biphasic("bi200", 200).Train(3, 100),      // 3 pulses at 100 Hz
        // Default: the pulse used so far, negative first, 500 us per phase (Kumar et al. 2026: 1 ms, 1 V
        // peak to peak). Waveforms that are not charge-balanced are allowed but named in the start dialog.
        private static readonly Waveform[] Waveforms =
        {
            Waveform.Biphasic("bi500", 500),
        };

        private const int TrialsPerCondition = 50;     // trials per site, per waveform and per amplitude (per STG output too in paired mode)
        private static readonly bool PairedTrials = false;   // false: one site per trial; true: STG 1 and STG 2 on two different sites
        private const double Stg2DelayMinMs = 100;     // paired mode: STG 2 starts this long after STG 1 (random in [min, max])
        private const double Stg2DelayMaxMs = 300;
        private const double IntervalMinS = 2.0;       // random wait between the starts of consecutive trials, in [min, max]
        private const double IntervalMaxS = 4.0;

        // Amplitude that a waveform level of 1 stands for, in mV (voltage mode). CONFIRM for your culture
        // (an amplitude sweep, below, finds the lowest amplitude that evokes responses). The API itself
        // counts in uV: the first random-site session used 1000, which is only +-1 mV.
        private const double AmplitudeMv = 500;
        private const double MaxAmplitudeMv = 1000;    // safety cap: refuse to start above this
        private const ulong TailUs = 1000;             // zero output after the last pulse, before the trial's data ends

        // Amplitude sweep: null = off. Otherwise every condition is run at each amplitude, lowest first,
        // e.g. new double[] { 100, 250, 500, 1000 } with a few sites and TrialsPerCondition = 10.
        private static readonly double[] AmplitudeSweepMv = null;

        // Hardware settings, applied to ALL electrodes at the start, so the recording behaves the same
        // throughout. Blanking holds the amplifier inputs during each pulse: fewer saturated channels, but
        // the stimulated site no longer shows in the artifact. Turn it off for a session where the sites
        // must be confirmed from the recording.
        private static readonly bool HardwareBlanking = true;
        private static readonly bool AmplifierProtection = true;

        // Recording straight to <name>.h5. Close Multi Channel Experimenter first. With false, nothing is
        // recorded here and Experimenter must record, as before.
        private static readonly bool RecordToH5 = true;
        private const int SamplingRateHz = 20000;
        private const double PreStimS = 60;            // recorded before the first trial
        private const double PostStimS = 60;           // recorded after the last trial
        private const double SpontaneousMinutes = 30;  // length of a recording without stimulation
        private const int H5GzipLevel = 1;             // 0 = no compression (larger files, least CPU)

        private static readonly int? FixedSeed = null; // null: a new seed each session (it is logged); set a number to repeat a schedule
        private const string DeviceId = "E-00278";     // device used; E-00278, E-00281 and E-00303 share the wiring
        private const string ScriptVersion = "waveforms-h5-1";

        // -----------------------------------------------------------------------------

        private static readonly string[] IndexToLabel = ElectrodeMap.IndexToLabel;
        private static readonly uint ElectrodeCount = (uint)ElectrodeMap.IndexToLabel.Length;   // API electrode indices 0 .. ElectrodeCount - 1

        private CancellationTokenSource stimCancelToken;
        private readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private volatile string status = "";
        private volatile Recorder activeRecorder;
        private bool sessionRunning, closeWhenDone;

        public Form1()
        {
            InitializeComponent();
            btnStart.Text = StimulationEnabled ? (RecordToH5 ? "Start Recording + Stimulation" : "Start Stimulation") : "Start Recording";
            button1.Text = "Stop";
            statusTimer.Tick += (s, e) => ShowStatus();
            FormClosing += (s, e) =>
            {
                if (!sessionRunning)
                    return;
                e.Cancel = true;            // finish the files first, then close
                closeWhenDone = true;
                stimCancelToken?.Cancel();
            };
        }

        private void ShowStatus()
        {
            Recorder r = activeRecorder;
            string text = status;
            if (r != null)
                text += "\nRecorded " + r.RecordedS.ToString("F0", CultureInfo.InvariantCulture) + " s" +
                        (r.QueuedBlocks > 20 ? "; disk is behind by " + (r.QueuedBlocks / 10.0).ToString("F0", CultureInfo.InvariantCulture) + " s" : "") +
                        (r.ErrorCount > 0 ? "; " + r.ErrorCount + " device errors" : "");
            lblStatus.Text = text;
        }

        private async void BtnStartClick(object sender, EventArgs e)
        {
            uint[] sites;
            try
            {
                sites = StimulationEnabled ? ResolveSites() : new uint[0];
                CheckSettings(sites);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Settings problem:\n\n" + ex.Message);
                return;
            }

            string basePath = AskBasePath();
            if (basePath == null)
                return;
            if (RecordToH5 && File.Exists(basePath + ".h5"))
            {
                MessageBox.Show(basePath + ".h5 already exists. Recordings are never overwritten: choose another name.");
                return;
            }

            var inv = CultureInfo.InvariantCulture;
            int nTrials = sites.Length * TrialsPerCondition * Amplitudes().Length * Waveforms.Length;
            double minutes = StimulationEnabled
                ? nTrials * (IntervalMinS + IntervalMaxS) / 2 / 60 + (RecordToH5 ? (PreStimS + PostStimS) / 60 : 0)
                : SpontaneousMinutes;
            string unbalanced = string.Join(", ", Waveforms.Where(w => !w.IsChargeBalanced).Select(w => w.Name));
            string text =
                (RecordToH5 ? "Multi Channel Experimenter must be CLOSED: this program records to\n" + basePath + ".h5 at " + SamplingRateHz + " Hz.\n\n"
                            : "Is Multi Channel Experimenter already recording?\n\n") +
                (StimulationEnabled
                    ? sites.Length + " sites x " + Waveforms.Length + " waveforms (" + string.Join(", ", Waveforms.Select(w => w.Name)) + ") x " +
                      Amplitudes().Length + " amplitudes x " + TrialsPerCondition + " = " + nTrials + " trials (" + (PairedTrials ? "paired" : "single") + ").\n" +
                      "Amplitude: " + string.Join(", ", Amplitudes().Select(a => a.ToString(inv))) + " mV; hardware blanking " + (HardwareBlanking ? "on" : "off") + ".\n" +
                      (unbalanced.Length > 0 ? "NOT charge-balanced: " + unbalanced + ".\n" : "")
                    : "No stimulation.\n") +
                "About " + minutes.ToString("F0", inv) + " min.\n\n" +
                "Keep all files named " + Path.GetFileName(basePath) + ".* together.";
            if (MessageBox.Show(text, "Start session", MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;

            stimCancelToken = new CancellationTokenSource();
            CancellationToken token = stimCancelToken.Token;
            btnStart.Enabled = false;
            sessionRunning = true;
            statusTimer.Start();

            string summary = await Task.Run(() => RunSession(sites, basePath, token));

            statusTimer.Stop();
            sessionRunning = false;
            status = "";
            ShowStatus();
            btnStart.Enabled = true;
            MessageBox.Show(summary);
            if (closeWhenDone)
                Close();
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
            var inv = CultureInfo.InvariantCulture;
            if (!StimulationEnabled)
            {
                if (!RecordToH5)
                    throw new ArgumentException("StimulationEnabled and RecordToH5 are both false: nothing to do.");
                return;
            }
            if (sites.Length < (PairedTrials ? 3 : 1))
                throw new ArgumentException(PairedTrials
                    ? "Paired trials need at least 3 sites, so STG 1 and STG 2 never get the same electrode."
                    : "No sites to stimulate.");
            if (Waveforms.Length == 0)
                throw new ArgumentException("Waveforms is empty.");
            if (Waveforms.Select(w => w.Name).Distinct().Count() != Waveforms.Length)
                throw new ArgumentException("Two waveforms have the same name.");
            if (IntervalMaxS < IntervalMinS || Stg2DelayMaxMs < Stg2DelayMinMs)
                throw new ArgumentException("A maximum is smaller than its minimum.");
            foreach (double mv in Amplitudes())
                if (mv <= 0 || mv > MaxAmplitudeMv)
                    throw new ArgumentException("Amplitude " + mv.ToString(inv) + " mV is outside (0, " + MaxAmplitudeMv.ToString(inv) + "] mV.");
            double trialMs = (PairedTrials ? Stg2DelayMaxMs : 0) + (Waveforms.Max(w => w.TotalUs) + TailUs) / 1000.0;
            if (IntervalMinS * 1000 < trialMs + 50)
                throw new ArgumentException("IntervalMinS is shorter than the longest trial (" + trialMs.ToString("F0", inv) + " ms) plus 50 ms.");
        }

        // The amplitudes of this session, in mV: the sweep if set, else the one amplitude.
        private static double[] Amplitudes()
        {
            return AmplitudeSweepMv != null ? AmplitudeSweepMv.OrderBy(a => a).ToArray() : new[] { AmplitudeMv };
        }

        // Where the session's files go: the path without extension (<base>.h5, .stimlog.csv, .notes.yaml).
        private static string AskBasePath()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = RecordToH5 ? "Session name (recording, log and notes)" : "Session name (use the recording's base name)";
                dialog.Filter = "Session (name without extension)|*.*";
                dialog.OverwritePrompt = false;
                dialog.FileName = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture);
                if (dialog.ShowDialog() != DialogResult.OK)
                    return null;
                string path = dialog.FileName;
                foreach (string ext in new[] { ".stimlog.csv", ".notes.yaml", ".h5" })
                    if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        path = path.Substring(0, path.Length - ext.Length);
                return path;
            }
        }

        // Wait on the PC clock; false if the user stopped the session.
        private static bool Wait(double seconds, CancellationToken token)
        {
            return seconds <= 0 ? !token.IsCancellationRequested : !token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
        }

        private string RunSession(uint[] sites, string basePath, CancellationToken token)
        {
            var inv = CultureInfo.InvariantCulture;
            string logPath = basePath + ".stimlog.csv", notesPath = basePath + ".notes.yaml", h5Path = basePath + ".h5";
            int seed = FixedSeed ?? Environment.TickCount;
            var rng = new Random(seed);
            List<List<Delivery>> trials = StimulationEnabled
                ? Schedule.Build(new ScheduleSettings
                {
                    Sites = sites, AmplitudesMv = Amplitudes(), Waveforms = Waveforms, TrialsPerCondition = TrialsPerCondition,
                    Paired = PairedTrials, Stg2DelayMinMs = Stg2DelayMinMs, Stg2DelayMaxMs = Stg2DelayMaxMs,
                }, rng)
                : new List<List<Delivery>>();
            int done = 0;
            string error = null;
            var clock = Stopwatch.StartNew();
            var active = new List<uint>();
            var sentS = new Dictionary<int, List<double>> { { 1, new List<double>() }, { 2, new List<double>() } };   // PC time of every pulse, per output

            CMcsUsbListNet cDeviceList = new CMcsUsbListNet(DeviceEnumNet.MCS_MEAUSB_DEVICE);
            if (cDeviceList.Count == 0)
                return "No MEA USB device connected.";
            var deviceEntry = cDeviceList.GetUsbListEntry(0);
            CStg200xDownloadNet cStgDevice = StimulationEnabled ? new CStg200xDownloadNet() : null;
            Recorder recorder = null;

            if (RecordToH5)
            {
                status = "Starting the recording...";
                recorder = new Recorder();
                try
                {
                    recorder.Start(deviceEntry, h5Path, SamplingRateHz, DeviceId, ScriptVersion, H5GzipLevel);
                }
                catch (Exception ex)
                {
                    recorder.Dispose();
                    return "The recording could not be started; nothing was stimulated.\n\n" + ex.Message;
                }
                activeRecorder = recorder;
            }

            WriteNotes(notesPath, seed, sites, trials.Count, recorder);
            StreamWriter log = null;
            try
            {
                if (!StimulationEnabled)
                {
                    status = "Recording spontaneous activity (" + SpontaneousMinutes.ToString(inv) + " min). Stop ends it early.";
                    Wait(SpontaneousMinutes * 60, token);
                }
                else
                {
                    log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
                    log.WriteLine("trial,seq,stg,api_index,label,amplitude,unit,phase_us,waveform,pulse,offset_ms,interval_s,pc_time,pc_elapsed_s");

                    cStgDevice.Connect(deviceEntry, 0);
                    cStgDevice.SetVoltageMode();
                    SetUpAllElectrodes(cStgDevice);   // same hardware settings on every electrode, all switched off

                    bool go = true;
                    if (recorder != null)
                    {
                        status = "Recording before stimulation (" + PreStimS.ToString(inv) + " s)...";
                        go = Wait(PreStimS, token);
                    }

                    int seq = 0;
                    double interval = 0;
                    for (int t = 0; go && t < trials.Count && !token.IsCancellationRequested; t++)
                    {
                        status = "Trial " + (t + 1).ToString(inv) + " of " + trials.Count.ToString(inv);
                        // Switch off the previous trial's electrodes, so they are not stimulated again.
                        foreach (uint e in active)
                            DisableElectrode(cStgDevice, e);
                        active = trials[t].Select(p => p.Electrode).ToList();

                        double trialStart = clock.Elapsed.TotalSeconds;
                        SendTrial(cStgDevice, trials[t]);
                        double sent = clock.Elapsed.TotalSeconds;   // just after the start command
                        string pcTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", inv);

                        foreach (Delivery d in trials[t])
                            for (int k = 0; k < d.Waveform.PulseCount; k++)
                            {
                                seq++;
                                double offsetMs = (d.DelayUs + d.Waveform.PulseOffsetUs(k)) / 1000.0;
                                sentS[d.Stg].Add(sent + offsetMs / 1000);
                                log.WriteLine(string.Join(",",
                                    (t + 1).ToString(inv),
                                    seq.ToString(inv),
                                    d.Stg.ToString(inv),
                                    d.Electrode.ToString(inv),
                                    IndexToLabel[d.Electrode],
                                    d.AmplitudeUv.ToString(inv),
                                    "uV",
                                    d.Waveform.DurationsUs[0].ToString(inv),
                                    d.Waveform.Name,
                                    (k + 1).ToString(inv),
                                    offsetMs.ToString("F3", inv),
                                    interval.ToString("F3", inv),
                                    pcTime,
                                    sent.ToString("F4", inv)));
                            }
                        done = t + 1;

                        // Random wait before the next trial, measured from this trial's start.
                        interval = IntervalMinS + rng.NextDouble() * (IntervalMaxS - IntervalMinS);
                        if (!Wait(trialStart + interval - clock.Elapsed.TotalSeconds, token))
                            break;
                    }

                    if (recorder != null)
                    {
                        // After a stop, still record 1 s so the last pulse is in the file.
                        bool stopped = token.IsCancellationRequested;
                        status = stopped ? "Stopping..." : "Recording after stimulation (" + PostStimS.ToString(inv) + " s)...";
                        if (stopped) Thread.Sleep(1000); else Wait(PostStimS, token);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                if (cStgDevice != null)
                {
                    try
                    {
                        for (uint e = 0; e < ElectrodeCount; e++)
                            DisableElectrode(cStgDevice, e);
                    }
                    catch { }
                    try { cStgDevice.Disconnect(); } catch { }
                }
                if (log != null)
                    log.Dispose();
            }

            string recordingSummary = "";
            if (recorder != null)
            {
                status = "Closing the recording...";
                Recorder.Result result = null;
                try
                {
                    result = recorder.Stop(sentS.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
                }
                catch (Exception ex)
                {
                    error = (error == null ? "" : error + "\n") + "closing the recording: " + ex.Message;
                }
                activeRecorder = null;
                recorder.Dispose();
                if (result != null)
                {
                    recordingSummary = RecordingSummary(result, sentS);
                    AppendRecordingNotes(notesPath, result, sentS);
                }
            }

            AppendNotesEnd(notesPath, done, error ?? (token.IsCancellationRequested ? "stopped by user" : "completed"));
            string head = error != null ? "Error during the session:\n\n" + error + "\n\n" : "Session finished.\n\n";
            return head + (StimulationEnabled ? done + " of " + trials.Count + " trials sent.\nLog: " + logPath + "\n" : "") + recordingSummary;
        }

        private static string RecordingSummary(Recorder.Result r, Dictionary<int, List<double>> sentS)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("\nRecorded " + r.DurationS.ToString("F1", inv) + " s (" + r.Samples.ToString(inv) + " samples).");
            foreach (SidebandDecoder.Pulses p in r.Pulses)
            {
                int n = sentS[p.Stg].Count;
                if (n == 0)
                    continue;
                sb.AppendLine(p.Found
                    ? "STG " + p.Stg + ": all " + n + " pulses found in the recording (word " + p.Word + ", bit " + p.Bit + "; timing within " + p.MaxResidualMs.ToString("F1", inv) + " ms)."
                    : "STG " + p.Stg + ": the " + n + " pulses were NOT found in the sideband, so the file has NO events for this output. " +
                      "The raw words are in /Sideband/Words; see the notes file.");
            }
            if (r.Errors.Length > 0)
                sb.AppendLine(r.Errors.Length + " device/file errors (the recording may have gaps); first: " + r.Errors[0]);
            return sb.ToString();
        }

        // Upload one trial to the STG and start it. Each output gets its own waveform and sideband;
        // only the outputs used in this trial are triggered.
        private void SendTrial(CStg200xDownloadNet cStgDevice, List<Delivery> deliveries)
        {
            uint channelMap = 0;
            foreach (Delivery d in deliveries)
            {
                uint channel = (uint)(d.Stg - 1);
                ConfigureElectrode(cStgDevice, d.Electrode, d.Stg == 1 ? ElectrodeDacMuxEnumNet.Stg1 : ElectrodeDacMuxEnumNet.Stg2);

                int[] amplitude, stimulusActive;
                ulong[] duration;
                Schedule.Expand(d, TailUs, out amplitude, out duration, out stimulusActive);
                SendChannel(cStgDevice, channel, amplitude, duration, stimulusActive);
                channelMap |= 1u << (int)channel;
            }

            cStgDevice.SetupTrigger(0, new uint[] { channelMap }, new uint[] { channelMap }, new uint[] { 1 });
            cStgDevice.SendStart(1);
        }

        // stimulusActive: 1 while a pulse is being delivered (including 0 V steps inside the pulse), else 0.
        private void SendChannel(CStg200xDownloadNet cStgDevice, uint stgChannel, int[] amplitude, ulong[] duration, int[] stimulusActive)
        {
            uint Bit0Time = 40; // bit0 (blanking switch) activation duration prolongation in µs
            uint Bit3Time = 800; // bit3 (stimulation switch) activation duration prolongation in µs
            uint Bit4Time = 40; // bit4 (stimulus selection switch) activation duration prolongation in µs

            // 0x40: Stimulus Active
            // 0x80: Marker (minimal length: 200us)
            int[] sideband = stimulusActive.Select(x => x == 0 ? 0 : 0xc0).ToArray();

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
        private static void WriteNotes(string path, int seed, uint[] sites, int nTrials, Recorder recorder)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# Written by MEA2100_Stimulation (" + ScriptVersion + "). Fill in culture and div.");
            sb.AppendLine("culture: \"\"");
            sb.AppendLine("div: ");
            sb.AppendLine("device: " + DeviceId);
            sb.AppendLine("script_version: " + ScriptVersion);
            sb.AppendLine("started: " + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", inv));
            if (recorder != null)
                sb.AppendLine("recording: {file: \"" + Path.GetFileName(path).Replace(".notes.yaml", ".h5") + "\", sampling_rate_hz: " + SamplingRateHz.ToString(inv) +
                              ", compressed: " + (recorder.Compressed ? "true" : "false") + ", pre_stim_s: " + PreStimS.ToString(inv) +
                              ", post_stim_s: " + PostStimS.ToString(inv) + ", stream_layout: " + recorder.Layout + "}");
            if (!StimulationEnabled)
            {
                sb.AppendLine("mode: spontaneous");
                sb.AppendLine("planned_minutes: " + SpontaneousMinutes.ToString(inv));
            }
            else
            {
                sb.AppendLine("seed: " + seed.ToString(inv));
                sb.AppendLine("mode: " + (PairedTrials ? "paired" : "single"));
                sb.AppendLine("stimulation: {mode: voltage, amplitude_mv: [" + string.Join(", ", Amplitudes().Select(a => a.ToString(inv))) + "]}");
                sb.AppendLine("waveforms:   # levels are fractions of the amplitude");
                foreach (Waveform w in Waveforms)
                    sb.AppendLine("  - " + w.Describe());
                sb.AppendLine("hardware_blanking: " + (HardwareBlanking ? "true" : "false"));
                sb.AppendLine("amplifier_protection: " + (AmplifierProtection ? "true" : "false"));
                sb.AppendLine("interval_s: [" + IntervalMinS.ToString(inv) + ", " + IntervalMaxS.ToString(inv) + "]");
                if (PairedTrials)
                    sb.AppendLine("stg2_delay_ms: [" + Stg2DelayMinMs.ToString(inv) + ", " + Stg2DelayMaxMs.ToString(inv) + "]");
                sb.AppendLine("trials_per_condition: " + TrialsPerCondition.ToString(inv));
                sb.AppendLine("sites: [" + string.Join(", ", sites.Select(s => IndexToLabel[s])) + "]");
                sb.AppendLine("trials_planned: " + nTrials.ToString(inv));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // What the recording contains and how the pulses were found in it.
        private static void AppendRecordingNotes(string path, Recorder.Result r, Dictionary<int, List<double>> sentS)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("recorded_s: " + r.DurationS.ToString("F3", inv));
            sb.AppendLine("recorded_samples: " + r.Samples.ToString(inv));
            foreach (SidebandDecoder.Pulses p in r.Pulses)
                if (sentS[p.Stg].Count > 0)
                    sb.AppendLine("stg" + p.Stg + "_events: {pulses_sent: " + sentS[p.Stg].Count.ToString(inv) + ", found: " + (p.Found ? "true" : "false") +
                                  (p.Found ? ", word: " + p.Word.ToString(inv) + ", bit: " + p.Bit.ToString(inv) + ", max_residual_ms: " + p.MaxResidualMs.ToString("F2", inv) : "") +
                                  ", candidate_bits: " + p.Candidates.ToString(inv) + "}");
            sb.AppendLine("sideband_words: |");
            sb.Append(string.IsNullOrEmpty(r.SidebandReport) ? "  (none)" + Environment.NewLine : r.SidebandReport);
            sb.AppendLine("recording_errors: [" + string.Join(", ", r.Errors.Select(e => "\"" + e.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") + "\"")) + "]");
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // Appended at the end, so anything typed into the notes during the session is kept.
        private static void AppendNotesEnd(string path, int done, string status)
        {
            var inv = CultureInfo.InvariantCulture;
            File.AppendAllText(path,
                (StimulationEnabled ? "trials_sent: " + done.ToString(inv) + Environment.NewLine : "") +
                "ended: " + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", inv) + Environment.NewLine +
                "status: \"" + status.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") + "\"" + Environment.NewLine,
                new UTF8Encoding(false));
        }
    }
}
