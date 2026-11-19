using CodexForVS.Client;
using Xunit;

namespace CodexForVS.Tests;
public sealed class ApprovalResponseFactoryTests
{
    [Theory]
    [InlineData("item/commandExecution/requestApproval")]
    [InlineData("item/fileChange/requestApproval")]
    public void CommandAndFileRequestsDecline(string method) { Assert.True(ApprovalResponseFactory.TryCreateDenial(method, out var result)); Assert.Equal("decline", (string?)result["decision"]); }
    [Fact]
    public void PermissionRequestGrantsNothing() { Assert.True(ApprovalResponseFactory.TryCreateDenial("item/permissions/requestApproval", out var result)); Assert.Equal("{}", result["permissions"]!.ToString()); }
    [Fact]
    public void PlanQuestionsReceiveNoFabricatedAnswers() { Assert.True(ApprovalResponseFactory.TryCreateDenial("item/tool/requestUserInput", out var result)); Assert.Equal("{}", result["answers"]!.ToString()); }
    [Theory]
    [InlineData("mcpServer/elicitation/request")]
    public void InputRequestsDecline(string method) { Assert.True(ApprovalResponseFactory.TryCreateDenial(method, out var result)); Assert.Equal("decline", (string?)result["action"]); Assert.True(result["content"]!.Type == Newtonsoft.Json.Linq.JTokenType.Null); }
    [Fact]
    public void UnknownRequestIsNotAnswered() { Assert.False(ApprovalResponseFactory.TryCreateDenial("unknown/request", out var result)); Assert.Empty(result.Properties()); }
}
