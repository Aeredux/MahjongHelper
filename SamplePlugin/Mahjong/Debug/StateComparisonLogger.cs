using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using SamplePlugin.Mahjong;

namespace SamplePlugin.Mahjong.Debug;

/// <summary>
/// Logs before/after comparisons of AgentId.Emj state to identify which memory locations
/// change when tiles are drawn, discarded, or game phase changes occur.
/// 
/// Strategy: Capture full 0x200-byte snapshot of AgentId.Emj+0x28 at regular intervals,
/// compute deltas, and log them with user annotation of the game event.
/// </summary>
public static unsafe class StateComparisonLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MahjongHelper");
    private static readonly string ComparisonLogPath = Path.Combine(LogDirectory, "state_comparison.log");

    private static byte[]? _lastSnapshot;
    private static DateTime? _lastSnapshotTime;
    private static int _comparisonCount = 0;

    public static void CaptureSnapshot(nint agentEmjAddr)
    {
        try
        {
            // AgentId.Emj+0x28 is the main state pointer we're interested in
            nint statePtr = Marshal.ReadIntPtr(agentEmjAddr + 0x28);
            if (statePtr == nint.Zero)
                return;

            var snapshot = new byte[0x200];
            Marshal.Copy(statePtr, snapshot, 0, 0x200);

            if (_lastSnapshot != null)
            {
                // Compute delta
                var deltas = FindDeltas(_lastSnapshot, snapshot);
                if (deltas.Any())
                {
                    // Convert to 4-tuple format for logging
                    var deltasWithDesc = deltas.Select(d => (d.offset, d.oldValue, d.newValue, "auto")).AsEnumerable();
                    LogComparison("auto", deltasWithDesc, snapshot);
                }
            }

            _lastSnapshot = snapshot;
            _lastSnapshotTime = DateTime.UtcNow;
        }
        catch
        {
            // Silently ignore read errors
        }
    }

    public static void LogAnnotatedEvent(string eventName, string description, nint agentEmjAddr)
    {
        try
        {
            // Capture new snapshot
            nint statePtr = Marshal.ReadIntPtr(agentEmjAddr + 0x28);
            if (statePtr == nint.Zero)
                return;

            var snapshot = new byte[0x200];
            Marshal.Copy(statePtr, snapshot, 0, 0x200);

            if (_lastSnapshot != null)
            {
                var deltas = FindDeltas(_lastSnapshot, snapshot);
                var annotated = new List<(int, byte, byte, string)>();
                foreach (var (off, oldVal, newVal) in deltas)
                {
                    annotated.Add((off, oldVal, newVal, description));
                }
                LogComparison($"event:{eventName}", annotated.AsEnumerable(), snapshot);
            }

            _lastSnapshot = snapshot;
            _lastSnapshotTime = DateTime.UtcNow;
        }
        catch
        {
            // Silently ignore
        }
    }

    private static List<(int offset, byte oldValue, byte newValue)> FindDeltas(byte[] oldSnapshot, byte[] newSnapshot)
    {
        var deltas = new List<(int, byte, byte)>();
        var minLen = Math.Min(oldSnapshot.Length, newSnapshot.Length);

        for (int i = 0; i < minLen; i++)
        {
            if (oldSnapshot[i] != newSnapshot[i])
            {
                deltas.Add((i, oldSnapshot[i], newSnapshot[i]));
            }
        }

        return deltas;
    }

    private static void LogComparison(
        string source,
        IEnumerable<(int offset, byte oldValue, byte newValue, string description)> deltas,
        byte[]? newSnapshot = null)
    {
        try
        {
            _comparisonCount++;

            var sb = new StringBuilder();
            sb.AppendLine($"\n=== State Comparison #{_comparisonCount} [{source}] {DateTime.UtcNow:O} ===");
            sb.AppendLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Source: {source}");
            var deltaList = deltas.ToList();
            sb.AppendLine($"Changed bytes: {deltaList.Count()}");
            sb.AppendLine();

            // Group deltas by region for easier analysis
            var regions = new Dictionary<int, List<(int, byte, byte, string)>>();
            const int regionSize = 0x10;

            foreach (var (off, oldVal, newVal, desc) in deltaList)
            {
                int regionKey = (off / regionSize) * regionSize;
                if (!regions.ContainsKey(regionKey))
                    regions[regionKey] = new List<(int, byte, byte, string)>();
                regions[regionKey].Add((off, oldVal, newVal, desc));
            }

            sb.AppendLine("Byte-level changes:");
            foreach (var (regionStart, changes) in regions.OrderBy(x => x.Key))
            {
                sb.AppendLine($"  +0x{regionStart:X2}:");
                foreach (var (off, oldVal, newVal, desc) in changes.OrderBy(x => x.Item1))
                {
                    sb.AppendLine($"    +0x{off:X2}: 0x{oldVal:X2} → 0x{newVal:X2}  ({oldVal} → {newVal})");
                }
            }

            // If we're logging an annotated event, include raw hex dump of the changed region
            if (source.StartsWith("event") && newSnapshot != null)
            {
                sb.AppendLine("\nUpdated snapshot (hex):");
                // Dump key regions around the largest changes
                var topChanges = deltaList.OrderByDescending(x => Math.Abs(x.newValue - x.oldValue)).Take(5).ToList();
                foreach (var (off, _, _, _) in topChanges)
                {
                    int dumpStart = Math.Max(0, off - 0x08);
                    int dumpEnd = Math.Min(newSnapshot.Length, off + 0x10);
                    sb.AppendLine($"  Around offset +0x{off:X2}:");
                    for (int i = dumpStart; i < dumpEnd; i += 0x10)
                    {
                        sb.Append("    ");
                        for (int j = i; j < i + 0x10 && j < dumpEnd; j++)
                        {
                            sb.Append($"{newSnapshot[j]:X2} ");
                        }
                        sb.AppendLine();
                    }
                }
            }

            sb.AppendLine();

            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(ComparisonLogPath, sb.ToString());
        }
        catch
        {
            // Silently fail — never crash the plugin
        }
    }

    public static string GetLogPath() => ComparisonLogPath;
}
