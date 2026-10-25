using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SelfAvoidingPaths;

/// <summary>Callback receiving one (state key, count limbs) record.</summary>
public delegate void RecordHandler(ReadOnlySpan<byte> key, ReadOnlySpan<ulong> val);

/// <summary>
/// A frontier layer split into <see cref="StateStore.ShardCount"/> hash
/// partitions. Backed either by RAM (<see cref="RamLayer"/>) or disk files
/// (<see cref="DiskLayer"/>). A key always maps to one partition by hash, so a
/// partition can be deduped independently.
/// </summary>
public interface ILayer : IDisposable
{
    int Partitions { get; }
    long Count { get; }
    int KeyWidth { get; }
    void Read(int partition, RecordHandler handler);
    void ReleasePartition(int partition);
}

/// <summary>Accumulates the next layer; <see cref="Finish"/> yields it as an <see cref="ILayer"/>.</summary>
public interface ISink
{
    void Add(ReadOnlySpan<byte> key, ReadOnlySpan<ulong> val);
    ILayer Finish();
}

// ---------------------------------------------------------------- RAM backend

public sealed class RamLayer(StateStore store) : ILayer
{
    private readonly StateStore _s = store;
    public int Partitions => StateStore.ShardCount;
    public long Count => _s.Count;
    public int KeyWidth => _s.KeyWidth;

    public void Read(int partition, RecordHandler handler)
    {
        var sh = _s.Shards[partition];
        if (sh is null) return;
        int kw = _s.KeyWidth, c = sh.Count;
        for (int e = 0; e < c; e++)
            handler(sh.Keys.AsSpan(e * kw, kw), sh.ValAt(e));
    }

    public void ReleasePartition(int partition) => _s.ReleaseShard(partition);
    public void Dispose() { }
}

public sealed class RamSink(int keyWidth, int valWidth, int initCapPerShard = 8) : ISink
{
    private readonly StateStore _s = new(keyWidth, valWidth, initCapPerShard);
    public void Add(ReadOnlySpan<byte> key, ReadOnlySpan<ulong> val) => _s.Accumulate(key, val);
    public ILayer Finish() => new RamLayer(_s);
}

// --------------------------------------------------------------- disk backend

public sealed class DiskLayer : ILayer
{
    private readonly string _dir;
    private readonly int _kw, _vw, _rec;
    private readonly string?[] _files;
    private readonly bool _ownsFiles;   // false for a resumed checkpoint (do not delete)
    private readonly bool _compress;
    public int Partitions => _files.Length;
    public long Count { get; }
    public int KeyWidth => _kw;

    public DiskLayer(string dir, int kw, int vw, string?[] files, long count,
        bool ownsFiles = true, bool compress = false)
    {
        _dir = dir; _kw = kw; _vw = vw; _rec = kw + vw * 8; _files = files; Count = count;
        _ownsFiles = ownsFiles; _compress = compress;
    }

    public void Read(int partition, RecordHandler handler)
    {
        string? f = _files[partition];
        if (f is null || !File.Exists(f)) return;
        using var s = OpenRead(f, _compress);
        byte[] buf = new byte[_rec];
        while (ReadRecord(s, buf, _rec))
            handler(buf.AsSpan(0, _kw), MemoryMarshal.Cast<byte, ulong>(buf.AsSpan(_kw, _vw * 8)));
    }

    // Spill-file I/O helpers (optionally Deflate-compressed; compressed streams
    // are not seekable, so records are read until a clean end-of-stream).
    internal static Stream OpenRead(string path, bool compress)
    {
        Stream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16);
        return compress ? new DeflateStream(fs, CompressionMode.Decompress) : fs;
    }

    internal static Stream OpenWrite(string path, bool compress)
    {
        Stream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        return compress ? new DeflateStream(fs, CompressionLevel.Fastest) : fs;
    }

    internal static bool ReadRecord(Stream s, byte[] buf, int rec)
    {
        int off = 0;
        while (off < rec)
        {
            int n = s.Read(buf, off, rec - off);
            if (n == 0) return off == 0 ? false : throw new EndOfStreamException("truncated spill record");
            off += n;
        }
        return true;
    }

    public void ReleasePartition(int partition)
    {
        string? f = _files[partition];
        if (f is not null) { if (_ownsFiles) TryDelete(f); _files[partition] = null; }
    }

    public void Dispose()
    {
        if (!_ownsFiles) return; // aliased checkpoint files: leave them in place
        foreach (var f in _files) if (f is not null) TryDelete(f);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    internal static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}

public sealed class DiskSink : ISink
{
    private readonly string _dir;
    private readonly int _kw, _vw, _rec;
    private readonly bool _compress;
    private readonly Stream[] _raw;
    private readonly string[] _rawPaths;
    private readonly object[] _locks;
    private const int P = StateStore.ShardCount;

    public DiskSink(int keyWidth, int valWidth, string root, int layerId, bool compress = false)
    {
        _kw = keyWidth; _vw = valWidth; _rec = keyWidth + valWidth * 8; _compress = compress;
        _dir = Path.Combine(root, $"L{layerId}");
        Directory.CreateDirectory(_dir);
        _raw = new Stream[P];
        _rawPaths = new string[P];
        _locks = new object[P];
        for (int p = 0; p < P; p++)
        {
            _rawPaths[p] = Path.Combine(_dir, $"raw_{p}.bin");
            _raw[p] = DiskLayer.OpenWrite(_rawPaths[p], compress);
            _locks[p] = new object();
        }
    }

    public void Add(ReadOnlySpan<byte> key, ReadOnlySpan<ulong> val)
    {
        int p = (int)(StateStore.Hash(key) & (P - 1));
        lock (_locks[p])
        {
            _raw[p].Write(key);
            _raw[p].Write(MemoryMarshal.AsBytes(val));
        }
    }

    public ILayer Finish()
    {
        for (int p = 0; p < P; p++) _raw[p].Dispose();

        var files = new string?[P];
        long total = 0;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        object sync = new();

        Parallel.For(0, P, opts, p =>
        {
            // Dedup partition p in RAM: all its keys share (hash & mask)==p, so a
            // single open-addressing shard holds the partition's distinct states.
            var shard = new StateStore.Shard(_kw, _vw);
            byte[] buf = new byte[_rec];
            using (var s = DiskLayer.OpenRead(_rawPaths[p], _compress))
            {
                while (DiskLayer.ReadRecord(s, buf, _rec))
                {
                    var key = buf.AsSpan(0, _kw);
                    var val = MemoryMarshal.Cast<byte, ulong>(buf.AsSpan(_kw, _vw * 8));
                    ulong h = StateStore.Hash(key);
                    lock (shard.Sync) shard.Add(key, val, h);
                }
            }
            DiskLayer.TryDelete(_rawPaths[p]);

            string outPath = Path.Combine(_dir, $"layer_{p}.bin");
            long cnt = shard.Count;
            using (var os = DiskLayer.OpenWrite(outPath, _compress))
            {
                for (int e = 0; e < shard.Count; e++)
                {
                    os.Write(shard.Keys.AsSpan(e * _kw, _kw));
                    os.Write(MemoryMarshal.AsBytes(shard.ValAt(e)));
                }
            }
            files[p] = outPath;
            lock (sync) total += cnt;
        });

        return new DiskLayer(_dir, _kw, _vw, files, total, ownsFiles: true, compress: _compress);
    }
}
