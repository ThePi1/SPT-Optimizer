using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace SPTOptimizer.Profiler
{
    /// <summary>Everything the writer needs to finish a capture, snapshotted on the main thread at stop.</summary>
    internal sealed class CaptureInfo
    {
        public string Directory;
        public string Header;
        public string[] SlotNames;
        public string[] CounterNames;
        public string[] ExperimentNames;
        public SlowWork.Item[] SlowItems;
        public string[] MethodNames;
        public long[] MethodTotSelf, MethodTotIncl, MethodTotCalls, MethodMaxSelf, MethodMaxIncl;
        public int[] MethodFramesSeen;
        public long FramesDropped;
        public bool KeepBinary;
    }

    /// <summary>
    /// Background writer. During a capture the main thread hands over filled chunks of float rows,
    /// which are appended raw to a .bin file (no allocations on either side). When the capture stops,
    /// the writer converts the .bin into frames.csv, methods.csv and summary.txt.
    /// </summary>
    internal static class CaptureWriter
    {
        public const int ChunkRows = 128;
        private const int PoolSize = 32;

        private struct Job
        {
            public int Kind; // 0 = open, 1 = chunk, 2 = finish
            public float[] Chunk;
            public int Rows;
            public CaptureInfo Info;
        }

        private static readonly object Lock = new object();
        private static readonly Queue<Job> Jobs = new Queue<Job>(PoolSize * 2);
        private static readonly Stack<float[]> Pool = new Stack<float[]>(PoolSize);
        private static Thread _thread;
        private static ManualLogSource _log;

        private static FileStream _bin;
        private static string _binPath;
        private static readonly byte[] ByteBuf = new byte[ChunkRows * FrameRecorder.Stride * 4];

        public static void Start(ManualLogSource log)
        {
            _log = log;
            for (int i = 0; i < PoolSize; i++) Pool.Push(new float[ChunkRows * FrameRecorder.Stride]);
            _thread = new Thread(Run) { IsBackground = true, Name = "SPTOptimizer.CaptureWriter", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public static float[] RentChunk()
        {
            lock (Lock) return Pool.Count > 0 ? Pool.Pop() : null;
        }

        public static void SubmitChunk(float[] chunk, int rows) => Enqueue(new Job { Kind = 1, Chunk = chunk, Rows = rows });

        public static void Open(string directory) => Enqueue(new Job { Kind = 0, Info = new CaptureInfo { Directory = directory } });

        public static void Finish(CaptureInfo info) => Enqueue(new Job { Kind = 2, Info = info });

        private static void Enqueue(Job job)
        {
            lock (Lock)
            {
                Jobs.Enqueue(job);
                Monitor.Pulse(Lock);
            }
        }

        private static void Run()
        {
            while (true)
            {
                Job job;
                lock (Lock)
                {
                    while (Jobs.Count == 0) Monitor.Wait(Lock);
                    job = Jobs.Dequeue();
                }
                try
                {
                    switch (job.Kind)
                    {
                        case 0:
                            System.IO.Directory.CreateDirectory(job.Info.Directory);
                            _binPath = Path.Combine(job.Info.Directory, "frames.bin");
                            _bin = new FileStream(_binPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                            break;
                        case 1:
                            if (_bin != null)
                            {
                                int bytes = job.Rows * FrameRecorder.Stride * 4;
                                Buffer.BlockCopy(job.Chunk, 0, ByteBuf, 0, bytes);
                                _bin.Write(ByteBuf, 0, bytes);
                            }
                            break;
                        case 2:
                            _bin?.Dispose();
                            _bin = null;
                            Report.Write(_binPath, job.Info, _log);
                            if (!job.Info.KeepBinary) File.Delete(_binPath);
                            _log.LogMessage("Capture written to " + job.Info.Directory);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError("Capture writer: " + ex);
                }
                finally
                {
                    if (job.Kind == 1)
                    {
                        lock (Lock) Pool.Push(job.Chunk);
                    }
                }
            }
        }
    }

    /// <summary>Turns a raw frames.bin into frames.csv, methods.csv and summary.txt.</summary>
    internal static class Report
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Write(string binPath, CaptureInfo info, ManualLogSource log)
        {
            int stride = FrameRecorder.Stride;
            long bytes = new FileInfo(binPath).Length;
            int frames = (int)(bytes / (stride * 4));

            // Load everything; a 10-minute capture at 150 fps is ~90k rows * 1 KB = ~100 MB, fine for a one-off.
            var data = new float[(long)frames * stride];
            using (var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read))
            {
                var buf = new byte[stride * 4 * 1024];
                long offset = 0;
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    Buffer.BlockCopy(buf, 0, data, (int)offset, read);
                    offset += read;
                }
            }

            // Slot columns that were ever non-zero
            int slotCount = info.SlotNames.Length;
            var usedSlots = new List<int>();
            for (int s = 0; s < slotCount; s++)
            {
                for (int f = 0; f < frames; f++)
                {
                    if (data[(long)f * stride + Col.Count + s] != 0f) { usedSlots.Add(s); break; }
                }
            }

            // Unity counter columns that were ever non-zero
            var usedCounters = new List<int>();
            var counterNames = info.CounterNames ?? new string[0];
            for (int k = 0; k < counterNames.Length; k++)
            {
                for (int f = 0; f < frames; f++)
                {
                    float v = data[(long)f * stride + FrameRecorder.CounterBase + k];
                    if (v != 0f && !float.IsNaN(v)) { usedCounters.Add(k); break; }
                }
            }

            WriteCsv(Path.Combine(info.Directory, "frames.csv"), data, frames, stride, usedSlots, usedCounters, info);
            if (info.MethodNames.Length > 0) WriteMethods(Path.Combine(info.Directory, "methods.csv"), info, frames);
            WriteSummary(Path.Combine(info.Directory, "summary.txt"), data, frames, stride, usedSlots, usedCounters, info);
            WriteSlowWork(info);
        }

        private static void WriteCsv(string path, float[] data, int frames, int stride, List<int> usedSlots, List<int> usedCounters, CaptureInfo info)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20))
            {
                var sb = new StringBuilder();
                sb.Append(string.Join(",", Col.Names));
                foreach (int s in usedSlots) sb.Append(',').Append(info.SlotNames[s]);
                foreach (int k in usedCounters) sb.Append(',').Append(info.CounterNames[k]);
                w.WriteLine(sb.ToString());

                for (int f = 0; f < frames; f++)
                {
                    sb.Clear();
                    long o = (long)f * stride;
                    for (int c = 0; c < Col.Count; c++)
                    {
                        if (c > 0) sb.Append(',');
                        float v = data[o + c];
                        if (c == Col.Experiment)
                        {
                            int id = (int)v;
                            var en = info.ExperimentNames ?? new string[0];
                            sb.Append(id >= 0 && id < en.Length ? en[id] : id.ToString(Inv));
                        }
                        else if (c == Col.Top1Id || c == Col.Top2Id || c == Col.Top3Id)
                        {
                            int id = (int)v;
                            if (id >= 0 && id < info.MethodNames.Length && data[o + c + 1] > 0f) sb.Append(info.MethodNames[id]);
                        }
                        else
                        {
                            AppendNum(sb, v);
                        }
                    }
                    foreach (int s in usedSlots)
                    {
                        sb.Append(',');
                        AppendNum(sb, data[o + Col.Count + s]);
                    }
                    foreach (int k in usedCounters)
                    {
                        sb.Append(',');
                        AppendNum(sb, data[o + FrameRecorder.CounterBase + k]);
                    }
                    w.WriteLine(sb.ToString());
                }
            }
        }

        private static void AppendNum(StringBuilder sb, float v)
        {
            if (float.IsNaN(v)) return;
            if (v == 0f) sb.Append('0');
            else sb.Append(v.ToString("0.####", Inv));
        }

        private static void WriteMethods(string path, CaptureInfo info, int frames)
        {
            double t = FrameRecorder.TicksToMs;
            var order = Enumerable.Range(0, info.MethodNames.Length).Where(i => info.MethodTotCalls[i] > 0).OrderByDescending(i => info.MethodTotSelf[i]);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("method,calls,calls_per_frame,self_ms_per_frame,incl_ms_per_frame,self_total_ms,incl_total_ms,max_self_ms_in_frame,max_incl_ms_in_frame,self_us_per_call,frames_present");
                foreach (int i in order)
                {
                    long calls = info.MethodTotCalls[i];
                    w.WriteLine(string.Join(",",
                        "\"" + info.MethodNames[i] + "\"",
                        calls.ToString(Inv),
                        F(calls / (double)Math.Max(frames, 1)),
                        F(info.MethodTotSelf[i] * t / Math.Max(frames, 1)),
                        F(info.MethodTotIncl[i] * t / Math.Max(frames, 1)),
                        F(info.MethodTotSelf[i] * t),
                        F(info.MethodTotIncl[i] * t),
                        F(info.MethodMaxSelf[i] * t),
                        F(info.MethodMaxIncl[i] * t),
                        F(info.MethodTotSelf[i] * t * 1000.0 / Math.Max(calls, 1)),
                        info.MethodFramesSeen[i].ToString(Inv)));
                }
            }
        }

        private static string F(double v) => v.ToString("0.####", Inv);

        private static void WriteSummary(string path, float[] data, int frames, int stride, List<int> usedSlots, List<int> usedCounters, CaptureInfo info)
        {
            var sb = new StringBuilder();
            sb.AppendLine(info.Header);
            sb.AppendLine($"Frames recorded: {frames}   dropped (writer backlog): {info.FramesDropped}");
            if (frames == 0)
            {
                File.WriteAllText(path, sb.ToString());
                return;
            }

            float[] Column(int c)
            {
                var a = new float[frames];
                for (int f = 0; f < frames; f++) a[f] = data[(long)f * stride + c];
                return a;
            }

            var ft = Column(Col.FrameMs);
            var sorted = (float[])ft.Clone();
            Array.Sort(sorted);
            double duration = ft.Sum(x => (double)x) / 1000.0;
            double avg = ft.Average(x => (double)x);
            int worst1 = Math.Max(1, frames / 100);
            double low1 = 1000.0 / sorted.Skip(frames - worst1).Average(x => (double)x);
            int worst01 = Math.Max(1, frames / 1000);
            double low01 = 1000.0 / sorted.Skip(frames - worst01).Average(x => (double)x);

            sb.AppendLine();
            sb.AppendLine("== Frame time ==");
            sb.AppendLine($"Duration {duration:0.0} s, avg {avg:0.00} ms ({1000.0 / avg:0.0} fps)");
            sb.AppendLine($"p50 {P(sorted, 50):0.00}  p90 {P(sorted, 90):0.00}  p95 {P(sorted, 95):0.00}  p99 {P(sorted, 99):0.00}  p99.9 {P(sorted, 99.9):0.00}  max {sorted[frames - 1]:0.00} ms");
            sb.AppendLine($"1% low {low1:0.0} fps, 0.1% low {low01:0.0} fps");
            sb.AppendLine($"Frames > 25 ms: {ft.Count(x => x > 25)}   > 33 ms: {ft.Count(x => x > 33.3f)}   > 50 ms: {ft.Count(x => x > 50)}   > 100 ms: {ft.Count(x => x > 100)}");

            sb.AppendLine();
            sb.AppendLine("== Memory / GC ==");
            var gcCount = Column(Col.GcCollections);
            var gcFrames = Enumerable.Range(0, frames).Where(f => gcCount[f] > 0).ToList();
            sb.AppendLine($"GC collections: {gcCount.Sum()}   frames with a collection: {gcFrames.Count}");
            if (gcFrames.Count > 0)
                sb.AppendLine($"Frame time on GC frames: avg {gcFrames.Average(f => (double)ft[f]):0.00} ms, max {gcFrames.Max(f => ft[f]):0.00} ms");
            var gcEnabled = Column(Col.GcEnabled);
            sb.AppendLine($"GC enabled on {gcEnabled.Count(x => x > 0)} / {frames} frames");
            var used = Column(Col.MonoUsedMB);
            sb.AppendLine($"Mono used: start {used[0]:0} MB, end {used[frames - 1]:0} MB, max {used.Max():0} MB; heap end {data[(long)(frames - 1) * stride + Col.MonoHeapMB]:0} MB");
            var alloc = Column(Col.GcAllocKB);
            if (!float.IsNaN(alloc[0]))
            {
                var allocSorted = (float[])alloc.Clone();
                Array.Sort(allocSorted);
                sb.AppendLine($"GC alloc per frame: avg {alloc.Average(x => (double)x):0.0} KB, p99 {P(allocSorted, 99):0.0} KB, max {allocSorted[frames - 1]:0.0} KB");
            }
            else
            {
                sb.AppendLine("GC alloc per frame: not available in this player");
            }

            sb.AppendLine();
            sb.AppendLine("== Other counters (avg / p99) ==");
            foreach (int c in new[] { Col.AlivePlayers, Col.DrawCalls, Col.SetPassCalls, Col.Batches, Col.TrianglesK, Col.FtCpuMainMs, Col.FtCpuRenderMs, Col.FtPresentWaitMs, Col.FtGpuMs, Col.InstrCalls, Col.CameraRenders, Col.BotsAlive, Col.BotsVisible, Col.BotsIk, Col.BotsNear, Col.BotsAnimAlways })
            {
                var col = Column(c);
                if (float.IsNaN(col[0]) || col.All(x => x == 0f)) { sb.AppendLine($"{Col.Names[c],-22} n/a"); continue; }
                var s = (float[])col.Clone();
                Array.Sort(s);
                sb.AppendLine($"{Col.Names[c],-22} {col.Average(x => (double)x),10:0.00} {P(s, 99),10:0.00}");
            }

            sb.AppendLine();
            sb.AppendLine("== PlayerLoop breakdown (ms per frame; FixedUpdate entries sum all steps in the frame) ==");
            sb.AppendLine("   Cam.<camera>.Cull/.Render are carved out of the slot the camera rendered in (usually PostLateUpdate.FinishFrameRendering).");
            sb.AppendLine($"{"slot",-62} {"avg",8} {"p95",8} {"p99",8} {"max",8} {"%frame",7}");
            var slotStats = new List<(int slot, double avg, float p95, float p99, float max)>();
            foreach (int s in usedSlots)
            {
                var col = Column(Col.Count + s);
                var so = (float[])col.Clone();
                Array.Sort(so);
                slotStats.Add((s, col.Average(x => (double)x), P(so, 95), P(so, 99), so[frames - 1]));
            }
            foreach (var st in slotStats.OrderByDescending(x => x.avg).Take(45))
            {
                sb.AppendLine($"{Trunc(info.SlotNames[st.slot], 62),-62} {st.avg,8:0.000} {st.p95,8:0.000} {st.p99,8:0.000} {st.max,8:0.00} {100.0 * st.avg / avg,6:0.0}%");
            }

            // Per-phase totals
            sb.AppendLine();
            sb.AppendLine("== Phase totals (avg ms) ==");
            foreach (var g in slotStats.GroupBy(x => info.SlotNames[x.slot].Split('.')[0]).OrderByDescending(g => g.Sum(x => x.avg)))
            {
                sb.AppendLine($"{g.Key,-30} {g.Sum(x => x.avg),8:0.000}");
            }

            // Per camera
            var camStats = slotStats.Where(x => info.SlotNames[x.slot].StartsWith("Cam.")).ToList();
            if (camStats.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== Cameras (avg ms per frame; camera_renders avg " + Column(Col.CameraRenders).Average(x => (double)x).ToString("0.0", Inv) + " renders/frame) ==");
                sb.AppendLine($"{"camera",-50} {"cull",8} {"render",8} {"total",8} {"p99 tot",8}");
                foreach (var g in camStats.GroupBy(x => { var n = info.SlotNames[x.slot]; return n.Substring(4, n.LastIndexOf('.') - 4); })
                    .OrderByDescending(g => g.Sum(x => x.avg)))
                {
                    double cull = g.Where(x => info.SlotNames[x.slot].EndsWith(".Cull")).Sum(x => x.avg);
                    double rend = g.Where(x => info.SlotNames[x.slot].EndsWith(".Render")).Sum(x => x.avg);
                    var tot = new float[frames];
                    foreach (var x in g) for (int f = 0; f < frames; f++) tot[f] += data[(long)f * stride + Col.Count + x.slot];
                    Array.Sort(tot);
                    sb.AppendLine($"{Trunc(g.Key, 50),-50} {cull,8:0.000} {rend,8:0.000} {cull + rend,8:0.000} {P(tot, 99),8:0.000}");
                }
            }

            WriteExperiments(sb, data, frames, stride, usedSlots, usedCounters, info);

            if (usedCounters.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== Unity counters and release-build markers (marker times may sum several threads) ==");
                sb.AppendLine($"{"counter",-70} {"avg",10} {"p95",10} {"p99",10} {"max",10}");
                foreach (int k in usedCounters.OrderBy(k => info.CounterNames[k], StringComparer.Ordinal))
                {
                    var col = Column(FrameRecorder.CounterBase + k);
                    var so = (float[])col.Clone();
                    Array.Sort(so);
                    sb.AppendLine($"{Trunc(info.CounterNames[k], 70),-70} {col.Average(x => (double)x),10:0.###} {P(so, 95),10:0.###} {P(so, 99),10:0.###} {so[frames - 1],10:0.###}");
                }
            }

            if (info.MethodNames.Length > 0)
            {
                double t = FrameRecorder.TicksToMs;
                sb.AppendLine();
                sb.AppendLine("== Instrumented methods, by self time (see methods.csv for all) ==");
                sb.AppendLine($"{"method",-80} {"self/fr",8} {"incl/fr",8} {"calls/fr",9} {"maxself",8}");
                foreach (int i in Enumerable.Range(0, info.MethodNames.Length).Where(i => info.MethodTotCalls[i] > 0).OrderByDescending(i => info.MethodTotSelf[i]).Take(40))
                {
                    sb.AppendLine($"{Trunc(info.MethodNames[i], 80),-80} {info.MethodTotSelf[i] * t / frames,8:0.000} {info.MethodTotIncl[i] * t / frames,8:0.000} {info.MethodTotCalls[i] / (double)frames,9:0.0} {info.MethodMaxSelf[i] * t,8:0.00}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("== 25 worst frames ==");
            foreach (int f in Enumerable.Range(0, frames).OrderByDescending(f => ft[f]).Take(25).OrderBy(f => f))
            {
                long o = (long)f * stride;
                var top = usedSlots.Select(s => (s, v: data[o + Col.Count + s])).OrderByDescending(x => x.v).Take(4)
                    .Select(x => $"{info.SlotNames[x.s]}={x.v:0.0}");
                sb.Append($"t={data[o + Col.Time],7:0.00}s frame {data[o + Col.Frame],7:0} {ft[f],7:0.0} ms  gc={data[o + Col.GcCollections]:0}  ");
                sb.Append(string.Join(" ", top));
                int m1 = (int)data[o + Col.Top1Id];
                if (m1 >= 0 && m1 < info.MethodNames.Length && data[o + Col.Top1Ms] > 0f) sb.Append($"  | top method {info.MethodNames[m1]}={data[o + Col.Top1Ms]:0.0}");
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>
        /// A/B table. Frames are split into segments of constant experiment id; the first SettleSeconds of every
        /// segment are dropped (switch hitch, overlay). Each experiment segment is compared with the mean of the
        /// Baseline segments directly before and after it, so slow drift (bots moving, weather) cancels out.
        /// </summary>
        private static void WriteExperiments(StringBuilder sb, float[] data, int frames, int stride, List<int> usedSlots, List<int> usedCounters, CaptureInfo info)
        {
            const float SettleSeconds = 3f;
            var names = info.ExperimentNames ?? new string[0];
            bool any = false;
            for (int f = 0; f < frames && !any; f++) any = data[(long)f * stride + Col.Experiment] != 0f;
            if (!any) return;

            // Metrics: (label, per-frame getter)
            int Slot(string name) => usedSlots.FirstOrDefault(s => info.SlotNames[s] == name) is int s && info.SlotNames[s] == name ? s : -1;
            int Ctr(string name) => usedCounters.FirstOrDefault(k => info.CounterNames[k].Contains(name)) is int k && info.CounterNames[k].Contains(name) ? k : -1;
            var cullSlots = usedSlots.Where(s => info.SlotNames[s].StartsWith("Cam.") && info.SlotNames[s].EndsWith(".Cull")).ToList();
            var renderSlots = usedSlots.Where(s => info.SlotNames[s].StartsWith("Cam.") && info.SlotNames[s].EndsWith(".Render")).ToList();
            int anim = Slot("PreLateUpdate.DirectorUpdateAnimationBegin");
            int animEnd = Slot("PreLateUpdate.DirectorUpdateAnimationEnd");
            int update = Slot("Update.ScriptRunBehaviourUpdate");
            int late = Slot("PreLateUpdate.ScriptRunBehaviourLateUpdate");
            int skin = Slot("PostLateUpdate.UpdateAllSkinnedMeshes");
            int wait = Slot("TimeUpdate.WaitForLastPresentationAndUpdateTime");
            float SlotVal(long o, int s) => s >= 0 ? data[o + Col.Count + s] : 0f;
            var metrics = new List<(string label, Func<long, float> get)>
            {
                ("frame_ms", o => data[o + Col.FrameMs]),
                ("cam_cull", o => cullSlots.Sum(s => data[o + Col.Count + s])),
                ("cam_render", o => renderSlots.Sum(s => data[o + Col.Count + s])),
                ("anim", o => SlotVal(o, anim)),
                ("anim_end", o => SlotVal(o, animEnd)),
                ("update", o => SlotVal(o, update)),
                ("lateupd", o => SlotVal(o, late)),
                ("skinning", o => SlotVal(o, skin)),
                ("present_wait", o => SlotVal(o, wait)),
                ("draws", o => data[o + Col.DrawCalls]),
                ("setpass", o => data[o + Col.SetPassCalls]),
                ("bots_vis", o => data[o + Col.BotsVisible]),
                ("bots_ik", o => data[o + Col.BotsIk]),
                ("bots_alw", o => data[o + Col.BotsAnimAlways]),
            };
            foreach (var (label, key) in new[] { ("main_thr", "CPU Main Thread Frame Time"), ("render_thr", "CPU Render Thread Frame Time"), ("gpu", "GPU Frame Time"), ("shadow_casters", "Shadow Casters Count") })
            {
                int k = Ctr(key);
                if (k >= 0) metrics.Add((label, o => data[o + FrameRecorder.CounterBase + k]));
            }

            // Segments
            var segs = new List<(int id, int start, int end)>(); // [start, end)
            int segStart = 0;
            for (int f = 1; f <= frames; f++)
            {
                if (f == frames || data[(long)f * stride + Col.Experiment] != data[(long)segStart * stride + Col.Experiment])
                {
                    segs.Add(((int)data[(long)segStart * stride + Col.Experiment], segStart, f));
                    segStart = f;
                }
            }

            const float SlowMs = 25f;
            double[] SegMeans(int start, int end, out int n, out double p95FrameMs, out double p99FrameMs, out double slowPerMin)
            {
                float t0 = data[(long)start * stride + Col.Time] + SettleSeconds;
                var sums = new double[metrics.Count];
                var ft = new List<float>();
                n = 0;
                for (int f = start; f < end; f++)
                {
                    long o = (long)f * stride;
                    if (data[o + Col.Time] < t0) continue;
                    for (int m = 0; m < metrics.Count; m++) sums[m] += metrics[m].get(o);
                    ft.Add(data[o + Col.FrameMs]);
                    n++;
                }
                ft.Sort();
                p95FrameMs = ft.Count > 0 ? ft[Math.Min(ft.Count - 1, (int)Math.Ceiling(0.95 * ft.Count) - 1)] : double.NaN;
                p99FrameMs = ft.Count > 0 ? ft[Math.Min(ft.Count - 1, (int)Math.Ceiling(0.99 * ft.Count) - 1)] : double.NaN;
                double seconds = ft.Sum(x => (double)x) / 1000.0;
                slowPerMin = seconds > 0 ? ft.Count(x => x > SlowMs) / seconds * 60.0 : double.NaN;
                if (n > 0) for (int m = 0; m < metrics.Count; m++) sums[m] /= n;
                return sums;
            }

            // Hitch metrics ride along as extra "metrics" so they get deltas too.
            var segMeans = segs.Select(s =>
            {
                var mm = SegMeans(s.start, s.end, out int n, out double p95, out double p99, out double slow);
                return (s.id, n, p95, mm: mm.Concat(new[] { p99, slow }).ToArray());
            }).ToList();
            var labels = metrics.Select(m => m.label).Concat(new[] { "p99", $"slow{SlowMs:0}/min" }).ToList();

            sb.AppendLine();
            sb.AppendLine($"== A/B experiments (first {SettleSeconds:0} s of each segment skipped; delta = experiment minus the nearest Baseline segment before and after it) ==");
            sb.AppendLine("Segments: " + string.Join(" ", segMeans.Select(s => $"{(s.id < names.Length ? names[s.id] : s.id.ToString())}({s.n})")));
            sb.AppendLine($"{"experiment",-20} {"frames",7} {"p95",7} " + string.Join(" ", labels.Select(l => $"{l,13}")));

            for (int id = 0; id < names.Length; id++)
            {
                var mine = segMeans.Select((s, i) => (s, i)).Where(x => x.s.id == id && x.s.n > 0).ToList();
                if (mine.Count == 0) continue;
                int total = mine.Sum(x => x.s.n);
                double p95 = mine.Sum(x => x.s.p95 * x.s.n) / total;
                int mc = labels.Count;
                var abs = new double[mc];
                var delta = new double[mc];
                var perRepeat = new List<double>();
                int deltaN = 0;
                foreach (var (s, i) in mine)
                {
                    for (int m = 0; m < mc; m++) abs[m] += s.mm[m] * s.n / total;
                    if (id == 0) continue;
                    // Nearest Baseline segment on each side (adjacent in the paired layout, up to a few steps away in round-robin).
                    var nb = new List<int>();
                    for (int j = i - 1; j >= 0; j--) if (segMeans[j].id == 0 && segMeans[j].n > 0) { nb.Add(j); break; }
                    for (int j = i + 1; j < segMeans.Count; j++) if (segMeans[j].id == 0 && segMeans[j].n > 0) { nb.Add(j); break; }
                    if (nb.Count == 0) continue;
                    for (int m = 0; m < mc; m++) delta[m] += s.mm[m] - nb.Average(j => segMeans[j].mm[m]);
                    perRepeat.Add(s.mm[0] - nb.Average(j => segMeans[j].mm[0]));
                    deltaN++;
                }
                sb.AppendLine($"{names[id],-20} {total,7} {p95,7:0.00} " + string.Join(" ", abs.Select(v => $"{v,13:0.000}")));
                if (deltaN > 0)
                {
                    sb.AppendLine($"{"  delta vs base",-20} {"",7} {"",7} " + string.Join(" ", delta.Select(v => $"{v / deltaN,13:+0.000;-0.000}")));
                    sb.AppendLine($"{"  frame_ms per repeat",-20} " + string.Join(" ", perRepeat.Select(v => $"{v:+0.00;-0.00}")) +
                                  (perRepeat.Count > 1 ? $"   (spread {perRepeat.Max() - perRepeat.Min():0.00})" : ""));
                }
            }
        }

        /// <summary>slow_work.csv (every slow item) and slow_work.txt (grouped by name).</summary>
        private static void WriteSlowWork(CaptureInfo info)
        {
            var items = info.SlowItems ?? new SlowWork.Item[0];
            using (var w = new StreamWriter(Path.Combine(info.Directory, "slow_work.csv"), false, new UTF8Encoding(false)))
            {
                w.WriteLine("time_s,frame,ms,kind,name");
                foreach (var it in items)
                    w.WriteLine($"{it.Time.ToString("0.###", Inv)},{it.Frame},{it.Ms.ToString("0.###", Inv)},{it.Kind},\"{it.Name?.Replace("\"", "'")}\"");
            }
            var sb = new StringBuilder();
            sb.AppendLine($"Single work items over {SlowWork.ThresholdMs} ms (JobScheduler jobs, async continuations, coroutine steps): {items.Length}");
            sb.AppendLine($"{"count",6} {"total ms",9} {"max ms",8} {"avg ms",8}  kind       name");
            foreach (var g in items.GroupBy(i => i.Kind + "|" + i.Name).OrderByDescending(g => g.Sum(i => i.Ms)))
            {
                var first = g.First();
                sb.AppendLine($"{g.Count(),6} {g.Sum(i => i.Ms),9:0.0} {g.Max(i => i.Ms),8:0.0} {g.Average(i => i.Ms),8:0.0}  {first.Kind,-10} {first.Name}");
            }
            File.WriteAllText(Path.Combine(info.Directory, "slow_work.txt"), sb.ToString());
        }

        private static float P(float[] sorted, double pct)
        {
            int i = (int)Math.Ceiling(pct / 100.0 * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, i))];
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
