using CodexForVS.Core;
using Xunit;

namespace CodexForVS.Tests;

public sealed class SessionModeTests
{
    [Fact]
    public void PlanAndImplementRemainDistinctWorkflowChoices()
    {
        Assert.NotEqual(SessionMode.Plan, SessionMode.Implement);
    }
}
