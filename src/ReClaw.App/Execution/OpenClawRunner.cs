using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;

namespace ReClaw.App.Execution;

public sealed class OpenClawRunner
{
    private readonly ProcessRunner runner;

    public OpenClawRunner(ProcessRunner runner)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<ActionResult> RunAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string[] args,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        IDictionary<string, string>? environmentOverrides = null)
    {
        OpenClawCommand command;
        ProcessRunSpec spec;
        string commandLine;
        try
        {
            (command, spec, commandLine) = BuildRunSpec(context, args, environmentOverrides);
        }
        catch (InvalidOperationException)
        {
            return new ActionResult(false, Error: "openclaw CLI not found. Set OPENCLAW_ENTRY or OPENCLAW_REPO.");
        }

        var gatewayTimeoutSeconds = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_COMMAND_TIMEOUT_SECONDS");
        if (!string.IsNullOrWhiteSpace(gatewayTimeoutSeconds)
            && int.TryParse(gatewayTimeoutSeconds, out var gatewaySeconds)
            && gatewaySeconds > 0
            && actionId.StartsWith("gateway-", StringComparison.OrdinalIgnoreCase)
            && args.Length > 0
            && (string.Equals(args[0], "gateway", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "logs", StringComparison.OrdinalIgnoreCase)))
        {
            spec = spec with { Timeout = TimeSpan.FromSeconds(gatewaySeconds) };
        }

        if (actionId == "gateway-logs")
        {
            var timeoutSeconds = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_LOGS_TIMEOUT_SECONDS");
            if (int.TryParse(timeoutSeconds, out var seconds) && seconds > 0)
            {
                spec = spec with { Timeout = TimeSpan.FromSeconds(seconds) };
            }
        }

        if (args.Length > 0
            && string.Equals(args[0], "doctor", StringComparison.OrdinalIgnoreCase)
            && !args.Any(arg => string.Equals(arg, "--interactive", StringComparison.OrdinalIgnoreCase))
            && spec.Timeout is null)
        {
            var timeoutSeconds = Environment.GetEnvironmentVariable("RECLAW_DOCTOR_TIMEOUT_SECONDS");
            if (!int.TryParse(timeoutSeconds, out var seconds) || seconds <= 0)
            {
                seconds = 120;
            }

            spec = spec with { Timeout = TimeSpan.FromSeconds(seconds) };
        }

        var commandDetail = command.WorkingDirectory == null
            ? commandLine
            : $"{commandLine} (cwd: {command.WorkingDirectory})";
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Command", commandDetail));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Executable", spec.FileName));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Arguments", string.Join(' ', spec.Arguments)));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "WorkingDir", spec.WorkingDirectory ?? "(null)"));
        var result = await runner.RunAsync(actionId, correlationId, spec, events, cancellationToken).ConfigureAwait(false);

        var summary = new OpenClawCommandSummary(
            commandLine,
            result.ExitCode,
            result.TimedOut,
            result.StdOut,
            result.StdErr,
            result.StdOutLineCount,
            result.StdErrLineCount,
            result.OutputTruncated);

        if (ContainsInteractivePrompt(summary))
        {
            return new ActionResult(false, Output: summary, Error: "openclaw interactive prompt detected; automatic flows must be non-interactive. Use Open Terminal or pass --non-interactive --yes.", ExitCode: result.ExitCode);
        }

        if (result.TimedOut)
        {
            return new ActionResult(false, Output: summary, Error: "openclaw command timed out.", ExitCode: result.ExitCode);
        }

        return result.ExitCode == 0
            ? new ActionResult(true, Output: summary, ExitCode: result.ExitCode)
            : new ActionResult(false, Output: summary, Error: $"openclaw exited with code {result.ExitCode}", ExitCode: result.ExitCode);
    }

    internal static (OpenClawCommand Command, ProcessRunSpec Spec, string CommandLine) BuildRunSpec(ActionContext context, string[] args, IDictionary<string, string>? environmentOverrides = null)
    {
        var command = OpenClawLocator.Resolve(context);
        if (command is null)
        {
            throw new InvalidOperationException("openclaw CLI not found.");
        }

        var finalArgs = command.BaseArgs.Concat(args).ToArray();
        var commandLine = string.Join(' ', new[] { command.FileName }.Concat(finalArgs));
        var spec = new ProcessRunSpec(command.FileName, finalArgs, command.WorkingDirectory, environmentOverrides);
        return (command, spec, commandLine);
    }

    private static bool ContainsInteractivePrompt(OpenClawCommandSummary summary)
    {
        var lines = summary.StdOut.Concat(summary.StdErr);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Contains("Start gateway service now", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (line.Contains("Yes / No", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
