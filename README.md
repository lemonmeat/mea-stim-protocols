# mea-stim-protocols

Stimulation scripts for recording sessions on the MCS MEA2100-Mini with the 3D-MIND array. A temporary tool: it exists to collect better test recordings until the lab's closed-loop testing suite replaces it.

**Status:** `MEA2100_Stimulation/Form1.cs` runs random-site, random-timing sessions and writes the log and notes below. It compiles against `McsUsbNet.dll`, and its scheduling logic has been tested, but it has **not yet been run on the hardware**.

## Running a session

1. Edit the settings block at the top of `Form1.cs`:
   - `SiteLabels`: electrode labels to stimulate (null = all except `ExcludedLabels`);
   - `PulsesPerSite`, `PairedTrials`, `IntervalMinS`/`IntervalMaxS`;
   - `AmplitudeUv`, `PhaseUs`;
   - `FixedSeed`.
2. Start recording in Multi Channel Experimenter.
3. Click **Start Stimulation**, save the log under the recording's base name, and confirm. The dialog shows the number of trials and the estimated duration.
4. **Stop Stimulation** ends the session early. The log and notes stay valid up to the last trial sent.

**Modes:**
- **Single** (default): one electrode per trial, alternating STG 1 and STG 2.
- **Paired:** STG 1 and STG 2 stimulate two different electrodes in the same trial, STG 2 after a random 100–300 ms delay. The script never puts the same electrode on both outputs in one trial.

**Schedule:** every site is used once per block, in a new random order each block, so all sites get the same number of pulses. Before each trial, the previous trial's electrodes are switched off, so only the logged electrodes are stimulated.

## Goal

Run stimulation sessions whose recordings can be analysed per stimulation site:

- **random electrode:** each pulse goes to an electrode chosen at random (balanced: every chosen site gets the same number of pulses);
- **random timing:** random intervals between pulses (e.g. 2–4 s), long enough for responses to finish;
- **20 kHz recording** of the raw stream;
- **known site for every pulse**, written to a log next to the recording.

## How a session works

```
C# script (McsUsbNet)            Multi Channel Experimenter
  chooses electrode + timing        records the raw stream at 20 kHz
  stimulates via the STG            and the STG Start/Stop events
  logs every pulse                  (these mark each pulse's time, not its electrode)
          │                                    │
          └──────────── same base name ────────┘
   <recording>.stimlog.csv  <recording>.notes.yaml  <recording>.h5
```

- Experimenter does the recording; the script only stimulates and logs. They run at the same time and share nothing but files.
- The recording does **not** store which electrode was stimulated. The log provides it.
- During analysis, the k-th logged pulse on STG 1 (or STG 2) is matched to the k-th recorded `STG 1 Single Pulse Start` event. The random intervals act as a fingerprint that confirms the match. PC clock times are not used for alignment.

## Files each session must produce

The analysis package (`meagraph`, in the ECE 398 (IW) project) will read these files. Treat this format as the contract between the two.

**`<recording>.stimlog.csv`**: one row per pulse, written and flushed as each pulse is sent.

| Column | Meaning | Example |
|---|---|---|
| `trial` | trial number, from 1 (two rows share it in paired mode) | 1 |
| `seq` | pulse number in the session, from 1 | 1 |
| `stg` | stimulator output that fired (must match the recorded events) | 1 |
| `api_index` | electrode index used in the McsUsbNet API | 0 |
| `label` | MCS electrode label for that index | 47 |
| `amplitude` | amplitude of each phase | 1000 |
| `unit` | amplitude unit: uV in voltage mode, nA in current mode (McsUsbNet docs) | uV |
| `phase_us` | duration of each phase of the biphasic pulse (negative first), in µs | 300 |
| `offset_ms` | start of this pulse after the trial start (0, or the STG 2 delay in paired mode) | 0.000 |
| `interval_s` | random wait between the previous trial's start and this one's | 2.731 |
| `pc_time` | PC time the trial was sent (for troubleshooting only) | 2026-10-08T14:03:11.204 |
| `pc_elapsed_s` | seconds since the session started, by the PC clock | 12.4031 |

**`<recording>.notes.yaml`**: one per session.
- culture and DIV;
- device ID (e.g. E-00303);
- voltage or current mode;
- sampling rate;
- random seed;
- script version (git commit);
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
- **Index table.** `IndexToLabel` lists indices 0–58. Label 57 is missing (probably index 59) and label 15 is included. 15 is the reference electrode on E-00303, so it is excluded from random sites by default. Confirm index 59 before adding it.
- **Amplitude.** The API counts voltage in µV, so the current `AmplitudeUv = 1000` means ±1 mV phases (Kumar et al. 2026 used 1 V peak-to-peak). Check that this is intended.
- **Device ID.** The index → label table was made on `E-00281`; recordings so far are from `E-00303`. The electrode → (row, col, layer) map in meagraph belongs to E-00303. Confirm the two devices are folded and wired identically before using E-00281 data.

## References

- McsUsbNet stimulation example: https://github.com/multichannelsystems/McsUsbNet_Examples/blob/master/Examples/CSharp/MEA2100_Stimulation/Form1.cs
- Analysis package and data format: the `meagraph` project (`docs/DATA_FORMAT.md`).
