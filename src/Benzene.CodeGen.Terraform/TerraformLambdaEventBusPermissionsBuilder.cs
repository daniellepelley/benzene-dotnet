using Benzene.CodeGen.Core;
using Benzene.CodeGen.Core.Writers;

namespace Benzene.CodeGen.Terraform;

public static class NameFormatter
{
    /// <summary>
    /// Turns a caller-supplied name into a Terraform identifier, for resource labels and the
    /// references built from them (<c>aws_iam_role.{name}_role</c>). Every character that is not a
    /// letter, digit or underscore becomes <c>_</c> (so <c>order-service</c> is <c>order_service</c>, as
    /// it always was), and a name that would start with a digit gets a leading <c>_</c>.
    /// </summary>
    /// <remarks>
    /// Labels are not string literals, so <see cref="HclLiteral"/> cannot make them safe: a label has to
    /// be an identifier, and a <c>"</c> in the name used to close the label and let the rest of the name
    /// be read as HCL (#212/#263's escaping covered the literals, not the labels). Any name this changes
    /// beyond the dash was already producing a label Terraform rejects.
    /// </remarks>
    public static string UnderScoreCase(string name)
    {
        var identifier = new System.Text.StringBuilder(name.Length + 1);
        foreach (var c in name)
        {
            identifier.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (identifier.Length == 0 || char.IsAsciiDigit(identifier[0]))
        {
            identifier.Insert(0, '_');
        }

        return identifier.ToString();
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
            // topics are user-authored Benzene message topics - HclLiteral escapes each one for safe
            // embedding as an HCL string literal within this jsonencode(...) object constructor
            // (#212/#263).
            lineWriter.WriteLine($"filter_policy = jsonencode({{\"topic\" = [{string.Join(",", topics.Select(HclLiteral.Format))}]}})");
        }

        lineWriter.WriteLine("}");

        return lineWriter.GetLines();
    }
}
