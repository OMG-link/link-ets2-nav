using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using TruckLib.HashFs.HashFsV2;
using static TruckLib.HashFs.HashFsV2.Consts;

namespace TruckLib.HashFs;

// Plain files and directories have the same four metadata words and no packed texture.
internal sealed record NavigationArchiveEntry : IEntry
{
    public ulong Hash { get; }
    public ulong Offset { get; }
    public uint Size { get; }
    public uint CompressedSize { get; }
    public bool IsDirectory { get; set; }
    public bool IsCompressed { get; }

    private NavigationArchiveEntry(ulong hash, bool isDirectory, ReadOnlySpan<byte> metadata)
    {
        const uint sizeMask = 0x0fff_ffff;
        uint packedSize = BinaryPrimitives.ReadUInt32LittleEndian(metadata);
        Hash = hash;
        IsDirectory = isDirectory;
        IsCompressed = (packedSize & 0x1000_0000) != 0;
        CompressedSize = packedSize & sizeMask;
        Size = BinaryPrimitives.ReadUInt32LittleEndian(metadata[4..]) & sizeMask;
        Offset = BinaryPrimitives.ReadUInt32LittleEndian(metadata[12..]) * BlockSize;
    }

    internal static bool TryAdd(Dictionary<ulong, IEntry> entries, EntryTableEntry entry, ReadOnlySpan<byte> table)
    {
        if (entry.MetadataCount == 0) return false;
        var descriptors = table[checked((int)entry.MetadataIndex * MetadataTableBlockSize)..];
        var primary = (MetadataChunkType)descriptors[3];
        if (primary is not (MetadataChunkType.Plain or MetadataChunkType.Directory)) return false;

        var metadata = descriptors[(entry.MetadataCount * MetadataTableBlockSize)..];
        entries.Add(entry.Hash, new NavigationArchiveEntry(entry.Hash, primary == MetadataChunkType.Directory, metadata));
        return true;
    }

    internal static bool MatchesOtherRepresentation(IEntry stored, IEntry supplied)
    {
        // Extract also accepts value copies, including a plain EntryV2 from the standard reader.
        bool otherRepresentation = (stored is NavigationArchiveEntry && supplied is EntryV2 { TobjMetadata: null })
            || (supplied is NavigationArchiveEntry && stored is EntryV2 { TobjMetadata: null });
        return otherRepresentation && stored.Hash == supplied.Hash && stored.Offset == supplied.Offset
            && stored.Size == supplied.Size && stored.CompressedSize == supplied.CompressedSize
            && stored.IsDirectory == supplied.IsDirectory && stored.IsCompressed == supplied.IsCompressed;
    }
}
