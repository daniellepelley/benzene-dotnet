using Benzene.Mesh.Usage.ApplicationInsights;
using Xunit;

namespace Benzene.Mesh.Test;

/// <summary>
/// #78: <see cref="LogsQueryUsageQuery"/> interpolates configured metric/dimension names into a KQL
/// string literal - <see cref="LogsQueryUsageQuery.EscapeKqlStringLiteral"/> is the defence-in-depth
/// guard (config-time values today, but escaped/validated the same as a live input would be). Tested
/// directly (via <c>InternalsVisibleTo</c>) rather than through <c>QueryAsync</c>, which would require
/// mocking Azure.Monitor.Query's SDK-internal <c>Response&lt;LogsQueryResult&gt;</c>.
/// </summary>
public class LogsQueryUsageQueryTest
{
    [Fact]
    public void EscapeKqlStringLiteral_PlainName_IsUnchanged()
    {
        Assert.Equal("benzene.messages.processed",
            LogsQueryUsageQuery.EscapeKqlStringLiteral("benzene.messages.processed", "MetricName"));
    }

    [Fact]
    public void EscapeKqlStringLiteral_EscapesDoubleQuotes_SoAValueCannotBreakOutOfTheLiteral()
    {
        // A misconfigured dimension name containing a `"` would otherwise close the KQL string literal
        // early and let the rest of the value run as query syntax.
        var escaped = LogsQueryUsageQuery.EscapeKqlStringLiteral("topic\" | take 1000 //", "TopicDimension");

        Assert.Equal("topic\\\" | take 1000 //", escaped);
        Assert.DoesNotContain("\" |", escaped); // the quote is escaped, not a live literal boundary
    }

    [Fact]
    public void EscapeKqlStringLiteral_EscapesBackslashes_BeforeQuotes()
    {
        var escaped = LogsQueryUsageQuery.EscapeKqlStringLiteral("a\\b\"c", "ResultDimension");

        Assert.Equal("a\\\\b\\\"c", escaped);
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    public void EscapeKqlStringLiteral_RejectsLineBreaks(string value)
    {
        // A line break can't occur in a legitimate metric/dimension name and would let a misconfigured
        // value inject a whole extra KQL statement (past the string literal, not just past a quote) -
        // rejected outright rather than escaped.
        var ex = Assert.Throws<ArgumentException>(() => LogsQueryUsageQuery.EscapeKqlStringLiteral(value, "MetricName"));
        Assert.Equal("MetricName", ex.ParamName);
    }
}
