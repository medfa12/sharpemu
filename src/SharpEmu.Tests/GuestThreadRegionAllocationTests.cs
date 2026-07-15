// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Guest thread stack/TLS region allocation must be atomic: two concurrent
/// thread creations may never be handed the same region. PhysicalVirtualMemory.Map
/// silently reuses an already-mapped region instead of throwing, so an
/// unsynchronized probe-then-map lets two threads share (and zero-fill) one live
/// stack. The fake memory below widens the probe window with a rendezvous so an
/// unserialized allocator deterministically hands out duplicate bases.
/// </summary>
public sealed class GuestThreadRegionAllocationTests
{
    private const ulong StackBaseAddress = 0x7FFF_E000_0000UL;
    private const ulong StackSize = 0x0020_0000UL;

    // DirectExecutionBackend's static initializer resolves the native import
    // gateway through HostPlatform.Current, which requires an x86-64 process
    // (see HostPlatform.Create). The region-allocation logic under test is
    // arch-independent, but it cannot be reached on a non-x86-64 host, so skip
    // there instead of tripping the platform gate.
    private static bool NativeExecutionSupported =>
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

    [Fact]
    public void ConcurrentStackRegionMapsReceiveDistinctBases()
    {
        if (!NativeExecutionSupported)
        {
            return;
        }

        var memory = new RacyVirtualMemory();
        var bases = new ulong[2];
        var results = new bool[2];
        RunConcurrently(index => results[index] = TryMapStackRegion(memory, out bases[index]));

        Assert.True(results[0]);
        Assert.True(results[1]);
        Assert.NotEqual(bases[0], bases[1]);
        Assert.Equal(2, memory.RegionCount);
    }

    [Fact]
    public void ConcurrentTlsRegionMapsReceiveDistinctBases()
    {
        if (!NativeExecutionSupported)
        {
            return;
        }

        var memory = new RacyVirtualMemory();
        var bases = new ulong[2];
        var results = new bool[2];
        RunConcurrently(index => results[index] = TryMapTlsRegion(memory, out bases[index]));

        Assert.True(results[0]);
        Assert.True(results[1]);
        Assert.NotEqual(bases[0], bases[1]);
        Assert.Equal(2, memory.RegionCount);
    }

    private static void RunConcurrently(Action<int> body)
    {
        var threads = new Thread[2];
        var failures = new Exception?[2];
        for (int t = 0; t < 2; t++)
        {
            int index = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    body(index);
                }
                catch (Exception ex)
                {
                    failures[index] = ex;
                }
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Null(failures[0]);
        Assert.Null(failures[1]);
    }

    private static bool TryMapStackRegion(IVirtualMemory memory, out ulong mappedBase)
    {
        var method = GetBackendMethod("TryMapGuestThreadRegion");
        var args = new object?[]
        {
            memory,
            StackBaseAddress,
            StackSize,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write,
            0UL,
            null,
        };
        var result = (bool)method.Invoke(null, args)!;
        mappedBase = (ulong)args[4]!;
        return result;
    }

    private static bool TryMapTlsRegion(IVirtualMemory memory, out ulong tlsBase)
    {
        var method = GetBackendMethod("TryMapGuestThreadTlsRegion");
        var args = new object?[] { memory, 0UL, null };
        var result = (bool)method.Invoke(null, args)!;
        tlsBase = (ulong)args[1]!;
        return result;
    }

    private static MethodInfo GetBackendMethod(string name)
    {
        var method = typeof(DirectExecutionBackend).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }

    /// <summary>
    /// Mirrors PhysicalVirtualMemory's overlap behavior: Map on an existing
    /// region silently succeeds without adding a new one. The first snapshot
    /// from each mapper waits briefly for the other mapper's first snapshot,
    /// so a probe-then-map without a shared allocation gate sees a stale free
    /// list on both threads and picks the same candidate.
    /// </summary>
    private sealed class RacyVirtualMemory : IVirtualMemory
    {
        private readonly object _gate = new();
        private readonly List<VirtualMemoryRegion> _regions = new();
        private readonly ManualResetEventSlim _bothProbed = new();
        private int _probes;

        public int RegionCount
        {
            get
            {
                lock (_gate)
                {
                    return _regions.Count;
                }
            }
        }

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
        {
            VirtualMemoryRegion[] snapshot;
            lock (_gate)
            {
                snapshot = _regions.ToArray();
            }

            if (Interlocked.Increment(ref _probes) >= 2)
            {
                _bothProbed.Set();
            }

            _bothProbed.Wait(TimeSpan.FromMilliseconds(250));
            return snapshot;
        }

        public void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection)
        {
            lock (_gate)
            {
                var mapEnd = virtualAddress + memorySize;
                foreach (var region in _regions)
                {
                    if (virtualAddress < region.VirtualAddress + region.MemorySize &&
                        region.VirtualAddress < mapEnd)
                    {
                        return;
                    }
                }

                _regions.Add(new VirtualMemoryRegion(virtualAddress, memorySize, fileOffset, 0, protection));
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _regions.Clear();
            }
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            destination.Clear();
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            return true;
        }
    }
}
