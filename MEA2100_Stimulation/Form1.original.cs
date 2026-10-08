using Mcs.Usb;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;


namespace MEA2100_Stimulation
{
    public partial class Form1 : Form
    {
        private CancellationTokenSource stimCancelToken;

        public Form1()
        {
            InitializeComponent();
        }

        private async void BtnStartClick(object sender, EventArgs e)
        {
            stimCancelToken = new CancellationTokenSource();
            CancellationToken token = stimCancelToken.Token;

            await Task.Run(() =>
            {
                RunStimulationLoop(token);
            });

            MessageBox.Show("Stimulation stopped.");
        }

        private void button1_Click(object sender, EventArgs e)
        {
            stimCancelToken?.Cancel();
        }

        private void RunStimulationLoop(CancellationToken token)
        {
            CMcsUsbListNet cDeviceList =
                new CMcsUsbListNet(DeviceEnumNet.MCS_MEAUSB_DEVICE);

            if (cDeviceList.Count == 0)
                return;

            var deviceEntry = cDeviceList.GetUsbListEntry(0);
            CStg200xDownloadNet cStgDevice = new CStg200xDownloadNet();

            try
            {
                cStgDevice.Connect(deviceEntry, 0);
                cStgDevice.SetVoltageMode();

                uint[] electrodesPattern1 = new uint[] { 0 };
                uint[] electrodesPattern2 = new uint[] { 1 };
                uint[] electrodesPattern3 = new uint[] { 2 };
                uint[] electrodesPattern4 = new uint[] { 3 };

                // electrodes to be defined
                //Index 0 = E-00281 47
                //Index 1 = E - 00281 48
                //Index 2 = E - 00281 46
                //Index 3 = E - 00281 45
                //Index 4 = E - 00281 38
                //Index 5 = E - 00281 37
                //Index 6 = E - 00281 28
                //Index 7 = E - 00281 36
                //Index 8 = E - 00281 27
                //Index 9 = E - 00281 17
                //Index 10 = E - 00281 26
                //Index 11 = E - 00281 16
                //Index 12 = E - 00281 35
                //Index 13 = E - 00281 25
                //Index 14 = E - 00281 15
                //Index 15 = E - 00281 14
                //Index 16 = E - 00281 24
                //Index 17 = E - 00281 34
                //Index 18 = E - 00281 13
                //Index 19 = E - 00281 23
                //Index 20 = E - 00281 12
                //Index 21 = E - 00281 22
                //Index 22 = E - 00281 33
                //Index 23 = E - 00281 21
                //Index 24 = E - 00281 32
                //Index 25 = E - 00281 31
                //Index 26 = E - 00281 44
                //Index 27 = E - 00281 43
                //Index 28 = E - 00281 41
                //Index 29 = E - 00281 42
                //Index 30 = E - 00281 52
                //Index 31 = E - 00281 51
                //Index 32 = E - 00281 53
                //Index 33 = E - 00281 54
                //Index 34 = E - 00281 61
                //Index 35 = E - 00281 62
                //Index 36 = E - 00281 71
                //Index 37 = E - 00281 63
                //Index 38 = E - 00281 72
                //Index 39 = E - 00281 82
                //Index 40 = E - 00281 73
                //Index 41 = E - 00281 83
                //Index 42 = E - 00281 64
                //Index 43 = E - 00281 74
                //Index 44 = E - 00281 84
                //Index 45 = E - 00281 85
                //Index 46 = E - 00281 75
                //Index 47 = E - 00281 65
                //Index 48 = E - 00281 86
                //Index 49 = E - 00281 76
                //Index 50 = E - 00281 87
                //Index 51 = E - 00281 77
                //Index 52 = E - 00281 66
                //Index 53 = E - 00281 78
                //Index 54 = E - 00281 67
                //Index 55 = E - 00281 68
                //Index 56 = E - 00281 55
                //Index 57 = E - 00281 56
                //Index 58 = E - 00281 58

                while (!token.IsCancellationRequested) // for (int cycle = 0; cycle < 20; cycle++) -> if you want to specify number of cycles
                {
                    // Pattern 1 + Pattern 2 together
                    SendTwoPatternsTogether(
                        cStgDevice,

                        electrodesPattern1,
                        ElectrodeDacMuxEnumNet.Stg1,
                        0,
                        new int[] { -1000, 1000, 0 },
                        new ulong[] { 300, 300, 299400 },

                        electrodesPattern2,
                        ElectrodeDacMuxEnumNet.Stg2,
                        1,
                        new int[] { 0,-1000, 1000, 0 },
                        new ulong[] { 99700,300, 300, 199700 }
                    );


                    // if you want to repeat the number of cycles
                    //List<int> amplitudeList = new List<int>();
                    //List<ulong> durationList = new List<ulong>();

                    //for (int i = 0; i < 10; i++)
                    //{
                    //    // Pattern A
                    //    amplitudeList.AddRange(new int[]{-500, 500, 0});

                    //    durationList.AddRange(new ulong[]{300, 300, 100000});

                    //    // Pattern B
                    //    amplitudeList.AddRange(new int[]
                    //    {-800, 800, 0});

                    //    durationList.AddRange(new ulong[]
                    //    {300, 300, 100000});
                    //}

                    //int[] amplitude = amplitudeList.ToArray();
                    //ulong[] duration = durationList.ToArray();
                    //int[] stimulusActive = amplitude.Select(x => x == 0 ? 0 : 1).ToArray();
                    //int[] sideband = new int[amplitude.Length];


                    Thread.Sleep(300);

                    if (token.IsCancellationRequested)
                        break;

                    // Pattern 3 + Pattern 4 together
                    SendTwoPatternsTogether(
                        cStgDevice,

                        electrodesPattern3,
                        ElectrodeDacMuxEnumNet.Stg1,
                        0,
                        new int[] { -1000, 1000, 0 },
                        new ulong[] { 300, 300, 699400 },

                        electrodesPattern4,
                        ElectrodeDacMuxEnumNet.Stg2,
                        1,
                        new int[] { 0, -1000, 1000, 0 },
                        new ulong[] { 299700, 300, 300, 399700 }
                    );

                    Thread.Sleep(700);
                }

                cStgDevice.Disconnect();
            }
            catch (Exception ex)
            {
                try { cStgDevice.Disconnect(); } catch { }

                MessageBox.Show("Error during stimulation:\n\n" + ex.Message);
            }
        }

        private void SendTwoPatternsTogether(
            CStg200xDownloadNet cStgDevice,

            uint[] electrodes1,
            ElectrodeDacMuxEnumNet stgMux1,
            uint stgChannel1,
            int[] amplitude1,
            ulong[] duration1,

            uint[] electrodes2,
            ElectrodeDacMuxEnumNet stgMux2,
            uint stgChannel2,
            int[] amplitude2,
            ulong[] duration2)
        {
            uint Bit0Time = 40; // bit0 (blanking switch) activation duration prolongation in µs
            uint Bit3Time = 800; // bit3 (stimulation switch) activation duration prolongation in µs
            uint Bit4Time = 40; // bit4 (stimulus selection switch) activation duration prolongation in µs

            foreach (uint electrode in electrodes1)
            {
                ConfigureElectrode(cStgDevice, electrode, stgMux1);
            }

            foreach (uint electrode in electrodes2)
            {
                ConfigureElectrode(cStgDevice, electrode, stgMux2);
            }

            // specify when stimulus is active
            int[] stimulusActive1 = amplitude1.Select(x => x == 0 ? 0 : 1).ToArray();
            int[] stimulusActive2 = amplitude2.Select(x => x == 0 ? 0 : 1).ToArray();

            // 0x40: Stimulus Active
            // 0x80: Marker (minimal length: 200us)
            int[] sideband1 = amplitude1.Select(x => x == 0 ? 0 : 0xc0).ToArray();
            int[] sideband2 = amplitude2.Select(x => x == 0 ? 0 : 0xc0).ToArray();

            //int[] sideband1 = new int[amplitude1.Length];
            //int[] sideband2 = new int[amplitude2.Length];

            cStgDevice.PrepareAndSendData(
                stgChannel1,
                amplitude1,
                duration1,
                STG_DestinationEnumNet.channeldata_voltage
            );

            var sidebandData1 =
                cStgDevice.Stimulus.CreateSideband(
                    stimulusActive1,
                    sideband1,
                    duration1,
                    Bit0Time,
                    Bit3Time,
                    Bit4Time
                );

            cStgDevice.PrepareAndSendData(
                stgChannel1,
                sidebandData1.Sideband,
                sidebandData1.Duration,
                STG_DestinationEnumNet.syncoutdata
            );

            cStgDevice.PrepareAndSendData(
                stgChannel2,
                amplitude2,
                duration2,
                STG_DestinationEnumNet.channeldata_voltage
            );

            var sidebandData2 =
                cStgDevice.Stimulus.CreateSideband(
                    stimulusActive2,
                    sideband2,
                    duration2,
                    Bit0Time,
                    Bit3Time,
                    Bit4Time
                );

            cStgDevice.PrepareAndSendData(
                stgChannel2,
                sidebandData2.Sideband,
                sidebandData2.Duration,
                STG_DestinationEnumNet.syncoutdata
            );

            cStgDevice.SetupTrigger(
                0,
                new uint[] { 0x03 }, // STG channel 0 + 1
                new uint[] { 0x03 }, // sideband channel 0 + 1
                new uint[] { 1 }
            );

            cStgDevice.SendStart(1);
        }

        private void ConfigureElectrode(
            CStg200xDownloadNet cStgDevice,
            uint electrode,
            ElectrodeDacMuxEnumNet stgMux)
        {
            cStgDevice.SetElectrodeModeHsOnIfb(electrode, ElectrodeModeEnumNet.emAutomatic);

            cStgDevice.SetElectrodeDacMuxHsOnIfb(electrode,0,stgMux );

            cStgDevice.SetElectrodeEnableHsOnIfb(electrode,0,true);

            cStgDevice.SetBlankingEnableHsOnIfb(electrode,true);

            cStgDevice.SetEnableAmplifierProtectionSwitchHsOnIfb(electrode,true);
        }
    }
}