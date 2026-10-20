using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace KSP.Retooled.Patch;

public static partial class PatchBuilder
{
    /// <summary>A run of target bytes identical to <see cref="Length"/> bytes of <see cref="Source"/> at <see cref="SourceOffset"/>.</summary>
    readonly record struct CopyRun(long Start, long Length, FileEntry Source, long SourceOffset)
    {
        public long End => Start + Length;
    }

    /// <summary>
    /// Finds target runs that are byte-identical to stock content anywhere in scope. Unity relocates texture, mesh and
    /// audio payloads between files and offsets but mostly keeps their bytes, so chunks cut by a content-defined (gear)
    /// hash line up on both sides regardless of position; each hit is verified and extended byte by byte.
    /// </summary>
    sealed class CopyMatcher : IDisposable
    {
        const int MinChunk = 4 << 10, MaxChunk = 128 << 10;
        const ulong CutMask = (1UL << 14) - 1;
        /// <summary>Shorter runs cost more manifest than they save.</summary>
        public const int MinRun = 1 << 10;

        static readonly ulong[] Gear = MakeGear();

        readonly Dictionary<(UInt128 Hash, int Length), (FileEntry File, long Offset)> index = new();
        readonly ConcurrentDictionary<FileEntry, Lazy<MappedFile>> contents = new();

        /// <summary>Indexes stock files in order; on duplicate content the earlier file wins, so pass core files first.</summary>
        public CopyMatcher(IReadOnlyList<FileEntry> stock)
        {
            var chunks = new List<(UInt128, int, long)>[stock.Count];
            Parallel.For(0, stock.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                var bytes = Contents(stock[i]).Span;
                var list = chunks[i] = [];
                foreach (var (offset, length) in Chunks(bytes)) list.Add((Hash(bytes.Slice(offset, length)), length, offset));
            });
            for (int i = 0; i < stock.Count; i++)
                foreach (var (hash, length, offset) in chunks[i])
                    index.TryAdd((hash, length), (stock[i], offset));
        }

        public int Count => index.Count;

        public void Dispose()
        {
            foreach (var mapped in contents.Values) mapped.Value.Dispose();
        }

        public List<CopyRun> Match(FileEntry file, Func<FileEntry, bool> inScope)
        {
            using var mapped = new MappedFile(file.Full, file.Size);
            var target = mapped.Span;
            var runs = new List<CopyRun>();
            int coveredTo = 0;
            foreach (var (offset, length) in Chunks(target))
            {
                if (offset + length <= coveredTo) continue;
                if (!index.TryGetValue((Hash(target.Slice(offset, length)), length), out var hit) || !inScope(hit.File)) continue;
                var source = Contents(hit.File).Span;
                int sourceOffset = (int)hit.Offset;
                if (!target.Slice(offset, length).SequenceEqual(source.Slice(sourceOffset, length))) continue;

                // a chunk can straddle the end of the previous run; only the uncovered part is new
                int start = Math.Max(offset, coveredTo), sourceStart = sourceOffset + (start - offset);
                while (start > coveredTo && sourceStart > 0 && target[start - 1] == source[sourceStart - 1])
                {
                    start--;
                    sourceStart--;
                }
                int end = offset + length;
                end += target[end..].CommonPrefixLength(source[(sourceOffset + length)..]);

                if (end - start >= MinRun) runs.Add(new CopyRun(start, end - start, hit.File, sourceStart));
                coveredTo = end;
            }
            return runs;
        }

        MappedFile Contents(FileEntry file) =>
            contents.GetOrAdd(file, f => new Lazy<MappedFile>(() => new MappedFile(f.Full, f.Size))).Value;

        static UInt128 Hash(ReadOnlySpan<byte> bytes)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes, digest);
            return new UInt128(BitConverter.ToUInt64(digest), BitConverter.ToUInt64(digest[8..]));
        }

        static List<(int Offset, int Length)> Chunks(ReadOnlySpan<byte> data)
        {
            var chunks = new List<(int, int)>(data.Length / (16 << 10) + 1);
            int start = 0;
            ulong h = 0;
            for (int i = 0; i < data.Length; i++)
            {
                h = (h << 1) + Gear[data[i]];
                int length = i - start + 1;
                if ((length >= MinChunk && (h & CutMask) == 0) || length >= MaxChunk)
                {
                    chunks.Add((start, length));
                    start = i + 1;
                    h = 0;
                }
            }
            if (start < data.Length) chunks.Add((start, data.Length - start));
            return chunks;
        }

        static ulong[] MakeGear()
        {
            var gear = new ulong[256];
            var rng = new Random(0x5EED);
            for (int i = 0; i < gear.Length; i++) gear[i] = (ulong)rng.NextInt64() ^ ((ulong)rng.Next() << 63);
            return gear;
        }
    }
}
