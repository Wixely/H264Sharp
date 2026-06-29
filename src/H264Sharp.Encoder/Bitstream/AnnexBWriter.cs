using H264Sharp.Decoder.Bitstream;

namespace H264Sharp.Encoder.Bitstream;

/// <summary>Wraps NAL RBSP payloads with start codes and inserts emulation-prevention bytes.</summary>
public static class AnnexBWriter
{
    /// <summary>Convert a (nal_unit_type, nal_ref_idc, rbsp[]) tuple into an EBSP byte string,
    /// prefixed with the 1-byte NAL header. Inserts emulation prevention 0x03 bytes where
    /// the RBSP contains 00 00 0,1,2,3 sequences (spec §7.4.1.1).</summary>
    public static byte[] BuildNalUnit(NalUnitType nalType, byte nalRefIdc, ReadOnlySpan<byte> rbsp)
    {
        // NAL header: forbidden_zero_bit(0) + nal_ref_idc(2) + nal_unit_type(5)
        byte header = (byte)(((nalRefIdc & 0x3) << 5) | ((byte)nalType & 0x1F));

        // Insert emulation prevention bytes.
        var ebsp = InsertEmulationPreventionBytes(rbsp);
        var result = new byte[1 + ebsp.Length];
        result[0] = header;
        Array.Copy(ebsp, 0, result, 1, ebsp.Length);
        return result;
    }

    /// <summary>Write a sequence of NAL units to a stream using Annex-B framing (start code 00 00 00 01).</summary>
    public static void WriteAnnexB(Stream output, IEnumerable<byte[]> nalUnits)
    {
        foreach (byte[] nal in nalUnits)
        {
            output.WriteByte(0);
            output.WriteByte(0);
            output.WriteByte(0);
            output.WriteByte(1);
            output.Write(nal, 0, nal.Length);
        }
    }

    public static byte[] InsertEmulationPreventionBytes(ReadOnlySpan<byte> rbsp)
    {
        // Worst case: every byte triplet becomes 4 bytes. Pre-grow conservatively.
        int n = rbsp.Length;
        var tmp = new byte[n + (n / 2) + 4];
        int outLen = 0;
        int zeros = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = rbsp[i];
            if (zeros >= 2 && b <= 0x03)
            {
                if (outLen + 1 >= tmp.Length) Array.Resize(ref tmp, tmp.Length * 2);
                tmp[outLen++] = 0x03;
                zeros = 0;
            }
            if (outLen >= tmp.Length) Array.Resize(ref tmp, tmp.Length * 2);
            tmp[outLen++] = b;
            zeros = (b == 0) ? zeros + 1 : 0;
        }
        if (outLen == tmp.Length) return tmp;
        var result = new byte[outLen];
        Array.Copy(tmp, result, outLen);
        return result;
    }
}
