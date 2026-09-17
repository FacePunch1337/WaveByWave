using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UColliders.CoACD
{
    /// <summary>
    /// Lightweight profiler for CoACD pipeline stages.
    /// Accumulates timing data across calls and reports a summary.
    /// Thread-safe for use with Parallel.For.
    /// </summary>
    internal static class CoACDProfiler
    {
        struct TimingEntry
        {
            public long totalTicks;
            public int callCount;
        }

        static readonly Dictionary<string, TimingEntry> _timings = new Dictionary<string, TimingEntry>();
        static readonly object _lock = new object();
        static bool _enabled;

        /// <summary>
        /// Enable or disable profiling. When disabled, Begin/End are no-ops.
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Clear all accumulated timing data.
        /// </summary>
        public static void Reset()
        {
            lock (_lock) { _timings.Clear(); }
        }

        /// <summary>
        /// Begin a timed section. Returns a timestamp to pass to End().
        /// </summary>
        public static long Begin()
        {
            if (!_enabled) return 0;
            return Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// End a timed section and accumulate the elapsed time.
        /// </summary>
        public static void End(string name, long startTimestamp)
        {
            if (!_enabled) return;
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            lock (_lock)
            {
                TimingEntry entry;
                if (!_timings.TryGetValue(name, out entry))
                    entry = new TimingEntry();
                entry.totalTicks += elapsed;
                entry.callCount++;
                _timings[name] = entry;
            }
        }

        /// <summary>
        /// Get a formatted summary of all profiled sections, sorted by total time descending.
        /// </summary>
        public static string GetReport()
        {
            lock (_lock)
            {
                if (_timings.Count == 0)
                    return "UColliders Profiler: no data collected.";

                double freq = Stopwatch.Frequency;
                var entries = new List<KeyValuePair<string, TimingEntry>>(_timings);
                entries.Sort((a, b) => b.Value.totalTicks.CompareTo(a.Value.totalTicks));

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== UColliders Performance Report ===");
                sb.AppendLine($"{"Section",-30} {"Total ms",10} {"Calls",8} {"Avg ms",10}");
                sb.AppendLine(new string('-', 62));

                foreach (var kv in entries)
                {
                    double totalMs = kv.Value.totalTicks / freq * 1000.0;
                    double avgMs = kv.Value.callCount > 0 ? totalMs / kv.Value.callCount : 0;
                    sb.AppendLine($"{kv.Key,-30} {totalMs,10:F2} {kv.Value.callCount,8} {avgMs,10:F3}");
                }

                return sb.ToString();
            }
        }
    }
}
