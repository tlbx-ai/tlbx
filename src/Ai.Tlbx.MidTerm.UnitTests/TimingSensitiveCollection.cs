using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

// These tests exercise real timers and bounded flush latency. Keep unrelated
// parallel test work from consuming their scheduling budget on CI runners.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "Timing-sensitive services";
}
