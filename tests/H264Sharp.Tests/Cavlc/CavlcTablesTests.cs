using System.Reflection;
using H264Sharp.Decoder.Cavlc;

namespace H264Sharp.Tests.Cavlc;

public sealed class CavlcTablesTests
{
    // Asserts each generated table has the byte-length the source .cpp declares.
    // Catches regex bugs / silent truncation from the generator.
    [Theory]
    [InlineData("VlcChromaTable", 512)]
    [InlineData("VlcTable_0", 512)]
    [InlineData("VlcTable_0_0", 512)]
    [InlineData("VlcTable_0_1", 8)]
    [InlineData("VlcTable_0_2", 4)]
    [InlineData("VlcTable_0_3", 4)]
    [InlineData("VlcTable_1", 512)]
    [InlineData("VlcTable_1_0", 128)]
    [InlineData("VlcTable_1_1", 16)]
    [InlineData("VlcTable_1_2", 4)]
    [InlineData("VlcTable_1_3", 4)]
    [InlineData("VlcTable_2", 512)]
    [InlineData("VlcTable_2_0", 8)]
    [InlineData("VlcTable_2_1", 8)]
    [InlineData("VlcTable_2_2", 8)]
    [InlineData("VlcTable_2_3", 8)]
    [InlineData("VlcTable_2_4", 4)]
    [InlineData("VlcTable_2_5", 4)]
    [InlineData("VlcTable_2_6", 4)]
    [InlineData("VlcTable_2_7", 4)]
    [InlineData("VlcTable_3", 128)]
    [InlineData("VlcTableNeedMoreBitsThread", 3)]
    [InlineData("VlcTableMoreBitsCount0", 4)]
    [InlineData("VlcTableMoreBitsCount1", 4)]
    [InlineData("VlcTableMoreBitsCount2", 8)]
    [InlineData("NcMapTable", 17)]
    [InlineData("VlcTrailingOneTotalCoeffTable", 124)]
    [InlineData("ZeroLeftTable0", 4)]
    [InlineData("ZeroLeftTable1", 8)]
    [InlineData("ZeroLeftTable2", 8)]
    [InlineData("ZeroLeftTable3", 16)]
    [InlineData("ZeroLeftTable4", 16)]
    [InlineData("ZeroLeftTable5", 16)]
    [InlineData("ZeroLeftTable6", 16)]
    public void TableHasExpectedByteLength(string fieldName, int expectedLength)
    {
        byte[] table = GetTable(fieldName);
        Assert.Equal(expectedLength, table.Length);
    }

    [Fact]
    public void NcMapTable_KnownValues()
    {
        // g_kuiNcMapTable[17] = { 0,0,1,1,2,2,2,2,3,3,3,3,3,3,3,3,3 }
        byte[] map = GetTable("NcMapTable");
        Assert.Equal(0, map[0]);
        Assert.Equal(0, map[1]);
        Assert.Equal(1, map[2]);
        Assert.Equal(1, map[3]);
        Assert.Equal(2, map[4]);
        Assert.Equal(3, map[8]);
        Assert.Equal(3, map[16]);
    }

    [Fact]
    public void VlcTrailingOneTotalCoeff_FirstEntries()
    {
        // (TotalCoeff=0, TrailingOnes=0): {0,0}
        // (TotalCoeff=1, TrailingOnes=0): {0,1}
        // (TotalCoeff=1, TrailingOnes=1): {1,1}
        byte[] t = GetTable("VlcTrailingOneTotalCoeffTable");
        Assert.Equal(0, t[0]); Assert.Equal(0, t[1]);
        Assert.Equal(0, t[2]); Assert.Equal(1, t[3]);
        Assert.Equal(1, t[4]); Assert.Equal(1, t[5]);
    }

    private static byte[] GetTable(string name)
    {
        FieldInfo? f = typeof(CavlcTables).GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        var value = (byte[])f!.GetValue(null)!;
        return value;
    }
}
