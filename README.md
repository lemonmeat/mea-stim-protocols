# mea-stim-protocols

Stimulation scripts for recording sessions on the MCS MEA2100-Mini with the 3D-MIND array. A temporary tool: it exists to collect better test recordings until the lab's closed-loop testing suite replaces it.

**Status (branch `random-amplitude-sweep`, version `random-amplitude-sweep-1`):** the amplitude sweep now interleaves amplitudes with sites and waveforms in random order, instead of running them one after the other, lowest first. Everything else is as in `custom-waveforms-h5-recording` (`waveforms-h5-1`), which added custom waveforms and the program's own recording straight to `.h5`.

- **Rebuild in Visual Studio before a session.** The `.exe` in `bin/` was built from `waveforms-h5-1` and does not contain this change.
- The waveform, schedule, pulse-finding and HDF5 code is tested without hardware (`tests/`), and the analysis package reads the file it writes.
- `waveforms-h5-1` recorded the 2026-11-10 and 2026-11-12 sessions on the MEA2100: all pulses were found, and meagraph reads the files. This branch changes only the order of the trials. Do "First test on the hardware" below before trusting a recording from a new set-up.

## Running a session

1. Edit the settings block at the top of `Form1.cs`:
   - `StimulationEnabled`: `false` records spontaneous activity for `SpontaneousMinutes`;
   - `SiteLabels`: electrode labels to stimulate (null = all except `ExcludedLabels`);
   - `Waveforms`: the waveforms to test (see below);
   - `TrialsPerCondition`, `PairedTrials`, `IntervalMinS`/`IntervalMaxS`;
   - `AmplitudeMv` (what a waveform level of 1 means, in **mV**; capped by `MaxAmplitudeMv`);
   - `AmplitudeSweepMv` (optional: test several amplitudes, interleaved in random order);
   - `HardwareBlanking`, `AmplifierProtection` (applied to all 60 electrodes at the start);
   - `RecordToH5`, `SamplingRateHz`, `PreStimS`, `PostStimS`, `H5GzipLevel`;
   - `FixedSeed`, `DeviceId`.
2. With `RecordToH5 = true`: **close Multi Channel Experimenter** (only one program can read the device's data). With `false`: start recording in Experimenter, as before.
3. Click **Start**, choose the session name, and confirm. The dialog shows the trials, the estimated duration and any waveform that is not charge-balanced.
4. **Stop** ends the session early. The log, notes and recording stay valid up to the last trial sent. Closing the window during a session also stops it and finishes the files first.

A session writes `<name>.stimlog.csv`, `<name>.notes.yaml` and, with `RecordToH5`, `<name>.h5`. An existing `.h5` is never overwritten.

**Modes:**
- **Single** (default): one electrode per trial, alternating STG 1 and STG 2.
- **Paired:** STG 1 and STG 2 stimulate two different electrodes in the same trial with the same waveform, STG 2 after a random 100–300 ms delay. The script never puts the same electrode on both outputs in one trial.

**Schedule:** there are `TrialsPerCondition` blocks. A block contains every (site, waveform, amplitude) combination once, in a new random order. So sites, waveforms and amplitudes are interleaved, and every combination gets the same number of trials. The number of trials is sites × waveforms × amplitudes × `TrialsPerCondition`.
- Amplitudes are not run one after the other, because the culture changes during a session: active and silent periods, drift, and effects of the stimulation itself. Interleaved, these changes fall on every amplitude alike. Run one after the other, they would look like an effect of amplitude.
- The highest amplitude can come first, so list only amplitudes the culture is known to tolerate (`MaxAmplitudeMv` still caps them).
- In paired mode, both outputs use the trial's waveform and amplitude.
- `notes.yaml` records `amplitude_order: interleaved`. Sessions from earlier versions ran amplitudes lowest first.

Before each trial, the previous trial's electrodes are switched off, so only the logged electrodes are stimulated. Before each trial, the previous trial's electrodes are switched off, so only the logged electrodes are stimulated.

## Waveforms

A waveform is a list of voltage steps. Each step is a fraction of the amplitude (−1 to 1) and a duration in µs (a multiple of 20 µs, the stimulator's resolution). Builders in `Waveform.cs`:

| Code | Waveform |
|---|---|
| `Waveform.Biphasic("bi500", 500)` | −1 then +1, 500 µs each (the default, as before) |
| `Waveform.Biphasic("bi500pos", 500, negativeFirst: false)` | positive phase first |
| `Waveform.Biphasic("bi200gap", 200, gapUs: 100)` | 100 µs at 0 V between the phases |
| `Waveform.Monophasic("mono200", 200)` | one phase; **not charge-balanced** |
| `Waveform.Asymmetric("asym", 200, ratio: 4)` | −1 for 200 µs, then +0.25 for 800 µs (balanced) |
| `Waveform.Custom("tri", new[] { -1, 0.5, 0.5 }, new ulong[] { 200, 200, 200 })` | any steps |
| `Waveform.Biphasic("bi200", 200).Train(3, 100)` | 3 pulses at 100 Hz in one trial |

- Names go into the log, so they must be unique and contain no spaces or commas.
- A waveform that is not charge-balanced is allowed, but the start dialog names it. Unbalanced pulses can damage electrodes and cells; use them deliberately.
- Each pulse of a train gets its own log row and its own Start/Stop event. Pulses of a train must be at least 1 ms apart.
- The whole pulse, including 0 V steps inside it, counts as one pulse: one Start and one Stop event.
- The amplitude sweep multiplies every waveform. To compare shapes at different amplitudes within one waveform, use levels below 1.

## Recording to `.h5`

With `RecordToH5 = true` the program records the 60 electrode channels itself, from `PreStimS` before the first trial to `PostStimS` after the last, and writes `<name>.h5` directly. No export with Multi Channel DataManager is needed.

- **Format.** The file follows the layout that DataManager exports (`RawData` protocol 3), so `meagraph info/audit/detect/view` read it like any other recording. It has one analog stream: the raw data. There are no MCS filter streams, which the analysis does not use anyway.
- **Pulse times.** The device's data stream carries digital "sideband" words in which some bits are high while a stimulator output is active. At the end of the session the program looks for the bit of each output: the one that rises exactly once per logged pulse, at times that agree with the log. It writes those times as `STG n Single Pulse Start/Stop` events, exact to the sample. The notes file records which word and bit were used.
- **If the pulses are not found,** the file has no events for that output and the closing message says so. The unprocessed words are always saved in `/Sideband/Words`, so the events can still be recovered afterwards.
- **Size.** About 18 GB per hour uncompressed at 20 kHz; less with `H5GzipLevel = 1` (the default). How much less depends on the signal and has not been measured on a real recording.
- **If the program crashes,** the file should be readable up to about the last second, but HDF5 does not guarantee this.
- **The window shows** the seconds recorded, whether the disk is keeping up, and device errors. Device errors can mean lost samples; they are listed in the notes file.

### First test on the hardware

The recording has not been run on the device. These are the assumptions to check, in this order. A 2-minute session is enough: `SiteLabels = new[] { "47", "12" }`, `TrialsPerCondition = 5`, `PreStimS = PostStimS = 10`, a culture or a test model connected.

1. **It starts.** If the start fails, the message gives the reason and nothing is stimulated. The device set-up follows the McsUsbNet example `MEA2100_Recording_and_Stimulation` (60 electrode channels, 32-bit samples, digital and sideband words on). The notes file records the stream layout the device reported (`stream_layout`).
2. **Pulses are found.** The closing message must say "all N pulses found" for STG 1 and STG 2. If not, look at the `sideband_words` section of the notes file: it lists every digital bit and how often it rose, which shows whether any bit follows the pulses.
3. **Amplitudes are right.** `meagraph info` and `meagraph audit` on the new file: noise should be a few µV, as in an Experimenter recording of the same culture. The scale comes from the device (`resolution_per_digit` in the notes; Experimenter files have 8670e-12 V).
4. **Channels are in the right order.** The program assumes the data stream delivers the electrodes in the order of `ElectrodeMap.IndexToLabel`. Check with one session with `HardwareBlanking = false` and a low amplitude: `meagraph audit` must report that each logged site has the largest artifact on its own pulses.
5. **No lost samples.** The closing message must report no device errors, and the recorded duration must match the wall-clock duration of the session.
6. **Same result as Experimenter.** Record the same culture once with Experimenter and once with this program, and compare noise levels and spike counts per electrode.

## Goal

Run stimulation sessions whose recordings can be analysed per stimulation site:

- **random electrode:** each pulse goes to an electrode chosen at random (balanced: every chosen site gets the same number of pulses);
- **random timing:** random intervals between pulses (e.g. 2–4 s), long enough for responses to finish;
- **20 kHz recording** of the raw stream;
- **known site for every pulse**, written to a log next to the recording.

## How a session works

```
C# script (McsUsbNet)            Multi Channel Experimenter, or this program (RecordToH5)
  chooses electrode + timing        records the raw stream at 20 kHz
  stimulates via the STG            and the STG Start/Stop events
  logs every pulse                  (these mark each pulse's time, not its electrode)
          │                                    │
          └──────────── same base name ────────┘
   <recording>.stimlog.csv  <recording>.notes.yaml  <recording>.h5
```

- With `RecordToH5 = false`, Experimenter does the recording; the script only stimulates and logs. They run at the same time and share nothing but files. With `RecordToH5 = true`, the script records too and Experimenter stays closed.
- The recording does **not** store which electrode was stimulated. The log provides it.
- During analysis, the k-th logged pulse on STG 1 (or STG 2) is matched to the k-th recorded `STG 1 Single Pulse Start` event. The random intervals act as a fingerprint that confirms the match. PC clock times are not used for alignment.

## Files each session must produce

The analysis package (`meagraph`, in the ECE 398 (IW) project) reads these files (`meagraph.io.stimlog`; format also in its `docs/DATA_FORMAT.md`). Treat this format as the contract between the two.

**`<recording>.stimlog.csv`**: one row per pulse, written and flushed as each pulse is sent.

| Column | Meaning | Example |
|---|---|---|
| `trial` | trial number, from 1 (two rows share it in paired mode) | 1 |
| `seq` | pulse number in the session, from 1 | 1 |
| `stg` | stimulator output that fired (must match the recorded events) | 1 |
| `api_index` | electrode index used in the McsUsbNet API | 0 |
| `label` | MCS electrode label for that index | 47 |
| `amplitude` | amplitude that a waveform level of 1 stands for | 500000 |
| `unit` | amplitude unit: uV in voltage mode, nA in current mode (McsUsbNet docs) | uV |
| `phase_us` | duration of the waveform's first step, in µs | 500 |
| `waveform` | waveform name; its steps are in the notes file | bi500 |
| `pulse` | number of this pulse within its train, from 1 | 1 |
| `offset_ms` | start of this pulse after the trial start (STG 2 delay in paired mode, plus the position in a train) | 0.000 |
| `interval_s` | random wait between the previous trial's start and this one's | 2.731 |
| `pc_time` | PC time the trial was sent (for troubleshooting only) | 2026-10-08T14:03:11.204 |
| `pc_elapsed_s` | seconds since the session started when the trial's start command returned, by the PC clock | 12.4031 |

**`<recording>.notes.yaml`**: one per session.
- culture and DIV;
- device ID (e.g. E-00303);
- voltage or current mode;
- sampling rate;
- random seed;
- script version (git commit);
- the waveforms (steps and durations), and for a recording its settings, the stream layout and how the pulses were found;
- the blocks that ran;
- anything unusual.

## Suggested session design

| Block | Duration | Purpose |
|---|---|---|
| 1. Spontaneous baseline | 30 min | Connectivity from spontaneous activity |
| 2. Calibration | 5–10 min | Low-amplitude pulses on each chosen electrode: checks the index → label table and the artifact |
| 3. Random stimulation | 30–60 min | E.g. 16 electrodes (4 per layer) × 50–60 pulses, shuffled order, 2–4 s random intervals |
| 4. Spontaneous baseline | 30 min | Before/after comparison |

Choose an amplitude that evokes responses **without saturating the whole array**. If every channel saturates during pulses, the artifact can't confirm the stimulation site, and the blind period after each pulse gets longer.

## Things to check

- **Pulse counts.** After each session, the number of log rows per STG output must equal the number of `Single Pulse Start` events for that output in the `.h5`.
- **Site inside the recording (optional test).** Every recording has empty `STG 1/2 Marker Start/Stop` event entries. Test whether sync-out pulses sent from the API (`syncoutdata`) are recorded there. If they are, a marker's duration could encode the electrode index, so the `.h5` carries its own sites.
- **Index table.** `ElectrodeMap.IndexToLabel` now lists all 60 indices. Indices 0–58 were measured on E-00281. All of them agree with the ChannelID → label table that Experimenter wrote into the 2026-11-03 recordings (E-00278), and that table gives index 59 = label 57. So 57 is now a stimulation site by default. Label 15 (the reference electrode) stays excluded.
- **Amplitude.** The API counts voltage in µV. The first session (2026-10-08) sent 1000, which is only ±1 mV. The setting is now `AmplitudeMv`. Its default of ±500 mV with 500 µs phases follows Kumar et al. 2026 (1 V peak-to-peak, 1 ms). Confirm it, or find your own with an amplitude sweep.
- **2026-10-08 sessions:** the culture was not connected and the headstage was not closed, so those recordings contain no neural data. The stimulator findings below still hold.
- **Hardware settings on all electrodes (fixed in random-sites-2).** The first version set hardware blanking and amplifier protection only on an electrode when it was first stimulated. In the 2026-10-08 recording, each electrode's artifact fell from about 2.3 mV to 0.02 mV once it had been used. Electrode 57 (index 59, which is not in the table) was never set up, so it kept a large artifact on every pulse. All 60 electrodes are now set up identically before the first pulse.
- **Blanking hides the site.** With `HardwareBlanking = true`, the stimulated electrode does not stand out in the recording, so `meagraph audit` cannot confirm the logged sites. Run one session with it off to verify the index → label table, if the array does not saturate.
- **Keep the notes file.** Copy `<name>.notes.yaml` along with `<name>.stimlog.csv` into the recording's folder. The first session's notes were not copied.
- **Sampling rate.** The first session was recorded at 10 kHz. With `RecordToH5` the rate is `SamplingRateHz` (20 kHz); otherwise it is set in Multi Channel Experimenter.
- **Device ID.** The index → label table was made on `E-00281`; recordings so far are from `E-00303`. The electrode → (row, col, layer) map in meagraph belongs to E-00303. Confirm the two devices are folded and wired identically before using E-00281 data.

## Tests without hardware

`tests/WriterCheck` builds a schedule with several waveforms, plays it into a simulated data stream, and writes a recording and a log with the application's own code. `tests/check.py` then reads them back with h5py and, if installed, meagraph.

```
dotnet run --project tests/WriterCheck -- <output folder>
python tests/check.py <output folder>
```

It needs the .NET SDK (8 or later), not Visual Studio. On Windows and Linux the HDF5 library comes with the NuGet package. On an Apple-silicon Mac it does not: put a `libhdf5.dylib` (for example h5py's) on `DYLD_LIBRARY_PATH`.

## References

- McsUsbNet recording and stimulation example: https://github.com/multichannelsystems/McsUsbNet_Examples/blob/master/Examples/CSharp/MEA2100_Recording_and_Stimulation/Form1.cs
- McsUsbNet stimulation example: https://github.com/multichannelsystems/McsUsbNet_Examples/blob/master/Examples/CSharp/MEA2100_Stimulation/Form1.cs
- Analysis package and data format: the `meagraph` project (`docs/DATA_FORMAT.md`).
