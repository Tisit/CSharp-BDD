using System.Diagnostics;
using System.Numerics;
using SelfAvoidingPaths;

// Known values of A007764(n): self-avoiding rook paths joining opposite
// corners of an n x n grid graph. Source: OEIS A007764 / Iwashita et al. 2012.
var known = new Dictionary<int, BigInteger>
{
    [1] = BigInteger.Parse("1"),
    [2] = BigInteger.Parse("2"),
    [3] = BigInteger.Parse("12"),
    [4] = BigInteger.Parse("184"),
    [5] = BigInteger.Parse("8512"),
    [6] = BigInteger.Parse("1262816"),
    [7] = BigInteger.Parse("575780564"),
    [8] = BigInteger.Parse("789360053252"),
    [9] = BigInteger.Parse("3266598486981642"),
    [10] = BigInteger.Parse("41044208702632496804"),
    [11] = BigInteger.Parse("1568758030464750013214100"),
    [12] = BigInteger.Parse("182413291514248049241470885236"),
    [13] = BigInteger.Parse("64528039343270018963357185158482118"),
    [14] = BigInteger.Parse("69450664761521361664274701548907358996488"),
    [15] = BigInteger.Parse("227449714676812739631826459327989863387613323440"),
    [16] = BigInteger.Parse("2266745568862672746374567396713098934866324885408319028"),
    [17] = BigInteger.Parse("68745445609149931587631563132489232824587945968099457285419306"),
    [18] = BigInteger.Parse("6344814611237963971310297540795524400449443986866480693646369387855336"),
    [19] = BigInteger.Parse("1782112840842065129893384946652325275167838065704767655931452474605826692782532"),
    [20] = BigInteger.Parse("1523344971704879993080742810319229690899454255323294555776029866737355060592877569255844"),
    [21] = BigInteger.Parse("3962892199823037560207299517133362502106339705739463771515237113377010682364035706704472064940398"),
    [22] = BigInteger.Parse("31374751050137102720420538137382214513103312193698723653061351991346433379389385793965576992246021316463868"),
    [23] = BigInteger.Parse("755970286667345339661519123315222619353103732072409481167391410479517925792743631234987038883317634987271171404439792"),
    [24] = BigInteger.Parse("55435429355237477009914318489061437930690379970964331332556958646484008407334885544566386924020875711242060085408513482933945720"),
    [25] = BigInteger.Parse("12371712231207064758338744862673570832373041989012943539678727080484951695515930485641394550792153037191858028212512280926600304581386791094"),
    [26] = BigInteger.Parse("8402974857881133471007083745436809127296054293775383549824742623937028497898215256929178577083970960121625602506027316549718402106494049978375604247408"),
    [27] = BigInteger.Parse("17369931586279272931175440421236498900372229588288140604663703720910342413276134762789218193498006107082296223143380491348290026721931129627708738890853908108906396"),
};

// Optional global flag: --maxram <GB> aborts a run cleanly if the working set
// exceeds the cap (so a too-large n fails gracefully instead of being OOM-killed).
double maxRamGb = double.PositiveInfinity;
var argList = new List<string>(args);
int mi = argList.IndexOf("--maxram");
if (mi >= 0 && mi + 1 < argList.Count && double.TryParse(argList[mi + 1],
        System.Globalization.CultureInfo.InvariantCulture, out double cap))
{
    maxRamGb = cap;
    argList.RemoveRange(mi, 2);
}

// --fast enables array pooling: ~1.4x faster but ~2x peak RAM (default is
// memory-lean, which reaches higher n).
if (argList.Remove("--fast"))
    SelfAvoidingPaths.StateStore.PoolingEnabled = true;

// --no-pack-keys disables bit-packed keys (~13% faster in-core, ~9% more RAM).
if (argList.Remove("--no-pack-keys"))
    SelfAvoidingPaths.FrontierCounter.PackKeys = false;

// --ooc [dir] enables out-of-core mode: layers larger than the RAM budget
// (--maxram, default 2 GB) spill to disk, so RAM stays bounded regardless of n.
bool ooc = false;
string? oocDir = null;
int oi = argList.IndexOf("--ooc");
if (oi >= 0)
{
    ooc = true;
    if (oi + 1 < argList.Count && !argList[oi + 1].StartsWith('-') && !int.TryParse(argList[oi + 1], out _))
    {
        oocDir = argList[oi + 1];
        argList.RemoveAt(oi + 1);
    }
    argList.RemoveAt(oi);
}
double oocBudgetGb = double.IsPositiveInfinity(maxRamGb) ? 2.0 : maxRamGb;
bool compressSpill = argList.Remove("--compress-spill");  // Deflate spill files (OOC I/O win)

// --checkpoint <dir> enables durable checkpoint/resume; --checkpoint-every <sec>
// sets the interval (default 300). --stop-at <k> (debug) stops after layer k.
string? checkpointDir = null;
int ci = argList.IndexOf("--checkpoint");
if (ci >= 0 && ci + 1 < argList.Count)
{
    checkpointDir = argList[ci + 1];
    argList.RemoveRange(ci, 2);
}
double checkpointEvery = 300;
int cei = argList.IndexOf("--checkpoint-every");
if (cei >= 0 && cei + 1 < argList.Count && double.TryParse(argList[cei + 1],
        System.Globalization.CultureInfo.InvariantCulture, out double cev))
{
    checkpointEvery = cev;
    argList.RemoveRange(cei, 2);
}
int stopAt = -1;
int sai = argList.IndexOf("--stop-at");
if (sai >= 0 && sai + 1 < argList.Count && int.TryParse(argList[sai + 1], out int sav))
{
    stopAt = sav;
    argList.RemoveRange(sai, 2);
}

// --lookahead <W> enables windowed lookahead (RAM-only; not with --ooc/--checkpoint).
int lookahead = -1;
int lai = argList.IndexOf("--lookahead");
if (lai >= 0 && lai + 1 < argList.Count && int.TryParse(argList[lai + 1], out int law))
{
    lookahead = Math.Max(0, law);
    argList.RemoveRange(lai, 2);
}
if (lookahead >= 0 && checkpointDir is not null)
{
    Console.Error.WriteLine("--lookahead cannot be combined with --checkpoint.");
    return 2;
}

// Dense (GGCount-style) engine flags:
//   --dense       exact count, dense in-place engine (no hash tables)
//   --mod <m>     dense engine, one run modulo m ("2^64" or a decimal); prints the residue
//   --crt         dense engine, exact count via coprime modular runs + CRT (lowest RAM)
bool dense = argList.Remove("--dense");
bool crt = argList.Remove("--crt");
// --crt-known: re-derive a published term (n <= 27): check every residue against the
// known value, stop once the moduli's product exceeds it, no held-out runs (~5% fewer
// runs). Not an independent computation; ignored (with a warning) when n is unknown.
bool crtKnown = argList.Remove("--crt-known");
// --mitm: meet in the middle for even n (--mod/--crt): sweep the top n/2 rows, then join.
bool mitm = argList.Remove("--mitm");
crt |= crtKnown;

// --bits <12|16|32|64>: modular storage width (default 64). Narrower widths cut
// per-run RAM (12 = 1.5 bytes/state, packed + chunked — what fits n=24 in
// 16 GB) at the cost of proportionally more CRT runs.
int storeBits = 64;
int bti = argList.IndexOf("--bits");
if (bti >= 0 && bti + 1 < argList.Count && int.TryParse(argList[bti + 1], out int bv))
{
    if (bv is not (12 or 16 or 32 or 64)) { Console.Error.WriteLine("--bits must be 12, 16, 32 or 64."); return 2; }
    storeBits = bv;
    argList.RemoveRange(bti, 2);
}

// --crt-part K/N [--crt-runs M]: run only this machine's share of a CRT
// campaign split over N machines (merge the state files with --crt-merge).
(int part, int parts)? crtPart = null;
int cpi = argList.IndexOf("--crt-part");
if (cpi >= 0)
{
    string[] kn = cpi + 1 < argList.Count ? argList[cpi + 1].Split('/') : [];
    if (kn.Length != 2 || !int.TryParse(kn[0], out int pk) || !int.TryParse(kn[1], out int pn) || pn < 1 || pk < 1 || pk > pn)
    {
        Console.Error.WriteLine("--crt-part requires K/N with 1 <= K <= N (e.g. 1/2).");
        return 2;
    }
    crtPart = (pk, pn);
    argList.RemoveRange(cpi, 2);
}
int? crtRuns = null;
int cri = argList.IndexOf("--crt-runs");
if (cri >= 0)
{
    if (cri + 1 >= argList.Count || !int.TryParse(argList[cri + 1], out int crv) || crv < 2)
    {
        Console.Error.WriteLine("--crt-runs requires an integer >= 2.");
        return 2;
    }
    crtRuns = crv;
    argList.RemoveRange(cri, 2);
}
crt |= crtPart is not null;

// --ladder [dir] [--ladder-gb G] [--rungs a,b,c]: residue ladder for CRT campaigns.
// Later runs start mid-grid from an exact row vector rebuilt from earlier runs'
// dumps (DenseCounter.Ladder.cs). Disk budget defaults to 90% of free space.
LadderOptions? ladderOpt = null;
int ldi = argList.IndexOf("--ladder");
if (ldi >= 0)
{
    // The directory is optional: the next argument is taken unless it is a flag or n.
    bool hasDir = ldi + 1 < argList.Count && !argList[ldi + 1].StartsWith("--") && !int.TryParse(argList[ldi + 1], out _);
    string ldir = hasDir ? argList[ldi + 1] : ".";
    argList.RemoveRange(ldi, hasDir ? 2 : 1);
    double? lgb = null;
    int lgi = argList.IndexOf("--ladder-gb");
    if (lgi >= 0)
    {
        if (lgi + 1 >= argList.Count || !double.TryParse(argList[lgi + 1], System.Globalization.CultureInfo.InvariantCulture, out double g) || g < 0)
        { Console.Error.WriteLine("--ladder-gb requires a number of GB."); return 2; }
        lgb = g;
        argList.RemoveRange(lgi, 2);
    }
    int[]? rungs = null;
    int rgi = argList.IndexOf("--rungs");
    if (rgi >= 0)
    {
        try { rungs = argList[rgi + 1].Split(',').Select(int.Parse).ToArray(); }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
        { Console.Error.WriteLine("--rungs requires a comma-separated list of row numbers (e.g. 6,12)."); return 2; }
        argList.RemoveRange(rgi, 2);
    }
    (double r, double w)? disk = null;
    int dki = argList.IndexOf("--ladder-disk");
    if (dki >= 0)
    {
        string[] rw = dki + 1 < argList.Count ? argList[dki + 1].Split(',') : [];
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (rw.Length != 2 || !double.TryParse(rw[0], ic, out double dr) || !double.TryParse(rw[1], ic, out double dw) || dr <= 0 || dw <= 0)
        { Console.Error.WriteLine("--ladder-disk requires READ,WRITE in GB/s (e.g. 0.5,0.5)."); return 2; }
        disk = (dr, dw);
        argList.RemoveRange(dki, 2);
    }
    ladderOpt = new LadderOptions(ldir, lgb, rungs, disk?.r, disk?.w);
    if (!crt || crtPart is not null) { Console.Error.WriteLine("--ladder needs --crt or --crt-known (not --crt-part)."); return 2; }
}

ulong? modArg = null;
int mdi = argList.IndexOf("--mod");
if (mdi >= 0 && mdi + 1 < argList.Count)
{
    string mv = argList[mdi + 1];
    if (mv is "2^64") { modArg = 0; storeBits = 64; }
    else if (mv is "2^32") { modArg = 0; storeBits = 32; }
    else if (mv is "max") modArg = 0;
    else if (ulong.TryParse(mv, out ulong mval) && mval >= 2) modArg = mval;
    else
    {
        Console.Error.WriteLine("--mod requires \"2^64\", \"2^32\", \"max\", or an integer >= 2.");
        return 2;
    }
    if (modArg is ulong mm && mm != 0 &&
        ((storeBits == 32 && mm > uint.MaxValue) || (storeBits == 12 && mm > 4096) || (storeBits == 16 && mm > 65536)))
    {
        Console.Error.WriteLine($"--mod value must be < 2^{storeBits} when --bits {storeBits}.");
        return 2;
    }
    argList.RemoveRange(mdi, 2);
}
dense |= crt || modArg is not null;
if (dense && (ooc || checkpointDir is not null || lookahead >= 0))
{
    Console.Error.WriteLine("--dense/--mod/--crt cannot be combined with --ooc, --checkpoint or --lookahead.");
    return 2;
}
if (mitm && (!dense || (!crt && modArg is null) || crtPart is not null || ladderOpt is not null))
{
    Console.Error.WriteLine("--mitm needs --mod or --crt (not --crt-part or --ladder).");
    return 2;
}
if (crt && modArg is not null)
{
    Console.Error.WriteLine("--crt and --mod are mutually exclusive (CRT picks its own moduli).");
    return 2;
}

// --cores <N> caps the worker count. Absent -> all logical cores. N is clamped
// to [1, ProcessorCount]; note this workload is memory-bandwidth-bound, so
// scaling past a handful of cores yields diminishing returns.
int? cores = null;
int ri = argList.IndexOf("--cores");
if (ri >= 0 && ri + 1 < argList.Count && int.TryParse(argList[ri + 1], out int rc))
{
    if (rc < 1)
    {
        Console.Error.WriteLine("--cores must be >= 1.");
        return 2;
    }
    cores = Math.Min(rc, Environment.ProcessorCount);
    argList.RemoveRange(ri, 2);
}
else if (ri >= 0)
{
    Console.Error.WriteLine("--cores requires a positive integer argument.");
    return 2;
}

args = argList.ToArray();

// Reclaim spill dirs orphaned by earlier interrupted/killed runs (stale > 1h;
// an active run keeps touching its dir, so it is never swept).
SweepStaleSpillDirs(System.IO.Path.GetTempPath(), oocDir);

if (args.Length == 0)
{
    Console.WriteLine("Self-avoiding corner-to-corner rook paths on an n x n grid (OEIS A007764).");
    Console.WriteLine("Frontier-based search (SIMPATH, direct/memory-lean form).\n");
    Console.WriteLine("Usage:");
    Console.WriteLine("  sapaths <n>            count paths on the n x n grid");
    Console.WriteLine("  sapaths --table <max>  count for n = 1..max");
    Console.WriteLine("  sapaths --selftest     verify against known A007764 values");
    Console.WriteLine("\nOptions:");
    Console.WriteLine("  --maxram <GB>          RAM budget: abort cap, or spill threshold with --ooc");
    Console.WriteLine("  --fast                 array pooling: ~1.4x faster, ~2x peak RAM");
    Console.WriteLine("  --ooc [dir]            out-of-core: spill large layers to disk (bounded RAM)");
    Console.WriteLine("  --compress-spill       compress spill files (less OOC disk I/O; use with --ooc)");
    Console.WriteLine("  --checkpoint <dir>     periodic checkpoint + auto-resume from <dir>");
    Console.WriteLine("  --checkpoint-every <s> checkpoint interval in seconds (default 300)");
    Console.WriteLine("  --lookahead <W>        windowed lookahead: fewer states (faster; RAM ok with --ooc)");
    Console.WriteLine("  --dense                dense in-place engine (GGCount-style; no hash tables)");
    Console.WriteLine("  --mod <m>              dense engine, count modulo m (\"2^64\"/\"2^32\"/\"max\"/decimal)");
    Console.WriteLine("  --crt                  dense engine, exact count via modular runs + CRT (lowest RAM)");
    Console.WriteLine("  --bits <12|16|32|64>   dense modular storage width (default 64; narrower = less RAM, more runs)");
    Console.WriteLine("  --crt-part K/N         run only part K of a CRT campaign split over N machines");
    Console.WriteLine("  --crt-runs <M>         split campaign: total runs (default: size estimate + margin + checks)");
    Console.WriteLine("  --crt-plan <n>         print a split campaign's run list (use with --bits)");
    Console.WriteLine("  --crt-merge <files...> combine + verify the part state files of a split campaign");
    Console.WriteLine("  --crt-known            CRT checked against the known a(n) (n <= 27): fewer runs, no held-out checks");
    Console.WriteLine("  --ladder [dir]         CRT: later runs start mid-grid from rebuilt exact row vectors (disk files in dir)");
    Console.WriteLine("  --ladder-gb <G>        ladder disk budget in GB (default 90% of free space)");
    Console.WriteLine("  --rungs <a,b,..>       ladder rows to use instead of the automatic plan");
    Console.WriteLine("  --ladder-disk <R,W>    ladder disk read,write speed in GB/s (default: measured)");
    Console.WriteLine("  --mitm                 --mod/--crt, even n: sweep the top half only, then join (meet in the middle)");
    Console.WriteLine("  --dense-info <n>       report dense state counts + projected RAM (no run)");
    Console.WriteLine("  --cores <N>            cap worker threads (default: all cores)");
    Console.WriteLine("  --no-pack-keys         disable bit-packed keys (~13% faster, ~9% more RAM)");
    Console.WriteLine("\nLive per-layer progress (states + RAM) is printed to stderr during a run.");
    return 0;
}

if (args[0] is "--selftest")
{
    bool ok = true;
    foreach (var n in known.Keys.Where(k => k <= 8).OrderBy(k => k))
    {
        BigInteger got;
        if (dense)
            got = crt ? DenseCounter.CountWithCrt(n, cores, storeBits: storeBits, ladder: ladderOpt, mitm: mitm)
                : n == 1 ? BigInteger.One
                : new DenseCounter(n).CountExact(cores);
        else
            got = new FrontierCounter(n).Count();
        bool pass = got == known[n];
        ok &= pass;
        Console.WriteLine($"n={n,2}  {(pass ? "OK  " : "FAIL")}  {got}");
        if (!pass) Console.WriteLine($"       expected {known[n]}");
    }
    Console.WriteLine(ok ? "\nAll self-tests passed." : "\nSELF-TEST FAILURE.");
    return ok ? 0 : 1;
}

// Cheap size probe: build the code tables (no giant value array) and report the
// dense state counts + projected RAM at each storage width, so feasibility of a
// large n can be checked in a second before committing to a multi-hour run.
if (args[0] is "--dense-info")
{
    if (args.Length < 2 || !int.TryParse(args[1], out int di) || di < 2)
    {
        Console.Error.WriteLine("usage: sapaths --dense-info <n>");
        return 2;
    }
    var probe = new DenseCounter(di);
    long st = probe.StateCount, df = probe.DeferredCount;
    const long lim = 2_147_483_591;   // .NET max single-dimension array length
    Console.WriteLine($"n = {di}: dense states = {st:N0}, deferred = {df:N0}");
    Console.WriteLine($"  + transition tables {probe.TransitionTableBytes / (1024.0 * 1024):F1} MB (all widths)");
    foreach (int b in new[] { 64, 32, 16, 12 })
    {
        double gb = (st + df) * (b / 8.0) / (1024.0 * 1024 * 1024);
        // 12/16-bit storage is native, so the array-element limit never applies to it.
        string note = b is 12 or 16 ? "native (no array limit)"
            : st <= lim && df <= lim ? "flat arrays OK"
            : "EXCEEDS array limit";
        Console.WriteLine($"  --bits {b,2}: ~{gb:F2} GB/run   {note}");
    }
    return 0;
}

BigInteger? KnownFor(int n)
{
    if (!crtKnown) return null;
    if (known.TryGetValue(n, out var v)) return v;
    Console.Error.WriteLine($"--crt-known: no known value for n={n}; using a normal (independently verified) campaign.");
    return null;
}

// Research: open-meander pair counts per class (should be A005316(k)) and list build time.
if (args[0] is "--meanders")
{
    int kmax = args.Length > 1 ? int.Parse(args[1]) : 19;
    var po = new ParallelOptions { MaxDegreeOfParallelism = cores ?? Environment.ProcessorCount };
    for (int k = 1; k <= kmax; k += 2)
    {
        var sw0 = Stopwatch.StartNew();
        var cls = new MeanderClass(k);
        long pairs = cls.AllPartners(po).Sum(l => (long)l.Length);
        Console.WriteLine($"k={k,2}  patterns={cls.Count,9:N0}  pairs={pairs,14:N0}  {sw0.Elapsed.TotalSeconds:F2}s");
    }
    return 0;
}

if (args[0] is "--crt-plan")
{
    if (args.Length < 2 || !int.TryParse(args[1], out int pn) || pn < 2)
    {
        Console.Error.WriteLine("usage: sapaths --crt-plan <n> [--bits 12|16|32|64] [--crt-runs M]");
        return 2;
    }
    BigInteger? kp = KnownFor(pn);
    int runs = crtRuns ?? (kp is BigInteger kpv ? DenseCounter.CrtKnownRuns(storeBits, kpv) : DenseCounter.CrtSplitRuns(pn, storeBits));
    int checks = kp is null ? DenseCounter.CrtCheckCount(storeBits) : 0;
    var list = DenseCounter.CrtModuli(storeBits, runs);
    Console.WriteLine(kp is null
        ? $"n = {pn}, --bits {storeBits}: {runs} runs ({runs - checks} combined + {checks} held-out verification)"
        : $"n = {pn}, --bits {storeBits}, --crt-known: {runs} runs (every residue checked against the known value)");
    for (int i = 0; i < runs; i++)
        Console.WriteLine($"  #{i + 1,3}  {(list[i] == 0 ? $"2^{storeBits}" : list[i].ToString()),22}{(i >= runs - checks ? "   (check)" : "")}");
    Console.WriteLine("With --crt-part K/N, part K runs list entries #i where (i-1) % N == K-1.");
    return 0;
}

if (args[0] is "--crt-merge")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: sapaths --crt-merge <sapcrt_*_part*.state> ...");
        return 2;
    }
    try
    {
        var (value, mn) = DenseCounter.MergeCrtParts(args.Skip(1), s => Console.Error.WriteLine($"  {s}"),
            crtKnown ? KnownFor : null);
        string check = known.TryGetValue(mn, out var exp) ? (exp == value ? "  [matches OEIS]" : "  [MISMATCH vs OEIS!]") : "";
        Console.WriteLine($"n = {mn}  ({mn}x{mn} grid, merged split CRT campaign)");
        Console.WriteLine($"paths = {value}{check}");
        Console.WriteLine($"digits = {value.ToString().Length}");
        return 0;
    }
    catch (Exception e) when (e is InvalidOperationException or IOException)
    {
        Console.Error.WriteLine($"merge failed: {e.Message}");
        return 1;
    }
}

if (args[0] is "--table")
{
    int max = args.Length > 1 ? int.Parse(args[1]) : 12;
    for (int n = 1; n <= max; n++)
    {
        if (dense) RunDense(n, known, crt, modArg, cores, storeBits, KnownFor(n), ladderOpt, mitm);
        else RunOne(n, known, maxRamGb, ooc, oocBudgetGb, oocDir, checkpointDir, checkpointEvery, stopAt, lookahead, compressSpill, cores);
    }
    return 0;
}

if (int.TryParse(args[0], out int nn) && nn >= 1)
{
    if (crtPart is (int pk, int pn))
    {
        BigInteger? kpart = KnownFor(nn);
        int runs = crtRuns ?? (kpart is BigInteger kpartv ? DenseCounter.CrtKnownRuns(storeBits, kpartv) : DenseCounter.CrtSplitRuns(nn, storeBits));
        string file = Path.GetFullPath($"sapcrt_{nn}_b{storeBits}_part{pk}of{pn}.state");
        var sw = Stopwatch.StartNew();
        DenseCounter.RunCrtPart(nn, storeBits, pk, pn, runs, file, cores,
            log: s => Console.Error.WriteLine($"\r  [n={nn}] {s}"),
            progress: (d, t) => { if (d == t || d % 50 == 0) Console.Error.Write($"\r  [n={nn}] update {d,5}/{t}  t={sw.Elapsed.TotalSeconds,8:F1}s   "); },
            known: kpart);
        Console.WriteLine($"part {pk}/{pn} of n={nn} ({storeBits}-bit, {runs} runs total) done in {sw.Elapsed}: {file}");
        Console.WriteLine("Merge all parts' state files on one machine with: sapaths --crt-merge <files...>");
        return 0;
    }
    if (dense) RunDense(nn, known, crt, modArg, cores, storeBits, crt ? KnownFor(nn) : null, ladderOpt, mitm);
    else RunOne(nn, known, maxRamGb, ooc, oocBudgetGb, oocDir, checkpointDir, checkpointEvery, stopAt, lookahead, compressSpill, cores);
    return 0;
}

Console.Error.WriteLine($"Unrecognized argument: {args[0]}");
return 2;

static double CurrentRamGb() => Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024 * 1024);

// Dense (GGCount-style) engine run: exact limbs, one modular run, or CRT.
static void RunDense(int n, Dictionary<int, BigInteger> known, bool crt, ulong? mod, int? cores, int storeBits,
    BigInteger? crtKnownValue = null, LadderOptions? ladder = null, bool mitm = false)
{
    var sw = Stopwatch.StartNew();
    var lastPrint = Stopwatch.StartNew();

    void Progress(int done, int total)
    {
        if (lastPrint.ElapsedMilliseconds >= 250 || done == total)
        {
            lastPrint.Restart();
            Console.Error.Write($"\r  [n={n}] update {done,5}/{total}  RAM={CurrentRamGb(),6:F2} GB  " +
                                $"t={sw.Elapsed.TotalSeconds,6:F1}s   ");
        }
    }

    if (n == 1)
    {
        Console.WriteLine($"n = 1  (1x1 grid)\npaths = 1  [matches OEIS]\n");
        return;
    }

    if (mitm && n % 2 != 0) Console.Error.WriteLine($"--mitm: n={n} is odd (needs a staircase cut, not implemented); plain sweep.");
    string mode;
    BigInteger? exact = null;
    ulong residue = 0;
    long states, deferred;
    if (crt)
    {
        mode = crtKnownValue is null ? $"dense+CRT ({storeBits}-bit)" : $"dense+CRT ({storeBits}-bit, checked vs known)";
        var dc = new DenseCounter(n) { Mitm = mitm };
        (states, deferred) = (dc.StateCount, dc.DeferredCount);
        // Completed runs persist here and are resumed automatically after a
        // crash/stop; the file is deleted when the campaign succeeds.
        string stateFile = Path.GetFullPath($"sapcrt_{n}_b{storeBits}.state");
        exact = DenseCounter.CountWithCrt(n, cores,
            log: s => Console.Error.WriteLine($"\r  [n={n}] {s}"), progress: Progress,
            storeBits: storeBits, stateFile: stateFile, known: crtKnownValue, ladder: ladder, counter: dc);
    }
    else if (mod is ulong m)
    {
        mode = m == 0 ? $"dense mod 2^{storeBits}" : $"dense mod {m} ({storeBits}-bit)";
        if (mitm && n % 2 == 0) mode += ", mitm";
        var dc = new DenseCounter(n) { Mitm = mitm };
        (states, deferred) = (dc.StateCount, dc.DeferredCount);
        residue = storeBits switch
        {
            12 => dc.CountMod12((uint)m, cores, Progress),
            16 => dc.CountMod16((uint)m, cores, Progress),
            32 => dc.CountMod32((uint)m, cores, Progress),
            _ => dc.CountMod(m, cores, Progress),
        };
    }
    else
    {
        mode = "dense exact";
        var dc = new DenseCounter(n);
        (states, deferred) = (dc.StateCount, dc.DeferredCount);
        exact = dc.CountExact(cores, Progress);
    }
    sw.Stop();
    Console.Error.WriteLine();

    double peakGb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024 * 1024);
    Console.WriteLine($"n = {n}  ({n}x{n} grid, {mode})");
    if (exact is BigInteger result)
    {
        string check = known.TryGetValue(n, out var exp)
            ? (exp == result ? "  [matches OEIS]" : "  [MISMATCH vs OEIS!]") : "";
        Console.WriteLine($"paths = {result}{check}");
        Console.WriteLine($"digits = {result.ToString().Length}   dense states = {states:N0} (+{deferred:N0} deferred)   " +
                          $"time = {sw.Elapsed.TotalSeconds:F2}s");
    }
    else
    {
        BigInteger baseMod = BigInteger.One << storeBits;   // free-wraparound modulus
        string check = known.TryGetValue(n, out var exp) && mod is ulong mm
            ? ((mm == 0 ? exp % baseMod : exp % mm) == residue ? "  [matches OEIS residue]" : "  [MISMATCH vs OEIS residue!]")
            : "";
        Console.WriteLine($"residue = {residue}{check}");
        Console.WriteLine($"dense states = {states:N0} (+{deferred:N0} deferred)   time = {sw.Elapsed.TotalSeconds:F2}s");
    }
    Console.WriteLine($"peak working set = {peakGb:F2} GB\n");
}

// Delete out-of-core spill directories left by earlier crashed/killed runs.
// Only dirs untouched for > 1h are removed, so a live run's dir is never taken.
static void SweepStaleSpillDirs(params string?[] roots)
{
    foreach (var root in roots.Where(r => r is not null).Distinct())
    {
        try
        {
            foreach (var d in Directory.GetDirectories(root!, "sap_ooc_*"))
            {
                try
                {
                    if (DateTime.Now - Directory.GetLastWriteTime(d) > TimeSpan.FromHours(1))
                        Directory.Delete(d, recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }
}

static void RunOne(int n, Dictionary<int, BigInteger> known, double maxRamGb,
    bool ooc = false, double oocBudgetGb = 2.0, string? oocDir = null,
    string? checkpointDir = null, double checkpointEvery = 300, int stopAt = -1, int lookahead = -1,
    bool compressSpill = false, int? cores = null)
{
    // In out-of-core mode spilling keeps RAM bounded, so the working-set abort
    // guard is disabled (the budget drives the spill threshold instead).
    if (ooc) maxRamGb = double.PositiveInfinity;
    long oocBudgetBytes = (long)(oocBudgetGb * 1024 * 1024 * 1024);
    var sw = Stopwatch.StartNew();
    var fc = new FrontierCounter(n);
    int total = fc.EdgeCount;
    var lastPrint = Stopwatch.StartNew();
    long maxStates = 0;
    double wsAtPeakGb = 0, heapAtPeakGb = 0;

    // Per-layer callback: live progress (throttled) + memory guard + profiling.
    void OnLayer(int layer, long states, bool spilled)
    {
        double ram = CurrentRamGb();

        // Capture memory at the widest layer (no forced GC -> no observer effect).
        if (states > maxStates)
        {
            maxStates = states;
            wsAtPeakGb = ram;
            heapAtPeakGb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024 * 1024);
        }

        if (ram > maxRamGb)
            throw new MemoryLimitException(layer, total, states, ram);

        if (lastPrint.ElapsedMilliseconds >= 250 || layer == total)
        {
            lastPrint.Restart();
            Console.Error.Write($"\r  [n={n}] layer {layer,4}/{total}  " +
                                $"states={states,14:N0}  RAM={ram,6:F2} GB  " +
                                $"{(spilled ? "[disk]" : "[ram] ")}  " +
                                $"t={sw.Elapsed.TotalSeconds,6:F1}s   ");
        }
    }

    long allocBefore = GC.GetTotalAllocatedBytes();
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

    void Log(string msg) => Console.Error.WriteLine($"\r  [n={n}] {msg}");

    BigInteger result;
    try
    {
        result = lookahead >= 0
            ? fc.CountLookahead(lookahead, OnLayer, cores, ooc, oocBudgetBytes, oocDir, compressSpill)
            : fc.Count(OnLayer, cores, ooc, oocBudgetBytes, oocDir, compressSpill,
                checkpointDir, checkpointEvery, stopAt, Log);
    }
    catch (MemoryLimitException ex)
    {
        Console.Error.WriteLine();
        Console.WriteLine($"n = {n}  ({n}x{n} grid) — ABORTED");
        Console.WriteLine($"hit RAM cap {maxRamGb:F2} GB at layer {ex.Layer}/{ex.Total} " +
                          $"({ex.States:N0} live states). Try a larger --maxram or smaller n.\n");
        return;
    }
    catch (StopRequested ex)
    {
        Console.Error.WriteLine();
        Console.WriteLine($"n = {n}  ({n}x{n} grid) — STOPPED after layer {ex.Layer} " +
                          $"(checkpoint saved). Re-run the same command to resume.\n");
        return;
    }
    sw.Stop();
    Console.Error.WriteLine(); // finish the progress line

    double allocGb = (GC.GetTotalAllocatedBytes() - allocBefore) / (1024.0 * 1024 * 1024);
    double peakGb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024 * 1024);
    (int dg0, int dg1, int dg2) = (GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2);
    double bytesPerState = maxStates > 0 ? heapAtPeakGb * 1024 * 1024 * 1024 / maxStates : 0;

    string check = known.TryGetValue(n, out var exp)
        ? (exp == result ? "  [matches OEIS]" : "  [MISMATCH vs OEIS!]")
        : "";
    Console.WriteLine($"n = {n}  ({n}x{n} grid, {n * n} vertices)");
    Console.WriteLine($"paths = {result}{check}");
    Console.WriteLine($"digits = {result.ToString().Length}   " +
                      $"peak live states = {fc.PeakStates:N0}   " +
                      $"time = {sw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"@peak: working set = {wsAtPeakGb:F2} GB   managed heap = {heapAtPeakGb:F2} GB   " +
                      $"bytes/state = {bytesPerState:F0}   (peak WS overall = {peakGb:F2} GB)");
    Console.WriteLine($"allocated total = {allocGb:F1} GB   GC gen0/1/2 = {dg0}/{dg1}/{dg2}");
    Console.WriteLine();
}

sealed class MemoryLimitException(int layer, int total, long states, double ramGb) : Exception
{
    public int Layer { get; } = layer;
    public int Total { get; } = total;
    public long States { get; } = states;
    public double RamGb { get; } = ramGb;
}
