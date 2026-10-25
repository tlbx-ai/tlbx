using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

// These tests exercise real timers, bounded flush latency or process-wide allocation
// budgets. Keep unrelated parallel work from consuming their budgets on CI runners.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "Timing-sensitive services";
}
