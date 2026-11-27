using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TruckLib;
using TruckLib.ScsMap;
using NativeMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

internal readonly record struct NavigationNodeLocation(SectorCoordinate Sector, ulong BackwardItemUid, ulong ForwardItemUid);

/// <summary>Physical archive locations for background route loading. The UID tables stay on disk.</summary>
internal sealed class NavigationSectorIndex : IDisposable
{
    private const ulong Magic = 0x315849534156414E; // NAVASIX1
    private const int Version = 1;
    private const int BucketCount = 65536;
    private const int PartCount = 128;
    private const int BucketsPerPart = BucketCount / PartCount;
    private const int NodeBytes = 28, ItemBytes = 12, ChecksumBytes = 32;
    private const int HeaderBytes = sizeof(ulong) + 2 * sizeof(int);
    private readonly FileStream stream;
    // Node start, item start for each bucket, then one end offset.
    private readonly long[] offsets;
    internal IReadOnlyList<ulong> FerryComponents { get; }

    private NavigationSectorIndex(FileStream stream, long[] offsets, ulong[] ferries)
    {
        this.stream = stream;
        this.offsets = offsets;
        FerryComponents = Array.AsReadOnly(ferries);
    }

    internal static NavigationSectorIndex OpenOrBuild(IFileSystem files, string mapPath, string cacheFile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        cacheFile = Path.GetFullPath(cacheFile);
        try { return Open(cacheFile, token); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or EndOfStreamException) { }

        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        string work = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(work);
        try
        {
            string candidate = Path.Combine(work, "index");
            Build(files, mapPath, work, candidate, token);
            token.ThrowIfCancellationRequested();
            File.Move(candidate, cacheFile, overwrite: true);
            return Open(cacheFile, token);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    internal bool TryGetNode(ulong uid, out NavigationNodeLocation location)
    {
        Span<byte> record = stackalloc byte[NodeBytes];
        if (Find(uid, 2 * Bucket(uid), record))
        {
            location = new(SectorAt(record[24..]), BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[16..]));
            return true;
        }
        location = default;
        return false;
    }

    internal bool TryGetItem(ulong uid, out SectorCoordinate sector)
    {
        Span<byte> record = stackalloc byte[ItemBytes];
        if (Find(uid, 2 * Bucket(uid) + 1, record))
        {
            sector = SectorAt(record[8..]);
            return true;
        }
        sector = default;
        return false;
    }

    private static SectorCoordinate SectorAt(ReadOnlySpan<byte> source) =>
        new(BinaryPrimitives.ReadInt16LittleEndian(source), BinaryPrimitives.ReadInt16LittleEndian(source[2..]));

    private bool Find(ulong uid, int table, Span<byte> record)
    {
        int stride = record.Length;
        long start = offsets[table], count = (offsets[table + 1] - start) / stride;
        Span<byte> page = stackalloc byte[4096];
        int pageRows = page.Length / stride;
        while (count > pageRows)
        {
            long middle = count / 2;
            ReadAt(start + middle * stride, record);
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(record);
            if (uid == key) return true;
            if (uid < key) count = middle;
            else { start += (middle + 1) * stride; count -= middle + 1; }
        }
        if (count == 0) return false;
        ReadAt(start, page[..((int)count * stride)]);
        int low = 0, high = (int)count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            var row = page.Slice(middle * stride, stride);
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(row);
            if (key == uid) { row.CopyTo(record); return true; }
            if (uid < key) high = middle;
            else low = middle + 1;
        }
        return false;
    }

    private void ReadAt(long position, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int read = RandomAccess.Read(stream.SafeFileHandle, buffer, position);
            if (read == 0) throw new EndOfStreamException();
            position += read;
            buffer = buffer[read..];
        }
    }

    private static int Bucket(ulong uid)
    {
        ulong key = uid ^ (uid >> 23) ^ (uid >> 47);
        return (int)(unchecked(key * 0x9E3779B97F4A7C15UL) >> 48);
    }

    private static NavigationSectorIndex Open(string path, CancellationToken token)
    {
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt64() != Magic || reader.ReadInt32() != Version) throw new InvalidDataException("Invalid sector index header.");
            int ferryCount = reader.ReadInt32();
            long[] offsets = new long[2 * BucketCount + 1];
            long dataStart = HeaderBytes + offsets.LongLength * sizeof(long) + (long)ferryCount * sizeof(ulong);
            long dataEnd = input.Length - ChecksumBytes;
            if (ferryCount < 0 || dataStart > dataEnd) throw new InvalidDataException("Invalid sector index length.");
            var ferries = new ulong[ferryCount];
            for (int i = 0; i < ferries.Length; i++) ferries[i] = reader.ReadUInt64();
            if (ferries.Length > 0 && ferries[0] == 0) throw new InvalidDataException("Invalid null ferry component UID.");
            for (int i = 0; i < offsets.Length; i++) offsets[i] = reader.ReadInt64();
            if (offsets[0] != dataStart || offsets[^1] != dataEnd) throw new InvalidDataException("Invalid sector index directory.");
            for (int i = 0; i + 1 < offsets.Length; i++)
                if (offsets[i + 1] < offsets[i] || (offsets[i + 1] - offsets[i]) % ((i & 1) == 0 ? NodeBytes : ItemBytes) != 0)
                    throw new InvalidDataException("Invalid sector index bucket.");
            byte[] digest = Checksum(input, dataEnd, token);
            Span<byte> stored = stackalloc byte[ChecksumBytes];
            input.ReadExactly(stored);
            if (!stored.SequenceEqual(digest)) throw new InvalidDataException("Damaged sector index.");
            return new(input, offsets, ferries);
        }
        catch { input.Dispose(); throw; }
    }

    private static byte[] Checksum(Stream file, long length, CancellationToken token)
    {
        file.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (length > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(length, buffer.Length);
                file.ReadExactly(buffer.AsSpan(0, count));
                hash.AppendData(buffer, 0, count);
                length -= count;
            }
            return hash.GetHashAndReset();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private enum RowKind { Node, Endpoint, Item }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Row(ulong Uid, ulong Back, ulong Forward, int SectorIndex, RowKind Kind);

    // Temporary native-endian rows are local to this build. Only the final portable tables survive.
    private sealed class Parts(string directory) : IDisposable
    {
        private readonly FileStream?[] writers = new FileStream?[PartCount];
        private string PathFor(int part) => Path.Combine(directory, part.ToString());
        internal void Add(Row row)
        {
            int part = Bucket(row.Uid) / BucketsPerPart;
            var output = writers[part] ??= new FileStream(PathFor(part), FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384);
            output.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref row, 1)));
        }
        internal Row[] Read(int part)
        {
            var writer = writers[part];
            if (writer is null) return [];
            writer.Dispose();
            writers[part] = null;
            using var input = File.OpenRead(PathFor(part));
            var rows = new Row[checked((int)(input.Length / Marshal.SizeOf<Row>()))];
            input.ReadExactly(MemoryMarshal.AsBytes(rows.AsSpan()));
            Array.Sort(rows, CompareRows);
            return rows;
        }
        private static int CompareRows(Row a, Row b)
        {
            int compare = Bucket(a.Uid).CompareTo(Bucket(b.Uid));
            if (compare != 0) return compare;
            // The node table and its expected endpoints precede the item table in every bucket.
            compare = (a.Kind == RowKind.Item).CompareTo(b.Kind == RowKind.Item);
            if (compare != 0) return compare;
            compare = a.Uid.CompareTo(b.Uid);
            if (compare != 0) return compare;
            compare = a.Kind.CompareTo(b.Kind);
            return compare != 0 ? compare : a.SectorIndex.CompareTo(b.SectorIndex);
        }
        public void Dispose()
        {
            foreach (var writer in writers) writer?.Dispose();
        }
    }

    private static void Build(IFileSystem files, string mapPath, string work, string candidate, CancellationToken token)
    {
        mapPath = Path.TrimEndingDirectorySeparator(mapPath);
        string directory = files.FileExists(mapPath)
            ? files.GetParent(mapPath) + files.DirectorySeparator + Path.GetFileNameWithoutExtension(mapPath) : mapPath;
        var paths = files.GetFiles(directory).Where(f => Path.GetExtension(f) == ".base").ToArray();
        var sectors = paths.Select(Sector.SectorCoordsFromSectorFilePath).ToArray();
        using var parts = new Parts(work);
        var ferryComponents = new HashSet<ulong>();
        // Only component UIDs are needed to distinguish a third-party road claim from an ordinary
        // scenery connection; all per-node declarations go directly into their disk partitions.
        var routeOwners = new HashSet<ulong>();
        for (int sector = 0; sector < paths.Length; sector++)
        {
            token.ThrowIfCancellationRequested();
            ReadSector(files, directory, paths[sector], token, OnItem, OnNode);
            void OnNode(ulong uid, ulong back, ulong forward) =>
                parts.Add(new(uid, back, forward, sector, RowKind.Node));
            void OnItem(MapItem item)
            {
                if (item is not (Road or Prefab or Ferry)) return;
                parts.Add(new(item.Uid, 0, 0, sector, RowKind.Item));
                if (item is Ferry ferry)
                {
                    ferryComponents.Add(ferry.Uid);
                    if (ferry.Prefab is { Uid: > 0 } dock) ferryComponents.Add(dock.Uid);
                    return;
                }
                routeOwners.Add(item.Uid);
                foreach (var endpoint in HostMapView.NodesOf(item))
                    parts.Add(new(endpoint.Uid, item.Uid, 0, sector, RowKind.Endpoint));
                if (item is Prefab { Ferry: { } connection })
                {
                    ferryComponents.Add(item.Uid);
                    ferryComponents.Add(connection.Uid);
                }
            }
        }

        using var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic); writer.Write(Version); writer.Write(ferryComponents.Count);
        foreach (ulong uid in ferryComponents.Order()) writer.Write(uid);
        long directoryStart = output.Position;
        long[] offsets = new long[2 * BucketCount + 1];
        output.Position += offsets.LongLength * sizeof(long);
        for (int part = 0; part < PartCount; part++)
        {
            token.ThrowIfCancellationRequested();
            var rows = parts.Read(part);
            int current = 0;
            for (int bucket = part * BucketsPerPart; bucket < (part + 1) * BucketsPerPart; bucket++)
            {
                token.ThrowIfCancellationRequested();
                offsets[2 * bucket] = output.Position;
                while (current < rows.Length && Bucket(rows[current].Uid) == bucket && rows[current].Kind != RowKind.Item)
                {
                    int first = current++;
                    while (current < rows.Length && rows[current].Uid == rows[first].Uid && rows[current].Kind != RowKind.Item) current++;
                    WriteNode(writer, rows.AsSpan(first, current - first), sectors, routeOwners);
                }
                offsets[2 * bucket + 1] = output.Position;
                while (current < rows.Length && Bucket(rows[current].Uid) == bucket)
                {
                    var item = rows[current++];
                    if (current < rows.Length && rows[current].Uid == item.Uid)
                        throw new InvalidDataException($"Native map item {item.Uid:X16} occurs twice.");
                    writer.Write(item.Uid);
                    WriteSector(writer, sectors[item.SectorIndex]);
                }
            }
        }
        long dataEnd = offsets[^1] = output.Position;
        output.Position = directoryStart;
        foreach (long offset in offsets) writer.Write(offset);
        writer.Flush();
        byte[] digest = Checksum(output, dataEnd, token);
        output.Write(digest);
        output.Flush(flushToDisk: true);
    }

    private static void WriteNode(BinaryWriter writer, ReadOnlySpan<Row> rows, SectorCoordinate[] sectors, HashSet<ulong> owners)
    {
        var node = rows[0];
        // Missing physical rows remain unresolved. No world-coordinate substitute is possible.
        if (node.Kind != RowKind.Node) return;
        bool expectedBack = false, expectedForward = false;
        foreach (var row in rows)
        {
            if (row.Kind == RowKind.Node)
            {
                if (row.Back != node.Back || row.Forward != node.Forward)
                    throw new InvalidDataException($"Native node {node.Uid:X16} changed its connection declarations across physical records.");
                continue;
            }
            bool back = node.Back == row.Back, forward = node.Forward == row.Back;
            if (back == forward) throw InvalidClaim(node.Uid, row.Back);
            expectedBack |= back;
            expectedForward |= forward;
        }
        if (!expectedBack && owners.Contains(node.Back)) throw InvalidClaim(node.Uid, node.Back);
        if (!expectedForward && owners.Contains(node.Forward)) throw InvalidClaim(node.Uid, node.Forward);
        writer.Write(node.Uid); writer.Write(node.Back); writer.Write(node.Forward);
        WriteSector(writer, sectors[node.SectorIndex]);
    }

    private static InvalidDataException InvalidClaim(ulong node, ulong owner) =>
        new($"Native node {node:X16} and component {owner:X16} do not declare the same connection.");

    private static void WriteSector(BinaryWriter writer, SectorCoordinate sector)
    {
        // Native sector filenames have a sign and four decimal digits per axis.
        writer.Write(checked((short)sector.X)); writer.Write(checked((short)sector.Z));
    }

    private static void ReadSector(IFileSystem files, string directory, string basePath, CancellationToken token,
        Action<MapItem> item, Action<ulong, ulong, ulong> node)
    {
        // Asking the native reader for one sector at a time bounds the graph to that sector;
        // its Nodes cover .base, .aux and .snd, independent of each node's world position.
        using var evidence = new SectorReadEvidence();
        var subset = new SectorFiles(files, directory, basePath, token);
        var map = new IndexMap(evidence, token);
        try { map.Read(directory, subset); }
        finally { evidence.End(); }
        token.ThrowIfCancellationRequested();
        if (evidence.Failures.Count != 0) throw new MapSectorReadException(evidence.Failures);
        foreach (var record in map.MapItems.Values) { token.ThrowIfCancellationRequested(); item(record); }
        foreach (Node record in map.Nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            node(record.Uid, record.BackwardItem?.Uid ?? 0, record.ForwardItem?.Uid ?? 0);
        }
    }

    private sealed class IndexMap(SectorReadEvidence evidence, CancellationToken token) : NativeMap
    {
        protected override void OnSectorLoading(Sector sector, int index, int total) => evidence.Begin(sector.Coordinate);
        protected override bool PostProcessItem(MapItem item)
        {
            token.ThrowIfCancellationRequested();
            return item is Road or Prefab or Ferry;
        }
    }

    private sealed class SectorFiles(IFileSystem source, string directory, string basePath, CancellationToken token) : IFileSystem
    {
        public char DirectorySeparator => source.DirectorySeparator;
        public IList<string> GetFiles(string path) => path == directory ? [basePath] : source.GetFiles(path);
        public bool FileExists(string path) => path != directory && source.FileExists(path);
        public Stream Open(string path) { token.ThrowIfCancellationRequested(); return source.Open(path); }
        public string GetParent(string path) => source.GetParent(path);
        public bool DirectoryExists(string path) => source.DirectoryExists(path);
        public byte[] ReadAllBytes(string path) => source.ReadAllBytes(path);
        public string ReadAllText(string path) => source.ReadAllText(path);
        public string ReadAllText(string path, Encoding encoding) => source.ReadAllText(path, encoding);
    }

    public void Dispose() => stream.Dispose();
}
