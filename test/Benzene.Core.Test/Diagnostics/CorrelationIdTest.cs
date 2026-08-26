using Benzene.Diagnostics.Correlation;
using Xunit;

namespace Benzene.Test.Diagnostics;

public class CorrelationIdTest
{
    [Fact]
    public void Get_NothingSet_ReturnsANonEmptySelfGeneratedValue()
    {
        var correlationId = new CorrelationId();

        Assert.False(string.IsNullOrEmpty(correlationId.Get()));
    }

    [Fact]
    public void Set_ValidValue_OverridesTheSelfGeneratedValue()
    {
        var correlationId = new CorrelationId();

        correlationId.Set("my-correlation-id");

        Assert.Equal("my-correlation-id", correlationId.Get());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Set_NullOrEmptyValue_LeavesTheExistingValueUnchanged(string value)
    {
        var correlationId = new CorrelationId();
        var original = correlationId.Get();

        correlationId.Set(value);

        Assert.Equal(original, correlationId.Get());
    }

    [Fact]
    public void Set_CalledTwice_LatestValueWins()
    {
        var correlationId = new CorrelationId();

        correlationId.Set("first");
        correlationId.Set("second");

        Assert.Equal("second", correlationId.Get());
    }

    // #64: a caller-controlled value carrying embedded CR/LF (plus forged content, as a real attacker
    // would send) must be rejected outright - the self-generated GUID stays in place - so it can never
    // round-trip verbatim into a log scope (CRLF/log-forging) or an outbound header (header injection).
    [Fact]
    public void Set_ValueWithEmbeddedCrLf_IsRejected_SelfGeneratedIdStaysInPlace()
    {
        var correlationId = new CorrelationId();
        var original = correlationId.Get();

        correlationId.Set("real-id\r\nX-Forged-Header: evil\r\n\r\nForged-Log-Line: injected");

        Assert.Equal(original, correlationId.Get());
        Assert.DoesNotContain("\r", correlationId.Get());
        Assert.DoesNotContain("\n", correlationId.Get());
    }

    [Theory]
    [InlineData("bad\rid")]
    [InlineData("bad\nid")]
    [InlineData("bad\tid")]
    [InlineData("bad\0id")]
    public void Set_ValueWithAnyControlCharacter_IsRejected(string value)
    {
        var correlationId = new CorrelationId();
        var original = correlationId.Get();

        correlationId.Set(value);

        Assert.Equal(original, correlationId.Get());
    }

    [Fact]
    public void Set_ValueLongerThanMaxLength_IsRejected()
    {
        var correlationId = new CorrelationId();
        var original = correlationId.Get();

        correlationId.Set(new string('a', CorrelationId.MaxLength + 1));

        Assert.Equal(original, correlationId.Get());
    }

    [Fact]
    public void Set_ValueAtMaxLength_IsAccepted()
    {
        var correlationId = new CorrelationId();
        var value = new string('a', CorrelationId.MaxLength);

        correlationId.Set(value);

        Assert.Equal(value, correlationId.Get());
    }
}
