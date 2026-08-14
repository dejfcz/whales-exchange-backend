using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace WhalesExchangeBackend.Services.ElectrumRpc;

/// <summary>
/// Exception data for errors in the Electrum RPC response.
/// </summary>
[SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated by JSON deserializer.")]
internal class ElectrumRpcErrorDataException
{
    /// <summary>Exception message.</summary>
    [JsonPropertyName("exception")]
    public string Exception { get; }

    /// <summary>Stack trace of the exception.</summary>
    [JsonPropertyName("traceback")]
    public string Traceback { get; }

    /// <summary>
    /// Creates a new instance of the object.
    /// </summary>
    /// <param name="exception">Exception message.</param>
    /// <param name="traceback">Traceback.</param>
    public ElectrumRpcErrorDataException(string exception, string traceback)
    {
        this.Exception = exception;
        this.Traceback = traceback;
    }
}