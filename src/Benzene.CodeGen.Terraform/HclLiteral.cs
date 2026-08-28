using System.Text;

namespace Benzene.CodeGen.Terraform;

/// <summary>
/// Escapes a settings-derived value (a Lambda/domain/subdomain name, an event-bus/rule name, or -
/// especially - a Benzene message topic) for safe embedding as an HCL string literal in generated
/// <c>.tf</c> content. Two independent hazards, both fixed here (#212/#263, mirroring the same fix
/// already applied to generated C# in <c>MessageHandlerSourceGenerator.cs</c> via
/// <c>SymbolDisplay.FormatLiteral</c>, and to generated YAML in
/// <c>Benzene.CodeGen.ApiGateway.YamlLiteral</c>):
/// <list type="bullet">
/// <item><c>"</c> and <c>\</c> would otherwise break out of the surrounding HCL string literal, the
/// same "unescaped interpolation into generated output" defect class as the C#/YAML cases.</item>
/// <item><c>${</c> and <c>%{</c> are HCL's own live template-interpolation and directive syntax -
/// this is the sharpest edge of the finding: a topic containing e.g.
/// <c>${aws_iam_role.admin.arn}</c> isn't just mangled, it is *evaluated* by Terraform as a real
/// expression referencing a symbol the generator never intended. HCL's own escaping convention for
/// a literal <c>${</c>/<c>%{</c> is to double the leading character (<c>$${</c>/<c>%%{</c>) - see
/// https://developer.hashicorp.com/terraform/language/expressions/strings#escape-sequences.</item>
/// </list>
/// </summary>
public static class HclLiteral
{
    public static string Format(string value)
    {
        var escaped = new StringBuilder(value.Length)
            .Append(value)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("${", "$${")
            .Replace("%{", "%%{")
            .ToString();

        return $"\"{escaped}\"";
    }
}
