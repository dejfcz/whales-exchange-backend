using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WhalesExchangeBackend.Controllers.InternalSupport;
using WhalesExchangeBackend.Data.Repository;
using WhalesExchangeBackend.Models;
using WhalesExchangeBackend.Services;
using WhalesExchangeBackend.Services.ElectrumRpc;
using WhalesExchangeBackend.SharedLib.Data;
using WhalesExchangeBackend.SharedLib.Exceptions;
using WhalesExchangeBackend.SharedLib.Models;
using WhalesExchangeBackend.SharedLib.Services.WebSocket.Messages;
using WhalesExchangeBackend.Utils;
using WhalesSecret.TradeScriptLib.Exceptions;
using WhalesSecret.TradeScriptLib.Logging;

namespace WhalesExchangeBackend.Controllers;

/// <summary>
/// Controller for REST API of the backend.
/// </summary>
[SuppressMessage("Microsoft.Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated by ASP.NET Core DI.")]
internal class RestApiController : InternalControllerBase
{
    /// <summary>Number of satoshis that are considered high enough to justify waiting for two confirmations instead of one.</summary>
    private const long TwoConfirmationAmountThresholdSats = 1_000_000;

    /// <summary>Maximum number of transactions allowed for a reverse swap client address.</summary>
    private const int MaxReverseSwapClientAddressHistoryLength = 100;

    /// <summary>Number of blocks the swap provider wants to have on its side to claim the funding transaction output in forward swaps.</summary>
    /// <remarks>Plus one here over the Swap Server implementation is because of the logic in <c>_claim_swap</c> method in the Swap Server implementation.</remarks>
    private const int ForwardSwapServerTimeoutBlocksBuffer = 60 + 1;

    /// <summary>Number of blocks after which the forward swap expires.</summary>
    private const int ForwardSwapTimeoutBlocks = 70;

    /// <summary>Number of blocks to serve as a safe buffer before for a lockup address timeout scenario.</summary>
    /// <remarks>
    /// When the swap provider publishes the funding transaction with an output paying to the lockup address, the client must be provided with enough time to spend this output
    /// before the time lock on the output allows the swap provider to claim the money back. If the client learned about the the funding transaction just before the time lock
    /// expires, the client may fail to spend it on time.
    /// </remarks>
    private const int LockupAddressTimeoutBuffer = 5;

    /// <summary>Instance logger.</summary>
    private readonly WsLogger log;

    /// <summary>Provides access to the current <see cref="HttpContext"/>.</summary>
    private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>Service that checks client swap limits.</summary>
    private readonly SwapLimitChecker swapLimitChecker;

    /// <summary>Provider of access to swap providers and their offers in the database.</summary>
    private readonly SwapProviderRepository swapProviderRepository;

    /// <summary>Provider of access to swaps in the database.</summary>
    private readonly SwapRepository swapRepository;

    /// <summary>Client that communicates with Electrum RPC server.</summary>
    private readonly ElectrumRpcClient electrumRpcClient;

    /// <summary>Monitor of blockchain data fetched from the Electrum backend client.</summary>
    private readonly BlockchainDataMonitor blockchainDataMonitor;

    /// <summary>
    /// Creates a new instance of the object.
    /// </summary>
    /// <param name="httpContextAccessor">Provides access to the current <see cref="HttpContext"/>.</param>
    /// <param name="swapLimitChecker">Service that checks client swap limits.</param>
    /// <param name="swapProviderRepository">Provider of access to swap providers and their offers in the database.</param>
    /// <param name="swapRepository">Provider of access to swaps in the database.</param>
    /// <param name="electrumRpcClient">Client that communicates with Electrum RPC server.</param>
    /// <param name="blockchainDataMonitor">Monitor of blockchain data fetched from the Electrum backend client.</param>
    public RestApiController(IHttpContextAccessor httpContextAccessor, SwapLimitChecker swapLimitChecker, SwapProviderRepository swapProviderRepository,
        SwapRepository swapRepository, ElectrumRpcClient electrumRpcClient, BlockchainDataMonitor blockchainDataMonitor)
    {
        this.httpContextAccessor = httpContextAccessor;
        HttpContext? context = this.httpContextAccessor.HttpContext;
        if (context is null)
            throw new SanityCheckException("HTTP context is null.");

        this.log = new(this.GetType().FullName!, $"{context.Connection.RemoteIpAddress}");

        this.swapLimitChecker = swapLimitChecker;
        this.swapProviderRepository = swapProviderRepository;
        this.swapRepository = swapRepository;
        this.electrumRpcClient = electrumRpcClient;
        this.blockchainDataMonitor = blockchainDataMonitor;

        this.log.Debug("*$");
    }

    /// <summary>
    /// Action that is executed when a list of swap providers is requested.
    /// </summary>
    /// <returns>Result of the action method.</returns>
    [HttpGet]
    [Route("get-swap-providers")]
    public async Task<IActionResult> GetSwapProvidersAsync()
    {
        this.log.Debug("*");

        IActionResult result;
        DateTime min20ago = DateTime.UtcNow.AddMinutes(-20);

        GetSwapProvidersResponse response;
        try
        {
            DbSwapProvider[] dbRecords = await this.swapProviderRepository.GetRecentAsync(min20ago).ConfigureAwait(false);

            RestSwapProvider[] providers = dbRecords
                .Select(RestSwapProvider.FromDbSwapProvider)
                .OrderByDescending(c => c.PoWBits)
                .ThenBy(c => c.Pubkey)
                .ToArray();

            response = new(providers);
        }
        catch (Exception e)
        {
            this.log.Error($"Exception occurred while getting list of swap providers: {e}");
            response = new($"Getting list of swap providers from the database failed. {e.Message}");
        }

        result = this.Ok(response);

        this.log.Debug("$");
        return result;
    }

    /// <summary>
    /// Action that is executed when a swap is requested.
    /// </summary>
    /// <param name="request">Swap request.</param>
    /// <returns>Result of the action method.</returns>
    [HttpPost]
    [Route("createswap")]
    public async Task<IActionResult> CreateSwapAsync([FromBody] CreateSwapRequest request)
    {
        this.log.Debug($"* {nameof(request)}='{request}'");

        HttpContext? context = this.httpContextAccessor.HttpContext;
        if (context is null)
            throw new SanityCheckException("HTTP context is null.");

        IPAddress? ipAddress = context.Connection.RemoteIpAddress;
        if (ipAddress is null)
            throw new SanityCheckException("Remote IP address is null.");

        IActionResult result;
        CreateSwapResponse response;

        if (request.PairId != CreateSwapRequest.SupportedPairIdStr)
        {
            string msg = $"Unsupported request pair. Only  '{CreateSwapRequest.SupportedPairIdStr}' is currently supported.";
            this.log.Error(msg);
            response = new(msg);
            result = this.Ok(response);

            this.log.Debug("$<UNSUPPORTED_REQUEST_PAIR>");
            return result;
        }

        string providerPk = request.PairHash;
        DbSwapProvider? provider;
        try
        {
            provider = await this.swapProviderRepository.GetByPubkeyAsync(providerPk).ConfigureAwait(false);
        }
        catch (DatabaseException e)
        {
            this.log.Error($"Exception occurred while trying to get swap provider with pubkey '{providerPk}' from the database: {e}");
            response = new($"Getting swap provider with pubkey '{providerPk}' failed.");
            result = this.Ok(response);

            this.log.Debug("$<GET_PROVIDER_DB_ERROR>");
            return result;
        }

        if (provider is null)
        {
            string msg = $"Swap provider with pubkey '{providerPk}' cannot be found.";
            this.log.Error(msg);
            response = new(msg);
            result = this.Ok(response);

            this.log.Debug("$<PROVIDER_NOT_FOUND>");
            return result;
        }

        string frontendId = RandomStringGenerator.Generate(DbSwap.FrontendIdLength);
        string userIpAddress = ipAddress.ToString();
        bool isPermitted = this.swapLimitChecker.RegisterSwap(ipAddress: userIpAddress, frontendSwapId: frontendId);
        if (!isPermitted)
        {
            this.log.Error($"Too many uncommitted swaps for the given IP address '{userIpAddress}'.");
            response = new("Too many uncommitted swaps for the given IP address.");

            result = this.Ok(response);
            this.log.Debug("$<IP_LIMIT_EXCEEDED>");
            return result;
        }

        if ((request.Type == CreateSwapRequest.ForwardSwapTypeStr) && (request.OrderSide == CreateSwapRequest.ForwardSwapOrderSideStr))
        {
            if (request.Invoice is not null)
            {
                if (request.RefundPublicKey is not null)
                {
                    response = await this.CreateForwardSwapAsync(request, provider, frontendId: frontendId, userIpAddress: userIpAddress, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                else response = new($"'{nameof(request.RefundPublicKey)}' is mandatory for forward swaps.");
            }
            else response = new($"'{nameof(request.Invoice)}' is mandatory for forward swaps.");
        }
        else if ((request.Type == CreateSwapRequest.ReverseSwapTypeStr) && (request.OrderSide == CreateSwapRequest.ReverseSwapOrderSideStr))
        {
            if (request.InvoiceAmount is not null)
            {
                if (request.ClaimPublicKey is not null)
                {
                    response = await this.CreateReverseSwapAsync(request, provider, frontendId: frontendId, userIpAddress: userIpAddress, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                else response = new($"'{nameof(request.ClaimPublicKey)}' is mandatory for reverse swaps.");
            }
            else response = new($"'{nameof(request.InvoiceAmount)}' is mandatory for reverse swaps.");
        }
        else response = new($"Unknown swap type '{request.Type}' and order side '{request.OrderSide}' combination.");

        if (!response.Success)
            _ = this.swapLimitChecker.UnregisterSwap(frontendId);

        result = this.Ok(response);

        this.log.Debug("$");
        return result;
    }

    /// <summary>
    /// Creates a new forward swap based on the given request and swap provider.
    /// </summary>
    /// <param name="request">Creates swap request that comes from the frontend.</param>
    /// <param name="provider">Swap provider selected by the user.</param>
    /// <param name="frontendId">Newly generated frontend ID of the swap.</param>
    /// <param name="userIpAddress">IP address of the user.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Swap and API response.</returns>
    private async Task<CreateSwapResponse> CreateForwardSwapAsync(CreateSwapRequest request, DbSwapProvider provider, string frontendId, string userIpAddress,
        CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(request)}='{request}',{nameof(provider)}='{provider}',{nameof(frontendId)}='{frontendId}',{nameof(userIpAddress)}='{userIpAddress}'");

        if (request.Invoice is null)
            throw new SanityCheckException($"'{nameof(request)}.{nameof(request.Invoice)}' is null.");

        if (request.RefundPublicKey is null)
            throw new SanityCheckException($"'{nameof(request)}.{nameof(request.RefundPublicKey)}' is null.");

        CreateSwapResponse result;
        ElectrumLightningInvoice decodedInvoice;
        try
        {
            decodedInvoice = await this.electrumRpcClient.DecodeInvoiceAsync(request.Invoice, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Debug($"Exception occurred while trying to decode invoice '{request.Invoice.ToBoundedString()}': {e}");
            result = new("Provided invoice is invalid.");

            this.log.Debug($"$<DECODING_FAILED>='{result}'");
            return result;
        }

        DateTime createTime = DateTimeOffset.FromUnixTimeSeconds(decodedInvoice.CreateTime).UtcDateTime;
        DateTime expiryTime = createTime.AddSeconds(decodedInvoice.ExpirySeconds);

        if (expiryTime <= DateTime.UtcNow)
        {
            this.log.Debug($"Provided invoice has expired at {expiryTime}.");

            result = new("Provided invoice has expired.");
            this.log.Debug($"$<EXPIRED>='{result}'");
            return result;
        }

        long invoiceAmount = decodedInvoice.AmountMsat / 1000;
        if (request.ExpectedAmount != invoiceAmount)
        {
            this.log.Debug($"Provided invoice amount {invoiceAmount} does not match expected amount {request.ExpectedAmount}.");

            result = new("Provided invoice amount does not match expected amount.");
            this.log.Debug($"$<INVALID_AMOUNT>='{result}'");
            return result;
        }

        string paymentHashHex = decodedInvoice.PaymentHashHex;

        long percentageFee = (long)Math.Ceiling(provider.PercentageFeeForward / 100m * request.ExpectedAmount);
        long miningFee = provider.MiningFeeForwardSat;
        long amountToPaySats = request.ExpectedAmount + percentageFee + miningFee;

        DbSwap swap;
        try
        {
            swap = await this.swapRepository.InsertForwardAsync(frontendId: frontendId, providerPubkey: provider.Pubkey, userIpAddress: userIpAddress,
                amountToPaySats: amountToPaySats, amountToReceiveSats: request.ExpectedAmount, refundPublicKeyHex: request.RefundPublicKey, invoice: request.Invoice,
                paymentHashHex: paymentHashHex).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Debug($"Exception occurred while inserting forward swap frontend ID '{frontendId}': {e}");

            result = new($"Creating new swap failed. {e.Message}");

            this.log.Debug($"$<INSERT_FAILED>='{result}'");
            return result;
        }

        ElectrumSwapData electrumSwapData;

        try
        {
            electrumSwapData = await this.electrumRpcClient.ForwardSwapAsync(invoice: request.Invoice, amountToPaySats, refundPublicKeyHex: request.RefundPublicKey,
                providerPk: provider.Pubkey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Error($"Exception occurred while creating a new forward swap: {e}");

            try
            {
                await this.swapRepository.MarkSwapRejectedAsync(swap.Id).ConfigureAwait(false);
            }
            catch (Exception ei)
            {
                this.log.Error($"Exception occurred while marking swap ID {swap.Id} as rejected: {ei}");
            }

            string errorMessage = $"Creating new forward swap failed. {e.Message}. Please reload the page and try again or use a different provider.";

            // Handle the special case: "Swap server error: no LN path for the payment could be found".
            if ((e is ElectrumRpcException rpcException) && rpcException.Message.Contains("no LN path", StringComparison.OrdinalIgnoreCase))
                errorMessage = "Creating new forward swap failed. Swap provider found no lightning route for the swap payment.";

            result = new(errorMessage);
            this.log.Debug($"$<SWAP_REJECTED>='{result}'");
            return result;
        }

        if (electrumSwapData.OnChainAmountSats != amountToPaySats)
        {
            this.log.Debug($"Electrum returned on-chain amount {electrumSwapData.OnChainAmountSats} that does not match expected amount {amountToPaySats}. Fees changed.");
            result = new("Swap provider's fee has changed. Please generate a new invoice and try again.");

            try
            {
                await this.swapRepository.MarkSwapRejectedAsync(swap.Id).ConfigureAwait(false);
            }
            catch (Exception ei)
            {
                this.log.Error($"Exception occurred while marking swap ID {swap.Id} as rejected: {ei}");
            }

            this.log.Debug($"$<INVALID_ONCHAIN_AMOUNT>='{result}'");
            return result;
        }

        SwapResponse swapResponse = new(id: swap.FrontendId, reverse: false, asset: "BTC", invoice: null, feeInvoice: null, timeoutBlockHeight: electrumSwapData.Locktime,
            sendAmountSats: electrumSwapData.OnChainAmountSats, receiveAmountSats: request.ExpectedAmount, onChainAmountSats: electrumSwapData.OnChainAmountSats,
            redeemScript: electrumSwapData.RedeemScriptHex, lockupAddress: electrumSwapData.LockupAddress);

        result = new(swapResponse);

        // Register the lockup address to be monitored by the blockchain data monitor. This will allow us to recognize whether the client paid the expected amount on time.
        // Note that the required amount here needs to include the fees for the on-chain transaction that will claim the funds.
        int timeoutHeight = (int)electrumSwapData.Locktime - ForwardSwapServerTimeoutBlocksBuffer;
        this.blockchainDataMonitor.RegisterMonitoredAddress(swapId: swap.Id, frontendId: swap.FrontendId, address: electrumSwapData.LockupAddress,
            amountSats: electrumSwapData.OnChainAmountSats, requiredConfirmations: 1, timeoutHeight: timeoutHeight, isLockupAddress: true, monitorSpending: false,
            fundingTransactionHash: null, fundingOutputIndex: null);

        try
        {
            await this.swapRepository.MarkSwapAcceptedAsync(id: swap.Id, electrumSwapData.LockupAddress, timeoutBlockHeight: electrumSwapData.Locktime,
                redeemScriptHex: electrumSwapData.RedeemScriptHex).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Debug($"Exception occurred while marking forward swap ID {swap.Id} as accepted: {e}");
            result = new($"Creating new swap failed. {e.Message}");
        }

        this.log.Debug($"$='{result}'");
        return result;
    }

    /// <summary>
    /// Creates a new reverse swap based on the given request and swap provider.
    /// </summary>
    /// <param name="request">Creates swap request that comes from the frontend.</param>
    /// <param name="provider">Swap provider selected by the user.</param>
    /// <param name="frontendId">Newly generated frontend ID of the swap.</param>
    /// <param name="userIpAddress">IP address of the user.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Swap and API response.</returns>
    private async Task<CreateSwapResponse> CreateReverseSwapAsync(CreateSwapRequest request, DbSwapProvider provider, string frontendId, string userIpAddress,
        CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(request)}='{request}',{nameof(provider)}='{provider}',{nameof(frontendId)}='{frontendId}',{nameof(userIpAddress)}='{userIpAddress}'");

        if (request.InvoiceAmount is null)
            throw new SanityCheckException($"'{nameof(request)}.{nameof(request.InvoiceAmount)}' is null.");

        if (request.ClaimPublicKey is null)
            throw new SanityCheckException($"'{nameof(request)}.{nameof(request.ClaimPublicKey)}' is null.");

        CreateSwapResponse result;
        ElectrumSwapData electrumSwapData;

        // Before creating the swap, we check the history of the claim address to make sure that we can safely get its history later when we need to monitor it for incoming
        // transactions. If the address has too many transactions in its history, we may not be able to get the full history because of Electrum server limitations.
        try
        {
            ElectrumGetAddressHistoryResponse historyResponse = await this.electrumRpcClient.GetAddressHistoryAsync(request.ClientAddress, cancellationToken).ConfigureAwait(false);
            if (historyResponse.Count > MaxReverseSwapClientAddressHistoryLength)
            {
                this.log.Debug($"Client address '{request.ClientAddress}' has {historyResponse.Count} > {MaxReverseSwapClientAddressHistoryLength} entries in its history.");
                result = new("Too many entries in the destination address. Use a fresh address instead.");

                this.log.Debug($"$<ADDRESS_REJECTED_TOO_MANY_ENTRIES>='{result}'");
                return result;
            }
        }
        catch (Exception e)
        {
            this.log.Debug($"Exception occurred while checking the history of the client address '{request.ClientAddress}' with Electrum: {e}");
            result = new("Destination address rejected. Try using a fresh address instead.");

            this.log.Debug($"$<ADDRESS_REJECTED_EXCEPTION>='{result}'");
            return result;
        }

        DbSwap swap;
        try
        {
            swap = await this.swapRepository.InsertReverseAsync(frontendId: frontendId, providerPubkey: provider.Pubkey, userIpAddress: userIpAddress,
                amountToPaySats: request.InvoiceAmount.Value, amountToReceiveSats: request.ExpectedAmount, claimAddress: request.ClientAddress,
                claimPublicKey: request.ClaimPublicKey).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Debug($"Exception occurred while inserting reverse swap frontend ID '{frontendId}': {e}");

            result = new($"Creating new swap failed. {e.Message}");

            this.log.Debug($"$<INSERT_FAILED>='{result}'");
            return result;
        }

        long prepaymentSats = 2 * provider.MiningFeeReverseSat;

        try
        {
            electrumSwapData = await this.electrumRpcClient.ReverseSwapAsync(lnAmountSats: request.InvoiceAmount.Value, onChainAmountSats: request.ExpectedAmount,
                prepaymentSats: prepaymentSats, preimageHash: request.PreimageHash, claimPk: request.ClaimPublicKey, providerPk: provider.Pubkey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Error($"Exception occurred while creating a new reverse swap: {e}");

            try
            {
                await this.swapRepository.MarkSwapRejectedAsync(swap.Id).ConfigureAwait(false);
            }
            catch (Exception ei)
            {
                this.log.Error($"Exception occurred while marking swap ID {swap.Id} as rejected: {ei}");
            }

            string errorMessage = $"Creating new forward swap failed. {e.Message}. Please reload the page and try again or use a different provider.";

            result = new(errorMessage);

            this.log.Debug($"$<SWAP_REJECTED>='{result}'");
            return result;
        }

        if (electrumSwapData.LightningAmountSats != request.InvoiceAmount.Value)
        {
            this.log.Debug($"Electrum returned lightning amount {electrumSwapData.LightningAmountSats} that does not match expected amount {
                request.InvoiceAmount.Value}. Fees changed.");
            result = new("Swap provider's fee has changed. Please create a new swap.");

            try
            {
                await this.swapRepository.MarkSwapRejectedAsync(swap.Id).ConfigureAwait(false);
            }
            catch (Exception ei)
            {
                this.log.Error($"Exception occurred while marking swap ID {swap.Id} as rejected: {ei}");
            }

            this.log.Debug($"$<INVALID_LIGHTNING_AMOUNT>='{result}'");
            return result;
        }

        SwapResponse swapResponse = new(id: swap.FrontendId, reverse: true, asset: "BTC", invoice: electrumSwapData.Invoice, feeInvoice: electrumSwapData.FeeInvoice,
            timeoutBlockHeight: electrumSwapData.Locktime, sendAmountSats: electrumSwapData.LightningAmountSats, receiveAmountSats: request.ExpectedAmount,
            onChainAmountSats: electrumSwapData.OnChainAmountSats, redeemScript: electrumSwapData.RedeemScriptHex, lockupAddress: electrumSwapData.LockupAddress);

        result = new(swapResponse);

        int requiredConfirmations = this.GetRequiredConfirmationsForAmount(request.ExpectedAmount);
        int timeoutHeight = (int)electrumSwapData.Locktime - requiredConfirmations - LockupAddressTimeoutBuffer;

        // Register the lockup address to be monitored by the blockchain data monitor. This will allow the client to be notified when the funding transaction is
        // seen and when it gets confirmed. Note that the required amount here needs to include the fees for the on-chain transaction that will claim the funds.
        this.blockchainDataMonitor.RegisterMonitoredAddress(swapId: swap.Id, frontendId: swap.FrontendId, address: electrumSwapData.LockupAddress,
            amountSats: electrumSwapData.OnChainAmountSats, requiredConfirmations: requiredConfirmations, timeoutHeight: timeoutHeight, isLockupAddress: true,
            monitorSpending: false, fundingTransactionHash: null, fundingOutputIndex: null);

        try
        {
            await this.swapRepository.MarkSwapAcceptedAsync(id: swap.Id, electrumSwapData.LockupAddress, timeoutBlockHeight: electrumSwapData.Locktime, redeemScriptHex: null)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Debug($"Exception occurred while marking reverse swap ID {swap.Id} as accepted: {e}");
            result = new($"Creating new swap failed. {e.Message}");
        }

        this.log.Debug($"$='{result}'");
        return result;
    }

    /// <summary>
    /// Action that is executed when a swap is requested to be deleted.
    /// </summary>
    /// <param name="request">Request to remove a swap.</param>
    /// <returns>Result of the action method.</returns>
    /// <remarks>We cannot really delete swap from the database, we just mark it as cancelled by the client.</remarks>
    [HttpPost]
    [Route("delete-swap")]
    public async Task<IActionResult> RemoveSwapAsync([FromBody] RemoveSwapRequest request)
    {
        this.log.Debug($"* {nameof(request)}='{request}'");

        IActionResult result;

        RemoveSwapResponse response;
        try
        {
            bool removed = await this.swapRepository.MarkClientCancelledAsync(request.Id, maximumStatus: SwapStatus.Accepted).ConfigureAwait(false);
            if (removed)
            {
                this.blockchainDataMonitor.UnregisterMonitoredAddressWithFrontendId(request.Id);

                _ = this.swapLimitChecker.UnregisterSwap(frontendSwapId: request.Id);
                response = new();
            }
            else response = new($"Could not delete swap with frontend ID '{request.Id}'.");
        }
        catch (Exception e)
        {
            this.log.Error($"Exception occurred while removing swap with frontend ID '{request.Id}': {e}");
            response = new($"Removing swap with frontend ID '{request.Id}' from the database failed. {e.Message}");
        }

        result = this.Ok(response);

        this.log.Debug("$");
        return result;
    }

    /// <summary>
    /// Gets number of confirmations required for the funding transaction based on the amount being swapped.
    /// </summary>
    /// <param name="amountSats">Amount being swapped in satoshis.</param>
    /// <returns>Number of confirmations required for the funding transaction.</returns>
    private int GetRequiredConfirmationsForAmount(long amountSats)
        => amountSats >= TwoConfirmationAmountThresholdSats ? 2 : 1;

    /// <summary>
    /// Action that is executed when a swap status is requested.
    /// </summary>
    /// <param name="frontendId">Frontend ID of the swap.</param>
    /// <returns>Result of the action method.</returns>
    [HttpGet]
    [Route("v2/swap/{frontendId}")]
    public async Task<IActionResult> GetSwapStatusAsync(string frontendId)
    {
        this.log.Debug($"* {nameof(frontendId)}='{frontendId}'");

        HttpContext? context = this.httpContextAccessor.HttpContext;
        if (context is null)
            throw new SanityCheckException("HTTP context is null.");

        IActionResult result;
        DbSwap?[] swaps;
        try
        {
            swaps = await this.swapRepository.GetSwapsByFrontendIdsAsync(new string[] { frontendId }).ConfigureAwait(false);
        }
        catch (DatabaseException e)
        {
            this.log.Error($"Exception occurred while trying to get swap frontend ID '{frontendId}' from the database: {e}");

            result = this.StatusCode((int)HttpStatusCode.InternalServerError, "Getting swap from the database failed.");
            this.log.Debug("$<GET_SWAP_ERROR>");
            return result;
        }

        if ((swaps.Length == 1) && (swaps[0] is not null))
        {
            DbSwap swap = swaps[0]!;
            swap = await this.UpdateSwapStatusIfNeededAsync(swap, context.RequestAborted).ConfigureAwait(false);

            SwapUpdate swapUpdate = SwapUpdate.FromDbSwap(swap);

            GetSwapStatusResponse response = new(status: swapUpdate.Status, failureReason: swapUpdate.FailureReason, swapUpdate.Transaction, error: null);
            result = this.Ok(response);
        }
        else
        {
            this.log.Debug($"Unable to get swap frontend ID '{frontendId}'.");

            GetSwapStatusResponse response = new(status: null, failureReason: null, transaction: null, error: $"Could not find swap with ID '{frontendId}'.");
            result = this.NotFound(response);
        }

        this.log.Debug("$");
        return result;
    }

    /// <summary>
    /// Updates swap status if it changed in a way the active swap monitoring would not detect.
    /// </summary>
    /// <param name="swap">Swap to update.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Original or updated swap.</returns>
    private async Task<DbSwap> UpdateSwapStatusIfNeededAsync(DbSwap swap, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(swap)}='{swap}'");

        swap = await this.UpdateForwardLatePaidSwapAsync(swap, cancellationToken).ConfigureAwait(false);
        swap = await this.UpdateForwardRefundedSwapAsync(swap, cancellationToken).ConfigureAwait(false);

        this.log.Debug($"$='{swap}'");
        return swap;
    }

    /// <summary>
    /// Updates forward swap in <see cref="SwapStatus.ClientErrorFundingTxNotCreated"/> status to <see cref="SwapStatus.ErrorFundingTxNotSpent"/>, if a funding transaction has
    /// been detected.
    /// </summary>
    /// <param name="swap">Swap to update.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Original or updated swap.</returns>
    private async Task<DbSwap> UpdateForwardLatePaidSwapAsync(DbSwap swap, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(swap)}='{swap}'");

        if (!swap.IsForward || (swap.Status != SwapStatus.ClientErrorFundingTxNotCreated) || (swap.LockupAddress is null) || (swap.FundingTxId is not null)
            || (swap.LockupOutputIndex is not null) || (swap.TimeoutBlockHeight is null))
        {
            this.log.Debug($"$<NO_MATCH>='{swap}'");
            return swap;
        }

        // Check if a funding transaction was created.
        long startHeight = swap.TimeoutBlockHeight.Value - ForwardSwapTimeoutBlocks;
        FundingInfo? fundingInfo = await this.blockchainDataMonitor.CheckIsFundedAsync(swapId: swap.Id, frontendId: swap.FrontendId, address: swap.LockupAddress,
            amountSats: swap.AmountToPaySats, startHeight: startHeight, cancellationToken).ConfigureAwait(false);

        if (fundingInfo is not null)
        {
            this.log.Debug($"Swap ID {swap.Id} funding transaction '{fundingInfo.TransactionId}' has been detected. Marking the swap as funded.");
            try
            {
                DbSwap? fundedSwap = await this.swapRepository.FundingTransactionSetAsync(swapId: swap.Id, isConfirmed: true, transactionId: fundingInfo.TransactionId,
                    fundingInfo.OutputIndex, transactionData: fundingInfo.TransactionData).ConfigureAwait(false);

                if (fundedSwap is not null)
                    swap = fundedSwap;
            }
            catch (DatabaseException e)
            {
                this.log.Error($"Exception occurred while trying to mark swap ID '{swap.Id}' as refunded in the database: {e}");
            }
        }

        this.log.Debug($"$='{swap}'");
        return swap;
    }

    /// <summary>
    /// Updates forward swap in <see cref="SwapStatus.ErrorFundingTxNotSpent"/> or <see cref="SwapStatus.ClientErrorFundingTxNotCreated"/> status to
    /// <see cref="SwapStatus.FundingTxRefunded"/>, if a refund transaction has been detected.
    /// </summary>
    /// <param name="swap">Swap to update.</param>
    /// <param name="cancellationToken">Cancellation token that allows the caller to cancel the operation.</param>
    /// <returns>Original or updated swap.</returns>
    private async Task<DbSwap> UpdateForwardRefundedSwapAsync(DbSwap swap, CancellationToken cancellationToken)
    {
        this.log.Debug($"* {nameof(swap)}='{swap}'");

        if (!swap.IsForward || ((swap.Status != SwapStatus.ErrorFundingTxNotSpent) && (swap.Status != SwapStatus.ClientErrorFundingTxNotCreated)) || (swap.LockupAddress is null)
            || (swap.FundingTxId is null) || (swap.LockupOutputIndex is null) || (swap.TimeoutBlockHeight is null))
        {
            this.log.Debug($"$<NO_MATCH>='{swap}'");
            return swap;
        }

        // Check if refund has been issued.
        long startHeight = swap.TimeoutBlockHeight.Value - ForwardSwapTimeoutBlocks;
        RefundInfo? refundInfo = await this.blockchainDataMonitor.CheckIsRefundedAsync(address: swap.LockupAddress, transactionId: swap.FundingTxId,
            outputIndex: swap.LockupOutputIndex.Value, startHeight: startHeight, cancellationToken).ConfigureAwait(false);

        if (refundInfo is not null)
        {
            this.log.Debug($"Swap ID {swap.Id} funding transaction output '{swap.FundingTxId}:{swap.LockupOutputIndex}' has been spent. Marking the swap as refunded.");
            try
            {
                DbSwap? refundedSwap = await this.swapRepository.SwapClaimedOrRefundedAsync(swap.Id, transactionId: refundInfo.TransactionId,
                    transactionData: refundInfo.TransactionData, isClaimed: false).ConfigureAwait(false);

                if (refundedSwap is not null)
                    swap = refundedSwap;
            }
            catch (DatabaseException e)
            {
                this.log.Error($"Exception occurred while trying to mark swap ID '{swap.Id}' as refunded in the database: {e}");
            }
        }

        this.log.Debug($"$='{swap}'");
        return swap;
    }

    /// <summary>
    /// Action that is executed when a swap's funding transaction is requested.
    /// </summary>
    /// <param name="request">Request to get funding transaction of a swap.</param>
    /// <returns>Result of the action method.</returns>
    [HttpPost]
    [Route("getswaptransaction")]
    public async Task<IActionResult> GetSwapTransactionAsync([FromBody] GetSwapTransactionRequest request)
    {
        this.log.Debug($"* {nameof(request)}='{request}'");

        string id = request.Id;
        IActionResult result;

        GetSwapTransactionResponse response;
        try
        {
            DbSwap?[] dbRecords = await this.swapRepository.GetSwapsByFrontendIdsAsync(new string[] { id }).ConfigureAwait(false);

            if (dbRecords.Length != 1)
                throw new SanityCheckException($"Expected 1 record, got {dbRecords.Length}");

            DbSwap? swap = dbRecords[0];
            if (swap is not null)
            {
                if (swap.FundingTxId is not null)
                {
                    response = new(transactionId: swap.FundingTxId, transactionData: swap.FundingTxData, timeoutBlockHeight: swap.TimeoutBlockHeight, error: null);
                }
                else
                {
                    this.log.Debug($"Swap frontend ID '{id}' does not have funding transaction yet.");
                    response = new($"No coins were locked up yet for swap '{id}'.");
                }
            }
            else
            {
                this.log.Debug($"Swap frontend ID '{id}' does not exist.");
                response = new($"Could not find swap with frontend ID '{id}'.");
            }
        }
        catch (Exception e)
        {
            this.log.Error($"Exception occurred while getting swap frontend ID '{id}' from the database: {e}");
            response = new($"Getting swap frontend ID '{id}' from the database failed. {e.Message}");
        }

        result = this.Ok(response);

        this.log.Debug("$");
        return result;
    }

    /// <summary>
    /// Action that is executed when a transaction broadcasting is requested.
    /// </summary>
    /// <param name="request">Request to broadcast a transaction.</param>
    /// <returns>Result of the action method.</returns>
    [HttpPost]
    [Route("broadcasttransaction")]
    public async Task<IActionResult> BroadcastTransactionAsync([FromBody] BroadcastTransactionRequest request)
    {
        this.log.Debug($"* {nameof(request)}='{request}'");

        HttpContext? context = this.httpContextAccessor.HttpContext;
        if (context is null)
            throw new SanityCheckException("HTTP context is null.");

        IActionResult result;

        if (request.Currency == "BTC")
        {
            try
            {
                string transactionId = await this.electrumRpcClient.BroadcastAsync(request.TransactionHex, context.RequestAborted).ConfigureAwait(false);
                BroadcastTransactionResponse response = new(transactionId);
                result = this.Ok(response);
            }
            catch (Exception e)
            {
                this.log.Error($"Exception occurred while broadcasting transaction: {e}");
                result = this.BadRequest($"Broadcasting transaction failed. {e.Message}");
            }
        }
        else result = this.BadRequest("Only BTC transactions are supported.");

        this.log.Debug("$");
        return result;
    }

    /// <summary>
    /// Action that is executed when chain fee rates are requested.
    /// </summary>
    /// <returns>Result of the action method.</returns>
    [HttpGet]
    [Route("v2/chain/fees")]
    public async Task<IActionResult> GetChainFeesAsync()
    {
        this.log.Debug("*");

        IActionResult result;

        HttpContext? context = this.httpContextAccessor.HttpContext;
        if (context is null)
            throw new SanityCheckException("HTTP context is null.");

        try
        {
            ElectrumFeeRate rate = await this.electrumRpcClient.GetFeeRateAsync(context.RequestAborted).ConfigureAwait(false);
            GetChainFeesResponse response = new(rate.FeeRateKvB / 1000);
            result = this.Ok(response);
        }
        catch (Exception e)
        {
            this.log.Error($"Exception occurred while getting fee rate: {e}");
            result = this.BadRequest($"Getting fee rate failed. {e.Message}");
        }

        this.log.Debug("$");
        return result;
    }
}