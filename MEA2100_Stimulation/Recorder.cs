using Mcs.Usb;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace MEA2100_Stimulation
{
    // Records the 60 electrode channels of the MEA2100-Mini straight to an HDF5 file, without
    // Multi Channel Experimenter (which must be closed: only one program can read the data stream).
    //
    // The device set-up follows the McsUsbNet example "MEA2100_Recording_and_Stimulation". The data
    // stream delivers, per sample, the electrode channels, the analog inputs of the interface board,
    // digital words (which include the stimulator sideband) and checksum words. The electrode
    // channels and every word after the analog channels are saved; the pulse times are then found in
    // those words (SidebandDecoder).
    //
    // NOT YET RUN ON THE HARDWARE. See README, "First test on the hardware".
    public sealed class Recorder : IDisposable
    {
        public const int Electrodes = 60;
        private const uint InterfaceAnalogChannels = 8;     // as in the McsUsbNet example

        private readonly CMeaUSBDeviceNet dacq = new CMeaUSBDeviceNet();
        private readonly BlockingCollection<KeyValuePair<int[], int>> queue = new BlockingCollection<KeyValuePair<int[], int>>();
        private readonly List<string> errors = new List<string>();
        private readonly object readLock = new object();
        private McsH5Writer writer;
        private SidebandDecoder decoder;
        private Thread writeThread;
        private Exception writeError;
        private int stride, wordOffset, nWords, threshold;
        private volatile bool running;
        private bool connected;
        private long framesRead;

        public int SamplingRateHz { get; private set; }
        public string Layout { get; private set; }          // the stream layout the device reported, for the notes
        public bool Compressed { get { return writer != null && writer.Compressed; } }
        public double RecordedS { get { return SamplingRateHz > 0 ? Interlocked.Read(ref framesRead) / (double)SamplingRateHz : 0; } }
        public int QueuedBlocks { get { return queue.Count; } }
        public int ErrorCount { get { lock (errors) return errors.Count; } }
        public string[] Errors { get { lock (errors) return errors.ToArray(); } }

        public Recorder()
        {
            dacq.ChannelDataEvent += OnChannelData;
            dacq.ErrorEvent += OnError;
        }

        // Connect, configure the data stream, open the file and start sampling. Sample 0 of the file
        // is the first sample the device delivers.
        public void Start(CMcsUsbListEntryNet device, string h5Path, int samplingRateHz, string deviceId, string programVersion, int gzipLevel)
        {
            var inv = CultureInfo.InvariantCulture;
            if (1000000 % samplingRateHz != 0)
                throw new ArgumentException("The sampling rate must give a whole number of us per sample (e.g. 10000, 20000, 25000, 50000 Hz).");

            uint status = dacq.Connect(device);
            if (status != 0)
                throw new InvalidOperationException("Could not connect to the device for recording: " + CMcsUsbNet.GetErrorText(status) +
                                                    "\n\nIs Multi Channel Experimenter still open? Close it: only one program can record.");
            connected = true;
            dacq.StopDacq(0);   // in case an earlier program did not stop sampling

            new CSCUFunctionNet(dacq).SetDacqLegacyMode(false);
            dacq.SetSamplerate(samplingRateHz, 0, 0);
            int actualRate = dacq.GetSamplerate(0);
            if (actualRate != samplingRateHz)
                throw new InvalidOperationException("The device samples at " + actualRate.ToString(inv) + " Hz, not the requested " + samplingRateHz.ToString(inv) + " Hz.");
            SamplingRateHz = samplingRateHz;

            dacq.SetDataMode(DataModeEnumNet.Signed_32bit, 0);
            int resolution, resolutionUnit;
            dacq.GetResolutionPerDigit(0, DacqGroupChannelEnumNet.HeadstageElectrodeGroup, out resolution, out resolutionUnit);
            if (resolution <= 0)
                throw new InvalidOperationException("The device reported no resolution per digit for the electrode channels.");

            // One headstage: 60 electrode channels, then the interface board's analog inputs.
            dacq.SetNumberOfAnalogChannels(Electrodes, 0, 0, InterfaceAnalogChannels, 0);
            dacq.EnableDigitalIn(DigitalDatastreamEnableEnumNet.DigitalIn | DigitalDatastreamEnableEnumNet.DigitalOut |
                                 DigitalDatastreamEnableEnumNet.Hs1SidebandLow | DigitalDatastreamEnableEnumNet.Hs1SidebandHigh, 0);
            dacq.EnableChecksum(true, 0);

            // Counts are in 16-bit words; in 32-bit mode a channel takes two.
            int analog16, digital16, checksum16, timestamp16, block16;
            dacq.GetChannelLayout(out analog16, out digital16, out checksum16, out timestamp16, out block16, 0);
            stride = block16 / 2;
            wordOffset = analog16 / 2;
            nWords = stride - wordOffset;
            Layout = "{analog: " + (analog16 / 2).ToString(inv) + ", digital: " + (digital16 / 2).ToString(inv) +
                     ", checksum: " + (checksum16 / 2).ToString(inv) + ", timestamp: " + (timestamp16 / 2).ToString(inv) +
                     ", per_sample: " + stride.ToString(inv) + ", resolution_per_digit: " + resolution.ToString(inv) + "e-" + resolutionUnit.ToString(inv) + " V}";
            if (wordOffset < Electrodes || nWords < 1)
                throw new InvalidOperationException("Unexpected data stream layout " + Layout + ": expected at least " + Electrodes +
                                                    " analog channels followed by digital words.");

            threshold = samplingRateHz / 10;                // hand over data every 0.1 s
            dacq.ChannelBlock.Init(block16);
            dacq.ChannelBlock.AddBlocksAndChannels(ChannelBlockTypeNet.OneHandleOneQueue, stride, samplingRateHz * 10, threshold,
                                                   SampleSizeNet.SampleSize32Signed, SampleDstSizeNet.SampleDstSize32, 0, 0);
            dacq.ChannelBlock.SetCheckChecksum((uint)checksum16, (uint)timestamp16);

            writer = new McsH5Writer(h5Path, deviceId, programVersion, ElectrodeMap.IndexToLabel, 1000000 / samplingRateHz,
                                     resolution, -resolutionUnit, 24, nWords, gzipLevel, "Recorded by MEA2100_Stimulation; data stream layout " + Layout);
            decoder = new SidebandDecoder(nWords);
            writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "h5 writer" };
            writeThread.Start();

            running = true;
            dacq.StartDacq();
        }

        // Called by McsUsbNet on its own thread whenever `threshold` frames are waiting.
        private void OnChannelData(CMcsUsbDacqNet sender, int handle, int numFrames)
        {
            if (running)
                ReadAvailable(threshold);
        }

        private void ReadAvailable(int atLeast)
        {
            lock (readLock)
            {
                try
                {
                    while (dacq.ChannelBlock.AvailFrames(0, 0) >= Math.Max(atLeast, 1))
                    {
                        int got;
                        int[] data = dacq.ChannelBlock.ReadFramesI32(0, 0, threshold, out got);
                        if (got <= 0)
                            break;
                        Interlocked.Add(ref framesRead, got);
                        queue.Add(new KeyValuePair<int[], int>(data, got));
                    }
                }
                catch (Exception ex)
                {
                    OnError("reading the data stream: " + ex.Message, 0);
                }
            }
        }

        private void OnError(string message, int action)
        {
            lock (errors)
                if (errors.Count < 100)
                    errors.Add(RecordedS.ToString("F1", CultureInfo.InvariantCulture) + " s: " + message);
        }

        private void WriteLoop()
        {
            try
            {
                long sinceFlush = 0;
                foreach (KeyValuePair<int[], int> block in queue.GetConsumingEnumerable())
                {
                    writer.Append(block.Key, block.Value, stride, wordOffset);
                    decoder.Feed(block.Key, block.Value, stride, wordOffset);
                    sinceFlush += block.Value;
                    if (sinceFlush >= SamplingRateHz)       // about once per second
                    {
                        writer.Flush();
                        sinceFlush = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                writeError = ex;
                OnError("writing the file: " + ex.Message, 0);
            }
        }

        public sealed class Result
        {
            public long Samples;
            public double DurationS;
            public List<SidebandDecoder.Pulses> Pulses = new List<SidebandDecoder.Pulses>();
            public string SidebandReport = "";
            public string[] Errors = new string[0];
        }

        // Stop sampling, find the pulses of each output in the sideband, write them as events and
        // close the file. loggedS[stg]: PC times (s) of the pulses sent on that output, in order.
        public Result Stop(IDictionary<int, double[]> loggedS)
        {
            var result = new Result();
            if (running)
            {
                running = false;
                try { dacq.StopDacq(0); } catch (Exception ex) { OnError("stopping: " + ex.Message, 0); }
                ReadAvailable(1);                           // what arrived after the last callback
            }
            queue.CompleteAdding();
            if (writeThread != null)
                writeThread.Join();

            if (writer != null && writeError == null)
            {
                result.Pulses = decoder.Assign(loggedS, SamplingRateHz);
                result.SidebandReport = decoder.Report();
                writer.WriteEvents(result.Pulses.Where(p => p.Found).Select(p => new McsH5Writer.StgEvents
                {
                    Stg = p.Stg, StartSample = p.StartSample, StopSample = p.StopSample, StartValue = p.StartValue, StopValue = p.StopValue,
                }).ToList());
            }
            if (writer != null)
            {
                try { writer.Close(); } catch (Exception ex) { OnError("closing the file: " + ex.Message, 0); }
                result.Samples = writer.FramesWritten;
                result.DurationS = writer.FramesWritten / (double)SamplingRateHz;
            }
            Disconnect();
            result.Errors = Errors;
            return result;
        }

        private void Disconnect()
        {
            if (!connected)
                return;
            connected = false;
            try { dacq.Disconnect(); } catch { }
        }

        public void Dispose()
        {
            if (running)
            {
                running = false;
                try { dacq.StopDacq(0); } catch { }
            }
            if (!queue.IsAddingCompleted)
                queue.CompleteAdding();
            if (writeThread != null)
                writeThread.Join(5000);
            if (writer != null)
                try { writer.Close(); } catch { }
            Disconnect();
        }
    }
}
