using System.Text.RegularExpressions;
using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core.Workflows;

public static class ApprovalRules
{
    /// <summary>Any one of the step's approvers can decide.</summary>
    public const string Any = "any";
    /// <summary>Every approver of the step must approve; one rejection rejects.</summary>
    public const string All = "all";

    public static string Normalize(string? rule) => rule == All ? All : Any;
}

/// <summary>One step of an approval chain: a set of people and whether one or all of them must approve.</summary>
public sealed class ChainStep
{
    public string Name { get; set; } = "";
    public List<string> Approvers { get; set; } = new();
    public string Rule { get; set; } = ApprovalRules.Any;
}

public sealed record DepartmentInfo(long Id, string Name, List<string> FinalApprovers, string FinalRule);

/// <summary>Where the engine looks up chain templates and departments (the web app backs this with its database).</summary>
public interface IApprovalDirectory
{
    Task<List<ChainStep>?> GetChainAsync(long chainId, CancellationToken ct = default);
    Task<DepartmentInfo?> GetDepartmentAsync(long departmentId, CancellationToken ct = default);
}

/// <summary>
/// Reading, checking and storing approval chains as formula values:
/// { name, steps: [ { name, approvers: [email…], rule } ] }.
/// </summary>
public static class ChainValue
{
    public const int MaxSteps = 10;
    public const int MaxApproversPerStep = 10;
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Cleans a chain supplied by a person (form field or saved template). Problems are added to
    /// <paramref name="errors"/>; returns null when there are no steps at all.
    /// </summary>
    public static Dictionary<string, object?>? Normalize(object? raw, string? submitterEmail, List<string> errors)
    {
        if (raw is not Dictionary<string, object?> record || Get(record, "steps") is not List<object?> rawSteps || rawSteps.Count == 0)
            return null;
        if (rawSteps.Count > MaxSteps) errors.Add($"An approval chain can have at most {MaxSteps} steps.");

        var steps = new List<object?>();
        foreach (var rawStep in rawSteps.Take(MaxSteps))
        {
            var number = steps.Count + 1;
            var source = rawStep as Dictionary<string, object?> ?? Values.NewDict();
            var name = Values.ToText(Get(source, "name")).Trim();
            if (name.Length == 0) name = $"Step {number}";
            if (name.Length > 100) name = name[..100];

            var entries = Get(source, "approvers") switch
            {
                List<object?> list => Values.Flatten(list).Select(Values.ToText),
                string text => text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries),
                _ => Enumerable.Empty<string>(),
            };
            var approvers = new List<object?>();
            foreach (var entry in entries.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct())
            {
                if (entry.Length > 320 || !EmailPattern.IsMatch(entry))
                {
                    errors.Add($"{name}: '{(entry.Length > 60 ? entry[..60] + "…" : entry)}' is not an email address.");
                    continue;
                }
                if (submitterEmail is not null && string.Equals(entry, submitterEmail, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{name}: you cannot be an approver on your own request.");
                    continue;
                }
                approvers.Add(entry);
            }
            if (approvers.Count == 0) errors.Add($"{name}: add at least one approver.");
            if (approvers.Count > MaxApproversPerStep) errors.Add($"{name}: no more than {MaxApproversPerStep} approvers per step.");

            var step = Values.NewDict();
            step["name"] = name;
            step["approvers"] = approvers.Take(MaxApproversPerStep).ToList();
            step["rule"] = ApprovalRules.Normalize(Values.ToText(Get(source, "rule")));
            steps.Add(step);
        }

        var result = Values.NewDict();
        var chainName = Values.ToText(Get(record, "name")).Trim();
        result["name"] = chainName.Length > 100 ? chainName[..100] : chainName;
        result["steps"] = steps;
        return result;
    }

    public static List<ChainStep> ToSteps(object? value)
    {
        var steps = new List<ChainStep>();
        if (value is not Dictionary<string, object?> record || Get(record, "steps") is not List<object?> list) return steps;
        foreach (var item in list.OfType<Dictionary<string, object?>>())
        {
            steps.Add(new ChainStep
            {
                Name = Values.ToText(Get(item, "name")),
                Approvers = (Get(item, "approvers") as List<object?> ?? new List<object?>())
                    .Select(a => Values.ToText(a).Trim().ToLowerInvariant()).Where(a => a.Length > 0).Distinct().ToList(),
                Rule = ApprovalRules.Normalize(Values.ToText(Get(item, "rule"))),
            });
        }
        return steps;
    }

    /// <summary>Short read-only description, e.g. "Team lead: a@x.org → Principal: b@x.org, c@x.org (all)".</summary>
    public static string Describe(object? value) =>
        string.Join(" → ", ToSteps(value).Select(s =>
            $"{s.Name}: {string.Join(", ", s.Approvers)}{(s.Rule == ApprovalRules.All && s.Approvers.Count > 1 ? " (all must approve)" : "")}"));

    public static bool IsChain(object? value) =>
        value is Dictionary<string, object?> record && Get(record, "steps") is List<object?>;

    internal static object? Get(Dictionary<string, object?> record, string key)
    {
        if (record.TryGetValue(key, out var value)) return value;
        foreach (var kv in record)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}
