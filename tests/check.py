"""Read the files written by WriterCheck with h5py and (if installed) meagraph. Usage: check.py <dir>"""
import sys
from pathlib import Path

import h5py
import numpy as np

d = Path(sys.argv[1])
total, t1, t2 = (np.array(line.split(), dtype=np.int64) for line in (d / "check.truth.txt").read_text().splitlines())
total = int(total[0])
fails = 0


def expect(ok, what):
    global fails
    print(("ok    " if ok else "FAIL  ") + what)
    fails += not ok


with h5py.File(d / "check.h5") as f:
    g = f["Data/Recording_0/AnalogStream/Stream_0"]
    data = g["ChannelData"]
    expect(data.shape == (60, total) and data.dtype == np.int32, f"ChannelData is (60, {total}) int32")
    s = np.arange(total)
    expect(all(np.array_equal(data[c], c * 1000 + s % 977 - 400) for c in (0, 17, 59)), "sample values exact on channels 0, 17, 59")
    expect(g["ChannelDataTimeStamps"][:].tolist() == [[0, 0, total - 1]], "ChannelDataTimeStamps covers every sample")
    info = g["InfoChannel"][:]
    expect(info["Label"][59] == b"E-00278 57" and info["Unit"][0] == b"V" and info["Tick"][0] == 50
           and info["ConversionFactor"][0] == 8670 and info["Exponent"][0] == -12, "InfoChannel values")
    expect(f["Data/Recording_0"].attrs["Duration"] == total * 50, "Duration attribute")
    w = f["Sideband/Words"]
    expect(w.shape == (4, total), "sideband words saved for every sample")
    expect(np.array_equal(np.flatnonzero(np.diff((w[1] >> 6) & 1) == 1) + 1, t1), "raw sideband word reproduces the STG 1 pulse starts")
    ev = f["Data/Recording_0/EventStream/Stream_0"]
    expect(np.array_equal(ev["EventEntity_1"][0], t1 * 50) and np.array_equal(ev["EventEntity_5"][0], t2 * 50), "event times exact (us)")
    expect(set(np.unique(ev["EventEntity_1"][2]) & 0xFF) == {0xD9}, "sideband value stored with each start event")

try:
    from meagraph.io import load_session
    from meagraph.io.mcs_events import read_stim_events
    from meagraph.io.stimlog import align_stimlog, find_stimlog, read_stimlog
except ImportError:
    print("meagraph is not installed: skipped the meagraph checks")
else:
    rec = d / "check.h5"
    session = load_session(rec)
    r = session.recording
    expect(r.get_sampling_frequency() == 20000 and r.get_num_samples() == total and r.get_num_channels() == 59,
           "meagraph loads the session: 20 kHz, all samples, 59 electrodes (15 = reference dropped)")
    uv = r.get_traces(start_frame=1000, end_frame=1003, channel_ids=["47"], return_in_uV=True)[:, 0]
    expect(np.allclose(uv, (np.arange(1000, 1003) % 977 - 400) * 8670e-6), "meagraph scales samples to uV")
    stim = read_stim_events(rec)
    expect([s.source for s in stim] == ["STG 1", "STG 2"] and np.allclose(stim[0].onsets_s, t1 / 20000) and stim[0].offsets_s is not None,
           "meagraph reads the STG events")
    log = read_stimlog(find_stimlog(rec))
    aligned, reports = align_stimlog(stim, log)
    for rep in reports:
        print("      " + rep.summary())
    expect(all(rep.complete and rep.method == "order" for rep in reports), "meagraph matches every logged pulse to a recorded one")
    expect(all(set(a.pulse_sites) <= {"47", "37", "12", "57"} for a in aligned), "per-pulse sites come from the log")

print("ALL OK" if not fails else f"{fails} FAILED")
sys.exit(1 if fails else 0)
