namespace Tasktor.Contracts;

/// <summary>Public contract ids (U1 D-H1). The major lives in the id.</summary>
public static class ContractIds
{
    /// <summary>Frozen: <c>schemas/tasktor.browser-1.json</c> is kept as published; new messages are <see cref="Browser2"/>.</summary>
    public const string Browser = "tasktor.browser/1";
    public const string Browser2 = "tasktor.browser/2";
    public const string Snapshot = "tasktor.snapshot/1";
    public const string Action = "tasktor.action/1";
    public const string Rules = "tasktor.rules/1";
    public const string Plugin = "tasktor.plugin/1";
    public const string Plugin2 = "tasktor.plugin/2";
    public const string Job = "tasktor.job/1";
    public const string Agent = "tasktor.agent/1";
    public const string Calendar = "tasktor.calendar/1";
    public const string Export = "tasktor.export/1";
    public const string Presets = "tasktor.presets/1";
    public const string Config = "tasktor.config/1";
    public const string Prereg = "tasktor.prereg/1";
    public const string Extract = "tasktor.extract/1";
    public const string WorkerJob = "tasktor.worker-job/1";
    public const string Model = "tasktor.model/1";
    public const string Eval = "tasktor.eval/1";
    public const string Overrides = "tasktor.overrides/1";
    public const string CaptureControl = "tasktor.capture-control/1";

    /// <summary>
    /// Base of each schema's <c>$id</c>. A URN, so the repo names no domain it does not own; files that want
    /// editor autocomplete point <c>$schema</c> at the schema file shipped next to them (<c>schemas/…</c>).
    /// </summary>
    public const string SchemaIdBase = "urn:tasktor:schema:";
}
