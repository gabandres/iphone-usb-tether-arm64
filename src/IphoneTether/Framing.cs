namespace IphoneTether;

/// <summary>Finds the Ethernet frames inside one bulk-IN transfer from the phone. Pure: no I/O.</summary>
public static class Framing
{
    const int EthernetHeader = 14;
    const uint Nth16Signature = 0x484D434E;   // "NCMH"
    const uint Ndp16Signature = 0x304D434E;   // "NCM0"
    const int MaxNdpChain = 64;

    /// <summary>
    /// Frames as (offset, length) into <paramref name="buf"/>, of which the first <paramref name="n"/>
    /// bytes are valid. Never returns a range outside those bytes, whatever the phone sent.
    /// </summary>
    public static List<(int Offset, int Length)> Frames(byte[] buf, int n, bool ncm)
    {
        var frames = new List<(int, int)>();
        if (n > buf.Length) n = buf.Length;
        if (!ncm)
        {
            // Legacy framing: two bytes of padding, then one frame.
            if (n >= 2 + EthernetHeader) frames.Add((2, n - 2));
            return frames;
        }

        // NCM: an NTH16 header, then a chain of NDP16 tables listing datagram offset/length pairs.
        if (n < 12 || BitConverter.ToUInt32(buf, 0) != Nth16Signature) return frames;
        int ndp = BitConverter.ToUInt16(buf, 10);
        for (int tables = 0; ndp != 0 && tables < MaxNdpChain; tables++)
        {
            if (ndp + 8 > n || BitConverter.ToUInt32(buf, ndp) != Ndp16Signature) break;
            int end = Math.Min(ndp + BitConverter.ToUInt16(buf, ndp + 4), n);
            for (int entry = ndp + 8; entry + 4 <= end; entry += 4)
            {
                int offset = BitConverter.ToUInt16(buf, entry), length = BitConverter.ToUInt16(buf, entry + 2);
                if (offset == 0 || length == 0) break;
                if (length >= EthernetHeader && offset + length <= n) frames.Add((offset, length));
            }
            ndp = BitConverter.ToUInt16(buf, ndp + 6);
        }
        return frames;
    }
}
