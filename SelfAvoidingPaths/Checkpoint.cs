using System.Runtime.InteropServices;

namespace SelfAvoidingPaths;

/// <summary>Metadata describing a loadable checkpoint (one completed layer).</summary>
public sealed record CheckpointInfo(
    string LayerDir, int CompletedEdge, int KeyWidth, int ValWidth,
    long Count, long PeakStates, long TotalStates);

/// <summary>Thrown to stop a run after a requested layer (leaves the checkpoint intact).</summary>
public sealed class StopRequested(int layer) : Exception { public int Layer { get; } = layer; }

/// <summary>
/// Durable checkpoint / resume for the frontier sweep. A checkpoint stores one
/// completed layer as 256 partition files (the <see cref="DiskLayer"/> on-disk
/// format) plus a manifest written last as the atomic commit marker. Commit is
/// write-to-temp-then-rename, so an interrupted write never corrupts the latest
/// good checkpoint.
/// </summary>
public static class Checkpoint
{
    private const int P = StateStore.ShardCount;

    public static void Write(string root, ILayer layer, int n, int edgeCount, int valWidth,
        int completedEdge, long peakStates, long totalStates)
    {
        int kw = layer.KeyWidth;
        Directory.CreateDirectory(root);
        string tmp = Path.Combine(root, "tmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        for (int p = 0; p < layer.Partitions; p++)
        {
            using var fs = new FileStream(Path.Combine(tmp, $"part_{p}.bin"),
                FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            layer.Read(p, (key, val) => { fs.Write(key); fs.Write(MemoryMarshal.AsBytes(val)); });
        }

        // Manifest written last: its presence marks the checkpoint as complete.
        File.WriteAllText(Path.Combine(tmp, "manifest.txt"),
            $"version=1\nn={n}\nedgeCount={edgeCount}\nvalWidth={valWidth}\nkeyWidth={kw}\n" +
            $"completedEdge={completedEdge}\ncount={layer.Count}\npeakStates={peakStates}\ntotalStates={totalStates}\n");

        string finalName = $"ck_{completedEdge}";
        string final = Path.Combine(root, finalName);
        if (Directory.Exists(final)) TryDeleteDir(final);
        Directory.Move(tmp, final);

        // Drop older checkpoints and any orphaned temp dirs.
        foreach (var d in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(d);
            if (name == finalName) continue;
            if (name.StartsWith("ck_") || name.StartsWith("tmp_")) TryDeleteDir(d);
        }
    }

    /// <summary>Latest valid checkpoint for this (n, edgeCount), or null.</summary>
    public static CheckpointInfo? TryLoad(string root, int n, int edgeCount)
    {
        if (!Directory.Exists(root)) return null;
        CheckpointInfo? best = null;
        foreach (var d in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(d);
            if (name.StartsWith("tmp_")) { TryDeleteDir(d); continue; }   // incomplete write
            if (!name.StartsWith("ck_")) continue;
            string man = Path.Combine(d, "manifest.txt");
            if (!File.Exists(man)) { TryDeleteDir(d); continue; }          // incomplete

            var kv = ParseManifest(man);
            if (kv is null || !kv.TryGetValue("n", out var sn) || int.Parse(sn) != n) continue;
            if (!kv.TryGetValue("edgeCount", out var se) || int.Parse(se) != edgeCount) continue;

            int ce = int.Parse(kv["completedEdge"]);
            if (best is null || ce > best.CompletedEdge)
                best = new CheckpointInfo(d, ce, int.Parse(kv["keyWidth"]), int.Parse(kv["valWidth"]),
                    long.Parse(kv["count"]), long.Parse(kv["peakStates"]), long.Parse(kv["totalStates"]));
        }
        return best;
    }

    /// <summary>Load a checkpoint layer: into RAM if small, else aliased on disk.</summary>
    public static ILayer LoadLayer(CheckpointInfo info, long ramThreshold)
    {
        var files = new string?[P];
        for (int p = 0; p < P; p++) files[p] = Path.Combine(info.LayerDir, $"part_{p}.bin");

        if (info.Count <= ramThreshold)
        {
            var st = new StateStore(info.KeyWidth, info.ValWidth);
            int rec = info.KeyWidth + info.ValWidth * 8;
            byte[] buf = new byte[rec];
            for (int p = 0; p < P; p++)
            {
                string f = files[p]!;
                if (!File.Exists(f)) continue;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16);
                long len = fs.Length;
                while (fs.Position < len)
                {
                    fs.ReadExactly(buf, 0, rec);
                    st.Accumulate(buf.AsSpan(0, info.KeyWidth),
                        MemoryMarshal.Cast<byte, ulong>(buf.AsSpan(info.KeyWidth, info.ValWidth * 8)));
                }
            }
            return new RamLayer(st);
        }

        // Alias the checkpoint files (do not delete them as they are consumed).
        return new DiskLayer(info.LayerDir, info.KeyWidth, info.ValWidth, files, info.Count, ownsFiles: false);
    }

    /// <summary>Remove all checkpoint data (call on successful completion).</summary>
    public static void Cleanup(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var d in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(d);
            if (name.StartsWith("ck_") || name.StartsWith("tmp_")) TryDeleteDir(d);
        }
    }

    private static Dictionary<string, string>? ParseManifest(string path)
    {
        try
        {
            var d = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) d[line[..eq]] = line[(eq + 1)..];
            }
            return d;
        }
        catch { return null; }
    }

    private static void TryDeleteDir(string d) { try { Directory.Delete(d, recursive: true); } catch { } }
}
