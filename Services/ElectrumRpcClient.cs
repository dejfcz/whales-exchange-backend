using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WhalesExchangeBackend.Services.ElectrumRpc;
using WhalesExchangeBackend.SharedLib.Exceptions;
using WhalesSecret.TradeScriptLib.Exceptions;
using WhalesSecret.TradeScriptLib.Logging;

namespace WhalesExchangeBackend.Services;

/// <summary>
/// Client that communicates with Electrum RPC server.
/// </summary>
[SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated by ASP.NET Core DI.")]
internal class ElectrumRpcClient
{
    /// <summary>Default query time for <c>get_submarine_swap_providers</c> call in seconds.</summary>
    private const int DefaultGetSubmarineSwapProvidersQueryTimeSeconds = 15;

    /// <summary>Instance logger.</summary>
    private readonly WsLogger log = WsLogger.GetCurrentClassLogger();

    /// <summary>Server configuration helper.</summary>
    private readonly ConfigHelper configHelper;

    /// <summary>HTTP client used for communication with Electrum RPC.</summary>
    private readonly HttpClient httpClient;

    /// <summary>JSON options for (de)serialization of message for/from Electrum RPC server.</summary>
    private readonly JsonSerializerOptions jsonOptions;

    /// <summary>Authentication header to send to the Electrum RPC server.</summary>
    private readonly AuthenticationHeaderValue authHeader;

    /// <summary>ID of the last request message.</summary>
    private long lastRequestId;

    /// <summary>
    /// Creates a new instance of the object.
    /// </summary>
    /// <param name="configHelper">Server configuration helper.</param>
    /// <param name="httpClient">HTTP client used for communication with Electrum RPC.</param>
    public ElectrumRpcClient(ConfigHelper configHelper, HttpClient httpClient)
    {
        this.log.Debug("*");

        this.configHelper = configHelper;
        this.httpClient = httpClient;

        this.jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        byte[] credentialBytes = Encoding.ASCII.GetBytes($"{this.configHelper.ElectrumRpcConfig.User}:{this.configHelper.ElectrumRpcConfig.Pass}");
        string base64Auth = Convert.ToBase64String(credentialBytes);
        this.authHeader = new("Basic", base64Auth);

        this.log.Debug("$");
    }

    /// <summary>
    /// Calls an Electrum RPC method with parameters.
    /// </summary>
    /// <typeparam name="TResult">Expected result type.</typeparam>
    /// <param name="method">RPC method name.</param>
    /// <param name="parameters">Array of parameters.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Electrum RPC response.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    private async Task<TResult> CallAsync<TResult>(string method, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(method)}='{method}',|{nameof(parameters)}|={parameters?.Count}");

        long id = Interlocked.Increment(ref this.lastRequestId);

        // With Electrum RPC, when no parameters are to be sent, we send empty array.
        object @params = Array.Empty<object>();
        if (parameters is not null)
            @params = parameters;

        ElectrumRpcRequest request = new(id, method, @params);

        TResult result;
        try
        {
            string json = JsonSerializer.Serialize(request, this.jsonOptions);
            using StringContent content = new(json, Encoding.UTF8, MediaTypeNames.Application.Json);

            using HttpRequestMessage httpRequest = new(HttpMethod.Post, this.configHelper.ElectrumRpcConfig.Uri)
            {
                Content = content,
            };

            httpRequest.Headers.Authorization = this.authHeader;

            using HttpResponseMessage response = await this.httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            this.log.Debug($"Received response was '{responseJson}'.");

            ElectrumRpcResponse<TResult>? rpcResponse = JsonSerializer.Deserialize<ElectrumRpcResponse<TResult>>(responseJson, this.jsonOptions);

            if ((rpcResponse is not null) && (rpcResponse.Error is not null))
                throw new ElectrumRpcException(rpcResponse.Error.Code, rpcResponse.Error.Message, rpcResponse.Error.Data);

            if ((rpcResponse is null) || (rpcResponse.Result is null))
                throw new OperationFailedException($"Calling Electrum RPC method '{method}' produced null response.");

            result = rpcResponse.Result;
        }
        catch (OperationFailedException)
        {
            this.log.Debug("$<EXCEPTION_FAILURE>");
            throw;
        }
        catch (ElectrumRpcException e)
        {
            this.log.Debug($"JSON RPC request for method '{method}' failed with exception: {e}");

            try
            {
                if (e.Message.EndsWith("internal error while executing RPC", StringComparison.OrdinalIgnoreCase))
                {
                    if (e.Details is not null)
                    {
                        JsonElement element = JsonSerializer.SerializeToElement(e.Details);
                        ElectrumRpcErrorDataException? exceptionData = JsonSerializer.Deserialize<ElectrumRpcErrorDataException>(element, this.jsonOptions);
                        if (exceptionData is not null)
                        {
                            this.log.Debug("$<EXCEPTION_ELECTRUM_DETAILED>");
                            throw new ElectrumRpcException(e.Code, exceptionData.Exception, details: null);
                        }
                    }
                }
            }
            catch (ElectrumRpcException)
            {
                this.log.Debug("$<EXCEPTION_ELECTRUM_DETAILED>");
                throw;
            }
            catch
            {
                // All other exceptions we ignore as they come from deserialization attempt.
            }

            this.log.Debug("$<EXCEPTION_ELECTRUM>");
            throw;
        }
        catch (Exception e)
        {
            this.log.Debug($"Generic exception occurred while processing JSON RPC request for method '{method}': {e}");
            this.log.Debug("$<EXCEPTION>");
            throw new OperationFailedException($"Calling Electrum RPC method '{method}' failed.", e);
        }

        this.log.Debug($"$='{result}'");
        return result;
    }

    /// <inheritdoc cref="GetSubmarineSwapProvidersAsync(int, CancellationToken)"/>
    public Task<ElectrumSwapProvider[]> GetSubmarineSwapProvidersAsync(CancellationToken cancellationToken)
        => this.GetSubmarineSwapProvidersAsync(queryTimeSec: DefaultGetSubmarineSwapProvidersQueryTimeSeconds, cancellationToken);

    /// <summary>
    /// Calls Electrum's <c>get_submarine_swap_providers</c> RPC method.
    /// </summary>
    /// <param name="queryTimeSec">Timeout for how long the relays should be queried for provider announcements.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>List of electrum swap providers.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumSwapProvider[]> GetSubmarineSwapProvidersAsync(int queryTimeSec, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(queryTimeSec)}={queryTimeSec}");

        Dictionary<string, object> parameters = new()
        {
            { "query_time", queryTimeSec },
        };

        ElectrumGetSubmarineSwapProviderResponse response = await this.CallAsync<ElectrumGetSubmarineSwapProviderResponse>(method: "get_submarine_swap_providers", parameters,
            cancellationToken).ConfigureAwait(false);

        ElectrumSwapProvider[] result = response.Values.ToArray();

        this.log.Debug($"|$|={result.Length}");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>wex_forward_swap</c> RPC method.
    /// </summary>
    /// <param name="invoice">Lightning invoice in hex format.</param>
    /// <param name="onchainAmountSat">Amount the client is supposed to send on-chain in satoshis.</param>
    /// <param name="refundPublicKeyHex">Public key that will be used in the on-chain refund transaction for the swap in hex format.</param>
    /// <param name="providerPk">Public key of the swap provider.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Information about the initiated forward swap.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumSwapData> ForwardSwapAsync(string invoice, long onchainAmountSat, string refundPublicKeyHex, string providerPk, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(invoice)}='{invoice.ToBoundedString()}',{nameof(onchainAmountSat)}={onchainAmountSat},{nameof(refundPublicKeyHex)}='{refundPublicKeyHex}',{
            nameof(providerPk)}='{providerPk}'");

        Dictionary<string, object> parameters = new()
        {
            { "invoice", invoice },
            { "onchain_amount_sat", onchainAmountSat },
            { "refundPublicKey", refundPublicKeyHex },
            { "provider_pk", providerPk },
        };

        ElectrumSwapData result = await this.CallAsync<ElectrumSwapData>(method: "wex_forward_swap", parameters, cancellationToken).ConfigureAwait(false);

        this.log.Debug($"$=`{result}`");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>wex_reverse_swap</c> RPC method.
    /// </summary>
    /// <param name="lnAmountSats">Amount to be sent by the user in satoshis.</param>
    /// <param name="onChainAmountSats">Amount to be received by the user in satoshis.</param>
    /// <param name="prepaymentSats">Lightning payment required by the swap provider in order to cover their mining fees. This is included in <paramref name="lnAmountSats"/>. This
    /// part of the operation is not trustless; the provider is trusted to fail this payment if the swap fails.</param>
    /// <param name="preimageHash">Hash of the preimage that will be used for the swap.</param>
    /// <param name="claimPk">Public key that will be used in the onchain claim transaction for the swap.</param>
    /// <param name="providerPk">Public key of the swap provider.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Information about the initiated reverse swap.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumSwapData> ReverseSwapAsync(long lnAmountSats, long onChainAmountSats, long prepaymentSats, string preimageHash, string claimPk, string providerPk,
        CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(lnAmountSats)}={lnAmountSats},{nameof(onChainAmountSats)}={onChainAmountSats},{nameof(prepaymentSats)}={prepaymentSats},{
            nameof(preimageHash)}='{preimageHash}',{nameof(claimPk)}='{claimPk}',{nameof(providerPk)}='{providerPk}'");

        Dictionary<string, object> parameters = new()
        {
            { "lightning_amount", lnAmountSats },
            { "onchain_amount", onChainAmountSats },
            { "prepayment", prepaymentSats },
            { "hash", preimageHash },
            { "claim_pk", claimPk },
            { "provider_pk", providerPk },
        };

        ElectrumSwapData result = await this.CallAsync<ElectrumSwapData>(method: "wex_reverse_swap", parameters, cancellationToken).ConfigureAwait(false);

        this.log.Debug($"$=`{result}`");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>getinfo</c> RPC method.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Information about the Electrum connection and blockchain.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumGetInfoResponse> GetInfoAsync(CancellationToken cancellationToken)
    {
        this.log.Debug("*");

        ElectrumGetInfoResponse result = await this.CallAsync<ElectrumGetInfoResponse>(method: "getinfo", parameters: null, cancellationToken).ConfigureAwait(false);

        this.log.Debug($"$='{result}'");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>getaddressunspent</c> RPC method.
    /// </summary>
    /// <param name="address">Bitcoin address to query for unspent outputs.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>List of unspent outputs for the specified address.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumGetAddressUnspentResponse> GetAddressUnspentAsync(string address, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(address)}='{address}'");

        Dictionary<string, object> parameters = new()
        {
            { "address", address },
        };

        ElectrumGetAddressUnspentResponse result = await this.CallAsync<ElectrumGetAddressUnspentResponse>(method: "getaddressunspent", parameters, cancellationToken)
            .ConfigureAwait(false);

        this.log.Debug($"|$|={result.Count}");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>gettransaction</c> RPC method.
    /// </summary>
    /// <param name="transactionId">Bitcoin transaction ID in hex format.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Raw transaction data in hex format, or <c>null</c> if the transaction ID could not be found.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    public async Task<string?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(transactionId)}='{transactionId}'");

        Dictionary<string, object> parameters = new()
        {
            { "txid", transactionId },
        };

        string? result = null;
        try
        {
            result = await this.CallAsync<string>(method: "gettransaction", parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationFailedException)
        {
            // Unknown transaction ID.
        }

        this.log.Debug($"$='{result}'");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>broadcast</c> RPC method.
    /// </summary>
    /// <param name="transactionData">Bitcoin transaction data in hex format.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Bitcoin transaction ID.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<string> BroadcastAsync(string transactionData, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(transactionData)}='{transactionData.ToBoundedString()}'");

        Dictionary<string, object> parameters = new()
        {
            { "tx", transactionData },
        };

        string result = await this.CallAsync<string>(method: "broadcast", parameters, cancellationToken).ConfigureAwait(false);

        this.log.Debug($"$='{result}'");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>deserialize</c> RPC method.
    /// </summary>
    /// <param name="transactionData">Bitcoin transaction data in hex format.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Deserialized transaction.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumTransaction> DeserializeAsync(string transactionData, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(transactionData)}='{transactionData.ToBoundedString()}'");

        Dictionary<string, object> parameters = new()
        {
            { "tx", transactionData },
        };

        ElectrumTransaction result = await this.CallAsync<ElectrumTransaction>(method: "deserialize", parameters, cancellationToken).ConfigureAwait(false);

        this.log.Debug($"$='{result}'");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>getaddresshistory</c> RPC method.
    /// </summary>
    /// <param name="address">Bitcoin address to query for transaction history.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>List of transaction history entries for the specified address.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumGetAddressHistoryResponse> GetAddressHistoryAsync(string address, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(address)}='{address}'");

        Dictionary<string, object> parameters = new()
        {
            { "address", address },
        };

        ElectrumGetAddressHistoryResponse result = await this.CallAsync<ElectrumGetAddressHistoryResponse>(method: "getaddresshistory", parameters, cancellationToken)
            .ConfigureAwait(false);

        this.log.Debug($"|$|={result.Count}");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>wex_decode_invoice</c> RPC method.
    /// </summary>
    /// <param name="invoice">Lightning invoice in hex format.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Deserialized invoice.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumLightningInvoice> DecodeInvoiceAsync(string invoice, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(invoice)}='{invoice.ToBoundedString()}'");

        Dictionary<string, object> parameters = new()
        {
            { "invoice", invoice },
        };

        ElectrumLightningInvoice result = await this.CallAsync<ElectrumLightningInvoice>(method: "wex_decode_invoice", parameters, cancellationToken).ConfigureAwait(false);

        this.log.Debug($"$='{result}'");
        return result;
    }

    /// <summary>
    /// Calls Electrum's <c>getfeerate</c> RPC method.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Information about current fee rates.</returns>
    /// <exception cref="ElectrumRpcException">Thrown when the Electrum server responded with an error.</exception>
    /// <exception cref="OperationFailedException">Thrown when the operation failed except for error returned by the Electrum server.</exception>
    public async Task<ElectrumFeeRate> GetFeeRateAsync(CancellationToken cancellationToken)
    {
        this.log.Debug("*");

        ElectrumFeeRate result = await this.CallAsync<ElectrumFeeRate>(method: "getfeerate", parameters: null, cancellationToken).ConfigureAwait(false);

        this.log.Debug($"$='{result}'");
        return result;
    }
}