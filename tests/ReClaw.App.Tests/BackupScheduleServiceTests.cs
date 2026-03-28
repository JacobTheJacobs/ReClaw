using System;
using System.IO;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using Xunit;

namespace ReClaw.App.Tests;

public sealed class BackupScheduleServiceTests
{
    [Fact]
    public void BuildCronExpression_Daily_Defaults()
    {
        var cron = BackupScheduleService.BuildCronExpression(
            BackupScheduleKind.Daily,
            expression: null,
            atTime: "03:30",
            dayOfWeek: null,
            dayOfMonth: null);

        Assert.Equal("30 3 * * *", cron);
    }

    [Fact]
    public void BuildCronExpression_Weekly_UsesDay()
    {
        var cron = BackupScheduleService.BuildCronExpression(
            BackupScheduleKind.Weekly,
            expression: null,
            atTime: "01:15",
            dayOfWeek: "Mon",
            dayOfMonth: null);

        Assert.Equal("15 1 * * 1", cron);
    }

    [Fact]
    public void BuildBackupCommand_OnlyConfig_UsesFlag()
    {
        var temp = Path.GetTempFileName();
        try
        {
            var context = new ActionContext(
                ConfigDirectory: Path.GetTempPath(),
                DataDirectory: Path.GetTempPath(),
                BackupDirectory: Path.GetTempPath(),
                LogsDirectory: Path.GetTempPath(),
                TempDirectory: Path.GetTempPath(),
                OpenClawHome: Path.GetTempPath(),
                OpenClawExecutable: temp,
                OpenClawEntry: null);

            var input = new BackupScheduleInput(
                Mode: BackupScheduleMode.Gateway,
                Kind: BackupScheduleKind.Daily,
                OnlyConfig: true,
                IncludeWorkspace: true);

            var command = BackupScheduleService.BuildBackupCommand(context, input);

            Assert.Contains("--only-config", command.Arguments);
            Assert.DoesNotContain("--no-include-workspace", command.Arguments);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public void BuildBackupCommand_NoWorkspace_UsesFlag()
    {
        var temp = Path.GetTempFileName();
        try
        {
            var context = new ActionContext(
                ConfigDirectory: Path.GetTempPath(),
                DataDirectory: Path.GetTempPath(),
                BackupDirectory: Path.GetTempPath(),
                LogsDirectory: Path.GetTempPath(),
                TempDirectory: Path.GetTempPath(),
                OpenClawHome: Path.GetTempPath(),
                OpenClawExecutable: temp,
                OpenClawEntry: null);

            var input = new BackupScheduleInput(
                Mode: BackupScheduleMode.Gateway,
                Kind: BackupScheduleKind.Daily,
                IncludeWorkspace: false);

            var command = BackupScheduleService.BuildBackupCommand(context, input);

            Assert.Contains("--no-include-workspace", command.Arguments);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
