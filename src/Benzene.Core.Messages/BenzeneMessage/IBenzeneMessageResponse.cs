namespace Benzene.Core.Messages.BenzeneMessage;

public interface IBenzeneMessageResponse
{
    string StatusCode { get; set; }

    /// <summary>
    /// The authoritative success/failure signal (wire-contracts.md §1.2). A receiver honors this over
    /// any classification it derives from <see cref="StatusCode"/> text, because an application-defined
    /// status is outside the framework's known vocabulary and means nothing to a receiver that doesn't
    /// share the sender's status vocabulary.
    /// </summary>
    bool IsSuccessful { get; set; }
    IDictionary<string, string> Headers { get; set; }
    string Body { get; set; }
}