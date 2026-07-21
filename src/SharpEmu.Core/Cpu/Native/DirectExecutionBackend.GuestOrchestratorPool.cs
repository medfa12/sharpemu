// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	// The dispatcher wakes many parked guest worker threads per frame (Astro Bot
	// cycles ~18 Havok + ~11 tbb + loader threads every frame on semaphores/event
	// flags). The default Pump path spins up a BRAND-NEW managed orchestrator Thread
	// for every wake and lets it exit when the guest blocks again. That thread only
	// drives the RunGuestThread bookkeeping and rents a (separately pooled) native
	// guest worker to run the actual guest code -- yet every create/destroy still
	// costs a managed Thread object, a committed stack, CLR registration and, worst
	// of all, cold-thread scheduling latency before the OS runs it. Serialized across
	// hundreds/thousands of wakes per frame this latency compounds toward ~2 s.
	//
	// The native guest worker pool (NativeGuestExecutor) already reuses OS threads
	// for guest CODE, but it is an emitted-assembly stub runner and cannot host the
	// managed RunGuestThread orchestrator that sits above it. This opt-in pool reuses
	// parked *orchestrator* threads for that layer instead. It is OFF by default so
	// the shipped behavior is byte-identical to today's per-dispatch new Thread;
	// enable it to A/B the win:
	//   SHARPEMU_POOL_GUEST_ORCHESTRATORS=1
	//
	// Safe to reuse a worker for any guest thread: the pooled worker runs the exact
	// same RunGuestThread(thread, reason) call the new-Thread path runs, and
	// RunGuestThread self-binds the guest-thread ambient (EnterGuestThread /
	// RestoreGuestThread) and saves/restores the Active* thread-statics, while the
	// native worker it rents rebinds the TLS base + CpuContext per run. The Pump path
	// already migrates a guest thread onto an arbitrary fresh host thread on every
	// dispatch, so no per-guest host-thread identity is assumed -- pooling is
	// invisible to guest code.
	private static readonly bool GuestOrchestratorPoolEnabled =
		string.Equals(
			Environment.GetEnvironmentVariable("SHARPEMU_POOL_GUEST_ORCHESTRATORS"),
			"1",
			StringComparison.Ordinal);

	private readonly object _orchestratorGate = new();
	private readonly List<GuestOrchestratorWorker> _allOrchestrators = new();
	private readonly Stack<GuestOrchestratorWorker> _idleOrchestrators = new();
	private bool _orchestratorsDisposed;

	// Number of orchestrator workers currently executing a guest thread. Teardown
	// drains on this instead of joining a per-guest host Thread (pooled workers park
	// rather than exit, so Join would hang), preserving the "no worker is in guest
	// code before we free executable memory" invariant.
	private int _activeOrchestratorRuns;

	// Dispatches RunGuestThread onto a reused (or freshly grown) orchestrator worker.
	// Fire-and-forget: the caller (Pump) never waits, matching the current
	// new-Thread().Start() semantics where up to 8 ready guest threads run
	// concurrently. Growable and never blocks for a free worker, so a guest that
	// needs N concurrent runnable threads can never deadlock the pool.
	private void DispatchGuestThreadOnOrchestrator(GuestThreadState thread, string reason)
	{
		var worker = RentOrchestrator();
		if (worker is null)
		{
			// Pool unavailable (teardown in flight, or a thread create failed). Fall
			// back to the historical per-dispatch thread so the wake is never dropped;
			// its HostThread is joined by RequestGuestThreadTeardown as before.
			var hostThread = new Thread(() => RunGuestThread(thread, reason))
			{
				IsBackground = true,
				Name = $"SharpEmu-{thread.Name}",
				Priority = MapGuestThreadPriority(thread.Priority),
			};
			lock (_guestThreadGate)
			{
				thread.HostThread = hostThread;
			}
			hostThread.Start();
			return;
		}

		// Pooled workers are not owned by any one guest thread, so leave HostThread
		// null (teardown drains them via the active-run count, never Join).
		var priority = MapGuestThreadPriority(thread.Priority);
		worker.Post(() => RunGuestThreadOnOrchestrator(thread, reason, priority));
	}

	// Wraps RunGuestThread with the per-run active-count bookkeeping teardown drains
	// on, plus the priority the new-Thread path used to set on the dispatch thread.
	private void RunGuestThreadOnOrchestrator(GuestThreadState thread, string reason, ThreadPriority priority)
	{
		Interlocked.Increment(ref _activeOrchestratorRuns);
		var restorePriority = ThreadPriority.Normal;
		var priorityAdjusted = false;
		try
		{
			try
			{
				restorePriority = Thread.CurrentThread.Priority;
				Thread.CurrentThread.Priority = priority;
				priorityAdjusted = true;
			}
			catch
			{
				// Priority is a scheduling hint only; a failure to set it must not
				// abort the guest run.
			}

			RunGuestThread(thread, reason);
		}
		finally
		{
			if (priorityAdjusted)
			{
				try
				{
					Thread.CurrentThread.Priority = restorePriority;
				}
				catch
				{
				}
			}
			Interlocked.Decrement(ref _activeOrchestratorRuns);
		}
	}

	private GuestOrchestratorWorker? RentOrchestrator()
	{
		lock (_orchestratorGate)
		{
			if (_orchestratorsDisposed || _guestTeardownRequested)
			{
				return null;
			}
			if (_idleOrchestrators.Count > 0)
			{
				return _idleOrchestrators.Pop();
			}
		}

		GuestOrchestratorWorker worker;
		try
		{
			worker = new GuestOrchestratorWorker(this);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Failed to create a guest orchestrator worker ({ex.GetType().Name}); " +
				"falling back to a per-dispatch thread.");
			return null;
		}

		lock (_orchestratorGate)
		{
			if (_orchestratorsDisposed)
			{
				worker.Stop();
				return null;
			}
			_allOrchestrators.Add(worker);
		}
		return worker;
	}

	private void ReturnOrchestrator(GuestOrchestratorWorker worker)
	{
		lock (_orchestratorGate)
		{
			if (!_orchestratorsDisposed)
			{
				_idleOrchestrators.Push(worker);
				return;
			}
		}
		worker.Stop();
	}

	// Waits (up to timeoutMs) for every orchestrator worker to leave guest code.
	// Called by RequestGuestThreadTeardown once _guestTeardownRequested is set and
	// Pump has stopped dispatching, so the count only decreases. Returns true when
	// no worker is running guest code.
	private bool DrainOrchestratorRuns(int timeoutMs)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (Volatile.Read(ref _activeOrchestratorRuns) > 0)
		{
			if (Environment.TickCount64 >= deadline)
			{
				return false;
			}
			Thread.Sleep(1);
		}
		return true;
	}

	private void DisposeGuestOrchestrators()
	{
		GuestOrchestratorWorker[] workers;
		lock (_orchestratorGate)
		{
			if (_orchestratorsDisposed)
			{
				return;
			}
			_orchestratorsDisposed = true;
			workers = _allOrchestrators.ToArray();
			_allOrchestrators.Clear();
			_idleOrchestrators.Clear();
		}
		foreach (var worker in workers)
		{
			worker.Stop();
		}
	}

	// A pooled managed thread that runs RunGuestThread work items. It parks on an
	// AutoResetEvent between dispatches and returns itself to the idle pool after
	// each run. Dispatch is fire-and-forget.
	private sealed class GuestOrchestratorWorker
	{
		private readonly DirectExecutionBackend _backend;
		private readonly AutoResetEvent _workAvailable = new(false);
		private readonly Thread _thread;
		private Action? _work;
		private volatile bool _stopping;

		public GuestOrchestratorWorker(DirectExecutionBackend backend)
		{
			_backend = backend;
			_thread = new Thread(ThreadMain)
			{
				IsBackground = true,
				Name = "SharpEmu-GuestOrchestrator",
			};
			_thread.Start();
		}

		// At most one pending work item: the worker is not back in the idle pool
		// (so cannot be re-rented) until it finishes the current run, so a second
		// Post cannot race the first. Ordering _work-before-Set plus the AutoResetEvent
		// latch means a Set that lands before the worker parks is never lost.
		public void Post(Action work)
		{
			_work = work;
			_workAvailable.Set();
		}

		public void Stop()
		{
			_stopping = true;
			try
			{
				_workAvailable.Set();
			}
			catch (ObjectDisposedException)
			{
			}
		}

		private void ThreadMain()
		{
			try
			{
				while (true)
				{
					_workAvailable.WaitOne();
					if (_stopping)
					{
						return;
					}

					var work = _work;
					_work = null;
					if (work is null)
					{
						continue;
					}

					try
					{
						work();
					}
					catch (Exception ex)
					{
						// RunGuestThread records faults on the guest thread state and
						// unwinds cleanly; a throw escaping it would tear down the whole
						// process on the historical per-dispatch thread. Keep the pool
						// alive and name the cause instead.
						Console.Error.WriteLine(
							$"[LOADER][ERROR] Guest orchestrator worker run faulted: {ex.GetType().Name}: {ex.Message}");
					}
					finally
					{
						_backend.ReturnOrchestrator(this);
					}
				}
			}
			finally
			{
				_workAvailable.Dispose();
			}
		}
	}
}
