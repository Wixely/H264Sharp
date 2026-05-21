namespace H264Decoder.Bitstream;

public enum NalUnitType : byte
{
    Unspecified = 0,
    SliceNonIdr = 1,
    SlicePartitionA = 2,
    SlicePartitionB = 3,
    SlicePartitionC = 4,
    SliceIdr = 5,
    Sei = 6,
    Sps = 7,
    Pps = 8,
    AccessUnitDelimiter = 9,
    EndOfSequence = 10,
    EndOfStream = 11,
    FillerData = 12,
}

public readonly struct NalUnit
{
    public byte NalRefIdc { get; }
    public NalUnitType NalUnitType { get; }
    public ReadOnlyMemory<byte> Rbsp { get; }

    public NalUnit(byte nalRefIdc, NalUnitType nalUnitType, ReadOnlyMemory<byte> rbsp)
    {
        NalRefIdc = nalRefIdc;
        NalUnitType = nalUnitType;
        Rbsp = rbsp;
    }
}
