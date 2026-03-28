using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;

namespace ReClaw.App.Execution;

public sealed record ProcessRunSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null);

public sealed record ProcessResult(
    int ExitCode,
    bool TimedOut,
    IReadOnlyList<string> StdOut,
    IReadOnlyList<string> StdErr,
    int StdOutLineCount,
    int StdErrLineCount,
    bool OutputTruncated);

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string actionId,
        Guid correlationId,
        ProcessRunSpec spec,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxCapturedLines = 200;
    private static readonly Regex AnsiRegex = new("\u001B\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public async Task<ProcessResult> RunAsync(
        string actionId,
        Guid correlationId,
        ProcessRunSpec spec,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory
        };

        foreach (var arg in spec.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (spec.Environment != null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start process.");
        }

        var capture = new ProcessCapture(MaxCapturedLines);
        var outputTask = ReadLinesAsync(process.StandardOutput, actionId, correlationId, events, capture, isError: false, cancellationToken);
        var errorTask = ReadLinesAsync(process.StandardError, actionId, correlationId, events, capture, isError: true, cancellationToken);

        using var timeoutCts = spec.Timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (spec.Timeout.HasValue)
        {
            timeoutCts!.CancelAfter(spec.Timeout.Value);
        }

        var waitToken = timeoutCts?.Token ?? cancellationToken;
        try
        {
            await process.WaitForExitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (spec.Timeout.HasValue && timeoutCts!.IsCancellationRequested)
        {
            KillProcess(process);
            await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(2000)).ConfigureAwait(false);
            return capture.Build(-1, TimedOut: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(2000)).ConfigureAwait(false);
            return capture.Build(-1, TimedOut: true);
        }

        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        return capture.Build(process.ExitCode, TimedOut: false);
    }

    private static async Task ReadLinesAsync(
        System.IO.StreamReader reader,
        string actionId,
        Guid correlationId,
        IProgress<ActionEvent> events,
        ProcessCapture capture,
        bool isError,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            var sanitized = SanitizeOutput(line);
            events.Report(new LogReceived(actionId, correlationId, DateTimeOffset.UtcNow, sanitized, isError));
            capture.Add(sanitized, isError);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {process.Id} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                killer?.WaitForExit(2000);
            }
            else
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private sealed class ProcessCapture
    {
        private readonly int maxLines;
        private readonly Queue<string> stdout = new();
        private readonly Queue<string> stderr = new();

        public int StdOutLineCount { get; private set; }
        public int StdErrLineCount { get; private set; }
        public bool OutputTruncated { get; private set; }

        public ProcessCapture(int maxLines)
        {
            this.maxLines = Math.Max(1, maxLines);
        }

        public void Add(string line, bool isError)
        {
            if (isError)
            {
                StdErrLineCount++;
                Enqueue(stderr, line);
            }
            else
            {
                StdOutLineCount++;
                Enqueue(stdout, line);
            }
        }

        public ProcessResult Build(int exitCode, bool TimedOut)
        {
            return new ProcessResult(
                exitCode,
                TimedOut,
                stdout.ToArray(),
                stderr.ToArray(),
                StdOutLineCount,
                StdErrLineCount,
                OutputTruncated);
        }

        private void Enqueue(Queue<string> queue, string line)
        {
            queue.Enqueue(line);
            if (queue.Count > maxLines)
            {
                queue.Dequeue();
                OutputTruncated = true;
            }
        }
    }

    internal static string SanitizeOutput(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : AnsiRegex.Replace(value, string.Empty);
    }
}
