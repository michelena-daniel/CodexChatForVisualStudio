using Newtonsoft.Json.Linq;

namespace CodexForVS.Client;

public static class ApprovalResponseFactory
{
    public static bool TryCreateDenial(string method, out JObject response)
    {
        switch (method)
        {
            case "item/commandExecution/requestApproval":
            case "item/fileChange/requestApproval":
                response = new JObject { ["decision"] = "decline" };
                return true;
            case "item/permissions/requestApproval":
                response = new JObject { ["permissions"] = new JArray() };
                return true;
            case "item/tool/requestUserInput":
            case "mcpServer/elicitation/request":
                response = new JObject { ["action"] = "decline", ["content"] = JValue.CreateNull() };
                return true;
            default:
                response = new JObject();
                return false;
        }
    }
}
