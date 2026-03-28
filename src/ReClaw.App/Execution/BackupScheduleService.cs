using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;

namespace ReClaw.App.Execution;

public sealed class BackupScheduleService
{
    private const string ScheduleStoreFile = "backup-schedules.json";
    private const string ScheduleFolderName = "schedules";

    private readonly ProcessRunner runner;

    public BackupScheduleService(ProcessRunner runner)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<BackupScheduleSummary> CreateAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupScheduleInput input,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        if (input.Mode == BackupScheduleMode.OsNative && input.Kind == BackupScheduleKind.Cron)
        {
            throw new InvalidOperationException("Cron schedules are only supported for gateway mode.");
        }

        var store = new ScheduleStore(GetStorePath(context));
        var schedules = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        var scheduleId = $"reclaw-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var expression = BuildCronExpression(input.Kind, input.Expression, input.AtTime, input.DayOfWeek, input.DayOfMonth);
        var command = BuildBackupCommand(context, input);
        var schedule = new BackupSchedule(
            scheduleId,
            input.Mode,
            input.Kind,
            expression,
            input.RetentionKeepLast,
            input.RetentionOlderThan,
            input.VerifyAfter,
            input.IncludeWorkspace,
            input.OnlyConfig,
            command.CommandLine,
            DateTimeOffset.UtcNow);

        var applied = input.Mode switch
        {
            BackupScheduleMode.Gateway => await ApplyGatewayScheduleAsync(actionId, correlationId, context, schedule, input, events, cancellationToken).ConfigureAwait(false),
            BackupScheduleMode.OsNative => await ApplyOsScheduleAsync(actionId, correlationId, context, schedule, input, events, cancellationToken).ConfigureAwait(false),
            _ => false
        };

        if (!applied)
        {
            throw new InvalidOperationException("Failed to apply backup schedule.");
        }

        schedules.RemoveAll(existing => string.Equals(existing.Id, schedule.Id, StringComparison.OrdinalIgnoreCase));
        schedules.Add(schedule);
        await store.SaveAsync(schedules, cancellationToken).ConfigureAwait(false);

        return new BackupScheduleSummary(schedules, true, "Schedule created.");
    }

    public async Task<BackupScheduleSummary> ListAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var store = new ScheduleStore(GetStorePath(context));
        var schedules = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new BackupScheduleSummary(schedules, false, null);
    }

    public async Task<BackupScheduleSummary> RemoveAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string? scheduleId,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
        {
            throw new InvalidOperationException("Schedule id is required.");
        }

        var store = new ScheduleStore(GetStorePath(context));
        var schedules = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var target = schedules.FirstOrDefault(schedule => string.Equals(schedule.Id, scheduleId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            throw new InvalidOperationException($"Schedule '{scheduleId}' not found.");
        }

        var removed = target.Mode switch
        {
            BackupScheduleMode.Gateway => await RemoveGatewayScheduleAsync(actionId, correlationId, context, target, events, cancellationToken).ConfigureAwait(false),
            BackupScheduleMode.OsNative => await RemoveOsScheduleAsync(actionId, correlationId, context, target, events, cancellationToken).ConfigureAwait(false),
            _ => false
        };

        if (!removed)
        {
            throw new InvalidOperationException("Failed to remove backup schedule.");
        }

        schedules.RemoveAll(schedule => string.Equals(schedule.Id, scheduleId, StringComparison.OrdinalIgnoreCase));
        await store.SaveAsync(schedules, cancellationToken).ConfigureAwait(false);
        return new BackupScheduleSummary(schedules, true, "Schedule removed.");
    }

    private static string GetStorePath(ActionContext context)
    {
        Directory.CreateDirectory(context.ConfigDirectory);
        return Path.Combine(context.ConfigDirectory, ScheduleStoreFile);
    }

    private static string GetScheduleFolder(ActionContext context)
    {
        var folder = Path.Combine(context.ConfigDirectory, ScheduleFolderName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal static string BuildCronExpression(
        BackupScheduleKind kind,
        string? expression,
        string? atTime,
        string? dayOfWeek,
        int? dayOfMonth)
    {
        if (kind == BackupScheduleKind.Cron)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new InvalidOperationException("Cron expression is required for cron schedules.");
            }

            return expression.Trim();
        }

        var (hour, minute) = ParseTime(atTime);
        switch (kind)
        {
            case BackupScheduleKind.Daily:
                return $"{minute} {hour} * * *";
            case BackupScheduleKind.Weekly:
                var cronDow = MapDayOfWeek(dayOfWeek);
                return $"{minute} {hour} * * {cronDow}";
            case BackupScheduleKind.Monthly:
                var dom = dayOfMonth.GetValueOrDefault(1);
                if (dom < 1 || dom > 28)
                {
                    throw new InvalidOperationException("DayOfMonth must be between 1 and 28.");
                }
                return $"{minute} {hour} {dom} * *";
            default:
                throw new InvalidOperationException("Unsupported schedule kind.");
        }
    }

    internal static (string FileName, string CommandLine, string? WorkingDirectory, IReadOnlyList<string> Arguments) BuildBackupCommand(
        ActionContext context,
        BackupScheduleInput input)
    {
        var command = OpenClawLocator.Resolve(context)
            ?? throw new InvalidOperationException("openclaw CLI not found.");

        var args = new List<string>();
        args.AddRange(command.BaseArgs);
        args.AddRange(new[] { "backup", "create" });
        if (input.VerifyAfter)
        {
            args.Add("--verify");
        }

        if (input.OnlyConfig)
        {
            args.Add("--only-config");
        }
        else if (!input.IncludeWorkspace)
        {
            args.Add("--no-include-workspace");
        }

        args.Add("--output");
        args.Add(context.BackupDirectory);

        var commandLine = BuildCommandLine(command.FileName, args);
        return (command.FileName, commandLine, command.WorkingDirectory, args);
    }

    private async Task<bool> ApplyGatewayScheduleAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupSchedule schedule,
        BackupScheduleInput input,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var openClawRunner = new OpenClawRunner(runner);
        var args = new List<string>
        {
            "cron",
            "add",
            "--id",
            schedule.Id,
            "--schedule",
            schedule.Expression,
            "--"
        };

        args.Add("backup");
        args.Add("create");
        if (input.VerifyAfter)
        {
            args.Add("--verify");
        }
        if (input.OnlyConfig)
        {
            args.Add("--only-config");
        }
        else if (!input.IncludeWorkspace)
        {
            args.Add("--no-include-workspace");
        }
        args.Add("--output");
        args.Add(context.BackupDirectory);

        var result = await openClawRunner.RunAsync(actionId, correlationId, context, args.ToArray(), events, cancellationToken)
            .ConfigureAwait(false);
        return result.Success;
    }

    private async Task<bool> RemoveGatewayScheduleAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupSchedule schedule,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var openClawRunner = new OpenClawRunner(runner);
        var args = new[] { "cron", "remove", schedule.Id };
        var result = await openClawRunner.RunAsync(actionId, correlationId, context, args, events, cancellationToken)
            .ConfigureAwait(false);
        return result.Success;
    }

    private async Task<bool> ApplyOsScheduleAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupSchedule schedule,
        BackupScheduleInput input,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var scheduleFolder = GetScheduleFolder(context);
        var script = await WriteScheduleScriptAsync(context, scheduleFolder, schedule, input, cancellationToken).ConfigureAwait(false);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var (time, dayOfWeek, dayOfMonth) = BuildOsScheduleParts(input.Kind, input.AtTime, input.DayOfWeek, input.DayOfMonth);
            var taskName = BuildTaskName(schedule);
            var args = new List<string>
            {
                "/Create",
                "/TN",
                taskName,
                "/TR",
                QuoteArgument(script),
                "/SC",
                input.Kind switch
                {
                    BackupScheduleKind.Daily => "DAILY",
                    BackupScheduleKind.Weekly => "WEEKLY",
                    BackupScheduleKind.Monthly => "MONTHLY",
                    _ => "DAILY"
                },
                "/ST",
                time,
                "/F"
            };

            if (input.Kind == BackupScheduleKind.Weekly && !string.IsNullOrWhiteSpace(dayOfWeek))
            {
                args.Add("/D");
                args.Add(dayOfWeek);
            }

            if (input.Kind == BackupScheduleKind.Monthly && !string.IsNullOrWhiteSpace(dayOfMonth))
            {
                args.Add("/D");
                args.Add(dayOfMonth);
            }

            var result = await runner.RunAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("schtasks", args),
                events,
                cancellationToken).ConfigureAwait(false);

            schedule = schedule with { ProviderId = taskName };
            return result.ExitCode == 0;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var label = BuildTaskName(schedule);
            var plistPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{label}.plist");
            Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
            var plist = BuildLaunchAgentPlist(label, script, input);
            await File.WriteAllTextAsync(plistPath, plist, cancellationToken).ConfigureAwait(false);

            var result = await runner.RunAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("launchctl", new[] { "load", plistPath }),
                events,
                cancellationToken).ConfigureAwait(false);

            schedule = schedule with { ProviderId = label };
            return result.ExitCode == 0;
        }

        var (unitName, timerPath) = await WriteSystemdUnitsAsync(context, schedule, input, script, cancellationToken).ConfigureAwait(false);
        var reload = await runner.RunAsync(actionId, correlationId, new ProcessRunSpec("systemctl", new[] { "--user", "daemon-reload" }), events, cancellationToken)
            .ConfigureAwait(false);
        if (reload.ExitCode != 0) return false;
        var enable = await runner.RunAsync(actionId, correlationId, new ProcessRunSpec("systemctl", new[] { "--user", "enable", "--now", unitName + ".timer" }), events, cancellationToken)
            .ConfigureAwait(false);
        schedule = schedule with { ProviderId = unitName };
        return enable.ExitCode == 0;
    }

    private async Task<bool> RemoveOsScheduleAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupSchedule schedule,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var taskName = schedule.ProviderId ?? BuildTaskName(schedule);
            var result = await runner.RunAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("schtasks", new[] { "/Delete", "/TN", taskName, "/F" }),
                events,
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var label = schedule.ProviderId ?? BuildTaskName(schedule);
            var plistPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{label}.plist");
            var unload = await runner.RunAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("launchctl", new[] { "unload", plistPath }),
                events,
                cancellationToken).ConfigureAwait(false);
            TryDelete(plistPath);
            return unload.ExitCode == 0;
        }

        var unitName = schedule.ProviderId ?? BuildTaskName(schedule);
        var stop = await runner.RunAsync(
            actionId,
            correlationId,
            new ProcessRunSpec("systemctl", new[] { "--user", "disable", "--now", unitName + ".timer" }),
            events,
            cancellationToken).ConfigureAwait(false);
        TryDelete(Path.Combine(GetSystemdUserPath(), unitName + ".timer"));
        TryDelete(Path.Combine(GetSystemdUserPath(), unitName + ".service"));
        return stop.ExitCode == 0;
    }

    private static async Task<string> WriteScheduleScriptAsync(
        ActionContext context,
        string scheduleFolder,
        BackupSchedule schedule,
        BackupScheduleInput input,
        CancellationToken cancellationToken)
    {
        var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "sh";
        var scriptPath = Path.Combine(scheduleFolder, $"backup-{schedule.Id}.{extension}");
        var command = BuildBackupCommand(context, input);
        var pruneCommand = $"reclaw backup prune --keep-last {input.RetentionKeepLast} --older-than {input.RetentionOlderThan} --apply";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var content = new StringBuilder();
            content.AppendLine("@echo off");
            content.AppendLine(command.CommandLine);
            content.AppendLine(pruneCommand);
            await File.WriteAllTextAsync(scriptPath, content.ToString(), cancellationToken).ConfigureAwait(false);
            return scriptPath;
        }

        var unixContent = new StringBuilder();
        unixContent.AppendLine("#!/usr/bin/env bash");
        unixContent.AppendLine("set -euo pipefail");
        unixContent.AppendLine(command.CommandLine);
        unixContent.AppendLine(pruneCommand);
        await File.WriteAllTextAsync(scriptPath, unixContent.ToString(), cancellationToken).ConfigureAwait(false);

        try
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // best effort
        }

        return scriptPath;
    }

    private static (string Time, string? DayOfWeek, string? DayOfMonth) BuildOsScheduleParts(
        BackupScheduleKind kind,
        string? atTime,
        string? dayOfWeek,
        int? dayOfMonth)
    {
        var (hour, minute) = ParseTime(atTime);
        var time = $"{hour:D2}:{minute:D2}";

        return kind switch
        {
            BackupScheduleKind.Weekly => (time, MapWindowsDayOfWeek(dayOfWeek), null),
            BackupScheduleKind.Monthly => (time, null, dayOfMonth.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)),
            _ => (time, null, null)
        };
    }

    private static string BuildTaskName(BackupSchedule schedule) => $"ReClaw.Backup.{schedule.Id}";

    private static string BuildLaunchAgentPlist(string label, string scriptPath, BackupScheduleInput input)
    {
        var (hour, minute) = ParseTime(input.AtTime);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        sb.AppendLine("<plist version=\"1.0\">");
        sb.AppendLine("<dict>");
        sb.AppendLine($"  <key>Label</key><string>{label}</string>");
        sb.AppendLine("  <key>ProgramArguments</key>");
        sb.AppendLine("  <array>");
        sb.AppendLine($"    <string>{scriptPath}</string>");
        sb.AppendLine("  </array>");
        sb.AppendLine("  <key>StartCalendarInterval</key>");
        sb.AppendLine("  <dict>");
        sb.AppendLine($"    <key>Hour</key><integer>{hour}</integer>");
        sb.AppendLine($"    <key>Minute</key><integer>{minute}</integer>");
        if (input.Kind == BackupScheduleKind.Weekly)
        {
            var weekday = MapLaunchdDayOfWeek(input.DayOfWeek);
            sb.AppendLine($"    <key>Weekday</key><integer>{weekday}</integer>");
        }
        if (input.Kind == BackupScheduleKind.Monthly)
        {
            var dom = input.DayOfMonth.GetValueOrDefault(1);
            sb.AppendLine($"    <key>Day</key><integer>{dom}</integer>");
        }
        sb.AppendLine("  </dict>");
        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        return sb.ToString();
    }

    private static async Task<(string UnitName, string TimerPath)> WriteSystemdUnitsAsync(
        ActionContext context,
        BackupSchedule schedule,
        BackupScheduleInput input,
        string scriptPath,
        CancellationToken cancellationToken)
    {
        var unitName = BuildTaskName(schedule).ToLowerInvariant().Replace('.', '-');
        var systemdPath = GetSystemdUserPath();
        Directory.CreateDirectory(systemdPath);

        var servicePath = Path.Combine(systemdPath, unitName + ".service");
        var timerPath = Path.Combine(systemdPath, unitName + ".timer");

        var (hour, minute) = ParseTime(input.AtTime);
        var onCalendar = input.Kind switch
        {
            BackupScheduleKind.Weekly => $"{MapSystemdDayOfWeek(input.DayOfWeek)} *-*-* {hour:D2}:{minute:D2}:00",
            BackupScheduleKind.Monthly => $"*-*-{input.DayOfMonth.GetValueOrDefault(1):D2} {hour:D2}:{minute:D2}:00",
            _ => $"*-*-* {hour:D2}:{minute:D2}:00"
        };

        var service = new StringBuilder();
        service.AppendLine("[Unit]");
        service.AppendLine("Description=ReClaw backup schedule");
        service.AppendLine("[Service]");
        service.AppendLine("Type=oneshot");
        service.AppendLine($"ExecStart={scriptPath}");

        var timer = new StringBuilder();
        timer.AppendLine("[Unit]");
        timer.AppendLine("Description=ReClaw backup schedule timer");
        timer.AppendLine("[Timer]");
        timer.AppendLine($"OnCalendar={onCalendar}");
        timer.AppendLine("Persistent=true");
        timer.AppendLine("[Install]");
        timer.AppendLine("WantedBy=timers.target");

        await File.WriteAllTextAsync(servicePath, service.ToString(), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(timerPath, timer.ToString(), cancellationToken).ConfigureAwait(false);
        return (unitName, timerPath);
    }

    private static string GetSystemdUserPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "systemd", "user");
    }

    private static (int Hour, int Minute) ParseTime(string? atTime)
    {
        if (string.IsNullOrWhiteSpace(atTime))
        {
            return (2, 0);
        }

        var parts = atTime.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            hour = Math.Clamp(hour, 0, 23);
            minute = Math.Clamp(minute, 0, 59);
            return (hour, minute);
        }

        return (2, 0);
    }

    private static int MapDayOfWeek(string? dayOfWeek)
    {
        var normalized = (dayOfWeek ?? "Sun").Trim().ToLowerInvariant();
        return normalized switch
        {
            "mon" or "monday" => 1,
            "tue" or "tuesday" => 2,
            "wed" or "wednesday" => 3,
            "thu" or "thursday" => 4,
            "fri" or "friday" => 5,
            "sat" or "saturday" => 6,
            _ => 0
        };
    }

    private static string MapWindowsDayOfWeek(string? dayOfWeek)
    {
        var normalized = (dayOfWeek ?? "Sun").Trim().ToLowerInvariant();
        return normalized switch
        {
            "mon" or "monday" => "MON",
            "tue" or "tuesday" => "TUE",
            "wed" or "wednesday" => "WED",
            "thu" or "thursday" => "THU",
            "fri" or "friday" => "FRI",
            "sat" or "saturday" => "SAT",
            _ => "SUN"
        };
    }

    private static int MapLaunchdDayOfWeek(string? dayOfWeek)
    {
        var normalized = (dayOfWeek ?? "Sun").Trim().ToLowerInvariant();
        return normalized switch
        {
            "mon" or "monday" => 2,
            "tue" or "tuesday" => 3,
            "wed" or "wednesday" => 4,
            "thu" or "thursday" => 5,
            "fri" or "friday" => 6,
            "sat" or "saturday" => 7,
            _ => 1
        };
    }

    private static string MapSystemdDayOfWeek(string? dayOfWeek)
    {
        var normalized = (dayOfWeek ?? "Sun").Trim().ToLowerInvariant();
        return normalized switch
        {
            "mon" or "monday" => "Mon",
            "tue" or "tuesday" => "Tue",
            "wed" or "wednesday" => "Wed",
            "thu" or "thursday" => "Thu",
            "fri" or "friday" => "Fri",
            "sat" or "saturday" => "Sat",
            _ => "Sun"
        };
    }

    private static string BuildCommandLine(string fileName, IEnumerable<string> args)
    {
        var joined = new StringBuilder();
        joined.Append(QuoteArgument(fileName));
        foreach (var arg in args)
        {
            joined.Append(' ');
            joined.Append(QuoteArgument(arg));
        }
        return joined.ToString();
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "\"\"";
        return value.Contains(' ') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class ScheduleStore
    {
        private readonly string path;
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public ScheduleStore(string path)
        {
            this.path = path;
        }

        public async Task<List<BackupSchedule>> LoadAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(path))
            {
                return new List<BackupSchedule>();
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var schedules = JsonSerializer.Deserialize<List<BackupSchedule>>(json, Options);
            return schedules ?? new List<BackupSchedule>();
        }

        public async Task SaveAsync(List<BackupSchedule> schedules, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(schedules, Options);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        }
    }
}
