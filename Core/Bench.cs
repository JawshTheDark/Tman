using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tman.Core;

/// <summary>
/// Self-contained CPU, memory and disk benchmarks. Everything here runs off the UI thread and is time-boxed,
/// so a run takes about the same time on any machine.
/// </summary>
internal static class Bench
{
    // ---------------- CPU ----------------

    /// <summary>One CPU workload. <see cref="Reference"/> is the single-thread rate that scores 1,000 points (one Ryzen 9 7950X core).</summary>
    public abstract class Kernel
    {
        public abstract string Name { get; }
        /// <summary>Display unit for the rate, e.g. "M values/s".</summary>
        public abstract string Unit { get; }
        public abstract double UnitScale { get; }
        public abstract double Reference { get; }
        public abstract object CreateState(int seed);
        /// <summary>Runs one chunk of work and returns how many operations it did.</summary>
        public abstract long Run(object state);
    }

    public sealed record KernelResult(string Name, string Unit, double SingleRate, double MultiRate, double UnitScale);

    public sealed record CpuResult(double SingleScore, double MultiScore, int Threads, List<KernelResult> Kernels);

    public static readonly Kernel[] Kernels = { new IntegerKernel(), new FloatKernel(), new SortKernel(), new CompressKernel() };

    sealed class IntegerKernel : Kernel
    {
        public override string Name => "Integer math";
        public override string Unit => "M values/s";
        public override double UnitScale => 1e6;
        public override double Reference => 1.98e9;

        sealed class State { public ulong[] Data = Array.Empty<ulong>(); public ulong Sink; }

        public override object CreateState(int seed)
        {
            var rng = new Random(seed);
            var data = new ulong[8192]; // 64 KB: stays in L2 so this measures the core, not memory
            for (int i = 0; i < data.Length; i++) data[i] = (ulong)rng.NextInt64();
            return new State { Data = data };
        }

        public override long Run(object state)
        {
            var s = (State)state;
            s.Sink += Hash(s.Data, s.Sink);
            return s.Data.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static ulong Hash(ulong[] data, ulong seed)
        {
            // Four independent lanes so the score reflects throughput, not just multiply latency.
            ulong a = seed, b = seed ^ 0x9E37, c = seed ^ 0x79B9, d = seed ^ 0x7F4A;
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                a = BitOperations.RotateLeft((a ^ data[i]) * 0x9E3779B97F4A7C15UL, 31) + (ulong)i;
                b = BitOperations.RotateLeft((b ^ data[i + 1]) * 0xBF58476D1CE4E5B9UL, 27) ^ (b >> 7);
                c = BitOperations.RotateLeft((c ^ data[i + 2]) * 0x94D049BB133111EBUL, 33) + (c << 3);
                d = BitOperations.RotateLeft((d ^ data[i + 3]) * 0xD6E8FEB86659FD93UL, 29) - (d >> 11);
            }
            return a ^ b ^ c ^ d;
        }
    }

    sealed class FloatKernel : Kernel
    {
        public override string Name => "Floating point";
        public override string Unit => "M iterations/s";
        public override double UnitScale => 1e6;
        public override double Reference => 5.26e8;

        sealed class State { public double OffsetX; public long Sink; }

        public override object CreateState(int seed) => new State { OffsetX = (seed % 7) * 1e-9 };

        public override long Run(object state)
        {
            var s = (State)state;
            long n = Mandelbrot(s.OffsetX);
            s.Sink += n;
            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static long Mandelbrot(double offset)
        {
            const int Size = 96, MaxIter = 256;
            long total = 0;
            for (int y = 0; y < Size; y++)
            {
                double ci = -1.25 + 2.5 * y / Size;
                for (int x = 0; x < Size; x++)
                {
                    double cr = -2.1 + offset + 2.8 * x / Size;
                    double zr = 0, zi = 0;
                    int n = 0;
                    while (n < MaxIter && zr * zr + zi * zi <= 4)
                    {
                        double t = zr * zr - zi * zi + cr;
                        zi = 2 * zr * zi + ci;
                        zr = t;
                        n++;
                    }
                    total += n;
                }
            }
            return total;
        }
    }

    sealed class SortKernel : Kernel
    {
        public override string Name => "Sorting";
        public override string Unit => "M items/s";
        public override double UnitScale => 1e6;
        public override double Reference => 2.43e7;

        sealed class State { public int[] Source = Array.Empty<int>(); public int[] Work = Array.Empty<int>(); }

        public override object CreateState(int seed)
        {
            var rng = new Random(seed);
            var src = new int[65536];
            for (int i = 0; i < src.Length; i++) src[i] = rng.Next();
            return new State { Source = src, Work = new int[src.Length] };
        }

        public override long Run(object state)
        {
            var s = (State)state;
            s.Source.CopyTo(s.Work, 0);
            Array.Sort(s.Work);
            return s.Work.Length;
        }
    }

    sealed class CompressKernel : Kernel
    {
        public override string Name => "Compression";
        public override string Unit => "MB/s";
        public override double UnitScale => 1 << 20;
        public override double Reference => 1.09e8;

        sealed class State { public byte[] Input = Array.Empty<byte>(); public byte[] Output = Array.Empty<byte>(); }

        static readonly string[] Words =
        {
            "process", "memory", "thread", "handle", "window", "service", "driver", "network", "disk", "power",
            "the", "a", "of", "and", "to", "in", "is", "for", "on", "with", "time", "data", "value", "system",
            "user", "file", "error", "cache", "queue", "frame", "update", "sample", "record", "total", "speed",
        };

        public override object CreateState(int seed)
        {
            // Text-like input (Brotli gets roughly 4:1 on it), generated so every thread has its own copy.
            var rng = new Random(seed);
            var sb = new StringBuilder(1 << 18);
            while (sb.Length < (1 << 18))
            {
                sb.Append(Words[rng.Next(Words.Length)]);
                sb.Append(rng.Next(12) == 0 ? ".\n" : " ");
                if (rng.Next(20) == 0) sb.Append(rng.Next(100000));
            }
            var input = Encoding.ASCII.GetBytes(sb.ToString(0, 1 << 18));
            return new State { Input = input, Output = new byte[BrotliEncoder.GetMaxCompressedLength(input.Length)] };
        }

        public override long Run(object state)
        {
            var s = (State)state;
            BrotliEncoder.TryCompress(s.Input, s.Output, out _, quality: 4, window: 22);
            return s.Input.Length;
        }
    }

    /// <summary>Runs every kernel single-threaded, then on all logical processors at once.</summary>
    public static CpuResult RunCpu(TimeSpan perKernel, IProgress<string> progress, CancellationToken ct)
    {
        int threads = Environment.ProcessorCount;
        var single = new double[Kernels.Length];
        var multi = new double[Kernels.Length];
        for (int k = 0; k < Kernels.Length; k++)
        {
            progress.Report($"CPU · {Kernels[k].Name} (1 thread)");
            single[k] = Measure(Kernels[k], 1, perKernel, ct);
        }
        for (int k = 0; k < Kernels.Length; k++)
        {
            progress.Report($"CPU · {Kernels[k].Name} ({threads} threads)");
            multi[k] = Measure(Kernels[k], threads, perKernel, ct);
        }
        double Score(double[] rates) => 1000 * Math.Exp(rates.Select((r, i) => Math.Log(Math.Max(r, 1) / Kernels[i].Reference)).Average());
        var kernels = Kernels.Select((k, i) => new KernelResult(k.Name, k.Unit, single[i], multi[i], k.UnitScale)).ToList();
        return new CpuResult(Score(single), Score(multi), threads, kernels);
    }

    /// <summary>Operations per second for one kernel on <paramref name="threads"/> threads, all starting together.</summary>
    static double Measure(Kernel kernel, int threads, TimeSpan duration, CancellationToken ct)
    {
        var states = Enumerable.Range(0, threads).Select(i => kernel.CreateState(1234 + i)).ToArray();
        foreach (var s in states) kernel.Run(s); // warm up caches and the JIT
        var ops = new long[threads];
        using var start = new Barrier(threads + 1);
        var sw = new Stopwatch();
        var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            long n = 0;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested) n += kernel.Run(states[i]);
            ops[i] = n;
        }) { IsBackground = true, Name = "Tman benchmark" }).ToList();
        foreach (var t in workers) t.Start();
        sw.Start();
        start.SignalAndWait();
        foreach (var t in workers) t.Join();
        ct.ThrowIfCancellationRequested();
        return ops.Sum() / sw.Elapsed.TotalSeconds;
    }

    // ---------------- Memory ----------------

    public sealed record MemoryResult(double ReadBps, double WriteBps, double CopyBps, double LatencyNs, long TestBytes);

    public static MemoryResult RunMemory(TimeSpan perTest, IProgress<string> progress, CancellationToken ct)
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = 64 };
        Native.GlobalMemoryStatusEx(ref status);
        // Big enough to blow past any CPU cache, small enough to leave the rest of the PC alone.
        long bytes = Math.Clamp((long)(status.ullAvailPhys / 4) & ~((64L << 20) - 1), 256L << 20, 1L << 30);
        int threads = Environment.ProcessorCount;
        var data = GC.AllocateUninitializedArray<long>((int)(bytes / 8));
        try
        {
            progress.Report("Memory · preparing");
            Parallel.For(0, threads, i => Slice(data, i, threads).Fill(i));

            progress.Report("Memory · read bandwidth");
            double read = Bandwidth(data, threads, perTest, ct, s => SumVector(s));
            progress.Report("Memory · write bandwidth");
            double write = Bandwidth(data, threads, perTest, ct, s => { s.Fill(7); return 0; });
            progress.Report("Memory · copy bandwidth");
            double copy = Bandwidth(data, threads, perTest, ct, s =>
            {
                int half = s.Length / 2;
                s[..half].CopyTo(s[half..]);
                return 0;
            }) / 2; // each pass moves half the slice
            progress.Report("Memory · latency");
            double latency = Latency(perTest, ct);
            return new MemoryResult(read, write, copy, latency, bytes);
        }
        finally
        {
            data = null!;
            GC.Collect();
        }
    }

    static Span<long> Slice(long[] data, int i, int count)
    {
        int per = data.Length / count / 8 * 8;
        return data.AsSpan(i * per, per);
    }

    /// <summary>Every thread sweeps its own slice over and over; returns bytes per second across all of them.</summary>
    static double Bandwidth(long[] data, int threads, TimeSpan duration, CancellationToken ct, SpanFunc pass)
    {
        long sliceBytes = (long)(data.Length / threads / 8 * 8) * 8;
        var passes = new long[threads];
        var sink = new long[threads];
        using var start = new Barrier(threads + 1);
        var sw = new Stopwatch();
        var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            long n = 0, acc = 0;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                acc += pass(Slice(data, i, threads));
                n++;
            }
            passes[i] = n;
            sink[i] = acc;
        }) { IsBackground = true, Name = "Tman benchmark" }).ToList();
        foreach (var t in workers) t.Start();
        sw.Start();
        start.SignalAndWait();
        foreach (var t in workers) t.Join();
        ct.ThrowIfCancellationRequested();
        return passes.Sum() * (double)sliceBytes / sw.Elapsed.TotalSeconds;
    }

    delegate long SpanFunc(Span<long> s);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static long SumVector(Span<long> s)
    {
        ref long r = ref MemoryMarshal.GetReference(s);
        Vector256<long> a0 = default, a1 = default, a2 = default, a3 = default;
        nuint i = 0, n = (nuint)s.Length;
        for (; i + 16 <= n; i += 16)
        {
            a0 += Vector256.LoadUnsafe(ref r, i);
            a1 += Vector256.LoadUnsafe(ref r, i + 4);
            a2 += Vector256.LoadUnsafe(ref r, i + 8);
            a3 += Vector256.LoadUnsafe(ref r, i + 12);
        }
        long sum = Vector256.Sum(a0 + a1 + a2 + a3);
        for (; i < n; i++) sum += Unsafe.Add(ref r, i);
        return sum;
    }

    /// <summary>Random pointer chase through 128 MB: each load depends on the previous one, so time per step is the latency.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static double Latency(TimeSpan duration, CancellationToken ct)
    {
        const int N = 32 << 20; // 32M ints = 128 MB
        var next = GC.AllocateUninitializedArray<int>(N);
        for (int i = 0; i < N; i++) next[i] = i;
        var rng = new Random(42);
        for (int i = N - 1; i > 0; i--) // Sattolo: one big cycle, so the chase never short-circuits
        {
            int j = rng.Next(i);
            (next[i], next[j]) = (next[j], next[i]);
        }
        int p = 0;
        long steps = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration && !ct.IsCancellationRequested)
        {
            for (int k = 0; k < 1 << 20; k++) p = next[p];
            steps += 1 << 20;
        }
        ct.ThrowIfCancellationRequested();
        GC.KeepAlive(p);
        return sw.Elapsed.TotalNanoseconds / steps;
    }

    // ---------------- Disk ----------------

    public sealed record DiskResult(string Folder, long FileBytes,
        double SeqRead, double SeqWrite, double Rnd32Read, double Rnd32Write, double Rnd1Read, double Rnd1Write);

    const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING: go to the drive, not the cache
    const int Seq = 1 << 20, Rnd = 4096;

    public static async Task<DiskResult> RunDisk(string folder, TimeSpan perTest, IProgress<string> progress, CancellationToken ct)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder))!;
        long free = new DriveInfo(root).AvailableFreeSpace;
        long size = free > (3L << 30) ? 1L << 30 : free > (1L << 30) ? 256L << 20 : throw new IOException("Not enough free space on that drive (needs at least 1 GB).");
        var path = Path.Combine(folder, $"tman-bench-{Guid.NewGuid():N}.tmp");
        var share = FileShare.ReadWrite | FileShare.Delete;
        using var async = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, share,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose | NoBuffering, size);
        progress.Report("Disk · creating a test file");
        await Pass(async, size, Seq, 8, random: false, write: true, TimeSpan.MaxValue, ct, onePass: true);

        progress.Report("Disk · sequential read");
        double seqRead = await Pass(async, size, Seq, 8, random: false, write: false, perTest, ct);
        // Write tests also stop after a fixed amount so a fast SSD isn't handed tens of GB per run.
        progress.Report("Disk · sequential write");
        double seqWrite = await Pass(async, size, Seq, 8, random: false, write: true, perTest, ct, maxBytes: 2 * size);
        progress.Report("Disk · random 4K read (queue 32)");
        double r32 = await Pass(async, size, Rnd, 32, random: true, write: false, perTest, ct);
        progress.Report("Disk · random 4K write (queue 32)");
        double w32 = await Pass(async, size, Rnd, 32, random: true, write: true, perTest, ct, maxBytes: size);

        using var sync = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, share, NoBuffering);
        progress.Report("Disk · random 4K read (queue 1)");
        double r1 = await Task.Run(() => SyncPass(sync, size, write: false, perTest, ct, long.MaxValue), ct);
        progress.Report("Disk · random 4K write (queue 1)");
        double w1 = await Task.Run(() => SyncPass(sync, size, write: true, perTest, ct, size / 2), ct);
        return new DiskResult(folder, size, seqRead, seqWrite, r32, w32, r1, w1);
    }

    /// <summary><paramref name="depth"/> overlapped requests kept in flight; returns bytes per second.</summary>
    static async Task<double> Pass(SafeFileHandle h, long size, int block, int depth, bool random, bool write,
        TimeSpan duration, CancellationToken ct, bool onePass = false, long maxBytes = long.MaxValue)
    {
        long blocks = size / block, next = 0, done = 0;
        var buffers = Enumerable.Range(0, depth).Select(_ => new AlignedBuffer(block)).ToArray();
        var sw = Stopwatch.StartNew();
        try
        {
            async Task Worker(int w)
            {
                var rng = new Random(w * 7919 + 17);
                var mem = buffers[w].Memory;
                while (!ct.IsCancellationRequested && (onePass || sw.Elapsed < duration) && Interlocked.Read(ref done) * block < maxBytes)
                {
                    long b;
                    if (random) b = rng.NextInt64(blocks);
                    else
                    {
                        b = Interlocked.Increment(ref next) - 1;
                        if (onePass && b >= blocks) break;
                        b %= blocks;
                    }
                    if (write) await RandomAccess.WriteAsync(h, mem, b * block, ct);
                    else await RandomAccess.ReadAsync(h, mem, b * block, ct);
                    Interlocked.Increment(ref done);
                }
            }
            await Task.WhenAll(Enumerable.Range(0, depth).Select(Worker));
            ct.ThrowIfCancellationRequested();
            return done * (double)block / sw.Elapsed.TotalSeconds;
        }
        finally
        {
            foreach (var b in buffers) ((IDisposable)b).Dispose();
        }
    }

    /// <summary>One request at a time, random 4K: what the drive does for a single waiting app.</summary>
    static double SyncPass(SafeFileHandle h, long size, bool write, TimeSpan duration, CancellationToken ct, long maxBytes)
    {
        long blocks = size / Rnd, done = 0;
        using var buffer = new AlignedBuffer(Rnd);
        var span = buffer.GetSpan();
        var rng = new Random(99);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration && !ct.IsCancellationRequested && done * Rnd < maxBytes)
        {
            long off = rng.NextInt64(blocks) * Rnd;
            if (write) RandomAccess.Write(h, span, off);
            else RandomAccess.Read(h, span, off);
            done++;
        }
        ct.ThrowIfCancellationRequested();
        return done * (double)Rnd / sw.Elapsed.TotalSeconds;
    }

    /// <summary>Page-aligned native buffer, as unbuffered I/O requires; filled with random (incompressible) bytes.</summary>
    sealed unsafe class AlignedBuffer : MemoryManager<byte>
    {
        byte* _ptr;
        readonly int _length;

        public AlignedBuffer(int length)
        {
            _length = length;
            _ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096);
            Random.Shared.NextBytes(new Span<byte>(_ptr, length));
        }

        public override Span<byte> GetSpan() => new(_ptr, _length);
        public override MemoryHandle Pin(int elementIndex = 0) => new(_ptr + elementIndex);
        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            if (_ptr == null) return;
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
    }
}
