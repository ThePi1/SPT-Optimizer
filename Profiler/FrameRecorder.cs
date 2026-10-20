using System;
using System.Collections.Generic;
using System.Diagnostics;
using Comfort.Common;
using EFT;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace SPTOptimizer.Profiler
{
    /// <summary>Fixed per-frame columns. PlayerLoop slot columns follow these.</summary>
    internal static class Col
    {
        public const int Frame = 0;
        public const int Time = 1;
        public const int FrameMs = 2;
        public const int UnityDeltaMs = 3;
        public const int GcCollections = 4;
        public const int GcAllocKB = 5;
        public const int MonoUsedMB = 6;
        public const int MonoHeapMB = 7;
        public const int GcEnabled = 8;
        public const int AlivePlayers = 9;
        public const int PosX = 10;
        public const int PosY = 11;
        public const int PosZ = 12;
        public const int DrawCalls = 13;
        public const int SetPassCalls = 14;
        public const int Batches = 15;
        public const int TrianglesK = 16;
        public const int FtCpuMainMs = 17;
        public const int FtCpuRenderMs = 18;
        public const int FtPresentWaitMs = 19;
        public const int FtGpuMs = 20;
        public const int InstrCalls = 21;
        public const int Top1Id = 22;
        public const int Top1Ms = 23;
        public const int Top2Id = 24;
        public const int Top2Ms = 25;
        public const int Top3Id = 26;
        public const int Top3Ms = 27;
        public const int CameraRenders = 28;
        public const int Experiment = 29;
        public const int BotsAlive = 30;
        public const int BotsVisible = 31;
        public const int BotsIk = 32;
        public const int BotsNear = 33;
        public const int BotsAnimAlways = 34;
        public const int Count = 35;

        public static readonly string[] Names =
        {
            "frame", "time_s", "frame_ms", "unity_delta_ms", "gc_collections", "gc_alloc_kb", "mono_used_mb", "mono_heap_mb",
            "gc_enabled", "alive_players", "pos_x", "pos_y", "pos_z", "draw_calls", "setpass_calls", "batches", "triangles_k",
            "ft_cpu_main_ms", "ft_cpu_render_ms", "ft_present_wait_ms", "ft_gpu_ms", "instr_calls",
            "top1_method", "top1_self_ms", "top2_method", "top2_self_ms", "top3_method", "top3_self_ms", "camera_renders", "experiment", "bots_alive", "bots_visible", "bots_ik", "bots_near", "bots_anim_always",
        };
    }

    /// <summary>
    /// Receives PlayerLoop marker callbacks, accumulates time per slot, and emits one row per frame
    /// into the capture's chunk buffers. Everything on the per-frame path is allocation-free.
    /// </summary>
    internal static class FrameRecorder
    {
        public const int MaxSlots = 256;
        public const int CounterBase = Col.Count + MaxSlots;
        public const int Stride = CounterBase + Counters.MaxCounters;
        public const int GapSlot = 0;

        public static readonly string[] SlotNames = new string[MaxSlots];
        public static int SlotCount;
        private static readonly Dictionary<string, int> SlotIds = new Dictionary<string, int>();

        private static readonly long[] Acc = new long[MaxSlots];
        private static int _current;
        private static long _last;
        private static long _frameStart;
        private static bool _havePrev;
        private static bool _emitErrorLogged;

        public static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        // Capture state
        public static bool Recording;
        public static long FramesRecorded;
        public static long FramesDropped;
        private static long _captureStart;
        private static int _gcCountPrev;
        private static float[] _chunk;
        private static int _chunkRows;

        private static ProfilerRecorder _gcAlloc, _drawCalls, _setPass, _batches, _tris;
        private static readonly FrameTiming[] Timings = new FrameTiming[1];
        public static bool FrameTimingWorks;

        static FrameRecorder()
        {
            GetSlot("Loop.Gap");
        }

        public static int GetSlot(string name)
        {
            if (SlotIds.TryGetValue(name, out int id)) return id;
            if (SlotCount >= MaxSlots) return GapSlot;
            id = SlotCount++;
            SlotIds[name] = id;
            SlotNames[id] = name;
            return id;
        }

        public static int CurrentSlot => _current;

        public static void Mark(int slot)
        {
            long now = Stopwatch.GetTimestamp();
            Acc[_current] += now - _last;
            _current = slot;
            _last = now;
        }

        /// <summary>Runs first in every PlayerLoop iteration: closes the previous frame's row.</summary>
        public static void FrameStart()
        {
            long now = Stopwatch.GetTimestamp();
            Acc[_current] += now - _last;
            _current = GapSlot;
            _last = now;

            if (_havePrev && Recording)
            {
                try
                {
                    EmitRow(now);
                }
                catch (Exception ex)
                {
                    // Log once per capture; this runs every frame.
                    if (!_emitErrorLogged)
                    {
                        _emitErrorLogged = true;
                        UnityEngine.Debug.LogError("[SPTOptimizer.Profiler] EmitRow failed: " + ex);
                    }
                    MethodTimer.DiscardFrame();
                }
            }
            else
            {
                MethodTimer.DiscardFrame();
            }

            Array.Clear(Acc, 0, SlotCount);
            SlowWork.ResetDepth();
            _frameStart = now;
            _havePrev = true;
        }

        private static void EmitRow(long now)
        {
            if (_chunk == null)
            {
                _chunk = CaptureWriter.RentChunk();
                _chunkRows = 0;
                if (_chunk == null)
                {
                    FramesDropped++;
                    MethodTimer.DiscardFrame();
                    return;
                }
            }

            int o = _chunkRows * Stride;
            float[] r = _chunk;
            Array.Clear(r, o, Stride);

            r[o + Col.Frame] = Time.frameCount;
            r[o + Col.Time] = (float)((now - _captureStart) / (double)Stopwatch.Frequency);
            r[o + Col.FrameMs] = (float)((now - _frameStart) * TicksToMs);
            r[o + Col.UnityDeltaMs] = Time.unscaledDeltaTime * 1000f;

            int gc = GC.CollectionCount(0);
            r[o + Col.GcCollections] = gc - _gcCountPrev;
            _gcCountPrev = gc;
            r[o + Col.GcAllocKB] = _gcAlloc.Valid ? _gcAlloc.LastValue / 1024f : float.NaN;
            r[o + Col.MonoUsedMB] = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576f;
            r[o + Col.MonoHeapMB] = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576f;
            r[o + Col.GcEnabled] = GarbageCollector.GCMode == GarbageCollector.Mode.Enabled ? 1 : 0;

            if (Singleton<GameWorld>.Instantiated)
            {
                var gw = Singleton<GameWorld>.Instance;
                r[o + Col.AlivePlayers] = gw.AllAlivePlayersList.Count;
                var p = gw.MainPlayer;
                if (p != null)
                {
                    var pos = p.Position;
                    r[o + Col.PosX] = pos.x;
                    r[o + Col.PosY] = pos.y;
                    r[o + Col.PosZ] = pos.z;
                }
            }

            r[o + Col.DrawCalls] = _drawCalls.Valid ? _drawCalls.LastValue : float.NaN;
            r[o + Col.SetPassCalls] = _setPass.Valid ? _setPass.LastValue : float.NaN;
            r[o + Col.Batches] = _batches.Valid ? _batches.LastValue : float.NaN;
            r[o + Col.TrianglesK] = _tris.Valid ? _tris.LastValue / 1000f : float.NaN;

            if (FrameTimingWorks)
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, Timings) > 0)
                {
                    r[o + Col.FtCpuMainMs] = (float)Timings[0].cpuMainThreadFrameTime;
                    r[o + Col.FtCpuRenderMs] = (float)Timings[0].cpuRenderThreadFrameTime;
                    r[o + Col.FtPresentWaitMs] = (float)Timings[0].cpuMainThreadPresentWaitTime;
                    r[o + Col.FtGpuMs] = (float)Timings[0].gpuFrameTime;
                }
            }
            else
            {
                r[o + Col.FtCpuMainMs] = r[o + Col.FtCpuRenderMs] = r[o + Col.FtPresentWaitMs] = r[o + Col.FtGpuMs] = float.NaN;
            }

            MethodTimer.EndFrame(r, o);
            r[o + Col.CameraRenders] = CameraTimer.TakeRenderCount();
            r[o + Col.Experiment] = Experiments.RecordedId;
            PlayerStats.Fill(r, o);
            Counters.Fill(r, o + CounterBase);

            int slotBase = o + Col.Count;
            for (int i = 0; i < SlotCount; i++) r[slotBase + i] = (float)(Acc[i] * TicksToMs);

            FramesRecorded++;
            if (++_chunkRows == CaptureWriter.ChunkRows) FlushChunk();
        }

        public static void FlushChunk()
        {
            if (_chunk == null) return;
            CaptureWriter.SubmitChunk(_chunk, _chunkRows);
            _chunk = null;
            _chunkRows = 0;
        }

        public static void BeginCapture()
        {
            _captureStart = Stopwatch.GetTimestamp();
            _gcCountPrev = GC.CollectionCount(0);
            FramesRecorded = 0;
            FramesDropped = 0;
            _havePrev = false;
            _emitErrorLogged = false;

            _gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");

            FrameTimingWorks = ProbeFrameTiming();
            Recording = true;
        }

        public static void EndCapture()
        {
            Recording = false;
            FlushChunk();
            _gcAlloc.Dispose();
            _drawCalls.Dispose();
            _setPass.Dispose();
            _batches.Dispose();
            _tris.Dispose();
        }

        public static string RecorderStatus()
        {
            return $"gc_alloc={_gcAlloc.Valid} draw_calls={_drawCalls.Valid} setpass={_setPass.Valid} batches={_batches.Valid} triangles={_tris.Valid} frame_timing={FrameTimingWorks}";
        }

        /// <summary>FrameTimingManager only reports in release players when Frame Timing Stats was enabled at build time.</summary>
        private static bool ProbeFrameTiming()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                return FrameTimingManager.IsFeatureEnabled();
            }
            catch
            {
                return false;
            }
        }
    }
}
