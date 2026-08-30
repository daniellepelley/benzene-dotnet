using Benzene.CodeGen.Core;
using Benzene.CodeGen.Core.Writers;

namespace Benzene.CodeGen.Terraform;

public static class NameFormatter
{
    public static string UnderScoreCase(string name)
    {
        return name.Replace("-", "_");
    }

    /// <summary>
    /// Escapes a value for embedding inside an HCL quoted string literal (<c>"..."</c>): backslash
    /// first, so an already-escaped sequence in the input isn't double-escaped, then the double quote
    /// that would otherwise terminate the literal early. #244: every value interpolated into a
    /// generated <c>.tf</c> file's string literals - a topic name, a Lambda name, an entry point -
    /// ultimately comes from caller/reflection-supplied data (a message handler's topic id, a
    /// deployment setting), not a fixed set of safe tokens, and none of it was escaped before this
    /// fix. A value containing <c>"</c> produced invalid HCL (an early-terminated string followed by
    /// dangling text); a value containing <c>\</c> produced a dangling/altered escape sequence.
    /// </summary>
    public static string EscapeHclString(string? value)
    {
        // Null-tolerant to match the interpolation this replaces: `$"{settings.Domain}"` on a null
        // Domain/SubDomain (both un-defaulted, optional settings) silently produced "", not a thrown
        // exception - the escaping fix must not turn that into a new crash.
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

public class TerraformLambdaEventBusPermissionsBuilder : ICodeBuilder<TerraformLambdaEventBusPermissionsSettings>
{
    public ICodeFile[] BuildCodeFiles(TerraformLambdaEventBusPermissionsSettings settings)
    {
        return new ICodeFile[]
        {
            new CodeFile("aws_lambda_permission.tf", BuildPermissions(settings)),
            new CodeFile("aws_sns_topic_subscription.tf", BuildSubscriptions(settings))
        };
    }

    public string[] BuildPermissions(TerraformLambdaEventBusPermissionsSettings settings)
    {
        var lineWriter = new LineWriter(2);

        foreach (var keyPairValue in settings.TopicsMap)
        {
            lineWriter.WriteLines(BuildPermission(settings.LambdaName, keyPairValue.Key, settings.SnsRemoteStateName));
        }

        return lineWriter.GetLines();
    }

    public string[] BuildPermission(string lambdaName, string snsTopic, string snsRemoteStateName = "sns")
    {
        var permissionName = $"{NameFormatter.UnderScoreCase(snsTopic)}_invoke_{NameFormatter.UnderScoreCase(lambdaName)}";

        var lineWriter = new LineWriter(2);
        lineWriter.WriteLine($"resource \"aws_lambda_permission\" \"{permissionName}\" {{");
        using (lineWriter.StartIndent())
        {
            lineWriter.WriteLine("action = \"lambda:InvokeFunction\"");
            lineWriter.WriteLine($"function_name = aws_lambda_function.{NameFormatter.UnderScoreCase(lambdaName)}.function_name");
            lineWriter.WriteLine("principal = \"sns.amazonaws.com\"");
            lineWriter.WriteLine("statement_id = \"AllowSubscriptionToSNSResponse\"");
            lineWriter.WriteLine($"source_arn = data.terraform_remote_state.{snsRemoteStateName}.outputs.{NameFormatter.UnderScoreCase(snsTopic)}");
        }

        lineWriter.WriteLine("}");

        return lineWriter.GetLines();
    }

    public string[] BuildSubscriptions(TerraformLambdaEventBusPermissionsSettings settings)
    {
        var lineWriter = new LineWriter(2);

        foreach (var keyPairValue in settings.TopicsMap)
        {
            lineWriter.WriteLines(BuildSubscription(settings.LambdaName, keyPairValue.Key, keyPairValue.Value, settings.SnsRemoteStateName));
        }

        return lineWriter.GetLines();
    }

    public string[] BuildSubscription(string lambdaName, string snsTopic, string[] topics, string snsRemoteStateName = "sns")
    {
        var subscriptionName = $"{NameFormatter.UnderScoreCase(lambdaName)}_{NameFormatter.UnderScoreCase(snsTopic)}_subscription";

        var lineWriter = new LineWriter(2);
        lineWriter.WriteLine($"resource \"aws_sns_topic_subscription\" \"{subscriptionName}\" {{");
        using (lineWriter.StartIndent())
        {
            lineWriter.WriteLine($"topic_arn = data.terraform_remote_state.{snsRemoteStateName}.outputs.{NameFormatter.UnderScoreCase(snsTopic)}");
            lineWriter.WriteLine("protocol = \"lambda\"");
            lineWriter.WriteLine($"endpoint = aws_lambda_function.{NameFormatter.UnderScoreCase(lambdaName)}.arn");
            lineWriter.WriteLine("endpoint_auto_confirms = true");
            lineWriter.WriteLine($"filter_policy = jsonencode({{\"topic\" = [{string.Join(",", topics.Select(topic => $"\"{NameFormatter.EscapeHclString(topic)}\""))}]}})");
        }

        lineWriter.WriteLine("}");

        return lineWriter.GetLines();
    }
}
