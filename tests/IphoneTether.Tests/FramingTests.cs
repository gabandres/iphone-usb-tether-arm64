using IphoneTether;
using Xunit;

public class FramingTests
{
    /// <summary>Builds an NCM transfer block: NTH16, the datagrams, then one NDP16 listing them.</summary>
    static byte[] Ntb(params int[] datagramLengths)
    {
        var block = new List<byte>();
        void U16(int v) { block.Add((byte)v); block.Add((byte)(v >> 8)); }
        block.AddRange("NCMH"u8.ToArray()); U16(12); U16(1); U16(0); U16(0);   // block length and NDP index patched below
        var entries = new List<(int, int)>();
        foreach (int length in datagramLengths)
        {
            entries.Add((block.Count, length));
            block.AddRange(Enumerable.Repeat((byte)0xEE, length));
            while (block.Count % 4 != 0) block.Add(0);
        }
        int ndp = block.Count;
        block.AddRange("NCM0"u8.ToArray()); U16(8 + 4 * (entries.Count + 1)); U16(0);
        foreach (var (offset, length) in entries) { U16(offset); U16(length); }
        U16(0); U16(0);
        var bytes = block.ToArray();
        BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 8);
        BitConverter.GetBytes((ushort)ndp).CopyTo(bytes, 10);
        return bytes;
    }

    static List<(int Offset, int Length)> Parse(byte[] transfer, bool ncm = true)
    {
        var buf = new byte[65536];
        transfer.CopyTo(buf, 0);
        return Framing.Frames(buf, transfer.Length, ncm);
    }

    [Fact]
    public void Legacy_transfer_is_one_frame_after_two_padding_bytes()
    {
        Assert.Equal([(2, 60)], Parse(new byte[62], ncm: false));
    }

    [Fact]
    public void Legacy_status_message_is_not_a_frame()
    {
        Assert.Empty(Parse([0x00, 0x01, 0x01, 0x00], ncm: false));
    }

    [Fact]
    public void Ncm_block_yields_each_datagram()
    {
        var frames = Parse(Ntb(60, 1514, 90));
        Assert.Equal([60, 1514, 90], frames.Select(f => f.Length));
        Assert.Equal(12, frames[0].Offset);
    }

    [Fact]
    public void Ncm_status_message_and_garbage_yield_nothing()
    {
        Assert.Empty(Parse([0x00, 0x01, 0x01, 0x00]));
        Assert.Empty(Parse(Enumerable.Repeat((byte)0xAB, 300).ToArray()));
    }

    [Fact]
    public void Ncm_datagram_past_the_received_bytes_is_dropped()
    {
        var block = Ntb(60, 200);
        int ndp = BitConverter.ToUInt16(block, 10);
        BitConverter.GetBytes((ushort)60000).CopyTo(block, ndp + 8 + 4 + 2);   // second datagram claims 60000 bytes
        Assert.Equal([60], Parse(block).Select(f => f.Length));
    }

    [Fact]
    public void Ncm_runt_datagram_is_dropped()
    {
        Assert.Equal([60], Parse(Ntb(8, 60)).Select(f => f.Length));
    }

    [Fact]
    public void Ncm_table_pointing_at_itself_terminates()
    {
        var block = Ntb(60);
        int ndp = BitConverter.ToUInt16(block, 10);
        BitConverter.GetBytes((ushort)ndp).CopyTo(block, ndp + 6);   // wNextNdpIndex = this table
        Assert.InRange(Parse(block).Count, 1, 64);
    }

    [Fact]
    public void Ncm_truncated_transfer_never_reads_outside_it()
    {
        var block = Ntb(60, 1514, 90);
        for (int n = 0; n < block.Length; n++)
        {
            var buf = new byte[65536];
            block.CopyTo(buf, 0);
            foreach (var (offset, length) in Framing.Frames(buf, n, ncm: true))
                Assert.True(offset >= 12 && offset + length <= n);
        }
    }
}
