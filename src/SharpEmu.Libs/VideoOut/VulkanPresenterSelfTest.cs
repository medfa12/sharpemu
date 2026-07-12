// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Drives the Vulkan presenter with synthetic frames so the swapchain path can
/// be verified on a host without a game image.
/// </summary>
public static class VulkanPresenterSelfTest
{
    private const int FramePacingMilliseconds = 15;
    private const int ShutdownTimeoutMilliseconds = 5000;

    public static int Run(uint width, uint height, int durationSeconds)
    {
        if (width == 0 || height == 0 || durationSeconds <= 0)
        {
            Console.Error.WriteLine("[PRESENT-TEST][ERROR] Invalid dimensions or duration.");
            return 1;
        }

        Console.Error.WriteLine(
            $"[PRESENT-TEST][INFO] Starting presenter self-test: {width}x{height} for {durationSeconds}s");

        var stopwatch = Stopwatch.StartNew();
        var submitted = 0;
        var stoppedEarly = false;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            if (VulkanVideoPresenter.HasStopped)
            {
                stoppedEarly = true;
                break;
            }

            VulkanVideoPresenter.Submit(GenerateTestFrame(width, height, submitted), width, height);
            submitted++;
            Thread.Sleep(FramePacingMilliseconds);
        }

        var presented = VulkanVideoPresenter.PresentedFrameTotal;
        var deviceName = VulkanVideoPresenter.SelectedDeviceName;
        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

        VulkanVideoPresenter.RequestClose();
        var shutdownWait = Stopwatch.StartNew();
        while (!VulkanVideoPresenter.HasStopped &&
               shutdownWait.ElapsedMilliseconds < ShutdownTimeoutMilliseconds)
        {
            Thread.Sleep(50);
        }

        var cleanShutdown = VulkanVideoPresenter.HasStopped;
        Console.Error.WriteLine(
            $"[PRESENT-TEST][INFO] device={deviceName ?? "<none>"} submitted={submitted} " +
            $"presented={presented} fps={presented / elapsedSeconds:F1} " +
            $"clean_shutdown={cleanShutdown}");

        string verdict;
        int exitCode;
        if (stoppedEarly)
        {
            verdict = "FAIL: presenter stopped before the test finished";
            exitCode = 1;
        }
        else if (presented == 0)
        {
            verdict = "FAIL: no frames were presented";
            exitCode = 1;
        }
        else
        {
            verdict = cleanShutdown ? "PASS" : "PASS (unclean shutdown)";
            exitCode = 0;
        }

        Console.Error.WriteLine($"[PRESENT-TEST][{(exitCode == 0 ? "PASS" : "FAIL")}] {verdict}");
        WriteReportFile(deviceName, submitted, presented, elapsedSeconds, cleanShutdown, verdict, exitCode);
        return exitCode;
    }

    /// <summary>
    /// Writes the outcome to SHARPEMU_PRESENT_TEST_REPORT when set, so
    /// automated harnesses get a result even if console streams are broken
    /// (e.g. a detached scheduled-task session).
    /// </summary>
    private static void WriteReportFile(
        string? deviceName,
        int submitted,
        long presented,
        double elapsedSeconds,
        bool cleanShutdown,
        string verdict,
        int exitCode)
    {
        var reportPath = Environment.GetEnvironmentVariable("SHARPEMU_PRESENT_TEST_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        try
        {
            File.WriteAllLines(reportPath,
            [
                $"device={deviceName ?? "<none>"}",
                $"submitted={submitted}",
                $"presented={presented}",
                $"fps={presented / elapsedSeconds:F1}",
                $"clean_shutdown={cleanShutdown}",
                $"verdict={verdict}",
                $"exit={exitCode}",
            ]);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PRESENT-TEST][WARN] Could not write report file: {exception.Message}");
        }
    }

    /// <summary>
    /// Produces a BGRA gradient with a moving white scanline so presented
    /// output is visibly animated and distinguishable from a stuck frame.
    /// </summary>
    internal static byte[] GenerateTestFrame(uint width, uint height, int frameIndex)
    {
        var frame = new byte[checked((int)(width * height * 4))];
        var scanline = (uint)(frameIndex % (int)height);
        var red = (byte)((frameIndex * 2) % 256);
        for (uint y = 0; y < height; y++)
        {
            var green = (byte)(y * 255 / height);
            var isScanline = y == scanline;
            var rowOffset = y * width * 4;
            for (uint x = 0; x < width; x++)
            {
                var offset = rowOffset + x * 4;
                if (isScanline)
                {
                    frame[offset] = 255;
                    frame[offset + 1] = 255;
                    frame[offset + 2] = 255;
                }
                else
                {
                    frame[offset] = (byte)(x * 255 / width);
                    frame[offset + 1] = green;
                    frame[offset + 2] = red;
                }

                frame[offset + 3] = 255;
            }
        }

        return frame;
    }
}
