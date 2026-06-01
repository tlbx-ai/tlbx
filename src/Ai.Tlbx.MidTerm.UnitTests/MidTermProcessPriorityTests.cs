using System.Diagnostics;
using Ai.Tlbx.MidTerm.Common.Process;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MidTermProcessPriorityTests
{
    [Theory]
    [InlineData(null, ProcessPriorityClass.AboveNormal)]
    [InlineData("", ProcessPriorityClass.AboveNormal)]
    [InlineData("aboveNormal", ProcessPriorityClass.AboveNormal)]
    [InlineData("above-normal", ProcessPriorityClass.AboveNormal)]
    [InlineData("normal", ProcessPriorityClass.Normal)]
    [InlineData("high", ProcessPriorityClass.High)]
    [InlineData("realtime", ProcessPriorityClass.AboveNormal)]
    [InlineData("unexpected", ProcessPriorityClass.AboveNormal)]
    public void ResolvePriorityClass_AllowsOnlyConservativeKnownClasses(
        string? input,
        ProcessPriorityClass expected)
    {
        Assert.Equal(expected, MidTermProcessPriority.ResolvePriorityClass(input));
    }

    [Fact]
    public void ShouldSetPriority_DoesNotLowerHighOrRealtimeProcesses()
    {
        Assert.False(MidTermProcessPriority.ShouldSetPriority(
            ProcessPriorityClass.High,
            ProcessPriorityClass.AboveNormal));
        Assert.False(MidTermProcessPriority.ShouldSetPriority(
            ProcessPriorityClass.RealTime,
            ProcessPriorityClass.AboveNormal));
    }

    [Fact]
    public void ShouldSetPriority_RaisesNormalToAboveNormal()
    {
        Assert.True(MidTermProcessPriority.ShouldSetPriority(
            ProcessPriorityClass.Normal,
            ProcessPriorityClass.AboveNormal));
    }
}
