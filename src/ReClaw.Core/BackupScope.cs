using System;
using System.Collections.Generic;
using System.Linq;

namespace ReClaw.Core;

internal sealed record BackupScopeInfo(string Raw, HashSet<string> Tokens);

internal static class BackupScope
{
    private const string ScopeExample = "full | config | creds | sessions | config+creds | config+creds+sessions";

    private static readonly Dictionary<string, string> TokenAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["credentials"] = "creds",
        ["credential"] = "creds"
    };

    private static readonly HashSet<string> AllowedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "config",
        "creds",
        "sessions",
        "workspace"
    };

    private static readonly HashSet<string> ConfigEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "openclaw.json",
        ".env",
        "user.md",
        "identity.md"
    };

    private static readonly HashSet<string> CredentialEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "credentials",
        "credential",
        "auth",
        "oauth",
        "secrets",
        "devices.json",
        "auth-profiles.json",
        "credentials.json",
        "tokens.json"
    };

    private static readonly HashSet<string> SessionEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "sessions",
        "memory",
        "logs",
        "history",
        "databases"
    };

    private static readonly HashSet<string> WorkspaceEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "workspace",
        "workspaces"
    };

    public static BackupScopeInfo Parse(string? scope, string defaultScope = "full")
    {
        var rawScope = !string.IsNullOrWhiteSpace(scope)
            ? scope.Trim().ToLowerInvariant()
            : defaultScope.Trim().ToLowerInvariant();

        if (rawScope == "full")
        {
            return new BackupScopeInfo("full", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "full" });
        }

        var tokens = rawScope
            .Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(token => TokenAliases.TryGetValue(token, out var alias) ? alias : token)
            .ToList();

        if (tokens.Count == 0)
        {
            throw new ArgumentException($"Invalid backup scope '{scope}'. Use: {ScopeExample}.");
        }

        foreach (var token in tokens)
        {
            if (!AllowedTokens.Contains(token))
            {
                throw new ArgumentException($"Invalid backup scope token '{token}'. Use: {ScopeExample}.");
            }
        }

        var unique = tokens
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BackupScopeInfo(string.Join('+', unique), new HashSet<string>(unique, StringComparer.OrdinalIgnoreCase));
    }

    public static bool ShouldIncludeTopLevelEntry(string entryName, BackupScopeInfo scopeInfo)
    {
        if (scopeInfo.Tokens.Contains("full"))
        {
            return true;
        }

        var category = CategorizeTopLevelEntry(entryName);
        return scopeInfo.Tokens.Contains(category);
    }

    public static string CategorizeTopLevelEntry(string entryName)
    {
        var normalized = (entryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "other";
        }

        if (ConfigEntries.Contains(normalized))
        {
            return "config";
        }

        if (CredentialEntries.Contains(normalized))
        {
            return "creds";
        }

        if (SessionEntries.Contains(normalized))
        {
            return "sessions";
        }

        if (WorkspaceEntries.Contains(normalized))
        {
            return "workspace";
        }

        return "other";
    }
}
