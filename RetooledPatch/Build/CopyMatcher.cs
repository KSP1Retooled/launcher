using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace KSP.Retooled.Patch.Build;

public static partial class PatchBuilder
{
    /// <summary>
    /// A run of target bytes identical to <see cref="Length"/> bytes of <see cref="Source"/> at
    /// <see cref="SourceOffset"/>.
    /// </summary>
    private readonly record struct CopyRun(long Start, long Length, FileEntry Source, long SourceOffset)
    {
        public long End => this.Start + this.Length;
    }

    /// <summary>
    /// Finds target runs that are byte-identical to stock content anywhere in scope. Unity relocates texture, mesh and
    /// audio payloads between files and offsets but mostly keeps their bytes, so chunks cut by a content-defined (gear)
    /// hash line up on both sides regardless of position; each hit is verified and extended byte by byte.
    /// </summary>
    private sealed class CopyMatcher : IDisposable
    {
        private const int MIN_CHUNK = 4 << 10, MAX_CHUNK = 128 << 10;
        private const ulong CUT_MASK = (1UL << 14) - 1;
        /// <summary>
        /// Shorter runs cost more manifest than they save.
        /// </summary>
        private const int MIN_RUN = 1 << 10;

        private static readonly ulong[] Gear = MakeGear();
        private readonly ConcurrentDictionary<FileEntry, Lazy<MappedFile>> contents = new();

        private readonly Dictionary<(UInt128 Hash, int Length), (FileEntry File, long Offset)> index = new();

        /// <summary>
        /// Indexes stock files in order; on duplicate content the earlier file wins, so pass core files first.
        /// </summary>
        public CopyMatcher(IReadOnlyList<FileEntry> stock)
        {
            List<(UInt128, int, long)>[] chunks = new List<(UInt128, int, long)>[stock.Count];
            Parallel.For(0, stock.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                ReadOnlySpan<byte> bytes = Contents(stock[i]).Span;
                List<(UInt128, int, long)> list = chunks[i] = [];
                foreach ((int offset, int length) in Chunks(bytes)) list.Add((Hash(bytes.Slice(offset, length)), length, offset));
            });

            for (int i = 0; i < stock.Count; i++)
            {
                foreach ((UInt128 hash, int length, long offset) in chunks[i])
                {
                    this.index.TryAdd((hash, length), (stock[i], offset));
                }
            }
        }

        public int Count => this.index.Count;

        public void Dispose()
        {
            foreach (Lazy<MappedFile> mapped in this.contents.Values) mapped.Value.Dispose();
        }

        public List<CopyRun> Match(FileEntry file, Func<FileEntry, bool> inScope)
        {
            using MappedFile mapped = new(file.full, file.size);
            ReadOnlySpan<byte> target = mapped.Span;
            List<CopyRun> runs = [];
            int coveredTo = 0;
            foreach ((int offset, int length) in Chunks(target))
            {
                if (offset + length <= coveredTo) continue;
                if (!this.index.TryGetValue((Hash(target.Slice(offset, length)), length), out (FileEntry File, long Offset) hit) || !inScope(hit.File)) continue;

                ReadOnlySpan<byte> source = Contents(hit.File).Span;
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

                if (end - start >= MIN_RUN) runs.Add(new CopyRun(start, end - start, hit.File, sourceStart));
                coveredTo = end;
            }

            return runs;
        }

        private MappedFile Contents(FileEntry file) => this.contents.GetOrAdd(file, f => new Lazy<MappedFile>(() => new MappedFile(f.full, f.size))).Value;

        private static UInt128 Hash(ReadOnlySpan<byte> bytes)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes, digest);
            return new UInt128(BitConverter.ToUInt64(digest), BitConverter.ToUInt64(digest[8..]));
        }

        private static List<(int Offset, int Length)> Chunks(ReadOnlySpan<byte> data)
        {
            List<(int, int)> chunks = new((data.Length / (16 << 10)) + 1);
            int start = 0;
            ulong h = 0;
            for (int i = 0; i < data.Length; i++)
            {
                h = (h << 1) + Gear[data[i]];
                int length = (i - start) + 1;
                if ((length >= MIN_CHUNK && (h & CUT_MASK) is 0) || length >= MAX_CHUNK)
                {
                    chunks.Add((start, length));
                    start = i + 1;
                    h     = 0;
                }
            }

            if (start < data.Length) chunks.Add((start, data.Length - start));
            return chunks;
        }

        private static ulong[] MakeGear()
        {
            ulong[] gear = new ulong[256];
            Random rng = new(0x5EED);
            for (int i = 0; i < gear.Length; i++) gear[i] = (ulong)rng.NextInt64() ^ ((ulong)rng.Next() << 63);
            return gear;
        }
    }
}
