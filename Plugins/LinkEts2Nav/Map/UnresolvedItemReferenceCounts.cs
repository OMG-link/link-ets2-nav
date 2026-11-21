using System.Numerics;

namespace LinkEts2Nav.Map;

/// <summary>Compact exact counts of references to item UIDs absent from the adopted host map.</summary>
internal sealed class UnresolvedItemReferenceCounts
{
    private const int BlockSize = 128;
    private readonly record struct Block(ulong FirstUid, int SourceIndex, int Offset);
    private readonly Block[] blocks;
    private readonly byte[] data;

    private UnresolvedItemReferenceCounts(Block[] blocks, byte[] data)
    {
        this.blocks = blocks;
        this.data = data;
    }

    public static UnresolvedItemReferenceCounts Empty { get; } = new([], []);

    public static UnresolvedItemReferenceCounts Build(List<ulong> references)
    {
        if (references.Count == 0)
            return Empty;
        references.Sort();
        var anchors = new List<Block>((references.Count - 1) / BlockSize + 1);
        int byteCount = 0;

        for (int i = 0; i < references.Count;)
        {
            int start = i;
            ulong previous = references[start];
            anchors.Add(new(previous, start, byteCount));
            do
            {
                // Repeated UIDs stay in one block, where each zero delta counts as a reference.
                ulong uid = references[i++];
                int bits = 64 - BitOperations.LeadingZeroCount(uid - previous);
                byteCount += Math.Max(1, (bits + 6) / 7);
                previous = uid;
            }
            while (i < references.Count && (i - start < BlockSize || references[i] == previous));
        }

        var data = new byte[byteCount];
        for (int block = 0; block < anchors.Count; block++)
        {
            var anchor = anchors[block];
            ulong previous = anchor.FirstUid;
            int offset = anchor.Offset;
            int end = block + 1 < anchors.Count ? anchors[block + 1].SourceIndex : references.Count;
            for (int i = anchor.SourceIndex; i < end; i++)
            {
                ulong uid = references[i];
                ulong delta = uid - previous;
                while (delta >= 128)
                {
                    data[offset++] = (byte)((delta & 127) | 128);
                    delta >>= 7;
                }
                data[offset++] = (byte)delta;
                previous = uid;
            }
        }

        return new(anchors.ToArray(), data);
    }

    public int Count(ulong itemUid)
    {
        int low = 0;
        int high = blocks.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (blocks[middle].FirstUid <= itemUid)
                low = middle + 1;
            else
                high = middle;
        }
        if (low == 0)
            return 0;

        int offset = blocks[low - 1].Offset;
        ulong uid = blocks[low - 1].FirstUid;
        int end = low < blocks.Length ? blocks[low].Offset : data.Length;
        int count = 0;
        while (offset < end)
        {
            int shift = 0;
            byte next;
            do
            {
                next = data[offset++];
                uid += (ulong)(next & 127) << shift;
                shift += 7;
            } while ((next & 128) != 0);
            if (uid > itemUid)
                break;
            if (uid == itemUid)
                count++;
        }
        return count;
    }
}
