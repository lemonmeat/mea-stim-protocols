using HDF.PInvoke;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MEA2100_Stimulation
{
    // Writes a recording straight to HDF5, in the layout that Multi Channel DataManager exports
    // (McsHdf5ProtocolType "RawData", version 3), so the analysis package (meagraph) reads it like
    // any other recording:
    //
    //   /Data/Recording_0/AnalogStream/Stream_0     raw electrode data: ChannelData (channels x samples,
    //                                               int32), InfoChannel, ChannelDataTimeStamps
    //   /Data/Recording_0/EventStream/Stream_0      "STG n Single Pulse Start/Stop" events (written at the end)
    //   /Sideband/Words                             the digital words of the data stream, unprocessed
    //
    // Differences from a DataManager file: strings in the tables are fixed-length, there is one
    // analog stream (raw; no MCS filter streams), and /Sideband is extra.
    //
    // Samples are buffered and written one whole chunk at a time. Flush() makes everything written so
    // far readable if the program dies; up to one chunk (about 0.1 s) is still in memory.
    public sealed class McsH5Writer : IDisposable
    {
        public const int ChunkFrames = 2048;
        private const string NullGuid = "00000000-0000-0000-0000-000000000000";

        private readonly int nChannels, nWords, tickUs;
        private readonly int[] chanBuf, wordBuf;     // [row * ChunkFrames + frame]
        private int fill;                            // frames in the buffers
        private long written;                        // frames in the file
        private long file = -1, recording = -1, analog = -1, chanData = -1, wordData = -1, timeStamps = -1, durationAttr = -1;
        private readonly string deviceId;

        public bool Compressed { get; private set; }
        public long FramesWritten { get { return written; } }
        public long FramesTotal { get { return written + fill; } }

        // labels[i]: MCS electrode label of data-stream channel i. Gain: volts per digit =
        // conversionFactor * 10^exponent.
        public McsH5Writer(string path, string deviceId, string programVersion, string[] labels, int tickUs,
                           long conversionFactor, int exponent, int adcBits, int nWords, int gzipLevel, string comment)
        {
            this.deviceId = deviceId;
            this.tickUs = tickUs;
            nChannels = labels.Length;
            this.nWords = nWords;
            chanBuf = new int[nChannels * ChunkFrames];
            wordBuf = new int[Math.Max(nWords, 1) * ChunkFrames];
            Compressed = gzipLevel > 0 && H5Z.filter_avail(H5Z.filter_t.DEFLATE) > 0;

            file = Check(H5F.create(path, H5F.ACC_EXCL), "create " + path);   // never overwrites a recording
            StringAttr(file, "GeneratingApplicationName", "MEA2100_Stimulation");
            StringAttr(file, "GeneratingApplicationVersion", programVersion);
            StringAttr(file, "McsHdf5ProtocolType", "RawData");
            Int32Attr(file, "McsHdf5ProtocolVersion", 3);

            long data = Group(file, "Data");
            DateTime now = DateTime.Now;
            StringAttr(data, "Comment", comment ?? "");
            StringAttr(data, "Date", now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture));
            Int64Attr(data, "DateInTicks", now.Ticks);
            StringAttr(data, "FileGUID", Guid.NewGuid().ToString());
            StringAttr(data, "MeaLayout", "ME21Combi60");
            StringAttr(data, "MeaName", "ME21Combi60");
            StringAttr(data, "ProgramName", "MEA2100_Stimulation");
            StringAttr(data, "ProgramVersion", programVersion);

            recording = Group(data, "Recording_0");
            H5G.close(data);
            Int32Attr(recording, "RecordingID", 0);
            Int64Attr(recording, "TimeStamp", 0);
            durationAttr = Int64Attr(recording, "Duration", 0, keepOpen: true);

            long streams = Group(recording, "AnalogStream");
            analog = Group(streams, "Stream_0");
            H5G.close(streams);
            StringAttr(analog, "Label", "Data Acquisition (1);MEA2100-Mini; Electrode Raw Data1");
            StringAttr(analog, "DataSubType", "Electrode");
            StringAttr(analog, "StreamType", "Analog");
            StringAttr(analog, "StreamGUID", Guid.NewGuid().ToString());
            StringAttr(analog, "SourceStreamGUID", NullGuid);
            Int32Attr(analog, "StreamInfoVersion", 1);

            var info = new Table()
                .Int32("ChannelID").Int32("RowIndex").Int32("GroupID").Int32("ElectrodeGroup")
                .String("Label", 32).String("RawDataType", 8).String("Unit", 8)
                .Int32("Exponent").Int32("ADZero").Int64("Tick").Int64("ConversionFactor").Int32("ADCBits")
                .String("HighPassFilterType", 8).String("HighPassFilterCutOffFrequency", 8).Int32("HighPassFilterOrder")
                .String("LowPassFilterType", 8).String("LowPassFilterCutOffFrequency", 8).Int32("LowPassFilterOrder");
            for (int i = 0; i < nChannels; i++)
                info.Row(i, i, 0, 0, deviceId + " " + labels[i], "Int", "V", exponent, 0, (long)tickUs, conversionFactor, adcBits,
                         "", "-1", -1, "", "-1", -1);
            info.Write(analog, "InfoChannel");

            chanData = Extendable(analog, "ChannelData", nChannels);
            timeStamps = Check(H5D.create(analog, "ChannelDataTimeStamps", H5T.NATIVE_INT64, Space(1, 3)), "ChannelDataTimeStamps");

            if (nWords > 0)
            {
                long sideband = Group(file, "Sideband");
                StringAttr(sideband, "Description",
                           "Words: the digital words of the data stream (rows) for every sample (columns), unprocessed. " +
                           "Same sample index as ChannelData.");
                wordData = Extendable(sideband, "Words", nWords);
                H5G.close(sideband);
            }
            Flush();
        }

        // Add frames. `data` holds `stride` words per frame: the electrode channels first, the
        // digital words at [wordOffset, wordOffset + nWords).
        public void Append(int[] data, int nFrames, int stride, int wordOffset)
        {
            for (int j = 0; j < nFrames; j++)
            {
                int at = j * stride;
                for (int c = 0; c < nChannels; c++)
                    chanBuf[c * ChunkFrames + fill] = data[at + c];
                for (int w = 0; w < nWords; w++)
                    wordBuf[w * ChunkFrames + fill] = data[at + wordOffset + w];
                if (++fill == ChunkFrames)
                    WriteBuffers();
            }
        }

        private void WriteBuffers()
        {
            if (fill == 0)
                return;
            WriteColumns(chanData, chanBuf, nChannels);
            if (wordData >= 0)
                WriteColumns(wordData, wordBuf, nWords);
            written += fill;
            fill = 0;
        }

        private void WriteColumns(long dataset, int[] buffer, int rows)
        {
            Check(H5D.set_extent(dataset, new[] { (ulong)rows, (ulong)(written + fill) }), "extend");
            long fileSpace = Check(H5D.get_space(dataset), "space");
            long memSpace = Space(rows, ChunkFrames);
            Check(H5S.select_hyperslab(fileSpace, H5S.seloper_t.SET, new[] { 0UL, (ulong)written }, null, new[] { (ulong)rows, (ulong)fill }, null), "select");
            Check(H5S.select_hyperslab(memSpace, H5S.seloper_t.SET, new[] { 0UL, 0UL }, null, new[] { (ulong)rows, (ulong)fill }, null), "select");
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { Check(H5D.write(dataset, H5T.NATIVE_INT32, memSpace, fileSpace, H5P.DEFAULT, pin.AddrOfPinnedObject()), "write samples"); }
            finally { pin.Free(); }
            H5S.close(memSpace);
            H5S.close(fileSpace);
        }

        // Make what is in the file consistent and readable: the sample count, the duration, and a flush to disk.
        public void Flush()
        {
            WriteArray(timeStamps, H5T.NATIVE_INT64, new long[] { 0, 0, written - 1 });
            WriteArray(durationAttr, H5T.NATIVE_INT64, new long[] { written * tickUs }, attribute: true);
            Check(H5F.flush(file, H5F.scope_t.GLOBAL), "flush");
        }

        // One "STG n Single Pulse" output: start and stop times in samples, and the sideband word at each.
        public sealed class StgEvents
        {
            public int Stg;
            public long[] StartSample, StopSample;
            public int[] StartValue, StopValue;
        }

        // The event stream, in the MCS layout: InfoEvent lists the eight STG entities; EventEntity_<id>
        // exists for those that have events (rows: time in us, duration, sideband word, -1, -1).
        public void WriteEvents(IList<StgEvents> events)
        {
            long streams = Group(recording, "EventStream");
            long stream = Group(streams, "Stream_0");
            H5G.close(streams);
            StringAttr(stream, "Label", "Stimulator (1);Stimulator; STG Events1");
            StringAttr(stream, "DataSubType", "StgSideband");
            StringAttr(stream, "StreamType", "Event");
            StringAttr(stream, "StreamGUID", Guid.NewGuid().ToString());
            StringAttr(stream, "SourceStreamGUID", NullGuid);
            Int32Attr(stream, "StreamInfoVersion", 1);

            string[] kinds = { "Single Pulse Start", "Single Pulse Stop", "Marker Start", "Marker Stop" };
            var info = new Table().Int32("EventID").Int32("GroupID").String("Label", 64).String("RawDataType", 8)
                .Int32("RawDataBytes").String("SourceChannelIDs", 8).String("SourceChannelLabels", 64);
            for (int stg = 1; stg <= 2; stg++)
                for (int k = 0; k < 4; k++)
                    info.Row((stg - 1) * 4 + k + 1, 0, deviceId + "  STG " + stg + " " + kinds[k], "Int", 4,
                             (69 + stg).ToString(CultureInfo.InvariantCulture), deviceId + " Sideband Data " + (stg - 1));
            info.Write(stream, "InfoEvent");

            foreach (StgEvents e in events)
            {
                if (e.Stg < 1 || e.Stg > 2)
                    continue;
                WriteEntity(stream, (e.Stg - 1) * 4 + 1, e.StartSample, e.StartValue);
                WriteEntity(stream, (e.Stg - 1) * 4 + 2, e.StopSample, e.StopValue);
            }
            H5G.close(stream);
        }

        private void WriteEntity(long stream, int eventId, long[] samples, int[] values)
        {
            if (samples == null || samples.Length == 0)
                return;
            int n = samples.Length;
            var block = new long[5 * n];
            for (int i = 0; i < n; i++)
            {
                block[i] = samples[i] * tickUs;
                block[2 * n + i] = values != null && i < values.Length ? values[i] : 0;
                block[3 * n + i] = -1;
                block[4 * n + i] = -1;
            }
            long d = Check(H5D.create(stream, "EventEntity_" + eventId.ToString(CultureInfo.InvariantCulture), H5T.NATIVE_INT64, Space(5, n)), "event entity");
            WriteArray(d, H5T.NATIVE_INT64, block);
            H5D.close(d);
        }

        public void Close()
        {
            if (file < 0)
                return;
            WriteBuffers();
            Flush();
            H5D.close(chanData);
            if (wordData >= 0) H5D.close(wordData);
            H5D.close(timeStamps);
            H5A.close(durationAttr);
            H5G.close(analog);
            H5G.close(recording);
            H5F.close(file);
            file = -1;
        }

        public void Dispose() { Close(); }

        // ---------------- HDF5 helpers ----------------

        private static long Check(long id, string what)
        {
            if (id < 0)
                throw new InvalidOperationException("HDF5 error: " + what);
            return id;
        }

        private static int Check(int status, string what)
        {
            if (status < 0)
                throw new InvalidOperationException("HDF5 error: " + what);
            return status;
        }

        private static long Group(long parent, string name)
        {
            return Check(H5G.create(parent, name), "group " + name);
        }

        private static long Space(params int[] dims)
        {
            var d = new ulong[dims.Length];
            for (int i = 0; i < dims.Length; i++) d[i] = (ulong)dims[i];
            return Check(H5S.create_simple(d.Length, d, null), "dataspace");
        }

        // A (rows x unlimited) int32 dataset, one chunk per ChunkFrames samples.
        private long Extendable(long parent, string name, int rows)
        {
            long space = Check(H5S.create_simple(2, new[] { (ulong)rows, 0UL }, new[] { (ulong)rows, H5S.UNLIMITED }), "dataspace");
            long plist = Check(H5P.create(H5P.DATASET_CREATE), "property list");
            Check(H5P.set_chunk(plist, 2, new[] { (ulong)rows, (ulong)ChunkFrames }), "chunk");
            if (Compressed)
            {
                Check(H5P.set_shuffle(plist), "shuffle");
                Check(H5P.set_deflate(plist, 1), "deflate");
            }
            long d = Check(H5D.create(parent, name, H5T.NATIVE_INT32, space, H5P.DEFAULT, plist), "dataset " + name);
            H5P.close(plist);
            H5S.close(space);
            return d;
        }

        private static void WriteArray(long target, long type, long[] values, bool attribute = false)
        {
            GCHandle pin = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                Check(attribute ? H5A.write(target, type, pin.AddrOfPinnedObject())
                                : H5D.write(target, type, H5S.ALL, H5S.ALL, H5P.DEFAULT, pin.AddrOfPinnedObject()), "write");
            }
            finally { pin.Free(); }
        }

        private static long StringType(int size)
        {
            long t = Check(H5T.copy(H5T.C_S1), "string type");
            Check(H5T.set_size(t, new IntPtr(size)), "string size");
            Check(H5T.set_strpad(t, H5T.str_t.NULLPAD), "string padding");
            return t;
        }

        private static void StringAttr(long target, string name, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value.Length == 0 ? "\0" : value);
            long type = StringType(bytes.Length);
            long space = Check(H5S.create(H5S.class_t.SCALAR), "dataspace");
            long attr = Check(H5A.create(target, name, type, space), "attribute " + name);
            GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { Check(H5A.write(attr, type, pin.AddrOfPinnedObject()), "attribute " + name); }
            finally { pin.Free(); }
            H5A.close(attr);
            H5S.close(space);
            H5T.close(type);
        }

        private static void Int32Attr(long target, string name, int value)
        {
            long space = Check(H5S.create(H5S.class_t.SCALAR), "dataspace");
            long attr = Check(H5A.create(target, name, H5T.NATIVE_INT32, space), "attribute " + name);
            GCHandle pin = GCHandle.Alloc(new[] { value }, GCHandleType.Pinned);
            try { Check(H5A.write(attr, H5T.NATIVE_INT32, pin.AddrOfPinnedObject()), "attribute " + name); }
            finally { pin.Free(); }
            H5A.close(attr);
            H5S.close(space);
        }

        private static long Int64Attr(long target, string name, long value, bool keepOpen = false)
        {
            long space = Check(H5S.create(H5S.class_t.SCALAR), "dataspace");
            long attr = Check(H5A.create(target, name, H5T.NATIVE_INT64, space), "attribute " + name);
            WriteArray(attr, H5T.NATIVE_INT64, new[] { value }, attribute: true);
            H5S.close(space);
            if (keepOpen)
                return attr;
            H5A.close(attr);
            return -1;
        }

        // A table of int32, int64 and fixed-length string columns (an HDF5 compound dataset).
        private sealed class Table
        {
            private readonly List<string> names = new List<string>();
            private readonly List<int> sizes = new List<int>();
            private readonly List<char> kinds = new List<char>();   // 'i' int32, 'l' int64, 's' string
            private readonly List<object[]> rows = new List<object[]>();

            public Table Int32(string name) { return Add(name, 'i', 4); }
            public Table Int64(string name) { return Add(name, 'l', 8); }
            public Table String(string name, int size) { return Add(name, 's', size); }

            private Table Add(string name, char kind, int size)
            {
                names.Add(name); kinds.Add(kind); sizes.Add(size);
                return this;
            }

            public void Row(params object[] values)
            {
                if (values.Length != names.Count)
                    throw new ArgumentException("row has " + values.Length + " values for " + names.Count + " columns");
                rows.Add(values);
            }

            public void Write(long parent, string name)
            {
                int rowSize = 0;
                foreach (int s in sizes) rowSize += s;
                long type = Check(H5T.create(H5T.class_t.COMPOUND, new IntPtr(rowSize)), "table type");
                var stringTypes = new List<long>();
                int offset = 0;
                for (int c = 0; c < names.Count; c++)
                {
                    long member = kinds[c] == 'i' ? H5T.NATIVE_INT32 : kinds[c] == 'l' ? H5T.NATIVE_INT64 : StringType(sizes[c]);
                    if (kinds[c] == 's') stringTypes.Add(member);
                    Check(H5T.insert(type, names[c], new IntPtr(offset), member), "table column " + names[c]);
                    offset += sizes[c];
                }

                var buffer = new byte[rowSize * rows.Count];
                for (int r = 0; r < rows.Count; r++)
                {
                    offset = r * rowSize;
                    for (int c = 0; c < names.Count; c++)
                    {
                        object v = rows[r][c];
                        if (kinds[c] == 'i')
                            Buffer.BlockCopy(BitConverter.GetBytes(Convert.ToInt32(v)), 0, buffer, offset, 4);
                        else if (kinds[c] == 'l')
                            Buffer.BlockCopy(BitConverter.GetBytes(Convert.ToInt64(v)), 0, buffer, offset, 8);
                        else
                        {
                            byte[] s = Encoding.UTF8.GetBytes((string)v);
                            if (s.Length > sizes[c])
                                throw new ArgumentException("'" + v + "' is longer than the " + sizes[c] + " bytes of column " + names[c]);
                            Buffer.BlockCopy(s, 0, buffer, offset, s.Length);
                        }
                        offset += sizes[c];
                    }
                }

                long space = Check(H5S.create_simple(1, new[] { (ulong)rows.Count }, null), "dataspace");
                long d = Check(H5D.create(parent, name, type, space), "table " + name);
                GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try { Check(H5D.write(d, type, H5S.ALL, H5S.ALL, H5P.DEFAULT, pin.AddrOfPinnedObject()), "table " + name); }
                finally { pin.Free(); }
                H5D.close(d);
                H5S.close(space);
                H5T.close(type);
                foreach (long t in stringTypes) H5T.close(t);
            }
        }
    }
}
