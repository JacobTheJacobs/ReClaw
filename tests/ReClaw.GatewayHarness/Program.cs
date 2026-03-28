using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using ReClaw.App.Platform;
using ReClaw.App.Validation;

namespace ReClaw.GatewayHarness;

public static class Program
{
    private sealed class Capture
    {
        public readonly List<ActionEvent> Events = new();
        public readonly List<string> LogLines = new();

        public void Handle(ActionEvent evt)
        {
            Events.Add(evt);
            if (evt is LogReceived log)
            {
                LogLines.Add($"{log.Timestamp:HH:mm:ss} {(log.IsError ? "ERR" : "OUT")}: {log.Line}");
            }
            else if (evt is StatusChanged status)
            {
                LogLines.Add($"{status.Timestamp:HH:mm:ss} {status.Status}: {status.Detail}");
            }
        }
    }

    public static async Task<int> Main(string[] args)
    {
        var openClawEntry = Environment.GetEnvironmentVariable("OPENCLAW_ENTRY");
        if (string.IsNullOrWhiteSpace(openClawEntry))
        {
            var defaultEntry = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "openclaw", "openclaw.mjs");
            Environment.SetEnvironmentVariable("OPENCLAW_ENTRY", defaultEntry);
        }

        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_COMMAND_TIMEOUT_SECONDS", "60");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_LOGS_TIMEOUT_SECONDS", "3");
        Environment.SetEnvironmentVariable("RECLAW_OPENCLAW_TERMINAL_TIMEOUT_SECONDS", "5");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_REPAIR_SKIP_SNAPSHOT", "1");
        Environment.SetEnvironmentVariable("RECLAW_OPENCLAW_TERMINAL_HEADLESS", "1");

        var capture = new Capture();
        var context = PathDefaults.CreateDefaultContext();
        var (_, _, executor) = DefaultActionRegistry.Create();
        var progress = new Progress<ActionEvent>(capture.Handle);

        var actions = new (string Id, int TimeoutSeconds, object Input)[]
        {
            ("gateway-start", 90, new EmptyInput()),
            ("gateway-status", 15, new EmptyInput()),
            ("gateway-logs", 5, new EmptyInput()),
            ("gateway-stop", 15, new EmptyInput()),
            ("openclaw-terminal", 5, new EmptyInput()),
            ("openclaw-cleanup-related", 10, new OpenClawCleanupInput(Apply: false, Confirm: false))
        };

        foreach (var (id, timeoutSeconds, input) in actions)
        {
            capture.Events.Clear();
            capture.LogLines.Clear();
            Console.WriteLine($"Starting {id} (timeout {timeoutSeconds}s)...");
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var result = await executor.ExecuteAsync(id, input, context, progress, cts.Token).ConfigureAwait(false);

            var commandStatus = capture.Events.OfType<StatusChanged>().FirstOrDefault(e => e.Status == "Command");
            var executableStatus = capture.Events.OfType<StatusChanged>().FirstOrDefault(e => e.Status == "Executable");
            var argsStatus = capture.Events.OfType<StatusChanged>().FirstOrDefault(e => e.Status == "Arguments");
            var workingDirStatus = capture.Events.OfType<StatusChanged>().FirstOrDefault(e => e.Status == "WorkingDir");

            Console.WriteLine($"=== {id} ===");
            Console.WriteLine($"CommandStatus: {commandStatus?.Detail}");
            Console.WriteLine($"Executable: {executableStatus?.Detail}");
            Console.WriteLine($"Args: {argsStatus?.Detail}");
            Console.WriteLine($"WorkingDir: {workingDirStatus?.Detail}");
            Console.WriteLine($"ExitCode: {result?.ExitCode?.ToString() ?? "(null)"}");
            Console.WriteLine($"Status: {(result?.Success == true ? "Succeeded" : "Failed")}");
            Console.WriteLine($"OutputType: {result?.Output?.GetType().FullName ?? "(null)"}");
            if (result?.Output is GatewayRepairSummary repair)
            {
                Console.WriteLine($"Outcome: {repair.Outcome}");
                Console.WriteLine($"Detected: runtime={repair.Detection.RuntimeVersion ?? "(unknown)"} config={repair.Detection.ConfigVersion ?? "(unknown)"} serviceExists={repair.Detection.GatewayServiceExists} active={repair.Detection.GatewayActive}");
                if (repair.Inventory is { } inventory)
                {
                    Console.WriteLine($"Inventory: activeRuntime={inventory.ActiveRuntime?.ExecutablePath ?? "(none)"} config={inventory.Config?.ConfigPath ?? "(none)"} services={inventory.Services.Count} artifacts={inventory.Artifacts.Count}");
                }
                if (repair.Steps.Count > 0)
                {
                    Console.WriteLine("Repair steps:");
                    foreach (var step in repair.Steps)
                    {
                        var detail = string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : $" ({step.Detail})";
                        Console.WriteLine($"- {step.Step}: {step.Status}{detail}");
                    }
                }
                if (repair.Attempts.Count > 0)
                {
                    Console.WriteLine("Repair attempts:");
                    foreach (var attempt in repair.Attempts)
                    {
                        Console.WriteLine($"- {attempt.StepId}: {(attempt.Succeeded ? "ok" : "fail")} ({attempt.Summary})");
                    }
                }
                if (repair.SuggestedActions.Count > 0)
                {
                    Console.WriteLine("Suggested actions:");
                    foreach (var suggestion in repair.SuggestedActions)
                    {
                        Console.WriteLine($"- {suggestion}");
                    }
                }
                if (repair.Notes.Count > 0)
                {
                    Console.WriteLine("Notes:");
                    foreach (var note in repair.Notes)
                    {
                        Console.WriteLine($"- {note}");
                    }
                }
            }
            if (result?.Output is OpenClawCleanupSummary cleanup)
            {
                Console.WriteLine($"Cleanup: applied={cleanup.Applied} candidates={cleanup.Candidates.Count} removed={cleanup.Removed.Count}");
            }
            if (result?.Warnings is { Count: > 0 })
            {
                Console.WriteLine("Warnings:");
                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"- {warning.Code}: {warning.Message}");
                }
            }
            Console.WriteLine("Logs:");
            foreach (var line in capture.LogLines)
            {
                Console.WriteLine(line);
            }
            Console.WriteLine();
        }

        return 0;
    }

}
