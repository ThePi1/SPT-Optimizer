using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// Visual check for A/B steps, without anyone watching.
    ///
    /// Inside each step's settle window (the 3 s the report skips) three shots are taken ~0.7 s apart:
    ///   A = experiment on, B = experiment briefly switched back to Baseline, C = experiment on again.
    /// A pixel counts as a real change only if A and C agree with each other and both differ from B. Weapon sway,
    /// foliage, particles, drifting cloud shadows and on-screen counters change between A and C as well, so they
    /// drop out. Baseline steps take the same three shots without switching: their score is the noise floor.
    /// Output: shots/NN_step_exp.png and _base.png (C and B, full size), NN_step_change.png (real changes in red),
    /// shots/diff.txt. Pixel reads happen on the main thread; encoding and comparison on worker threads.
    /// </summary>
    internal static class Screenshots
    {
        public static bool Enabled = true;
        private const int Down = 4;          // compare at 1/4 resolution per axis
        private const int Threshold = 20;    // per-channel difference that counts as "different"

        private class Shot
        {
            public int Step;
            public char Kind;                // 'a', 'b', 'c'
            public string Label;
            public bool BaselineStep;
            public int W, H;
            public byte[] R, G, B;
        }

        private static readonly List<Shot> Shots = new List<Shot>();
        private static readonly List<Task> Pending = new List<Task>();
        private static string _dir;
        private static MonoBehaviour _host;
        private static ManualLogSource _log;

        public static void Init(MonoBehaviour host, ManualLogSource log)
        {
            _host = host;
            _log = log;
        }

        public static void Begin(string captureDir)
        {
            lock (Shots) Shots.Clear();
            Pending.Clear();
            _dir = Path.Combine(captureDir, "shots");
        }

        public static void Request(int step, char kind, string label, bool baselineStep)
        {
            if (!Enabled || _dir == null || _host == null) return;
            _host.StartCoroutine(Take(step, kind, label, baselineStep));
        }

        private static IEnumerator Take(int step, char kind, string label, bool baselineStep)
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                var px = tex.GetPixels32();
                int w = tex.width, h = tex.height;
                var shot = new Shot { Step = step, Kind = kind, Label = label, BaselineStep = baselineStep };
                // Full-size PNGs only where someone will look at them: B and C of experiment steps, A of Baseline steps.
                string png = null;
                if (!baselineStep && kind == 'b') png = $"{step:00}_{label}_base.png";
                else if (!baselineStep && kind == 'c') png = $"{step:00}_{label}_exp.png";
                else if (baselineStep && kind == 'a') png = $"{step:00}_{label}.png";
                string dir = _dir;
                Pending.Add(Task.Run(() => Process(shot, px, w, h, dir, png)));
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Screenshot failed: " + ex.Message);
            }
            finally
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        private static void Process(Shot shot, Color32[] px, int w, int h, string dir, string png)
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (png != null)
                    File.WriteAllBytes(Path.Combine(dir, png), ImageConversion.EncodeArrayToPNG(px, GraphicsFormat.R8G8B8A8_SRGB, (uint)w, (uint)h));
                int dw = w / Down, dh = h / Down, n = Down * Down;
                shot.W = dw; shot.H = dh;
                shot.R = new byte[dw * dh]; shot.G = new byte[dw * dh]; shot.B = new byte[dw * dh];
                for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int yy = 0; yy < Down; yy++)
                    {
                        int row = (y * Down + yy) * w + x * Down;
                        for (int xx = 0; xx < Down; xx++)
                        {
                            var c = px[row + xx];
                            r += c.r; g += c.g; b += c.b;
                        }
                    }
                    int i = y * dw + x;
                    shot.R[i] = (byte)(r / n); shot.G[i] = (byte)(g / n); shot.B[i] = (byte)(b / n);
                }
                lock (Shots) Shots.Add(shot);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Screenshot processing failed: " + ex.Message);
            }
        }

        public static void Finish()
        {
            if (_dir == null) return;
            try { Task.WaitAll(Pending.ToArray(), TimeSpan.FromSeconds(60)); } catch { }
            List<Shot> shots;
            lock (Shots) shots = Shots.ToList();
            string dir = _dir;
            _dir = null;
            if (shots.Count > 0) Task.Run(() => WriteDiffs(shots, dir));
        }

        private static int D(Shot x, Shot y, int i) =>
            Math.Max(Math.Abs(x.R[i] - y.R[i]), Math.Max(Math.Abs(x.G[i] - y.G[i]), Math.Abs(x.B[i] - y.B[i])));

        /// <summary>% of pixels where A and C agree and both differ from B; plus the plain A-vs-B change.</summary>
        private static (double real, double raw) Score(Shot a, Shot b, Shot c, bool[] mask)
        {
            int real = 0, raw = 0, n = a.R.Length;
            for (int i = 0; i < n; i++)
            {
                int ab = D(a, b, i), cb = D(c, b, i), ac = D(a, c, i);
                if (ab > Threshold) raw++;
                bool isReal = ab > Threshold && cb > Threshold && ac <= Threshold / 2;
                if (isReal) real++;
                if (mask != null) mask[i] = isReal;
            }
            return (100.0 * real / n, 100.0 * raw / n);
        }

        private static void WriteChangeImage(Shot b, bool[] mask, string path)
        {
            int n = b.R.Length;
            var px = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                byte g = (byte)((b.R[i] + b.G[i] + b.B[i]) / 9);
                px[i] = mask[i] ? new Color32(255, 0, 0, 255) : new Color32(g, g, g, 255);
            }
            File.WriteAllBytes(path, ImageConversion.EncodeArrayToPNG(px, GraphicsFormat.R8G8B8A8_SRGB, (uint)b.W, (uint)b.H));
        }

        private static void WriteDiffs(List<Shot> shots, string dir)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Screenshot check. Per step, in the settle window: A = experiment, B = briefly back to Baseline, C = experiment again (~0.7 s apart).");
                sb.AppendLine("real = % of pixels where A and C agree and both differ from B (a change caused by the experiment).");
                sb.AppendLine("raw  = % of pixels where A differs from B at all (includes sway, foliage, particles, counters).");
                sb.AppendLine("Baseline steps take the same three shots without switching; their 'real' is the noise floor.");
                sb.AppendLine();
                sb.AppendLine($"{"step",4} {"experiment",-24} {"real %",8} {"raw %",8}");
                var byStep = shots.GroupBy(s => s.Step).OrderBy(g => g.Key);
                var results = new List<(string label, bool baseline, double real, double raw)>();
                foreach (var g in byStep)
                {
                    var a = g.FirstOrDefault(s => s.Kind == 'a');
                    var b = g.FirstOrDefault(s => s.Kind == 'b');
                    var c = g.FirstOrDefault(s => s.Kind == 'c');
                    if (a == null || b == null || c == null || a.R.Length != b.R.Length || c.R.Length != b.R.Length) continue;
                    var mask = new bool[b.R.Length];
                    var (real, raw) = Score(a, b, c, mask);
                    results.Add((a.Label, a.BaselineStep, real, raw));
                    sb.AppendLine($"{g.Key,4} {a.Label,-24} {real,8:0.00} {raw,8:0.0}{(a.BaselineStep ? "   (noise floor)" : "")}");
                    if (!a.BaselineStep) WriteChangeImage(b, mask, Path.Combine(dir, $"{g.Key:00}_{a.Label}_change.png"));
                }
                sb.AppendLine();
                sb.AppendLine("Per experiment (average over repeats):");
                foreach (var g in results.GroupBy(r => r.baseline ? "Baseline (noise floor)" : r.label))
                    sb.AppendLine($"  {g.Key,-24} real {g.Average(r => r.real),6:0.00}%   raw {g.Average(r => r.raw),5:0.0}%   ({g.Count()} steps)");
                File.WriteAllText(Path.Combine(dir, "diff.txt"), sb.ToString());
                _log?.LogInfo($"Screenshots: {results.Count} steps compared, see {Path.Combine(dir, "diff.txt")}");
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Screenshot comparison failed: " + ex);
            }
        }
    }
}
