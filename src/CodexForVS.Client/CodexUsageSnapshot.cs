using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace CodexForVS.Client;

public sealed class CodexUsageWindow
{
    internal CodexUsageWindow(string fallbackLabel, int used, long? minutes, DateTimeOffset? reset)
    {
        PercentRemaining = 100 - used;
        Label = minutes switch
        {
            null => fallbackLabel,
            10080 => "Weekly",
            long m when m % 1440 == 0 => $"{m / 1440}d",
            long m when m % 60 == 0 => $"{m / 60}h",
            _ => $"{minutes}m"
        };
        ResetsAt = reset;
    }

    public string Label { get; }
    public int PercentRemaining { get; }
    public DateTimeOffset? ResetsAt { get; }
    public string Description => $"{Label} · {PercentRemaining}% left";
    public string ResetText => ResetsAt.HasValue
        ? "Resets " + ResetsAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)
        : "Reset time unavailable";
}

public sealed class CodexUsageSnapshot
{
    private CodexUsageSnapshot(List<CodexUsageWindow> windows, bool? ordinaryUsageAllowed, bool rateLimitReached)
    {
        Windows = windows.AsReadOnly();
        OrdinaryUsageAllowed = ordinaryUsageAllowed;
        RateLimitReached = rateLimitReached;
    }

    public IReadOnlyList<CodexUsageWindow> Windows { get; }
    public bool? OrdinaryUsageAllowed { get; }
    public bool RateLimitReached { get; }

    internal static CodexUsageSnapshot? Parse(JObject? payload)
    {
        JObject? buckets = payload?["rateLimitsByLimitId"] as JObject;
        JObject? limits = buckets?["codex"] as JObject ?? payload?["rateLimits"] as JObject;
        if (limits == null) return null;
        string? bucket = (limits["limitId"] as JValue)?.Value as string;
        if (bucket != null && bucket != "codex") return null;

        List<CodexUsageWindow> windows = new List<CodexUsageWindow>(2);
        AddWindow(windows, limits["primary"] as JObject, "Primary");
        AddWindow(windows, limits["secondary"] as JObject, "Secondary");
        bool? allowed = payload?["ordinaryUsageAllowed"]?.Type == JTokenType.Boolean ? (bool?)payload["ordinaryUsageAllowed"] : null;
        string? reason = (limits["rateLimitReachedType"] as JValue)?.Value as string;
        bool reached = reason == "rate_limit_reached";
        return windows.Count == 0 && allowed != false && !reached ? null : new CodexUsageSnapshot(windows, allowed, reached);
    }

    private static void AddWindow(List<CodexUsageWindow> windows, JObject? value, string fallbackLabel)
    {
        long? used = Integer(value?["usedPercent"]);
        if (!used.HasValue || used < 0 || used > 100) return;
        long? minutes = Integer(value?["windowDurationMins"]);
        long? seconds = Integer(value?["resetsAt"]);
        DateTimeOffset? reset = null;
        if (seconds.HasValue && seconds >= -62135596800L && seconds <= 253402300799L)
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
        windows.Add(new CodexUsageWindow(fallbackLabel, (int)used, minutes > 0 ? minutes : null, reset));
    }

    private static long? Integer(JToken? field) =>
        field?.Type == JTokenType.Integer && long.TryParse(field.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
}
