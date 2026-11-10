namespace MEA2100_Stimulation
{
    public static class ElectrodeMap
    {
        // Hardware channel -> MCS electrode label, for E-00278, E-00281 and E-00303 (same wiring).
        // The index is both the McsUsbNet electrode index used for stimulation and the position of
        // the channel in the data stream. Indices 0-58 were measured on E-00281; all 60 agree with
        // the ChannelID -> Label table that Multi Channel Experimenter wrote into the 2026-11-03
        // recordings of E-00278, which is also where index 59 = label 57 comes from.
        // That the data stream delivers its channels in ChannelID order is assumed, not yet
        // checked on the hardware (README, "First test on the hardware").
        public static readonly string[] IndexToLabel =
        {
            "47", "48", "46", "45", "38", "37", "28", "36", "27", "17",
            "26", "16", "35", "25", "15", "14", "24", "34", "13", "23",
            "12", "22", "33", "21", "32", "31", "44", "43", "41", "42",
            "52", "51", "53", "54", "61", "62", "71", "63", "72", "82",
            "73", "83", "64", "74", "84", "85", "75", "65", "86", "76",
            "87", "77", "66", "78", "67", "68", "55", "56", "58", "57",
        };
    }
}
