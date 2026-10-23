using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CrushHour;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Crush Hour > Run Balance Baseline: runs <see cref="BalanceBaselineRunner"/> on a background thread so the
/// editor stays usable, shows progress in Unity's background-tasks bar (cancellable there), logs each run to the
/// Console and saves the Markdown table to Tools~/Verify/baseline.md.
/// Use this when Windows blocks the standalone program built by Tools~/Verify/verify.ps1.
/// </summary>
static class BalanceBaselineMenu
{
    static readonly int[] Seeds = { 11, 21, 33 };
    static readonly ConcurrentQueue<string> lines = new();
    static Task<string> task;
    static int progressId, done, total;
    static volatile bool cancel;

    [MenuItem("Tools/Crush Hour/Run Balance Baseline")]
    static void Run()
    {
        if (task != null && !task.IsCompleted)
        {
            Debug.LogWarning("Crush Hour: the balance baseline is already running (see the progress bar, bottom right).");
            return;
        }
        while (lines.TryDequeue(out _)) { }
        cancel = false;
        done = 0;
        total = BalanceBaselineRunner.RunCount(Seeds.Length);
        progressId = Progress.Start("Crush Hour balance baseline", $"{total} headless runs");
        Progress.RegisterCancelCallback(progressId, () =>
        {
            cancel = true;
            return true;
        });
        Debug.Log($"Crush Hour: balance baseline started ({total} runs, a few minutes). You can keep working; don't enter Play mode until it's done.");
        task = Task.Run(() => BalanceBaselineRunner.RunAll(Seeds, line =>
        {
            lines.Enqueue(line);
            Interlocked.Increment(ref done);
        }, () => cancel));
        EditorApplication.update += Poll;
    }

    static void Poll()
    {
        while (lines.TryDequeue(out var line)) Debug.Log("Crush Hour baseline: " + line);
        if (Progress.Exists(progressId)) Progress.Report(progressId, done / (float)total, $"{done}/{total} runs");
        if (!task.IsCompleted) return;

        EditorApplication.update -= Poll;
        if (task.IsFaulted)
        {
            Progress.Finish(progressId, Progress.Status.Failed);
            Debug.LogException(task.Exception);
            return;
        }
        if (cancel || task.Result == null)
        {
            Progress.Finish(progressId, Progress.Status.Canceled);
            Debug.Log("Crush Hour: balance baseline cancelled.");
            return;
        }
        string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools~", "Verify", "baseline.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, task.Result);
        Progress.Finish(progressId);
        Debug.Log($"Crush Hour: balance baseline done, saved to {path}\n\n{task.Result}");
    }
}
