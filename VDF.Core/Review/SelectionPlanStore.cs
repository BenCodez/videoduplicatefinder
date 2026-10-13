using System.Linq;
using System.Text.Json.Serialization;
using VDF.Core.Selection;

namespace VDF.Core.Review;

/// <summary>Persisted selection explanations are previews, never authorization for removal.</summary>
public static class SelectionPlanStore {
    public static async Task SaveAsync(string path, SelectionPlan plan, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(plan);
        await DurableJsonFile.WriteAsync(SafePaths.Normalize(path), new SelectionPlanEnvelope { Plan = plan },
            ReviewJsonContext.Default.SelectionPlanEnvelope, Validate, ct).ConfigureAwait(false);
    }

    public static async Task<SelectionPlan?> LoadAsync(string path, CancellationToken ct = default) {
        var envelope = await DurableJsonFile.ReadAsync(SafePaths.Normalize(path),
            ReviewJsonContext.Default.SelectionPlanEnvelope, Validate, ct).ConfigureAwait(false);
        return envelope?.Plan;
    }

    private static bool Validate(SelectionPlanEnvelope envelope) {
        if (envelope.Version != 1) throw new NotSupportedException("The selection plan schema is unsupported.");
        return envelope.Plan is not null && envelope.Plan.Decisions is not null &&
            envelope.Plan.Decisions.All(d => d is not null && d.Path is not null && d.Reason is not null);
    }
}

internal sealed class SelectionPlanEnvelope {
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonRequired]
    public SelectionPlan Plan { get; set; } = new();
}
