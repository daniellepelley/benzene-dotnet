using System.Linq;
using System.Text.Json.Serialization;
using Benzene.Schema.OpenApi;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi;

public class SchemaBuilderPolymorphismTest
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

    [Fact]
    public void Default_FlattensPolymorphicMembers()
    {
        var schemaBuilder = new SchemaBuilder();

        schemaBuilder.AddSchema(typeof(CheckoutRequest));
        var schemas = schemaBuilder.Build();

        var paymentMethod = schemas["PaymentMethod"];
        Assert.True(paymentMethod.OneOf == null || paymentMethod.OneOf.Count == 0);
        Assert.False(schemas.ContainsKey(nameof(CardPayment)));
    }

    [Fact]
    public void OneOfForPolymorphism_EmitsSubtypesAndDiscriminator()
    {
        var schemaBuilder = new SchemaBuilder(new SchemaGenerationOptions
        {
            UseOneOfForPolymorphism = true,
        });

        schemaBuilder.AddSchema(typeof(CheckoutRequest));
        var schemas = schemaBuilder.Build();

        Assert.True(schemas.ContainsKey(nameof(CardPayment)));
        Assert.True(schemas.ContainsKey(nameof(BankPayment)));

        // The base-typed member is a oneOf union of the declared subtypes... (#169: schema property
        // keys are camelCase, matching the wire - "payment", not the C# member name "Payment".)
        var payment = schemas[nameof(CheckoutRequest)].Properties["payment"];
        Assert.Equal(2, payment.OneOf.Count);
        Assert.Contains(payment.OneOf, x => x.Reference?.Id == nameof(CardPayment));
        Assert.Contains(payment.OneOf, x => x.Reference?.Id == nameof(BankPayment));

        // ...and the base schema carries the discriminator declared via [JsonPolymorphic]/[JsonDerivedType].
        var baseSchema = schemas[nameof(PaymentMethod)];
        Assert.Equal("kind", baseSchema.Discriminator?.PropertyName);
        Assert.Equal($"#/components/schemas/{nameof(CardPayment)}", baseSchema.Discriminator!.Mapping["card"]);
        Assert.Equal($"#/components/schemas/{nameof(BankPayment)}", baseSchema.Discriminator.Mapping["bank"]);
        Assert.Contains("kind", baseSchema.Required);
    }

    [Fact]
    public void AllOfForInheritance_DerivedSchemasReferenceTheBase()
    {
        var schemaBuilder = new SchemaBuilder(new SchemaGenerationOptions
        {
            UseAllOfForInheritance = true,
            UseOneOfForPolymorphism = true,
        });

        schemaBuilder.AddSchema(typeof(CheckoutRequest));
        var schemas = schemaBuilder.Build();

        var cardPayment = schemas[nameof(CardPayment)];
        Assert.Contains(cardPayment.AllOf, x => x.Reference?.Id == nameof(PaymentMethod));
        // The derived schema carries only its own properties; the base's come via the allOf $ref.
        // (#169: property keys are camelCase, matching the wire.)
        Assert.Contains("cardNumber", cardPayment.Properties.Keys);
        Assert.DoesNotContain("currency", cardPayment.Properties.Keys);
    }
}
