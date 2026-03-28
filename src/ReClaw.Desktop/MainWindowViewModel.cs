using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using ReClaw.App.Platform;
using ReClaw.Core;

namespace ReClaw.Desktop;

public enum BackupLevel
{
    Full,
    OpenClawOnly,
    ConfigOnly
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly Func<string, object, CancellationToken, Task<ActionResult>> execute;
    private readonly ActionContext context;
    private readonly IProgress<ActionEvent> progress;
    private const int MaxLogLines = 2000;
    private readonly Queue<string> logLines = new();
    private readonly StringBuilder logBuffer = new();
    private bool logDirty;
    private CancellationTokenSource? logFlushCts;
    private const string SectionHome = "Home";
    private const string SectionBackups = "Backups";
    private const string SectionRecovery = "Recovery";
    private readonly ObservableCollection<ActionDescriptor> filteredActions = new();
    private CancellationTokenSource? currentActionCts;

    public IReadOnlyList<ActionDescriptor> Actions { get; }
    public ReadOnlyObservableCollection<ActionDescriptor> FilteredActions { get; }
    public IReadOnlyList<ResetMode> ResetModes { get; } = Enum.GetValues<ResetMode>();
    public IReadOnlyList<BackupScheduleMode> ScheduleModes { get; } = Enum.GetValues<BackupScheduleMode>();
    public IReadOnlyList<BackupScheduleKind> ScheduleKinds { get; } = Enum.GetValues<BackupScheduleKind>();
    public IReadOnlyList<BackupLevel> BackupLevels { get; } = Enum.GetValues<BackupLevel>();

    [ObservableProperty]
    private string title = "ReClaw";

    [ObservableProperty]
    private string selectedCategory = "all";

    [ObservableProperty]
    private string selectedSection = SectionHome;

    [ObservableProperty]
    private string actionSearchText = string.Empty;

    [ObservableProperty]
    private string actionsMetaText = "No actions available.";

    [ObservableProperty]
    private string guidanceHint = "Ready";

    [ObservableProperty] private int gatewayTabIndex;
    [ObservableProperty] private int backupsTabIndex;
    [ObservableProperty] private int recoveryTabIndex;

    [ObservableProperty] private bool showBackupAdvanced;
    [ObservableProperty] private bool showCompareAdvanced;
    [ObservableProperty] private bool showRestoreAdvanced;
    [ObservableProperty] private bool showScheduleAdvanced;
    [ObservableProperty] private bool showRecoveryAdvanced;
    [ObservableProperty] private bool showRebuildAdvanced;
    [ObservableProperty] private bool showRollbackAdvanced;
    [ObservableProperty] private bool actionInputsExpanded;
    [ObservableProperty] private bool lastResultExpanded;

    [ObservableProperty] private string? archivePath;
    [ObservableProperty] private string? destinationPath;
    [ObservableProperty] private string scope = "config";
    [ObservableProperty] private string? password;
    [ObservableProperty] private string? backupCreateOutputPath;
    [ObservableProperty] private string? backupCreateScope;
    [ObservableProperty] private bool backupCreateVerify = true;
    [ObservableProperty] private bool backupCreateNoEncrypt;
    [ObservableProperty] private BackupLevel selectedBackupLevel = BackupLevel.Full;
    [ObservableProperty] private string backupLevelDescription =
        "Full backup: state, config, credentials, sessions, and workspaces.";
    [ObservableProperty] private string backupExportScope = "config+creds+sessions";
    [ObservableProperty] private bool backupExportVerify = true;
    [ObservableProperty] private string? backupExportOutputPath;
    [ObservableProperty] private bool backupExportEncrypt = true;
    [ObservableProperty] private bool preview = true;
    [ObservableProperty] private bool safeReset;
    [ObservableProperty] private bool confirmReset;
    [ObservableProperty] private ResetMode resetMode = ResetMode.PreserveBackups;
    [ObservableProperty] private bool runDoctor = true;
    [ObservableProperty] private bool runFix = true;
    [ObservableProperty] private bool exportDiagnostics = true;
    [ObservableProperty] private string? rollbackSourcePath;
    [ObservableProperty] private bool rollbackPreview = true;
    [ObservableProperty] private bool confirmRollback;
    [ObservableProperty] private string? diffLeftArchive;
    [ObservableProperty] private string? diffRightArchive;
    [ObservableProperty] private bool diffRedactSecrets = true;
    [ObservableProperty] private string? diffSummary;
    [ObservableProperty] private BackupScheduleMode scheduleMode = BackupScheduleMode.Gateway;
    [ObservableProperty] private BackupScheduleKind scheduleKind = BackupScheduleKind.Daily;
    [ObservableProperty] private string? scheduleExpression;
    [ObservableProperty] private string? scheduleAtTime = "02:00";
    [ObservableProperty] private string? scheduleDayOfWeek;
    [ObservableProperty] private int? scheduleDayOfMonth;
    [ObservableProperty] private int scheduleKeepLast = 7;
    [ObservableProperty] private string scheduleOlderThan = "30d";
    [ObservableProperty] private bool scheduleVerifyAfter = true;
    [ObservableProperty] private bool scheduleIncludeWorkspace = true;
    [ObservableProperty] private bool scheduleOnlyConfig;
    [ObservableProperty] private string? scheduleId;
    [ObservableProperty] private string? scheduleSummary;
    [ObservableProperty] private string? githubToken;
    [ObservableProperty] private string? githubRepo;
    [ObservableProperty] private bool backupToGithub;
    [ObservableProperty] private bool revealGatewayToken;
    [ObservableProperty] private bool rebuildPreserveConfig = true;
    [ObservableProperty] private bool rebuildPreserveCreds = true;
    [ObservableProperty] private bool rebuildPreserveSessions = true;
    [ObservableProperty] private bool rebuildPreserveWorkspace = true;
    [ObservableProperty] private bool rebuildCleanInstall;
    [ObservableProperty] private bool rebuildConfirmDestructive;
    [ObservableProperty] private bool confirmCleanupApply;
    [ObservableProperty] private bool isConfirmDialogOpen;
    [ObservableProperty] private string? confirmDialogTitle;
    [ObservableProperty] private string? confirmDialogMessage;
    [ObservableProperty] private string? confirmDialogPhrase;
    [ObservableProperty] private string? confirmDialogInput;

    // Tray icon settings
    [ObservableProperty] private bool minimizeToTray = true;
    [ObservableProperty] private bool traySettingsOpen;
    public bool ForceQuit { get; set; }
    public List<string> TrayCommands { get; set; } = new();
    public ObservableCollection<TrayCommandItem> TrayCommandItems { get; } = new();

    [ObservableProperty] private string? activeActionLabel;
    [ObservableProperty] private string? resultSummary;
    [ObservableProperty] private string? impactSummary;
    [ObservableProperty] private string? warningsText;
    [ObservableProperty] private string? rollbackSnapshot;
    [ObservableProperty] private string? diagnosticsBundle;
    [ObservableProperty] private string? stepsText;
    [ObservableProperty] private string? nextEscalation;
    [ObservableProperty] private string? journalPath;
    [ObservableProperty] private string? gatewayHealthText;
    [ObservableProperty] private string? artifactsText;
    [ObservableProperty] private string? gatewayTokenText;
    [ObservableProperty] private string? gatewayStartupReason;
    [ObservableProperty] private string? gatewayTroubleshootDetails;
    [ObservableProperty] private string? rebuildPlanText;
    [ObservableProperty] private string? rebuildVerificationText;
    [ObservableProperty] private string? currentIssue = "No active issues detected.";
    [ObservableProperty] private string? activeRuntimeText = "(unknown)";
    [ObservableProperty] private string? configPathText = "(unknown)";
    [ObservableProperty] private string? lastBackupText = "None yet";
    [ObservableProperty] private string? gatewayStateText = "Unknown";
    [ObservableProperty] private string? lastCheckedTime = "Never";
    [ObservableProperty] private string? lastHealthyTime = "Never";
    [ObservableProperty] private string? serviceStateText;
    [ObservableProperty] private string? lastFailureReason;
    [ObservableProperty] private string? tokenStateText = "Unknown";
    [ObservableProperty] private string? dashboardUrlText = "(not available)";
    [ObservableProperty] private string? browserDiagnosticsText = "(not available)";
    [ObservableProperty] private bool logsExpanded = true;
    [ObservableProperty] private string primaryHomeActionLabel = "Repair Gateway";
    [ObservableProperty] private ICommand? primaryHomeActionCommand;
    [ObservableProperty] private bool showHomeRepairAction = true;
    [ObservableProperty] private bool showHomeDashboardAction = true;
    [ObservableProperty] private string logs = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = "Idle";
    [ObservableProperty] private bool isGatewayHealthy;
    [ObservableProperty] private bool isSetupMode;
    [ObservableProperty] private bool showSetupRestoreOptions;
    [ObservableProperty] private string setupSummary = "OpenClaw is not installed yet.";
    [ObservableProperty] private string setupSecurityStatus = "Not run";
    [ObservableProperty] private string? setupSecurityDetails;
    [ObservableProperty] private string? setupStepsText;
    [ObservableProperty] private string? setupRuntimeText;
    [ObservableProperty] private bool isSetupIdle = true;
    [ObservableProperty] private string? setupEnvironmentHint;
    [ObservableProperty] private int setupWizardStep; // 0=welcome, 1=provider, 2=apikey, 3=usecase, 4=confirm, 5=installing, 6=done
    [ObservableProperty] private AiProvider selectedProvider = AiProvider.DeepSeek;
    [ObservableProperty] private string? wizardApiKey;
    [ObservableProperty] private bool wizardSkipAi;
    [ObservableProperty] private string wizardProviderInfo = string.Empty;
    [ObservableProperty] private bool useCaseProductivity;
    [ObservableProperty] private bool useCaseTracker;
    [ObservableProperty] private bool useCaseEfficiency;
    [ObservableProperty] private bool useCaseStocks;
    [ObservableProperty] private ImPlatform selectedImPlatform = ImPlatform.None;
    [ObservableProperty] private string wizardConfirmSummary = string.Empty;
    [ObservableProperty] private string wizardSkillsList = string.Empty;
    [ObservableProperty] private string? wizardAgentMessage;

    public bool IsSetupSecuritySafe => string.Equals(SetupSecurityStatus, "Safe", StringComparison.OrdinalIgnoreCase);
    public bool IsSetupSecurityWarning => string.Equals(SetupSecurityStatus, "Needs attention", StringComparison.OrdinalIgnoreCase);
    public bool IsSetupSecurityCritical => string.Equals(SetupSecurityStatus, "Critical", StringComparison.OrdinalIgnoreCase);
    public bool IsSetupSecurityNotRun => string.Equals(SetupSecurityStatus, "Not run", StringComparison.OrdinalIgnoreCase);

    // Wizard step visibility (0=welcome, 1=provider, 2=apikey, 3=usecase, 4=confirm, 5=installing, 6=done)
    public bool IsWizardWelcome => SetupWizardStep == 0;
    public bool IsWizardProvider => SetupWizardStep == 1;
    public bool IsWizardApiKey => SetupWizardStep == 2;
    public bool IsWizardUseCase => SetupWizardStep == 3;
    public bool IsWizardConfirm => SetupWizardStep == 4;
    public bool IsWizardInstalling => SetupWizardStep == 5;
    public bool IsWizardDone => SetupWizardStep == 6;
    public bool IsProviderDeepSeek => SelectedProvider == AiProvider.DeepSeek;
    public bool IsProviderAnthropic => SelectedProvider == AiProvider.Anthropic;
    public bool IsProviderOpenAI => SelectedProvider == AiProvider.OpenAI;
    public bool IsProviderBailian => SelectedProvider == AiProvider.Bailian;
    public bool IsProviderBailianCoding => SelectedProvider == AiProvider.BailianCodingPlan;
    public bool IsProviderNone => SelectedProvider == AiProvider.None;
    public bool NeedsApiKey => !WizardSkipAi && SelectedProvider != AiProvider.None && SelectedProvider != AiProvider.BailianCodingPlan;
    public bool IsImDingTalk => SelectedImPlatform == ImPlatform.DingTalk;
    public bool IsImFeishu => SelectedImPlatform == ImPlatform.Feishu;
    public bool IsImQQ => SelectedImPlatform == ImPlatform.QQ;
    public bool IsImDiscord => SelectedImPlatform == ImPlatform.Discord;
    public bool IsImNone => SelectedImPlatform == ImPlatform.None;
    public string WizardApiKeyLabel => SelectedProvider switch
    {
        AiProvider.DeepSeek => "DeepSeek API Key",
        AiProvider.Anthropic => "Anthropic API Key",
        AiProvider.OpenAI => "OpenAI API Key",
        AiProvider.Bailian => "Bailian API Key",
        _ => "API Key"
    };
    public string WizardStepIndicator => SetupWizardStep switch
    {
        0 => "Step 1 of 7 \u2014 Welcome",
        1 => "Step 2 of 7 \u2014 AI Provider",
        2 => "Step 3 of 7 \u2014 API Key",
        3 => "Step 4 of 7 \u2014 Use Cases",
        4 => "Step 5 of 7 \u2014 Confirm",
        5 => "Step 6 of 7 \u2014 Installing",
        6 => "Step 7 of 7 \u2014 Ready!",
        _ => $"Step {SetupWizardStep + 1} of 7"
    };
    public bool IsIdle => !IsBusy;
    public string BackupPathHint =>
        string.IsNullOrWhiteSpace(LastBackupText) || LastBackupText == "None yet"
            ? "No backup yet."
            : LastBackupText;
    public string RestorePathHint =>
        string.IsNullOrWhiteSpace(ArchivePath)
            ? "No restore yet."
            : ArchivePath!;
    public string GatewayBadgeText => IsGatewayHealthy ? "Healthy" : "Offline";
    public string GatewayStatusDetailText =>
        !string.IsNullOrWhiteSpace(GatewayHealthText)
            ? GatewayHealthText!
            : CurrentIssue ?? "Checking OpenClaw gateway...";
    public string LogLineCount => logLines.Count > 0 ? $"({logLines.Count})" : string.Empty;

    // Category tab state
    public bool IsCategoryAll => SelectedCategory == "all";
    public bool IsCategoryGateway => SelectedCategory == "gateway";
    public bool IsCategoryBackup => SelectedCategory == "backup";
    public bool IsCategoryTools => SelectedCategory == "tools";
    public bool IsCategoryDanger => SelectedCategory == "danger";
    public string LogsToggleText => LogsExpanded ? "Hide" : "Show";

    private TaskCompletionSource<bool>? confirmDialogTcs;

    public bool ConfirmDialogHasPhrase => !string.IsNullOrWhiteSpace(ConfirmDialogPhrase);

    public bool ConfirmDialogCanConfirm =>
        string.IsNullOrWhiteSpace(ConfirmDialogPhrase)
        || string.Equals(ConfirmDialogInput?.Trim(), ConfirmDialogPhrase, StringComparison.OrdinalIgnoreCase);

    private bool gatewayWasHealthy;
    private bool initialStatusCheckStarted;
    private bool allowRepoRuntime;
    private ActionDescriptor? pendingAction;

    public MainWindowViewModel()
    {
        Actions = ActionCatalog.All;
        FilteredActions = new ReadOnlyObservableCollection<ActionDescriptor>(filteredActions);
        var (_, _, executor) = DefaultActionRegistry.Create();
        context = PathDefaults.CreateDefaultContext();
        DestinationPath = context.OpenClawHome;
        progress = new Progress<ActionEvent>(HandleEvent);
        execute = (actionId, input, ct) => executor.ExecuteAsync(actionId, input, context, progress, ct);
        BackupCreateScope = MapBackupLevelToScope(SelectedBackupLevel);
        UpdateHomeActionState();
        EvaluateSetupMode();
        UpdateFilteredActions();
        // Initialize provider info for default selection
        WizardProviderInfo = "DeepSeek offers powerful AI models with generous free trial credits. Great for getting started quickly.";
        if (!IsSetupMode)
        {
            BeginInitialStatusCheck();
        }
    }

    internal MainWindowViewModel(
        Func<string, object, Task<ActionResult>> executor,
        ActionContext context,
        IProgress<ActionEvent>? progress = null)
    {
        Actions = ActionCatalog.All;
        FilteredActions = new ReadOnlyObservableCollection<ActionDescriptor>(filteredActions);
        this.context = context;
        DestinationPath = context.OpenClawHome;
        this.progress = progress ?? new Progress<ActionEvent>(HandleEvent);
        execute = (actionId, input, _) => executor(actionId, input);
        BackupCreateScope = MapBackupLevelToScope(SelectedBackupLevel);
        UpdateHomeActionState();
        EvaluateSetupMode();
        UpdateFilteredActions();
    }

    public bool IsHomeSelected => SelectedSection == SectionHome;
    public bool IsBackupsSelected => SelectedSection == SectionBackups;
    public bool IsRecoverySelected => SelectedSection == SectionRecovery;
    public bool IsGatewayUnhealthy => !IsGatewayHealthy;
    public bool IsOperatorMode => !IsSetupMode;
    public bool IsGatewayDetachedFallback =>
        string.Equals(GatewayStateText, "Running via detached fallback", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedSectionChanged(string value)
    {
        NotifyNavSelection();
    }

    partial void OnActionSearchTextChanged(string value)
    {
        UpdateFilteredActions();
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        UpdateFilteredActions();
        OnPropertyChanged(nameof(IsCategoryAll));
        OnPropertyChanged(nameof(IsCategoryGateway));
        OnPropertyChanged(nameof(IsCategoryBackup));
        OnPropertyChanged(nameof(IsCategoryTools));
        OnPropertyChanged(nameof(IsCategoryDanger));
    }

    partial void OnArchivePathChanged(string? value)
    {
        OnPropertyChanged(nameof(RestorePathHint));
    }

    partial void OnLastBackupTextChanged(string? value)
    {
        OnPropertyChanged(nameof(BackupPathHint));
    }

    partial void OnGatewayStateTextChanged(string? value)
    {
        UpdateHomeActionState();
        OnPropertyChanged(nameof(IsGatewayDetachedFallback));
    }

    partial void OnIsSetupModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOperatorMode));
    }

    partial void OnSetupWizardStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWizardWelcome));
        OnPropertyChanged(nameof(IsWizardProvider));
        OnPropertyChanged(nameof(IsWizardApiKey));
        OnPropertyChanged(nameof(IsWizardUseCase));
        OnPropertyChanged(nameof(IsWizardConfirm));
        OnPropertyChanged(nameof(IsWizardInstalling));
        OnPropertyChanged(nameof(IsWizardDone));
        OnPropertyChanged(nameof(WizardStepIndicator));
    }

    partial void OnSelectedProviderChanged(AiProvider value)
    {
        OnPropertyChanged(nameof(IsProviderDeepSeek));
        OnPropertyChanged(nameof(IsProviderAnthropic));
        OnPropertyChanged(nameof(IsProviderOpenAI));
        OnPropertyChanged(nameof(IsProviderBailian));
        OnPropertyChanged(nameof(IsProviderBailianCoding));
        OnPropertyChanged(nameof(IsProviderNone));
        OnPropertyChanged(nameof(NeedsApiKey));
        OnPropertyChanged(nameof(WizardApiKeyLabel));
        WizardProviderInfo = value switch
        {
            AiProvider.DeepSeek => "DeepSeek offers powerful AI models with generous free trial credits. Great for getting started quickly.",
            AiProvider.Anthropic => "Anthropic Claude models. Requires an API key from console.anthropic.com.",
            AiProvider.OpenAI => "OpenAI GPT models. Requires an API key from platform.openai.com.",
            AiProvider.Bailian => "Alibaba Bailian (Tongyi Qianwen). Get your key from bailian.console.aliyun.com.",
            AiProvider.BailianCodingPlan => "Free Alibaba Bailian Coding Plan. No API key needed \u2014 uses the free coding tier.",
            AiProvider.None => "Skip AI setup. You can configure an AI provider later in OpenClaw settings.",
            _ => string.Empty
        };
    }

    partial void OnWizardSkipAiChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsApiKey));
    }

    partial void OnSelectedImPlatformChanged(ImPlatform value)
    {
        OnPropertyChanged(nameof(IsImDingTalk));
        OnPropertyChanged(nameof(IsImFeishu));
        OnPropertyChanged(nameof(IsImQQ));
        OnPropertyChanged(nameof(IsImDiscord));
        OnPropertyChanged(nameof(IsImNone));
    }

    partial void OnIsBusyChanged(bool value)
    {
        IsSetupIdle = !value;
        OnPropertyChanged(nameof(IsIdle));
    }

    partial void OnLogsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(LogsToggleText));
    }

    partial void OnCurrentIssueChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            GuidanceHint = value;
        }
        OnPropertyChanged(nameof(GatewayStatusDetailText));
    }

    partial void OnGatewayHealthTextChanged(string? value)
    {
        OnPropertyChanged(nameof(GatewayStatusDetailText));
    }

    partial void OnSetupSecurityStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsSetupSecuritySafe));
        OnPropertyChanged(nameof(IsSetupSecurityWarning));
        OnPropertyChanged(nameof(IsSetupSecurityCritical));
        OnPropertyChanged(nameof(IsSetupSecurityNotRun));
    }

    partial void OnConfirmDialogPhraseChanged(string? value)
    {
        OnPropertyChanged(nameof(ConfirmDialogHasPhrase));
        OnPropertyChanged(nameof(ConfirmDialogCanConfirm));
    }

    partial void OnConfirmDialogInputChanged(string? value)
    {
        OnPropertyChanged(nameof(ConfirmDialogCanConfirm));
    }

    partial void OnSelectedBackupLevelChanged(BackupLevel value)
    {
        BackupCreateScope = MapBackupLevelToScope(value);
        BackupLevelDescription = value switch
        {
            BackupLevel.Full => "Full backup: state, config, credentials, sessions, and workspaces.",
            BackupLevel.OpenClawOnly => "OpenClaw only: state, config, credentials, sessions (no workspace).",
            BackupLevel.ConfigOnly => "Config only: the active OpenClaw config file.",
            _ => "Full backup: state, config, credentials, sessions, and workspaces."
        };
    }

    partial void OnIsGatewayHealthyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGatewayUnhealthy));
        OnPropertyChanged(nameof(GatewayBadgeText));
        if (string.Equals(GatewayStateText, "Running via detached fallback", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (value)
        {
            gatewayWasHealthy = true;
            LastHealthyTime = DateTimeOffset.Now.ToString("g");
            GatewayStateText = "Healthy";
        }
        else if (GatewayStateText != "Repairing" && GatewayStateText != "Needs confirmation")
        {
            GatewayStateText = gatewayWasHealthy ? "Unstable" : "Broken";
        }
        UpdateHomeActionState();
    }

    [RelayCommand]
    private void SelectCategory(string? category)
    {
        SelectedCategory = category ?? "all";
    }

    [RelayCommand]
    private void SelectHome()
    {
        SelectedSection = SectionHome;
        NotifyNavSelection();
    }

    [RelayCommand]
    private void SelectBackups()
    {
        SelectedSection = SectionBackups;
        NotifyNavSelection();
    }

    [RelayCommand]
    private void SelectRecovery()
    {
        SelectedSection = SectionRecovery;
        NotifyNavSelection();
    }

    [RelayCommand]
    private void OpenActionInputs()
    {
        ActionInputsExpanded = true;
    }

    [RelayCommand]
    private void CloseActionInputs()
    {
        ActionInputsExpanded = false;
        SetPendingAction(null);
    }

    [RelayCommand]
    private async Task ConfirmAndRunActionAsync()
    {
        var action = pendingAction;
        SetPendingAction(null);
        ActionInputsExpanded = false;

        if (action == null) return;

        if (action.RequiresArchive && string.IsNullOrWhiteSpace(ArchivePath))
        {
            StatusText = "Archive path required.";
            ResultSummary = "Provide an archive path first.";
            SetPendingAction(action);
            ActionInputsExpanded = true;
            return;
        }

        if (action.Capabilities.HasFlag(ActionCapability.Destructive) || !string.IsNullOrWhiteSpace(action.ConfirmPhrase))
        {
            var confirmed = await RequireConfirmationAsync(action.Id).ConfigureAwait(false);
            if (!confirmed)
            {
                StatusText = "Cancelled";
                return;
            }
        }

        var input = BuildInputForAction(action);
        await ExecuteAsync(action.Id, input).ConfigureAwait(false);
    }

    public bool HasPendingAction => pendingAction != null;
    public bool IsPendingBackupAction => pendingAction != null
        && pendingAction.Id.StartsWith("backup", StringComparison.OrdinalIgnoreCase);

    private void SetPendingAction(ActionDescriptor? action)
    {
        pendingAction = action;
        OnPropertyChanged(nameof(HasPendingAction));
        OnPropertyChanged(nameof(IsPendingBackupAction));
    }

    [RelayCommand]
    private void OpenLastResult()
    {
        if (string.IsNullOrWhiteSpace(ResultSummary)
            && string.IsNullOrWhiteSpace(ImpactSummary)
            && string.IsNullOrWhiteSpace(WarningsText)
            && string.IsNullOrWhiteSpace(RollbackSnapshot))
        {
            return;
        }

        LastResultExpanded = true;
    }

    [RelayCommand]
    private void CloseLastResult()
    {
        LastResultExpanded = false;
    }

    [RelayCommand]
    private async Task RunActionAsync(ActionDescriptor? action)
    {
        if (action == null)
        {
            return;
        }

        if (IsBusy)
        {
            StatusText = "Another action is running.";
            return;
        }

        ActiveActionLabel = action.Label;
        LastResultExpanded = false;

        // Backup and restore actions: show inputs popup first so user can set paths/options
        if (action.Id.StartsWith("backup", StringComparison.OrdinalIgnoreCase)
            || action.Id.StartsWith("restore", StringComparison.OrdinalIgnoreCase))
        {
            SetPendingAction(action);
            ActionInputsExpanded = true;
            return;
        }

        if (action.RequiresArchive && string.IsNullOrWhiteSpace(ArchivePath))
        {
            StatusText = "Archive path required.";
            ResultSummary = "Provide an archive path in Action Inputs.";
            ActionInputsExpanded = true;
            return;
        }

        if (string.Equals(action.Id, "dashboard-open", StringComparison.OrdinalIgnoreCase) && !EnsureGatewayHealthy("Dashboard"))
        {
            return;
        }

        if (string.Equals(action.Id, "gateway-url", StringComparison.OrdinalIgnoreCase) && !EnsureGatewayHealthy("Dashboard URL"))
        {
            return;
        }

        if (action.Capabilities.HasFlag(ActionCapability.Destructive) || !string.IsNullOrWhiteSpace(action.ConfirmPhrase))
        {
            var confirmed = await RequireConfirmationAsync(action.Id).ConfigureAwait(false);
            if (!confirmed)
            {
                StatusText = "Cancelled";
                return;
            }
        }

        var input = BuildInputForAction(action);
        await ExecuteAsync(action.Id, input).ConfigureAwait(false);
    }

    private void NotifyNavSelection()
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsBackupsSelected));
        OnPropertyChanged(nameof(IsRecoverySelected));
    }

    private object BuildInputForAction(ActionDescriptor action)
    {
        var inputType = action.InputType;

        if (inputType == typeof(EmptyInput))
        {
            return new EmptyInput();
        }

        if (inputType == typeof(BackupCreateInput))
        {
            return new BackupCreateInput(
                SourcePath: null,
                OutputPath: BackupCreateOutputPath,
                Password: Password,
                Verify: BackupCreateVerify,
                Scope: BackupCreateScope,
                BackupPath: null,
                NoEncrypt: BackupCreateNoEncrypt);
        }

        if (inputType == typeof(BackupRestoreInput))
        {
            return new BackupRestoreInput(
                ArchivePath,
                DestinationPath,
                Password,
                Scope,
                Preview,
                SafeReset,
                ResetMode,
                ConfirmReset,
                VerifyFirst: true);
        }

        if (inputType == typeof(BackupVerifyInput))
        {
            return new BackupVerifyInput(ArchivePath, Password);
        }

        if (inputType == typeof(BackupExportInput))
        {
            return new BackupExportInput(
                BackupExportScope,
                BackupExportVerify,
                BackupExportOutputPath,
                Password,
                BackupExportEncrypt);
        }

        if (inputType == typeof(BackupDiffInput))
        {
            return new BackupDiffInput(
                DiffLeftArchive,
                DiffRightArchive,
                DiffRedactSecrets,
                Password);
        }

        if (inputType == typeof(BackupScheduleInput))
        {
            return new BackupScheduleInput(
                ScheduleMode,
                ScheduleKind,
                ScheduleExpression,
                ScheduleAtTime,
                ScheduleDayOfWeek,
                ScheduleDayOfMonth,
                ScheduleKeepLast,
                ScheduleOlderThan,
                ScheduleVerifyAfter,
                ScheduleIncludeWorkspace,
                ScheduleOnlyConfig);
        }

        if (inputType == typeof(BackupScheduleRemoveInput))
        {
            return new BackupScheduleRemoveInput(ScheduleId);
        }

        if (inputType == typeof(BackupScheduleListInput))
        {
            return new BackupScheduleListInput();
        }

        if (inputType == typeof(DoctorInput))
        {
            return new DoctorInput(ExportDiagnostics: ExportDiagnostics);
        }

        if (inputType == typeof(FixInput))
        {
            return new FixInput(Password: Password, ExportDiagnostics: ExportDiagnostics);
        }

        if (inputType == typeof(RecoverInput))
        {
            return new RecoverInput(
                ArchivePath,
                DestinationPath,
                Password,
                Scope,
                Preview,
                SafeReset,
                ResetMode,
                ConfirmReset,
                RunDoctor,
                RunFix,
                ExportDiagnostics);
        }

        if (inputType == typeof(RollbackInput))
        {
            var snapshotPath = RollbackSourcePath ?? RollbackSnapshot;
            return new RollbackInput(
                snapshotPath,
                DestinationPath,
                Password,
                Scope,
                RollbackPreview,
                ConfirmRollback);
        }

        if (inputType == typeof(ResetInput))
        {
            return new ResetInput(
                ResetMode,
                Preview,
                ConfirmReset);
        }

        if (inputType == typeof(DiagnosticsExportInput))
        {
            return new DiagnosticsExportInput(null);
        }

        if (inputType == typeof(GatewayTokenInput))
        {
            return new GatewayTokenInput(RevealGatewayToken);
        }

        if (inputType == typeof(BrowserDiagnosticsInput))
        {
            return new BrowserDiagnosticsInput();
        }

        if (inputType == typeof(GatewayUrlInput))
        {
            return new GatewayUrlInput();
        }

        if (inputType == typeof(DashboardOpenInput))
        {
            return new DashboardOpenInput();
        }

        if (inputType == typeof(OpenClawCleanupInput))
        {
            return new OpenClawCleanupInput(Apply: false, Confirm: false);
        }

        if (inputType == typeof(OpenClawRebuildInput))
        {
            return new OpenClawRebuildInput(
                RebuildPreserveConfig,
                RebuildPreserveCreds,
                RebuildPreserveSessions,
                RebuildPreserveWorkspace,
                RebuildCleanInstall,
                RebuildConfirmDestructive,
                Password);
        }

        return new EmptyInput();
    }

    private void BeginInitialStatusCheck()
    {
        if (initialStatusCheckStarted)
        {
            return;
        }

        initialStatusCheckStarted = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RunGatewayStatusAsync();
            }
            catch
            {
                IsGatewayHealthy = false;
                GatewayStateText = "Broken";
                StatusText = "Gateway offline — use Gateway Start or Gateway Repair";
                CurrentIssue = "Gateway not running. Click Gateway Start or Gateway Repair to fix.";
            }
        });
    }

    [RelayCommand]
    private async Task RunRestoreAsync()
    {
        var input = new BackupRestoreInput(
            ArchivePath,
            DestinationPath,
            Password,
            Scope,
            Preview,
            SafeReset,
            ResetMode,
            ConfirmReset);

        await ExecuteAsync("backup-restore", input).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task RunBackupCreateAsync()
    {
        var input = new BackupCreateInput(
            SourcePath: null,
            OutputPath: BackupCreateOutputPath,
            Password: Password,
            Verify: BackupCreateVerify,
            Scope: BackupCreateScope,
            BackupPath: null,
            NoEncrypt: BackupCreateNoEncrypt);
        return ExecuteAsync("backup-create", input);
    }

    [RelayCommand]
    private Task RunBackupVerifyAsync()
    {
        var input = new BackupVerifyInput(ArchivePath, Password);
        return ExecuteAsync("backup-verify", input);
    }

    [RelayCommand]
    private Task RunBackupExportAsync()
    {
        var input = new BackupExportInput(
            BackupExportScope,
            BackupExportVerify,
            BackupExportOutputPath,
            Password,
            Encrypt: BackupExportEncrypt);
        return ExecuteAsync("backup-export", input);
    }

    [RelayCommand]
    private async Task RunRecoverAsync()
    {
        var input = new RecoverInput(
            ArchivePath,
            DestinationPath,
            Password,
            Scope,
            Preview,
            SafeReset,
            ResetMode,
            ConfirmReset,
            RunDoctor,
            RunFix,
            ExportDiagnostics);

        await ExecuteAsync("recover", input).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RunDoctorAsync()
    {
        var input = new DoctorInput(ExportDiagnostics: ExportDiagnostics);
        await ExecuteAsync("doctor", input).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RunFixAsync()
    {
        var input = new FixInput(Password: Password, ExportDiagnostics: ExportDiagnostics);
        await ExecuteAsync("fix", input).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RunRollbackAsync()
    {
        var snapshotPath = RollbackSourcePath ?? RollbackSnapshot;
        var input = new RollbackInput(
            snapshotPath,
            DestinationPath,
            Password,
            Scope,
            RollbackPreview,
            ConfirmRollback);

        await ExecuteAsync("rollback", input).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task RunGatewayStartAsync()
    {
        return ExecuteAsync("gateway-start", new EmptyInput());
    }

    [RelayCommand]
    private Task RunGatewayStopAsync()
    {
        return ExecuteAsync("gateway-stop", new EmptyInput());
    }

    [RelayCommand]
    private Task RunGatewayStatusAsync()
    {
        return ExecuteAsync("gateway-status", new EmptyInput());
    }

    [RelayCommand]
    private Task RunGatewayRepairAsync()
    {
        return ExecuteAsync("gateway-repair", new EmptyInput());
    }

    [RelayCommand]
    private void WizardNext()
    {
        switch (SetupWizardStep)
        {
            case 0: // Welcome -> Provider
                WizardAgentMessage = "Great! Let's pick your AI provider. I recommend DeepSeek if you're just getting started \u2014 it has free trial credits.";
                SetupWizardStep = 1;
                break;
            case 1: // Provider -> ApiKey or UseCase
                if (WizardSkipAi || SelectedProvider == AiProvider.None || SelectedProvider == AiProvider.BailianCodingPlan)
                {
                    WizardAgentMessage = "No problem! You can always configure AI later. Now let's pick what you want OpenClaw to do.";
                    SetupWizardStep = 3; // skip API key
                }
                else
                {
                    WizardAgentMessage = $"Good choice! Paste your {WizardApiKeyLabel} below. It will be stored securely in your config.";
                    SetupWizardStep = 2;
                }
                break;
            case 2: // ApiKey -> UseCase
                WizardAgentMessage = "Key saved! Now let's pick what you want to use OpenClaw for. I'll install the right skills automatically.";
                SetupWizardStep = 3;
                break;
            case 3: // UseCase -> Confirm
                BuildConfirmSummary();
                WizardAgentMessage = "Here's your setup plan. Review it and hit Install when you're ready \u2014 this will take about 3\u20135 minutes.";
                SetupWizardStep = 4;
                break;
            case 4: // Confirm -> Installing
                WizardAgentMessage = "Installing now... sit back and relax!";
                SetupWizardStep = 5;
                _ = RunWizardInstallAsync();
                break;
        }
    }

    [RelayCommand]
    private void WizardBack()
    {
        switch (SetupWizardStep)
        {
            case 1: SetupWizardStep = 0; break;
            case 2: SetupWizardStep = 1; break;
            case 3:
                SetupWizardStep = NeedsApiKey ? 2 : 1;
                break;
            case 4: SetupWizardStep = 3; break;
        }
    }

    [RelayCommand]
    private void WizardManualInstall()
    {
        // Jump directly to installing with current settings, skipping wizard
        SetupWizardStep = 5;
        WizardAgentMessage = "Running manual installation...";
        _ = ExecuteAsync("setup-install", new EmptyInput());
    }

    [RelayCommand]
    private void SelectProvider(string provider)
    {
        SelectedProvider = provider switch
        {
            "deepseek" => AiProvider.DeepSeek,
            "anthropic" => AiProvider.Anthropic,
            "openai" => AiProvider.OpenAI,
            "bailian" => AiProvider.Bailian,
            "bailian-coding" => AiProvider.BailianCodingPlan,
            "none" => AiProvider.None,
            _ => AiProvider.DeepSeek
        };
    }

    [RelayCommand]
    private void SelectImPlatform(string platform)
    {
        SelectedImPlatform = platform switch
        {
            "dingtalk" => ImPlatform.DingTalk,
            "feishu" => ImPlatform.Feishu,
            "qq" => ImPlatform.QQ,
            "discord" => ImPlatform.Discord,
            "none" => ImPlatform.None,
            _ => ImPlatform.None
        };
    }

    private UseCase GetSelectedUseCases()
    {
        var uc = UseCase.None;
        if (UseCaseProductivity) uc |= UseCase.DailyProductivity;
        if (UseCaseTracker) uc |= UseCase.InformationTracker;
        if (UseCaseEfficiency) uc |= UseCase.EfficiencyTools;
        if (UseCaseStocks) uc |= UseCase.StockMarket;
        return uc;
    }

    private IReadOnlyList<string> GetSkillsForUseCases()
    {
        var skills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (UseCaseProductivity)
        {
            skills.Add("summarize");
            skills.Add("weather");
            skills.Add("agent-browser");
            skills.Add("obsidian");
        }
        if (UseCaseTracker)
        {
            skills.Add("agent-browser");
            skills.Add("summarize");
            skills.Add("weather");
            skills.Add("proactive-agent");
        }
        if (UseCaseEfficiency)
        {
            skills.Add("agent-browser");
            skills.Add("self-improving-agent");
            skills.Add("proactive-agent");
            skills.Add("summarize");
        }
        if (UseCaseStocks)
        {
            skills.Add("a-share-real-time-data");
            skills.Add("stock-evaluator");
            skills.Add("agent-browser");
            skills.Add("summarize");
        }
        return skills.ToList();
    }

    private void BuildConfirmSummary()
    {
        var sb = new StringBuilder();
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();
        sb.AppendLine($"OS:          {os}");
        sb.AppendLine($"AI Model:    {(SelectedProvider == AiProvider.None ? "Skip (configure later)" : SelectedProvider.ToString())}");
        if (NeedsApiKey && !string.IsNullOrWhiteSpace(WizardApiKey))
            sb.AppendLine($"API Key:     ****{WizardApiKey[^Math.Min(4, WizardApiKey.Length)..]}");
        sb.AppendLine($"Chat via:    {(SelectedImPlatform == ImPlatform.None ? "Web Console" : SelectedImPlatform.ToString())}");

        var skills = GetSkillsForUseCases();
        if (skills.Count > 0)
        {
            sb.AppendLine($"Skills:      {string.Join(", ", skills)}");
            WizardSkillsList = string.Join("\n", skills.Select(s => $"  \u2022 {s}"));
        }
        else
        {
            sb.AppendLine("Skills:      None selected");
            WizardSkillsList = "  No skills selected. You can install them later.";
        }

        WizardConfirmSummary = sb.ToString();
    }

    private async Task RunWizardInstallAsync()
    {
        // Write full config (AI + gateway + plugins)
        await WriteAiConfigAsync();

        // Run the standard install flow
        var useCases = GetSelectedUseCases();
        var input = new SetupWizardInput(SelectedProvider, WizardApiKey, WizardSkipAi, useCases, SelectedImPlatform);
        await ExecuteAsync("setup-install", input);

        // Install skills based on use case selection via npx clawhub
        var skills = GetSkillsForUseCases();
        if (skills.Count > 0)
        {
            HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                $"Installing {skills.Count} skills..."));
            foreach (var skill in skills)
            {
                HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                    $"  Installing skill: {skill}"));
                try
                {
                    var npx = OperatingSystem.IsWindows() ? "npx.cmd" : "npx";
                    var psi = new ProcessStartInfo(npx, $"clawhub@latest install {skill}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        if (proc.ExitCode == 0)
                        {
                            HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                                $"  Installed: {skill}"));
                        }
                        else
                        {
                            var err = await proc.StandardError.ReadToEndAsync();
                            HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                                $"  Warning: {skill} install exited {proc.ExitCode}: {err.Trim()}", IsError: true));
                        }
                    }
                }
                catch (Exception ex)
                {
                    HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                        $"  Warning: Could not install {skill}: {ex.Message}", IsError: true));
                }
            }
            // Restart gateway to load new skills
            HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                "Restarting gateway to load skills..."));
            await ExecuteAsync("oc-gateway-restart", new EmptyInput());
        }

        EvaluateSetupMode();
        SetupWizardStep = 6;
        WizardAgentMessage = "All done! OpenClaw is installed and configured. Click below to start using ReClaw.";
    }

    private static string GenerateGatewayToken()
    {
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[32];
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task WriteAiConfigAsync()
    {
        try
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".openclaw");
            Directory.CreateDirectory(configDir);
            var configPath = Path.Combine(configDir, "openclaw.json");

            var gatewayToken = GenerateGatewayToken();
            var apiKey = WizardApiKey ?? "";

            // Use the full config templates from agent.md (includes gateway, plugins, models)
            var configJson = SelectedProvider switch
            {
                AiProvider.DeepSeek => $$"""
                {
                  "models": {
                    "providers": {
                      "deepseek": {
                        "baseUrl": "https://api.deepseek.com/v1",
                        "apiKey": "{{apiKey}}",
                        "api": "openai-completions"
                      }
                    }
                  },
                  "agents": { "defaults": { "model": { "primary": "deepseek/deepseek-chat" } } },
                  "gateway": {
                    "mode": "local",
                    "auth": { "mode": "token", "token": "{{gatewayToken}}" },
                    "http": { "endpoints": { "chatCompletions": { "enabled": true } } }
                  },
                  "plugins": { "enabled": true, "allow": [] }
                }
                """,
                AiProvider.Anthropic => $$"""
                {
                  "models": {
                    "providers": {
                      "anthropic": { "apiKey": "{{apiKey}}" }
                    }
                  },
                  "agents": { "defaults": { "model": { "primary": "anthropic/claude-sonnet-4-5" } } },
                  "gateway": {
                    "mode": "local",
                    "auth": { "mode": "token", "token": "{{gatewayToken}}" },
                    "http": { "endpoints": { "chatCompletions": { "enabled": true } } }
                  },
                  "plugins": { "enabled": true, "allow": [] }
                }
                """,
                AiProvider.OpenAI => $$"""
                {
                  "models": {
                    "providers": {
                      "openai": { "apiKey": "{{apiKey}}" }
                    }
                  },
                  "agents": { "defaults": { "model": { "primary": "openai/gpt-4o" } } },
                  "gateway": {
                    "mode": "local",
                    "auth": { "mode": "token", "token": "{{gatewayToken}}" },
                    "http": { "endpoints": { "chatCompletions": { "enabled": true } } }
                  },
                  "plugins": { "enabled": true, "allow": [] }
                }
                """,
                AiProvider.Bailian => $$"""
                {
                  "models": {
                    "providers": {
                      "dashscope": {
                        "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1",
                        "apiKey": "{{apiKey}}",
                        "api": "openai-completions"
                      }
                    }
                  },
                  "agents": { "defaults": { "model": { "primary": "dashscope/qwen-max" } } },
                  "gateway": {
                    "mode": "local",
                    "auth": { "mode": "token", "token": "{{gatewayToken}}" },
                    "http": { "endpoints": { "chatCompletions": { "enabled": true } } }
                  },
                  "plugins": { "enabled": true, "allow": [] }
                }
                """,
                AiProvider.BailianCodingPlan => $$"""
                {
                  "models": {
                    "mode": "merge",
                    "providers": {
                      "bailian": {
                        "baseUrl": "https://coding.dashscope.aliyuncs.com/v1",
                        "apiKey": "{{apiKey}}",
                        "api": "openai-completions",
                        "models": [
                          { "id": "qwen3.5-plus", "name": "qwen3.5-plus", "reasoning": false, "input": ["text","image"], "cost": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 }, "contextWindow": 1000000, "maxTokens": 65536 },
                          { "id": "qwen3-coder-plus", "name": "qwen3-coder-plus", "reasoning": false, "input": ["text"], "cost": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 }, "contextWindow": 262144, "maxTokens": 65536 }
                        ]
                      }
                    }
                  },
                  "agents": { "defaults": { "model": { "primary": "bailian/qwen3.5-plus" } } },
                  "gateway": {
                    "mode": "local",
                    "auth": { "mode": "token", "token": "{{gatewayToken}}" },
                    "http": { "endpoints": { "chatCompletions": { "enabled": true } } }
                  },
                  "plugins": { "enabled": true, "allow": [] }
                }
                """,
                // No AI provider selected - still write gateway config
                AiProvider.None => $$"""
                {
                  "gateway": {
                    "mode": "local",
                    "auth": { "mode": "token", "token": "{{gatewayToken}}" },
                    "http": { "endpoints": { "chatCompletions": { "enabled": true } } }
                  },
                  "plugins": { "enabled": true, "allow": [] }
                }
                """,
                _ => null
            };

            if (configJson != null)
            {
                // Merge with existing config if present
                if (File.Exists(configPath))
                {
                    try
                    {
                        var existing = await File.ReadAllTextAsync(configPath);
                        using var doc = System.Text.Json.JsonDocument.Parse(existing);
                        using var stream = new MemoryStream();
                        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
                        {
                            writer.WriteStartObject();
                            using var newDoc = System.Text.Json.JsonDocument.Parse(configJson);
                            var newKeys = new HashSet<string>();
                            foreach (var prop in newDoc.RootElement.EnumerateObject())
                                newKeys.Add(prop.Name);
                            // Keep existing keys not overwritten
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                if (!newKeys.Contains(prop.Name))
                                    prop.WriteTo(writer);
                            }
                            // Write new keys
                            foreach (var prop in newDoc.RootElement.EnumerateObject())
                                prop.WriteTo(writer);
                            writer.WriteEndObject();
                        }
                        configJson = Encoding.UTF8.GetString(stream.ToArray());
                    }
                    catch
                    {
                        // If we can't parse existing, just overwrite
                    }
                }
                await File.WriteAllTextAsync(configPath, configJson);
                HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                    $"Config written: {SelectedProvider} -> {configPath}"));
                HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                    $"Gateway token generated (keep this safe!)"));
            }
        }
        catch (Exception ex)
        {
            HandleEvent(new LogReceived("setup-wizard", Guid.Empty, DateTimeOffset.UtcNow,
                $"Warning: Could not write config: {ex.Message}"));
        }
    }

    [RelayCommand]
    private Task RunSetupInstallAsync()
    {
        return ExecuteAsync("setup-install", new EmptyInput());
    }

    [RelayCommand]
    private Task RunSetupRestoreAsync()
    {
        var input = new BackupRestoreInput(
            ArchivePath,
            DestinationPath,
            Password,
            Scope,
            Preview,
            SafeReset,
            ResetMode,
            ConfirmReset);
        return ExecuteAsync("setup-restore", input);
    }

    [RelayCommand]
    private Task RunSetupAdvancedAsync()
    {
        return ExecuteAsync("setup-advanced", new EmptyInput());
    }

    [RelayCommand]
    private Task RunGatewayLogsAsync()
    {
        return ExecuteAsync("gateway-logs", new EmptyInput());
    }

    [RelayCommand]
    private Task RunOpenClawTerminalAsync()
    {
        return ExecuteAsync("openclaw-terminal", new EmptyInput());
    }

    [RelayCommand]
    private void ClearLogs()
    {
        logLines.Clear();
        logBuffer.Clear();
        logDirty = false;
        Logs = string.Empty;
        StatusText = "Logs cleared";
        OnPropertyChanged(nameof(LogLineCount));
    }

    [RelayCommand]
    private void ToggleLogs()
    {
        LogsExpanded = !LogsExpanded;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        ActionSearchText = string.Empty;
    }

    [RelayCommand]
    private void OpenReleases()
    {
        OpenUrl("https://github.com/JacobTheJacobs/ReClaw/releases");
    }

    [RelayCommand]
    private void OpenAuthor()
    {
        OpenUrl("https://github.com/JacobTheJacobs");
    }

    [RelayCommand]
    private void StopAction()
    {
        if (currentActionCts == null)
        {
            StatusText = "No action running";
            return;
        }

        currentActionCts.Cancel();
        StatusText = "Stop requested";
    }

    [RelayCommand]
    private void SkipSetup()
    {
        allowRepoRuntime = true;
        IsSetupMode = false;
        BeginInitialStatusCheck();
    }

    [RelayCommand]
    private Task RunDiagnosticsExportAsync()
    {
        return ExecuteAsync("diagnostics-export", new DiagnosticsExportInput(null));
    }

    [RelayCommand]
    private Task RunGatewayUrlAsync()
    {
        if (!EnsureGatewayHealthy("Dashboard URL"))
        {
            return Task.CompletedTask;
        }
        return ExecuteAsync("gateway-url", new GatewayUrlInput());
    }

    [RelayCommand]
    private Task RunDashboardOpenAsync()
    {
        if (!EnsureGatewayHealthy("Dashboard"))
        {
            return Task.CompletedTask;
        }
        return ExecuteAsync("dashboard-open", new DashboardOpenInput());
    }

    [RelayCommand]
    private Task RunGatewayTokenShowAsync()
    {
        if (!EnsureGatewayHealthy("Gateway token"))
        {
            return Task.CompletedTask;
        }
        return ExecuteAsync("gateway-token-show", new GatewayTokenInput(RevealGatewayToken));
    }

    [RelayCommand]
    private Task RunGatewayTokenGenerateAsync()
    {
        if (!EnsureGatewayHealthy("Gateway token"))
        {
            return Task.CompletedTask;
        }
        return ExecuteAsync("gateway-token-generate", new EmptyInput());
    }

    [RelayCommand]
    private Task RunBrowserDiagnosticsAsync()
    {
        if (!EnsureGatewayHealthy("Browser diagnostics"))
        {
            return Task.CompletedTask;
        }
        return ExecuteAsync("gateway-browser-diagnostics", new BrowserDiagnosticsInput());
    }

    [RelayCommand]
    private Task RunOpenClawCleanupAsync()
    {
        return ExecuteAsync("openclaw-cleanup-related", new OpenClawCleanupInput(Apply: false, Confirm: false));
    }

    [RelayCommand]
    private async Task RunOpenClawCleanupApplyAsync()
    {
        var confirmed = await RequireConfirmationAsync("openclaw-cleanup-related").ConfigureAwait(false);
        if (!confirmed)
        {
            StatusText = "Cleanup cancelled.";
            return;
        }

        await ExecuteAsync("openclaw-cleanup-related", new OpenClawCleanupInput(Apply: true, Confirm: true)).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ConfirmDialogConfirm()
    {
        if (!ConfirmDialogCanConfirm)
        {
            return;
        }

        CloseConfirmDialog(true);
    }

    [RelayCommand]
    private void ConfirmDialogCancel()
    {
        CloseConfirmDialog(false);
    }

    [RelayCommand]
    private Task RunGatewayTroubleshootAsync()
    {
        return ExecuteAsync("gateway-troubleshoot", new EmptyInput());
    }

    [RelayCommand]
    private Task RunOpenClawRebuildAsync()
    {
        var input = new OpenClawRebuildInput(
            RebuildPreserveConfig,
            RebuildPreserveCreds,
            RebuildPreserveSessions,
            RebuildPreserveWorkspace,
            RebuildCleanInstall,
            RebuildConfirmDestructive,
            Password);

        return ExecuteAsync("openclaw-rebuild", input);
    }

    [RelayCommand]
    private Task RunBackupDiffAsync()
    {
        var input = new BackupDiffInput(DiffLeftArchive, DiffRightArchive, DiffRedactSecrets);
        return ExecuteAsync("backup-diff", input);
    }

    [RelayCommand]
    private Task RunScheduleCreateAsync()
    {
        var includeWorkspace = ScheduleIncludeWorkspace;
        if (ScheduleOnlyConfig)
        {
            includeWorkspace = false;
        }

        var input = new BackupScheduleInput(
            ScheduleMode,
            ScheduleKind,
            ScheduleExpression,
            ScheduleAtTime,
            ScheduleDayOfWeek,
            ScheduleDayOfMonth,
            ScheduleKeepLast,
            ScheduleOlderThan,
            ScheduleVerifyAfter,
            includeWorkspace,
            ScheduleOnlyConfig);

        return ExecuteAsync("backup-schedule-create", input);
    }

    [RelayCommand]
    private Task RunScheduleListAsync()
    {
        return ExecuteAsync("backup-schedule-list", new BackupScheduleListInput());
    }

    [RelayCommand]
    private Task RunScheduleRemoveAsync()
    {
        return ExecuteAsync("backup-schedule-remove", new BackupScheduleRemoveInput(ScheduleId));
    }

    private async Task<bool> RequireConfirmationAsync(string actionId)
    {
        var descriptor = Actions.FirstOrDefault(action => string.Equals(action.Id, actionId, StringComparison.OrdinalIgnoreCase));
        var label = descriptor?.Label ?? actionId;
        var phrase = descriptor?.ConfirmPhrase;
        var message = string.IsNullOrWhiteSpace(phrase)
            ? $"Confirm to run {label}."
            : $"Type {phrase} to confirm {label}.";

        return await ShowConfirmDialogAsync($"{label} confirmation", message, phrase).ConfigureAwait(false);
    }

    private Task<bool> ShowConfirmDialogAsync(string title, string message, string? phrase)
    {
        confirmDialogTcs?.TrySetResult(false);
        ConfirmDialogTitle = title;
        ConfirmDialogMessage = message;
        ConfirmDialogPhrase = phrase;
        ConfirmDialogInput = string.Empty;
        IsConfirmDialogOpen = true;
        confirmDialogTcs = new TaskCompletionSource<bool>();
        return confirmDialogTcs.Task;
    }

    private void CloseConfirmDialog(bool result)
    {
        if (confirmDialogTcs == null)
        {
            IsConfirmDialogOpen = false;
            return;
        }

        confirmDialogTcs.TrySetResult(result);
        confirmDialogTcs = null;
        IsConfirmDialogOpen = false;
        ConfirmDialogTitle = null;
        ConfirmDialogMessage = null;
        ConfirmDialogPhrase = null;
        ConfirmDialogInput = null;
    }

    private string ResolveActionLabel(string actionId)
    {
        var match = Actions.FirstOrDefault(action => string.Equals(action.Id, actionId, StringComparison.OrdinalIgnoreCase));
        return match?.Label ?? actionId;
    }

    private async Task ExecuteAsync(string actionId, object input)
    {
        currentActionCts?.Cancel();
        currentActionCts?.Dispose();
        currentActionCts = new CancellationTokenSource();
        try
        {
            ActiveActionLabel = ResolveActionLabel(actionId);
            ActionInputsExpanded = false;
            LastResultExpanded = false;
            IsBusy = true;
            LogsExpanded = true;
            StatusText = $"Running {actionId}...";
            if (string.Equals(actionId, "gateway-repair", StringComparison.OrdinalIgnoreCase))
            {
                GatewayStateText = "Repairing";
            }
            ResetOutputs();
            var result = await execute(actionId, input, currentActionCts.Token).ConfigureAwait(false);
            UpdateOutputs(result);
            UpdateSetupState(actionId, result);
            OpenLastResult();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
            ResultSummary = "Action cancelled.";
        }
        finally
        {
            IsBusy = false;
            currentActionCts?.Dispose();
            currentActionCts = null;
            // Refresh gateway status after every action
            BeginInitialStatusCheck();
        }
    }

    private void HandleEvent(ActionEvent actionEvent)
    {
        var line = actionEvent switch
        {
            StatusChanged status => $"{status.Timestamp:HH:mm:ss} {status.Status}: {status.Detail}",
            ActionStarted started => $"{started.Timestamp:HH:mm:ss} Started {started.ActionId}",
            ActionCompleted completed => $"{completed.Timestamp:HH:mm:ss} Completed {completed.ActionId}",
            ActionFailed failed => $"{failed.Timestamp:HH:mm:ss} Failed {failed.ActionId}: {failed.Error}",
            ActionCancelled cancelled => $"{cancelled.Timestamp:HH:mm:ss} Cancelled {cancelled.ActionId}",
            LogReceived log => $"{log.Timestamp:HH:mm:ss} {(log.IsError ? "ERR" : "OUT")}: {log.Line}",
            _ => $"{actionEvent.Timestamp:HH:mm:ss} {actionEvent.ActionId}"
        };

        logLines.Enqueue(line);
        while (logLines.Count > MaxLogLines)
        {
            logLines.Dequeue();
        }

        logDirty = true;
        ScheduleLogFlush();
    }

    private void ScheduleLogFlush()
    {
        if (logFlushCts != null) return;
        logFlushCts = new CancellationTokenSource();
        var token = logFlushCts.Token;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(FlushLogs);
        });
    }

    private void FlushLogs()
    {
        logFlushCts?.Dispose();
        logFlushCts = null;
        if (!logDirty) return;
        logDirty = false;
        logBuffer.Clear();
        var first = true;
        foreach (var line in logLines)
        {
            if (!first) logBuffer.Append(Environment.NewLine);
            logBuffer.Append(line);
            first = false;
        }
        Logs = logBuffer.ToString();
        OnPropertyChanged(nameof(LogLineCount));
    }

    private void ResetOutputs()
    {
        ResultSummary = null;
        ImpactSummary = null;
        WarningsText = null;
        RollbackSnapshot = null;
        LastResultExpanded = false;
        DiagnosticsBundle = null;
        StepsText = null;
        NextEscalation = null;
        JournalPath = null;
        GatewayHealthText = null;
        ArtifactsText = null;
        DiffSummary = null;
        ScheduleSummary = null;
        GatewayTokenText = null;
        RebuildPlanText = null;
        RebuildVerificationText = null;
        SetupSecurityStatus = "Not run";
        SetupSecurityDetails = null;
        SetupStepsText = null;
        SetupRuntimeText = null;
    }

    private void UpdateOutputs(ActionResult result)
    {
        ResultSummary = result.Success ? "Completed successfully." : result.Error ?? "Action failed.";
        StatusText = result.Success ? "Completed" : "Failed";
        WarningsText = result.Warnings is { Count: > 0 }
            ? string.Join(Environment.NewLine, result.Warnings.Select(w => $"{w.Code}: {w.Message}"))
            : null;
        var gatewayNotReadyWarning = (WarningItem?)null;
        if (result.Warnings is { Count: > 0 } warnings)
        {
            var startupReason = warnings.FirstOrDefault(w => string.Equals(w.Code, "gateway-startup-reason", StringComparison.OrdinalIgnoreCase));
            if (startupReason != null)
            {
                GatewayStartupReason = startupReason.Message;
            }
            var gatewayMode = warnings.FirstOrDefault(w => string.Equals(w.Code, "gateway-mode-unset", StringComparison.OrdinalIgnoreCase));
            if (gatewayMode != null)
            {
                GatewayStartupReason = gatewayMode.Message;
            }
            gatewayNotReadyWarning = warnings.FirstOrDefault(w => string.Equals(w.Code, "gateway-not-ready", StringComparison.OrdinalIgnoreCase));
            if (gatewayNotReadyWarning != null)
            {
                GatewayStartupReason = gatewayNotReadyWarning.Message;
            }

            var detachedFallback = warnings.FirstOrDefault(w => string.Equals(w.Code, "gateway-detached-fallback", StringComparison.OrdinalIgnoreCase));
            if (detachedFallback != null)
            {
                GatewayStateText = "Running via detached fallback";
                GatewayStartupReason = detachedFallback.Message;
                ServiceStateText = "Service unhealthy / detached fallback in use.";
            }
        }

        if (!string.IsNullOrWhiteSpace(GatewayStartupReason))
        {
            LastFailureReason = GatewayStartupReason;
        }

        if (result.Output is OpenClawCommandSummary statusSummary)
        {
            UpdateGatewayStateFromStatus(statusSummary);
        }

        if (result.Output is RestoreSummary restore)
        {
            if (restore.Preview != null)
                ImpactSummary = FormatRestoreImpact(restore.Preview);
            RollbackSnapshot = restore.SnapshotPath;
            if (!string.IsNullOrWhiteSpace(restore.SnapshotPath))
            {
                LastBackupText = restore.SnapshotPath;
            }
        }
        else if (result.Output is RecoverSummary recover)
        {
            if (recover.Restore?.Preview != null)
            {
                ImpactSummary = FormatRestoreImpact(recover.Restore.Preview);
                RollbackSnapshot = recover.Restore.SnapshotPath;
                if (!string.IsNullOrWhiteSpace(recover.Restore.SnapshotPath))
                {
                    LastBackupText = recover.Restore.SnapshotPath;
                }
            }

            if (recover.Steps.Count > 0)
            {
                StepsText = string.Join(Environment.NewLine, recover.Steps.Select(step =>
                    $"{step.Step}: {step.Status}{(string.IsNullOrWhiteSpace(step.Error) ? string.Empty : $" ({step.Error})")}"));
            }

            NextEscalation = recover.NextEscalation;
        }
        else if (result.Output is FixSummary fix)
        {
            RollbackSnapshot = fix.SnapshotPath;
        }
        else if (result.Output is OpenClawBackupCreateSummary backupCreate)
        {
            ResultSummary = backupCreate.Verified ? "Backup created and verified." : "Backup created.";
            ImpactSummary = $"Archive: {backupCreate.ArchivePath} | Scope: {(backupCreate.OnlyConfig ? "config" : backupCreate.IncludeWorkspace ? "full" : "config+creds+sessions")}";
            LastBackupText = backupCreate.ArchivePath;
        }
        else if (result.Output is OpenClawBackupVerifySummary backupVerify)
        {
            ResultSummary = "Backup verified.";
            ImpactSummary = $"Archive: {backupVerify.ArchivePath} | Assets: {backupVerify.AssetCount}";
            LastBackupText = backupVerify.ArchivePath;
        }
        else if (result.Output is RollbackSummary rollback)
        {
            ImpactSummary = FormatRestoreImpact(rollback.Preview);
        }
        else if (result.Output is GatewayRepairSummary gatewayRepair)
        {
            ResultSummary = $"Gateway {gatewayRepair.Outcome}";
            ImpactSummary =
                $"Runtime: {gatewayRepair.Detection.RuntimeVersion ?? "(unknown)"} | Config: {gatewayRepair.Detection.ConfigVersion ?? "(unknown)"} | Service: {gatewayRepair.Detection.GatewayServiceExists} | Active: {gatewayRepair.Detection.GatewayActive}";
            if (gatewayRepair.Steps.Count > 0)
            {
                StepsText = string.Join(Environment.NewLine, gatewayRepair.Steps.Select(step =>
                    $"{step.Step}: {step.Status}{(string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : $" ({step.Detail})")}"));
            }
            if (gatewayRepair.SuggestedActions.Count > 0)
            {
                var suggestions = string.Join(Environment.NewLine, gatewayRepair.SuggestedActions.Select(step => $"Suggested: {step}"));
                StepsText = string.IsNullOrWhiteSpace(StepsText) ? suggestions : $"{StepsText}{Environment.NewLine}{suggestions}";
            }
            if (gatewayRepair.Notes.Count > 0)
            {
                var notes = string.Join(Environment.NewLine, gatewayRepair.Notes.Select(step => $"Note: {step}"));
                StepsText = string.IsNullOrWhiteSpace(StepsText) ? notes : $"{StepsText}{Environment.NewLine}{notes}";
            }
            RollbackSnapshot = gatewayRepair.SnapshotPath;

            if (gatewayRepair.Inventory is { } inventory)
            {
                GatewayHealthText = $"Running: {inventory.Gateway.IsRunning} | Reachable: {inventory.Gateway.IsReachable} | Can tail logs: {inventory.Gateway.CanTailLogs}";
                IsGatewayHealthy = inventory.Gateway.IsRunning && inventory.Gateway.IsReachable;
                LastCheckedTime = DateTimeOffset.Now.ToString("g");
                ActiveRuntimeText = inventory.ActiveRuntime is { } runtimeInfo
                    ? $"{runtimeInfo.Version ?? "unknown"} ({runtimeInfo.Kind})"
                    : "(none)";
                ConfigPathText = inventory.Config?.ConfigPath ?? "(unknown)";

                if (inventory.Services.Count > 0)
                {
                    var primaryService = inventory.Services[0];
                    ServiceStateText = $"Exists: {primaryService.Exists} | Active: {primaryService.IsActive}";
                }
            }

            if (string.Equals(gatewayRepair.Outcome, "confirmation-needed", StringComparison.OrdinalIgnoreCase))
            {
                GatewayStateText = "Needs confirmation";
            }
        }
        else if (result.Output is GatewayTroubleshootSummary troubleshoot)
        {
            IsGatewayHealthy = troubleshoot.GatewayHealthy;
            LastCheckedTime = DateTimeOffset.Now.ToString("g");
            GatewayStartupReason = troubleshoot.StartupReason ?? "No startup reason found in logs or doctor output.";
            GatewayTroubleshootDetails = BuildGatewayTroubleshootDetails(troubleshoot);
            GatewayHealthText = BuildGatewayStatusLine(troubleshoot.GatewayStatus);
            ResultSummary = troubleshoot.GatewayHealthy ? "Gateway healthy." : "Gateway not healthy.";
        }
        else if (result.Output is OpenClawRebuildSummary rebuild)
        {
            ResultSummary = result.Success ? "OpenClaw rebuild completed." : result.Error ?? "OpenClaw rebuild failed.";
            var preserve = rebuild.PreserveScopes.Count == 0 ? "(none)" : string.Join(", ", rebuild.PreserveScopes);
            ImpactSummary = $"Preserve: {preserve} | Strategy: {rebuild.RuntimeStrategy}";
            RollbackSnapshot = rebuild.BackupPath;

            var removed = rebuild.RemovedItems.Count == 0 ? "none" : string.Join(", ", rebuild.RemovedItems);
            var installed = rebuild.InstalledItems.Count == 0 ? "none" : string.Join(", ", rebuild.InstalledItems);
            RebuildPlanText =
                $"Backup: {rebuild.BackupPath}{Environment.NewLine}" +
                $"Confirm destructive: {(RebuildConfirmDestructive ? "yes" : "no")}{Environment.NewLine}" +
                $"Reset mode: {rebuild.ResetMode}{Environment.NewLine}" +
                $"Preserve: {preserve}{Environment.NewLine}" +
                $"Remove: {removed}{Environment.NewLine}" +
                $"Install: {installed}{Environment.NewLine}" +
                $"Strategy: {rebuild.RuntimeStrategy}";

            var gatewayUrl = rebuild.Verification.GatewayUrl ?? rebuild.Verification.BrowserDiagnostics?.LocalUrl ?? "(unknown)";
            var dashboardUrl = rebuild.Verification.BrowserDiagnostics?.DashboardUrl ?? "(unknown)";
            var logsStatus = rebuild.Verification.LogsStatus?.TimedOut == true
                ? "timed out (expected)"
                : rebuild.Verification.LogsStatus?.ExitCode.ToString() ?? "(unknown)";
            var gatewayStatus = rebuild.Verification.GatewayStatus?.ExitCode.ToString() ?? "(unknown)";
            var dashboardStatus = rebuild.Verification.DashboardStatus?.ExitCode.ToString() ?? "(unknown)";
            RebuildVerificationText =
                $"Gateway URL: {gatewayUrl}{Environment.NewLine}" +
                $"Dashboard URL: {dashboardUrl}{Environment.NewLine}" +
                $"Gateway status exit: {gatewayStatus}{Environment.NewLine}" +
                $"Dashboard status exit: {dashboardStatus}{Environment.NewLine}" +
                $"Logs: {logsStatus}{Environment.NewLine}" +
                $"Gateway healthy: {rebuild.Verification.GatewayHealthy}{Environment.NewLine}" +
                $"Logs available: {rebuild.Verification.LogsAvailable}{Environment.NewLine}" +
                $"Browser ready: {rebuild.Verification.BrowserReady}";

            if (rebuild.Verification.VerificationFailures is { Count: > 0 } failures)
            {
                var failureText = string.Join(Environment.NewLine, failures.Select(failure => $"Failure: {failure}"));
                RebuildVerificationText = $"{RebuildVerificationText}{Environment.NewLine}{failureText}";
            }

            if (rebuild.Verification.VerificationWarnings is { Count: > 0 } verifyWarnings)
            {
                var warningText = string.Join(Environment.NewLine, verifyWarnings.Select(warning => $"Warning: {warning}"));
                RebuildVerificationText = $"{RebuildVerificationText}{Environment.NewLine}{warningText}";
            }

            if (rebuild.Verification.GatewayDiagnostics is { } diagnostics)
            {
                var diagnosticLines = new List<string>
                {
                    $"Service exists: {diagnostics.ServiceExists}",
                    $"Service active: {diagnostics.ServiceActive}"
                };
                if (!string.IsNullOrWhiteSpace(diagnostics.ServiceEntrypoint))
                {
                    diagnosticLines.Add($"Service entrypoint: {diagnostics.ServiceEntrypoint}");
                }
                if (!string.IsNullOrWhiteSpace(diagnostics.ServiceStatus))
                {
                    diagnosticLines.Add($"Service status: {diagnostics.ServiceStatus}");
                }
                if (diagnostics.ServiceTaskStatus != null)
                {
                    diagnosticLines.Add($"Service/task command: {diagnostics.ServiceTaskStatus.Command} (exit {diagnostics.ServiceTaskStatus.ExitCode})");
                }
                if (!string.IsNullOrWhiteSpace(diagnostics.Remediation))
                {
                    diagnosticLines.Add($"Remediation: {diagnostics.Remediation}");
                }
                RebuildVerificationText = $"{RebuildVerificationText}{Environment.NewLine}{string.Join(Environment.NewLine, diagnosticLines)}";
            }
            IsGatewayHealthy = rebuild.Verification.GatewayHealthy;
            LastCheckedTime = DateTimeOffset.Now.ToString("g");

            if (rebuild.Steps.Count > 0)
            {
                StepsText = string.Join(Environment.NewLine, rebuild.Steps.Select(step =>
                    $"{step.StepId}: {step.Status}{(string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : $" ({step.Detail})")}"));
            }

            if (rebuild.Inventory is { } inventory)
            {
                GatewayHealthText = $"Running: {inventory.Gateway.IsRunning} | Reachable: {inventory.Gateway.IsReachable} | Can tail logs: {inventory.Gateway.CanTailLogs}";
                ConfigPathText = inventory.Config?.ConfigPath ?? "(unknown)";

                if (inventory.Services.Count > 0)
                {
                    var primaryService = inventory.Services[0];
                    ServiceStateText = $"Exists: {primaryService.Exists} | Active: {primaryService.IsActive}";
                }
            }
        }
        else if (result.Output is OpenClawCleanupSummary cleanup)
        {
            ResultSummary = cleanup.Applied ? "Cleanup applied." : "Cleanup preview.";
            ArtifactsText = cleanup.Candidates.Count == 0
                ? "No related artifacts found."
                : string.Join(Environment.NewLine, cleanup.Candidates.Select(artifact =>
                    $"{artifact.Kind}: {artifact.Path} {(artifact.IsSafeToClean ? "(safe)" : "(review)") } - {artifact.Summary}"));
        }
        else if (result.Output is BackupDiffSummary diff)
        {
            ResultSummary = "Backup diff ready.";
            DiffSummary =
                $"Added: {diff.AddedAssets.Count} | Removed: {diff.RemovedAssets.Count} | Changed: {diff.ChangedAssets.Count}";
        }
        else if (result.Output is OpenClawBackupCreateSummary createSummary)
        {
            ResultSummary = createSummary.Verified ? "Backup created and verified." : "Backup created.";
            var scope = createSummary.OnlyConfig
                ? "config"
                : createSummary.IncludeWorkspace ? "full" : "config+creds+sessions";
            ImpactSummary = $"Archive: {createSummary.ArchivePath} | Scope: {scope} | Assets: {createSummary.Assets.Count}";
        }
        else if (result.Output is BackupVerificationSummary verifySummary)
        {
            ResultSummary = "Backup verified.";
            ImpactSummary =
                $"Archive: {verifySummary.ArchivePath} | Entries: {verifySummary.EntryCount} | Assets: {verifySummary.AssetCount}";
        }
        else if (result.Output is BackupExportSummary exportSummary)
        {
            ResultSummary = exportSummary.Verified ? "Backup exported and verified." : "Backup exported.";
            ImpactSummary = $"Archive: {exportSummary.ArchivePath} | Scope: {exportSummary.Scope}";
            LastBackupText = exportSummary.ArchivePath;
        }
        else if (result.Output is BackupScheduleSummary schedule)
        {
            ResultSummary = schedule.Applied ? "Schedule updated." : "Schedules loaded.";
            ScheduleSummary = schedule.Schedules.Count == 0
                ? "No schedules configured."
                : string.Join(Environment.NewLine, schedule.Schedules.Select(entry =>
                    $"{entry.Id}: {entry.Mode} {entry.Kind} {entry.Expression}"));
        }
        else if (result.Output is BrowserDiagnosticsSummary diagnostics)
        {
            ResultSummary = "Browser diagnostics ready.";
            ImpactSummary = $"Local: {diagnostics.LocalUrl ?? "(unknown)"} | Dashboard: {diagnostics.DashboardUrl ?? "(unknown)"}";
            BrowserDiagnosticsText = diagnostics.Warnings.Count > 0
                ? string.Join(Environment.NewLine, diagnostics.Warnings.Select(warning => $"Warning: {warning}"))
                : "No browser warnings detected.";
            DashboardUrlText = diagnostics.DashboardUrl ?? diagnostics.LocalUrl;
            TokenStateText = diagnostics.TokenPresent ? "Token present" : "Token missing";
        }
        else if (result.Output is GatewayTokenSummary token)
        {
            ResultSummary = token.Revealed ? "Gateway token revealed." : "Gateway token masked.";
            GatewayTokenText = token.TokenMasked ?? "(missing)";
            TokenStateText = token.TokenPresent ? "Token present" : "Token missing";
        }
        else if (result.Output is SetupAssistantSummary setup)
        {
            SetupSummary = setup.Summary;
            SetupSecurityStatus = setup.Security.Status;
            SetupSecurityDetails = setup.Security.Checks.Count == 0
                ? null
                : string.Join(Environment.NewLine, setup.Security.Checks.Select(check =>
                    $"{check.Name}: {(check.Success ? "ok" : "needs attention")}{(string.IsNullOrWhiteSpace(check.Detail) ? string.Empty : $" ({check.Detail})")}"));
            SetupStepsText = setup.Steps.Count == 0
                ? null
                : string.Join(Environment.NewLine, setup.Steps.Select(step =>
                    $"{step.Step}: {step.Status}{(string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : $" ({step.Detail})")}"));
            SetupRuntimeText = setup.InstallRuntime;
            if (!string.IsNullOrWhiteSpace(setup.DashboardUrl))
            {
                DashboardUrlText = setup.DashboardUrl;
            }
            IsGatewayHealthy = setup.GatewayHealthy;
            if (setup.DetachedFallback)
            {
                GatewayStateText = "Running via detached fallback";
            }
            else if (setup.GatewayHealthy)
            {
                GatewayStateText = "Healthy";
            }
            else if (!string.Equals(GatewayStateText, "Repairing", StringComparison.OrdinalIgnoreCase))
            {
                GatewayStateText = "Unstable";
            }
        }
        else if (result.Output is string outputPath)
        {
            if (outputPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                DashboardUrlText = outputPath;
            }
            else
            {
                ImpactSummary = $"Archive: {outputPath}";
                LastBackupText = outputPath;
            }
        }

        if (gatewayNotReadyWarning != null)
        {
            IsGatewayHealthy = false;
        }

        CurrentIssue = ResolveCurrentIssue();

        if (result.Output is IDiagnosticsBundleCarrier carrier)
        {
            DiagnosticsBundle = carrier.DiagnosticsBundlePath;
        }

        if (result.Output is IJournalCarrier journalCarrier)
        {
            JournalPath = journalCarrier.JournalPath;
        }
    }

    private static string FormatRestoreImpact(RestorePreview preview)
    {
        return $"Scope: {preview.Scope} | Entries: {preview.RestorePayloadEntries} | Overwrite: {preview.OverwritePayloadEntries} | Targets: {preview.Assets.Count}";
    }

    private string ResolveCurrentIssue()
    {
        if (GatewayStateText == "Unknown")
        {
            return "Gateway status unknown. Run Check Status.";
        }

        if (GatewayStateText == "Running via detached fallback")
        {
            return "Service unhealthy. Gateway is running via detached fallback. Dashboard access is available.";
        }

        if (GatewayStateText == "Repairing")
        {
            return "Repair in progress. Dashboard access is disabled until health is restored.";
        }

        if (!IsGatewayHealthy && gatewayWasHealthy)
        {
            return "Gateway unstable. Started successfully, then became unreachable again. Dashboard access is disabled until health is restored.";
        }

        if (!IsGatewayHealthy)
        {
            return "Gateway broken. Dashboard access is disabled until health is restored.";
        }

        return "Gateway healthy. Dashboard is ready.";
    }

    private bool EnsureGatewayHealthy(string actionLabel)
    {
        if (IsGatewayHealthy)
        {
            return true;
        }

        StatusText = "Gateway not healthy";
        ResultSummary = $"Gateway not healthy. {actionLabel} is blocked until it is running.";
        ImpactSummary = string.IsNullOrWhiteSpace(GatewayStartupReason)
            ? "Run the gateway troubleshooting ladder to capture the startup reason."
            : GatewayStartupReason;
        return false;
    }

    private static string BuildGatewayTroubleshootDetails(GatewayTroubleshootSummary summary)
    {
        var sections = new List<string>
        {
            BuildCommandExcerpt("openclaw status", summary.Status),
            BuildCommandExcerpt("openclaw gateway status", summary.GatewayStatus),
            BuildCommandExcerpt("openclaw logs --follow", summary.Logs),
            BuildCommandExcerpt("openclaw doctor", summary.Doctor),
            BuildCommandExcerpt("openclaw channels status --probe", summary.ChannelsProbe)
        };

        return string.Join(Environment.NewLine + Environment.NewLine, sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    private static string BuildGatewayStatusLine(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return "(no gateway status output)";
        }

        var lines = summary.StdOut.Concat(summary.StdErr)
            .Where(line => line.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || line.Contains("RPC probe", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Service", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim());
        var joined = string.Join(Environment.NewLine, lines);
        return string.IsNullOrWhiteSpace(joined) ? "(gateway status output unavailable)" : joined;
    }

    private static string BuildCommandExcerpt(string title, OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return $"{title}{Environment.NewLine}(no output)";
        }

        var lines = summary.StdOut.Concat(summary.StdErr)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(8)
            .ToArray();
        var output = lines.Length == 0 ? "(no output)" : string.Join(Environment.NewLine, lines);
        var meta = summary.TimedOut ? "timed out" : $"exit {summary.ExitCode}";
        return $"{title} ({meta}){Environment.NewLine}{output}";
    }

    private void UpdateGatewayStateFromStatus(OpenClawCommandSummary summary)
    {
        if (summary.Command == null
            || !summary.Command.Contains("gateway", StringComparison.OrdinalIgnoreCase)
            || !summary.Command.Contains("status", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LastCheckedTime = DateTimeOffset.Now.ToString("g");

        var lines = summary.StdOut.Concat(summary.StdErr)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        var runtimeLine = lines.FirstOrDefault(line => line.StartsWith("Runtime", StringComparison.OrdinalIgnoreCase));
        var rpcLine = lines.FirstOrDefault(line => line.StartsWith("RPC probe", StringComparison.OrdinalIgnoreCase));
        var serviceLine = lines.FirstOrDefault(line => line.StartsWith("Service", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(serviceLine))
        {
            ServiceStateText = serviceLine;
        }

        var runtimeRunning = runtimeLine != null
            && runtimeLine.Contains("running", StringComparison.OrdinalIgnoreCase);
        var rpcOk = rpcLine != null
            && rpcLine.Contains("ok", StringComparison.OrdinalIgnoreCase);

        GatewayHealthText = string.Join(" | ", new[]
        {
            runtimeLine ?? "Runtime: (unknown)",
            rpcLine ?? "RPC probe: (unknown)"
        });

        IsGatewayHealthy = runtimeRunning && rpcOk;

        if (!IsGatewayHealthy && string.IsNullOrWhiteSpace(GatewayStartupReason))
        {
            LastFailureReason = runtimeLine ?? rpcLine ?? "Gateway not ready.";
        }
    }

    private void UpdateHomeActionState()
    {
        if (string.Equals(GatewayStateText, "Healthy", StringComparison.OrdinalIgnoreCase))
        {
            PrimaryHomeActionLabel = "Open Dashboard";
            PrimaryHomeActionCommand = RunDashboardOpenCommand;
            ShowHomeDashboardAction = false;
            ShowHomeRepairAction = true;
            return;
        }

        if (string.Equals(GatewayStateText, "Running via detached fallback", StringComparison.OrdinalIgnoreCase))
        {
            PrimaryHomeActionLabel = "Repair Gateway";
            PrimaryHomeActionCommand = RunGatewayRepairCommand;
            ShowHomeDashboardAction = true;
            ShowHomeRepairAction = false;
            return;
        }

        if (string.Equals(GatewayStateText, "Broken", StringComparison.OrdinalIgnoreCase))
        {
            PrimaryHomeActionLabel = "Rebuild OpenClaw";
            PrimaryHomeActionCommand = RunOpenClawRebuildCommand;
            ShowHomeDashboardAction = false;
            ShowHomeRepairAction = true;
            return;
        }

        PrimaryHomeActionLabel = "Repair Gateway";
        PrimaryHomeActionCommand = RunGatewayRepairCommand;
        ShowHomeDashboardAction = true;
        ShowHomeRepairAction = false;
    }

    private void UpdateFilteredActions()
    {
        var query = NormalizeSearchQuery(ActionSearchText);
        var sorted = SortActions(Actions);

        // Apply category filter
        var categoryFiltered = SelectedCategory switch
        {
            "gateway" => sorted.Where(a => MatchesCategory(a, "gateway")).ToList(),
            "backup" => sorted.Where(a => MatchesCategory(a, "backup")).ToList(),
            "tools" => sorted.Where(a => a.Group == "tools" && !MatchesCategory(a, "gateway") && !MatchesCategory(a, "backup")).ToList(),
            "danger" => sorted.Where(a => a.Group == "danger" || a.Capabilities.HasFlag(ActionCapability.Destructive)).ToList(),
            _ => sorted
        };

        // Apply search filter
        var filtered = string.IsNullOrWhiteSpace(query)
            ? categoryFiltered
            : categoryFiltered.Where(action =>
            {
                var haystack = $"{action.Id} {action.Label} {action.Description} {action.Group} {string.Join(" ", action.Tags ?? Array.Empty<string>())}".ToLowerInvariant();
                return haystack.Contains(query, StringComparison.Ordinal);
            }).ToList();

        filteredActions.Clear();
        foreach (var action in filtered)
        {
            filteredActions.Add(action);
        }

        ActionsMetaText = BuildActionsMetaText(sorted.Count, filtered.Count, query);
    }

    private static bool MatchesCategory(ActionDescriptor action, string category)
    {
        var id = action.Id;
        var label = action.Label;
        return category switch
        {
            "gateway" => id.Contains("gateway", StringComparison.OrdinalIgnoreCase)
                || label.Contains("gateway", StringComparison.OrdinalIgnoreCase)
                || id.Contains("dashboard", StringComparison.OrdinalIgnoreCase),
            "backup" => id.Contains("backup", StringComparison.OrdinalIgnoreCase)
                || id.Contains("restore", StringComparison.OrdinalIgnoreCase)
                || id.Contains("rollback", StringComparison.OrdinalIgnoreCase)
                || label.Contains("backup", StringComparison.OrdinalIgnoreCase)
                || label.Contains("restore", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static List<ActionDescriptor> SortActions(IReadOnlyList<ActionDescriptor> actions)
    {
        var groupOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["easy"] = 0,
            ["tools"] = 1,
            ["danger"] = 2
        };

        return actions
            .OrderBy(action => groupOrder.TryGetValue(action.Group, out var order) ? order : 1)
            .ThenBy(action => action.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeSearchQuery(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static string BuildActionsMetaText(int total, int shown, string query)
    {
        if (total == 0)
        {
            return "No actions available.";
        }

        if (!string.IsNullOrWhiteSpace(query) && shown == 0)
        {
            return $"No matches for \"{query}\".";
        }

        if (shown < total)
        {
            return $"Showing {shown} of {total}. Refine search to find more.";
        }

        return $"Showing {shown} action{(shown == 1 ? string.Empty : "s")}.";
    }

    private static string MapBackupLevelToScope(BackupLevel level)
    {
        return level switch
        {
            BackupLevel.Full => "full",
            BackupLevel.OpenClawOnly => "config+creds+sessions",
            BackupLevel.ConfigOnly => "config",
            _ => "full"
        };
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var clipboard = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
        if (clipboard is null)
        {
            StatusText = "Clipboard unavailable";
            return;
        }

        await clipboard.SetTextAsync(Logs ?? string.Empty);
        StatusText = "Logs copied to clipboard";
    }

    [RelayCommand]
    private Task OpenLogsFileAsync()
    {
        var logRoot = Path.Combine(context.TempDirectory, "openclaw");
        if (!Directory.Exists(logRoot))
        {
            StatusText = "OpenClaw logs folder not found";
            return Task.CompletedTask;
        }

        var candidates = Directory.EnumerateFiles(logRoot, "openclaw-*.log")
            .Concat(Directory.EnumerateFiles(logRoot, "gateway-detached.log"));
        var latest = candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(latest))
        {
            StatusText = "No log file found";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = latest,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open log file: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open link: {ex.Message}";
        }
    }

    private void UpdateSetupState(string actionId, ActionResult result)
    {
        if (!actionId.StartsWith("setup-", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!result.Success)
        {
            return;
        }

        if (string.Equals(actionId, "setup-advanced", StringComparison.OrdinalIgnoreCase))
        {
            allowRepoRuntime = true;
        }

        EvaluateSetupMode();
        if (!IsSetupMode)
        {
            BeginInitialStatusCheck();
        }
    }

    private void EvaluateSetupMode()
    {
        var force = Environment.GetEnvironmentVariable("RECLAW_SETUP_FORCE");
        if (string.Equals(force, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(force, "true", StringComparison.OrdinalIgnoreCase))
        {
            IsSetupMode = true;
            SetupSummary = "OpenClaw is not installed yet.";
            PopulateEnvironmentHint();
            return;
        }

        var candidate = OpenClawLocator.ResolveWithSource(context);
        if (candidate == null)
        {
            IsSetupMode = true;
            SetupSummary = "OpenClaw is not installed yet.";
            PopulateEnvironmentHint();
            return;
        }

        if (string.Equals(candidate.Source, "repo-fallback", StringComparison.OrdinalIgnoreCase) && !allowRepoRuntime)
        {
            IsSetupMode = true;
            SetupSummary = "OpenClaw source runtime detected. Use Advanced Install to finish setup.";
            PopulateEnvironmentHint();
            return;
        }

        IsSetupMode = false;
    }

    private void PopulateEnvironmentHint()
    {
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        SetupRuntimeText = $"{os} ({arch})";

        if (OperatingSystem.IsWindows())
        {
            var wsl = OpenClawLocator.IsWsl2Default() ? "WSL2 available" : "WSL2 not available";
            SetupEnvironmentHint = $"{wsl}. Windows install will prefer WSL2 when available, native fallback otherwise.";
        }
        else if (OperatingSystem.IsMacOS())
        {
            SetupEnvironmentHint = "macOS detected. Install will use npm to install the OpenClaw CLI.";
        }
        else
        {
            SetupEnvironmentHint = "Linux detected. Install will use npm to install the OpenClaw CLI.";
        }
    }

    // ── Tray settings ──

    public void OpenTraySettings()
    {
        InitTrayCommandItems();
        TraySettingsOpen = true;
    }

    [RelayCommand]
    private void CloseTraySettings()
    {
        TraySettingsOpen = false;
        SaveTrayCommands();
    }

    private void InitTrayCommandItems()
    {
        TrayCommandItems.Clear();
        var enabled = new HashSet<string>(TrayCommands, StringComparer.OrdinalIgnoreCase);
        foreach (var action in Actions)
        {
            TrayCommandItems.Add(new TrayCommandItem
            {
                Id = action.Id,
                Label = action.Label,
                Emoji = action.Emoji,
                IsEnabled = enabled.Count == 0 || enabled.Contains(action.Id)
            });
        }
    }

    private void SaveTrayCommands()
    {
        TrayCommands = TrayCommandItems
            .Where(c => c.IsEnabled)
            .Select(c => c.Id)
            .ToList();
    }

    // ── File/folder browse ──

    [RelayCommand]
    private async Task BrowseArchivePathAsync()
    {
        var path = await PickFileAsync("Select backup archive", "Zip files", "*.zip");
        if (!string.IsNullOrWhiteSpace(path))
            ArchivePath = path;
    }

    [RelayCommand]
    private async Task BrowseDestinationPathAsync()
    {
        var path = await PickFolderAsync("Select destination folder");
        if (!string.IsNullOrWhiteSpace(path))
            DestinationPath = path;
    }

    [RelayCommand]
    private async Task BrowseBackupOutputPathAsync()
    {
        var path = await PickFolderAsync("Select backup output folder");
        if (!string.IsNullOrWhiteSpace(path))
            BackupCreateOutputPath = path;
    }

    [RelayCommand]
    private async Task BrowseRollbackSourcePathAsync()
    {
        var path = await PickFileAsync("Select rollback snapshot", "Zip files", "*.zip");
        if (!string.IsNullOrWhiteSpace(path))
            RollbackSourcePath = path;
    }

    [RelayCommand]
    private async Task BrowseDiffLeftAsync()
    {
        var path = await PickFileAsync("Select left archive for diff", "Zip files", "*.zip");
        if (!string.IsNullOrWhiteSpace(path))
            DiffLeftArchive = path;
    }

    [RelayCommand]
    private async Task BrowseDiffRightAsync()
    {
        var path = await PickFileAsync("Select right archive for diff", "Zip files", "*.zip");
        if (!string.IsNullOrWhiteSpace(path))
            DiffRightArchive = path;
    }

    [RelayCommand]
    private void RevealArchivePath()
    {
        RevealInExplorer(ArchivePath);
    }

    [RelayCommand]
    private void RevealDestinationPath()
    {
        RevealInExplorer(DestinationPath);
    }

    private void RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                StatusText = $"Path not found: {path}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to open: {ex.Message}";
        }
    }

    private static async Task<string?> PickFileAsync(string title, string filterName, string pattern)
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(filterName) { Patterns = new[] { pattern } },
                    new Avalonia.Platform.Storage.FilePickerFileType("All files") { Patterns = new[] { "*" } }
                }
            });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private static async Task<string?> PickFolderAsync(string title)
    {
        var window = GetMainWindow();
        if (window == null) return null;

        var folders = await window.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private static Window? GetMainWindow()
    {
        return (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    }
}

public class TrayCommandItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;

    private bool isEnabled;
    public bool IsEnabled
    {
        get => isEnabled;
        set => SetProperty(ref isEnabled, value);
    }
}
