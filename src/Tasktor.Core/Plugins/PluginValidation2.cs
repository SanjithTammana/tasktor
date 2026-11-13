using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Plugins;

namespace Tasktor.Core.Plugins;

/// <summary>A checked contract-2 output. A null part was absent (keep the last one); <see cref="Warnings"/> name parts dropped
/// for a missing capability. <see cref="StateBytes"/> is <see cref="State"/> exactly as it will be written (and measured).</summary>
public sealed record PluginOutputCheck(
    IReadOnlyList<Card>? Cards,
    string? BrowserCard,
    PluginPage? Page,
    IReadOnlyList<PluginNotice> Notices,
    DateTimeOffset? WakeAt,
    bool ClearWake,
    IReadOnlyDictionary<string, JsonElement>? State,
    byte[]? StateBytes,
    IReadOnlyList<PluginRequest> Requests,
    IReadOnlyList<string> Warnings);

public static partial class PluginValidation
{
    internal static JsonTypeInfo<IReadOnlyDictionary<string, JsonElement>> StateInfo =>
        (JsonTypeInfo<IReadOnlyDictionary<string, JsonElement>>)ContractsJsonContext.Default.GetTypeInfo(typeof(IReadOnlyDictionary<string, JsonElement>))!;

    public const int MaxCards2 = 6;
    public const int MaxActionsPerCard2 = 4;
    public const int MaxSections = 20;
    public const int MaxFields = 20;
    public const int MaxTextField = 64 * 1024;
    public const int MaxNoticesPerRun = 3;
    public const int MaxNoticeButtons = 3;
    public const int MaxStateBytes = 4 * 1024;
    public const int MaxRequests = 5;
    public const int MaxBlockedHosts = 50;
    public static readonly TimeSpan MinWake = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxWake = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxBlock = TimeSpan.FromHours(12);

    /// <summary>
    /// Parse and check a contract-2 run's stdout (CONTRACT S3). Over a limit, invalid JSON or an unknown field → an error (the
    /// run fails). A part whose capability is not in <paramref name="granted"/> is dropped with a warning; requests pass through
    /// for the host to answer (it refuses ungranted ones itself).
    /// </summary>
    public static (PluginOutputCheck? Output, string? Error) CheckOutput2(PluginManifest manifest, IReadOnlySet<PluginCapability> granted, string stdout, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(stdout);
        if (System.Text.Encoding.UTF8.GetByteCount(stdout) > MaxOutputBytes)
        {
            return (null, "output is over 64 KB");
        }

        PluginOutput2? o;
        try
        {
            o = JsonSerializer.Deserialize(stdout.Trim(), ContractsJsonContext.Strict.PluginOutput2);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, "output is not valid: " + ex.Message.Split('\n')[0]);
        }

        if (o is null)
        {
            return (null, "no output");
        }

        if (o.Contract != 2)
        {
            return (null, "output contract " + o.Contract + " does not match plugin.json (2)");
        }

        // Nullable annotations do not reach list elements: a null anywhere in a list is a malformed output, not a crash.
        if (HasNullElement(o))
        {
            return (null, "output is not valid: a list contains null");
        }

        var declared = (manifest.Actions ?? []).ToDictionary(a => a.Id, StringComparer.Ordinal);
        var warnings = new List<string>();
        bool Has(PluginCapability c, bool present)
        {
            if (!present || granted.Contains(c))
            {
                return present;
            }

            warnings.Add("Asked for " + PluginCapabilityText.Name(c) + " without permission");
            return false;
        }

        IReadOnlyList<Card>? cards = null;
        if (o.Cards is { } raw)
        {
            var (checkedCards, cardError) = CheckCards(manifest, raw, startedAt, MaxCards2, MaxActionsPerCard2);
            if (cardError is not null)
            {
                return (null, cardError);
            }

            cards = checkedCards;
        }

        string? browserCard = null;
        if (Has(PluginCapability.BrowserCard, o.BrowserCard is not null))
        {
            if ((o.Cards ?? []).All(c => c.Id != o.BrowserCard))
            {
                return (null, "browser_card \"" + o.BrowserCard + "\" is not one of this output's cards");
            }

            browserCard = o.BrowserCard;
        }

        PluginPage? page = null;
        if (Has(PluginCapability.Page, o.Page is not null))
        {
            if (CheckPage(o.Page!, declared) is { } pageError)
            {
                return (null, "page: " + pageError);
            }

            page = o.Page;
        }

        var notices = new List<PluginNotice>();
        if (o.Notify is { Count: > MaxNoticesPerRun })
        {
            return (null, "more than 3 notifications in one run");
        }

        if (Has(PluginCapability.Notify, o.Notify is { Count: > 0 }))
        {
            foreach (var n in o.Notify!)
            {
                if (!CardIdPattern().IsMatch(n.Id) || string.IsNullOrWhiteSpace(n.Title))
                {
                    return (null, "a notification needs an id (lower-case letters, digits, '.', '_', '-') and a title");
                }

                if ((n.Buttons?.Count ?? 0) > MaxNoticeButtons || (n.Buttons ?? []).Any(b => !declared.ContainsKey(b)))
                {
                    return (null, "notification " + n.Id + ": up to 3 buttons, each a declared action");
                }

                notices.Add(n);
            }
        }

        DateTimeOffset? wake = null;
        var clear = false;
        if (Has(PluginCapability.Timer, o.WakeAt is not null || o.ClearWake))
        {
            if (o.WakeAt is { } w && (w < startedAt + MinWake || w > startedAt + MaxWake))
            {
                return (null, "wake_at must be 1 second to 7 days ahead");
            }

            wake = o.WakeAt;
            clear = o.ClearWake;
        }

        IReadOnlyDictionary<string, JsonElement>? state = null;
        byte[]? stateBytes = null;
        if (Has(PluginCapability.State, o.State is not null))
        {
            if (o.State!.Values.Any(v => v.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)))
            {
                return (null, "state values must be text, numbers or true/false");
            }

            stateBytes = JsonSerializer.SerializeToUtf8Bytes(o.State, StateInfo);
            if (stateBytes.Length > MaxStateBytes)
            {
                return (null, "state is over 4 KB");
            }

            state = o.State;
        }

        var requests = o.Requests ?? [];
        if (requests.Count > MaxRequests)
        {
            return (null, "more than 5 requests in one run");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in requests)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || !ids.Add(r.Id))
            {
                return (null, "each request needs its own id");
            }

            if (CheckRequest(r, startedAt) is { } requestError)
            {
                return (null, "request " + r.Id + ": " + requestError);
            }
        }

        return (new PluginOutputCheck(cards, browserCard, page, notices, wake, clear, state, stateBytes, requests, warnings), null);
    }

    private static bool HasNullElement(PluginOutput2 o)
    {
        static bool Any<T>(IReadOnlyList<T>? list) => list is not null && list.Any(x => x is null);
        return Any(o.Cards) || Any(o.Notify) || Any(o.Requests)
            || (o.Cards ?? []).Any(c => c is not null && (Any(c.Blocks) || Any(c.Actions)))
            || (o.Notify ?? []).Any(n => n is not null && Any(n.Buttons))
            || (o.Requests ?? []).OfType<SiteBlockRequest>().Any(r => Any(r.Hosts))
            || (o.Page is { } p && (Any(p.Sections) || p.Sections.Any(s => s is not null && (Any(s.Blocks) || Any(s.Actions)
                || (s.Form is { } f && (Any(f.Fields) || f.Fields.Any(x => x is not null && Any(x.Choices))))))));
    }

    /// <summary>A form submission checked against the fields its page declared (type, choices, length).</summary>
    public static string? CheckSubmit(PluginForm form, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (key, value) in values)
        {
            var field = form.Fields.FirstOrDefault(f => f.Key == key);
            if (field is null)
            {
                return "The form has no field " + key;
            }

            var ok = field.Type switch
            {
                PluginFieldType.Toggle => value is "true" or "false",
                PluginFieldType.Choice => field.Choices?.Contains(value, StringComparer.Ordinal) == true,
                PluginFieldType.Number => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d),
                _ => true,
            };
            if (!ok || value.Length > (field.MaxLength ?? MaxTextField))
            {
                return field.Label + ": that value is not allowed";
            }
        }

        return null;
    }

    /// <summary>The capability a request needs.</summary>
    public static PluginCapability Needs(PluginRequest r) => r switch
    {
        JobPauseRequest or JobResumeRequest => PluginCapability.ActJob,
        ArrangeRequest => PluginCapability.ActArrange,
        SiteBlockRequest or SiteUnblockRequest => PluginCapability.BrowserBlock,
        _ => throw new ArgumentOutOfRangeException(nameof(r), "unknown request"),
    };

    private static string? CheckRequest(PluginRequest r, DateTimeOffset now) => r switch
    {
        JobPauseRequest p when string.IsNullOrWhiteSpace(p.Job) => "job is empty",
        JobPauseRequest p when p.Mode is not (null or "polite" or "freeze") => "mode must be polite or freeze",
        JobResumeRequest p when string.IsNullOrWhiteSpace(p.Job) => "job is empty",
        ArrangeRequest a when string.IsNullOrWhiteSpace(a.Preset) => "preset is empty",
        SiteBlockRequest b when b.Hosts.Count is 0 or > MaxBlockedHosts => "1 to 50 hosts",
        SiteBlockRequest b when b.Hosts.Any(h => !ValidHost(h)) => "hosts are bare lower-case host names",
        SiteBlockRequest b when b.Until <= now || b.Until > now + MaxBlock => "until must be within 12 hours",
        _ => null,
    };

    private static bool ValidHost(string? h) =>
        h is { Length: > 0 and <= 253 } && h.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') && !h.StartsWith('.') && !h.EndsWith('.');

    private static string? CheckPage(PluginPage page, Dictionary<string, Contracts.Plugins.PluginActionDecl> declared)
    {
        if (string.IsNullOrWhiteSpace(page.Title))
        {
            return "title is empty";
        }

        if (page.Sections.Count > MaxSections)
        {
            return "more than 20 sections";
        }

        var forms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in page.Sections)
        {
            foreach (var b in s.Blocks ?? [])
            {
                if (CheckBlock(b, declared) is { } blockError)
                {
                    return blockError;
                }
            }

            if ((s.Actions?.Count ?? 0) > MaxActionsPerCard2 || (s.Actions ?? []).Any(a => !declared.ContainsKey(a.Id)))
            {
                return "a section has up to 4 buttons, each a declared action";
            }

            if (s.Form is { } f)
            {
                if (!CardIdPattern().IsMatch(f.Id) || !forms.Add(f.Id))
                {
                    return "form id \"" + f.Id + "\" is not allowed or is used twice";
                }

                if (f.Fields.Count is 0 or > MaxFields || f.Fields.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != f.Fields.Count)
                {
                    return "form " + f.Id + ": 1 to 20 fields with different keys";
                }

                foreach (var field in f.Fields)
                {
                    if ((field.Value?.Length ?? 0) > MaxTextField || field.MaxLength is < 1 or > MaxTextField)
                    {
                        return "form " + f.Id + ": field " + field.Key + " is over 64 KB";
                    }

                    if (field.Type == PluginFieldType.Choice && field.Choices is not { Count: > 0 })
                    {
                        return "form " + f.Id + ": field " + field.Key + " needs choices";
                    }
                }
            }
        }

        return null;
    }
}
