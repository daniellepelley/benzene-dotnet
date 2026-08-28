using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Benzene.CodeGen.Client;
using Benzene.CodeGen.Core;
using Benzene.Schema.OpenApi;
using Benzene.Test.Autogen.CodeGen.Helpers;
using Microsoft.OpenApi.Models;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Client;

/// <summary>
/// The generated client must round-trip polymorphic contracts: allOf becomes base-class
/// inheritance, a discriminator becomes STJ polymorphism attributes, and a oneOf member site is
/// typed as the subtypes' shared base.
/// </summary>
public class SdkTypeBuilderPolymorphismTest
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(CardPayment), "card")]
    [JsonDerivedType(typeof(BankPayment), "bank")]
    public abstract class PaymentMethod
    {
        public string Currency { get; set; }
    }

    public class CardPayment : PaymentMethod
    {
        public string CardNumber { get; set; }
    }

    public class BankPayment : PaymentMethod
    {
        public string Iban { get; set; }
    }

    public class CheckoutRequest
    {
        public PaymentMethod Payment { get; set; }
    }

    private static ICodeFile[] BuildFiles()
    {
        var schemaBuilder = new SchemaBuilder(new SchemaGenerationOptions
        {
            UseAllOfForInheritance = true,
            UseOneOfForPolymorphism = true,
        });
        schemaBuilder.AddSchema(typeof(CheckoutRequest));

        return new OpenApiSchemaCSharpTypeBuilder("Benzene.Service.Clients.Payments")
            .BuildCodeFiles(schemaBuilder.Build());
    }

    private static string FileText(ICodeFile[] files, string name) =>
        files.Single(x => x.Name == name).Lines.ToText();

    [Fact]
    public void DerivedType_InheritsFromBase_WithOnlyItsOwnProperties()
    {
        var files = BuildFiles();
        var cardPayment = FileText(files, "CardPayment.cs");

        Assert.Contains("public class CardPayment : PaymentMethod", cardPayment);
        Assert.Contains("public string CardNumber { get; set; }", cardPayment);
        Assert.DoesNotContain("Currency", cardPayment);
    }

    [Fact]
    public void BaseType_GetsStjPolymorphismAttributes_AndNoDiscriminatorProperty()
    {
        var files = BuildFiles();
        var paymentMethod = FileText(files, "PaymentMethod.cs");

        Assert.Contains("using System.Text.Json.Serialization;", paymentMethod);
        Assert.Contains("[JsonPolymorphic(TypeDiscriminatorPropertyName = \"kind\")]", paymentMethod);
        Assert.Contains("[JsonDerivedType(typeof(CardPayment), \"card\")]", paymentMethod);
        Assert.Contains("[JsonDerivedType(typeof(BankPayment), \"bank\")]", paymentMethod);
        Assert.Contains("public string Currency { get; set; }", paymentMethod);
        // The discriminator is serializer metadata, not a POCO property.
        Assert.DoesNotContain("public string Kind", paymentMethod);
    }

    [Fact]
    public void OneOfMemberSite_IsTypedAsTheSharedBase()
    {
        var files = BuildFiles();
        var checkoutRequest = FileText(files, "CheckoutRequest.cs");

        Assert.Contains("public PaymentMethod Payment { get; set; }", checkoutRequest);
    }

    // #263: the discriminator property name and each mapping key come straight off the schema (which
    // may be hand-authored/deserialized, not only reflected off real C# attributes) and are
    // interpolated into the generated [JsonPolymorphic]/[JsonDerivedType] attribute arguments - an
    // unescaped quote used to break the attribute's string-literal argument outright.
    [Fact]
    public void Discriminator_AdversarialContent_QuoteAndBackslash_EscapedInGeneratedAttributes()
    {
        const string adversarialPropertyName = "ki\"nd\\type";
        const string adversarialMappingKey = "ca\"rd";

        var schema = new OpenApiSchema
        {
            Type = "object",
            Discriminator = new OpenApiDiscriminator
            {
                PropertyName = adversarialPropertyName,
                Mapping = new Dictionary<string, string> { [adversarialMappingKey] = "#/components/schemas/CardPayment" }
            },
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["currency"] = new OpenApiSchema { Type = "string" }
            }
        };

        var file = new OpenApiSchemaCSharpTypeBuilder("Benzene.Service.Clients.Payments")
            .BuildType(new KeyValuePair<string, OpenApiSchema>("PaymentMethod", schema));
        var text = file.Lines.ToText();

        Assert.Contains(
            $"[JsonPolymorphic(TypeDiscriminatorPropertyName = {CodeGenHelpers.ToCSharpStringLiteral(adversarialPropertyName)})]",
            text);
        Assert.Contains(
            $"[JsonDerivedType(typeof(CardPayment), {CodeGenHelpers.ToCSharpStringLiteral(adversarialMappingKey)})]",
            text);
        // The pre-fix raw interpolation ($"...\"{value}\"...") would have broken the attribute's
        // string literal open at the embedded quote instead of escaping it.
        Assert.DoesNotContain($"\"{adversarialPropertyName}\"", text);
        Assert.DoesNotContain($"\"{adversarialMappingKey}\"", text);
    }
}
