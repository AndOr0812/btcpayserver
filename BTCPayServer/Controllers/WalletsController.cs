﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.ModelBinders;
using BTCPayServer.Models;
using BTCPayServer.Models.WalletViewModels;
using BTCPayServer.Security;
using BTCPayServer.Services;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using LedgerWallet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using static BTCPayServer.Controllers.StoresController;

namespace BTCPayServer.Controllers
{
    [Route("wallets")]
    [Authorize(Policy = Policies.CanModifyStoreSettings.Key, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [AutoValidateAntiforgeryToken]
    public partial class WalletsController : Controller
    {
        public StoreRepository Repository { get; }
        public WalletRepository WalletRepository { get; }
        public BTCPayNetworkProvider NetworkProvider { get; }
        public ExplorerClientProvider ExplorerClientProvider { get; }

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly JsonSerializerSettings _serializerSettings;
        private readonly NBXplorerDashboard _dashboard;
        private readonly IAuthorizationService _authorizationService;
        private readonly IFeeProviderFactory _feeRateProvider;
        private readonly BTCPayWalletProvider _walletProvider;
        public RateFetcher RateFetcher { get; }
        [TempData]
        public string StatusMessage { get; set; }

        CurrencyNameTable _currencyTable;
        public WalletsController(StoreRepository repo,
                                 WalletRepository walletRepository,
                                 CurrencyNameTable currencyTable,
                                 BTCPayNetworkProvider networkProvider,
                                 UserManager<ApplicationUser> userManager,
                                 MvcNewtonsoftJsonOptions mvcJsonOptions,
                                 NBXplorerDashboard dashboard,
                                 RateFetcher rateProvider,
                                 IAuthorizationService authorizationService,
                                 ExplorerClientProvider explorerProvider,
                                 IFeeProviderFactory feeRateProvider,
                                 BTCPayWalletProvider walletProvider)
        {
            _currencyTable = currencyTable;
            Repository = repo;
            WalletRepository = walletRepository;
            RateFetcher = rateProvider;
            _authorizationService = authorizationService;
            NetworkProvider = networkProvider;
            _userManager = userManager;
            _serializerSettings = mvcJsonOptions.SerializerSettings;
            _dashboard = dashboard;
            ExplorerClientProvider = explorerProvider;
            _feeRateProvider = feeRateProvider;
            _walletProvider = walletProvider;
        }

        // Borrowed from https://github.com/ManageIQ/guides/blob/master/labels.md
        string[] LabelColorScheme = new string[] 
        {
            "#fbca04",
            "#0e8a16",
            "#ff7619",
            "#84b6eb",
            "#5319e7",
            "#000000",
            "#cc317c",
        };

        const int MaxLabelSize = 20;
        const int MaxCommentSize = 200;
        [HttpPost]
        [Route("{walletId}")]
        public async Task<IActionResult> ModifyTransaction(
            // We need addlabel and addlabelclick. addlabel is the + button if the label does not exists,
            // addlabelclick is if the user click on existing label. For some reason, reusing the same name attribute for both
            // does not work
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, string transactionId, 
                                string addlabel = null, 
                                string addlabelclick = null,
                                string addcomment = null, 
                                string removelabel = null)
        {
            addlabel = addlabel ?? addlabelclick;
            // Hack necessary when the user enter a empty comment and submit.
            // For some reason asp.net consider addcomment null instead of empty string...
            try
            {
                if (addcomment == null && Request?.Form?.TryGetValue(nameof(addcomment), out _) is true)
                {
                    addcomment = string.Empty;
                }
            }
            catch { }
            /////////
            
            DerivationSchemeSettings paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();

            var walletBlobInfoAsync = WalletRepository.GetWalletInfo(walletId);
            var walletTransactionsInfoAsync = WalletRepository.GetWalletTransactionsInfo(walletId);
            var wallet = _walletProvider.GetWallet(paymentMethod.Network);
            var walletBlobInfo = await walletBlobInfoAsync;
            var walletTransactionsInfo = await walletTransactionsInfoAsync;
            if (addlabel != null)
            {
                addlabel = addlabel.Trim().ToLowerInvariant().Replace(',',' ').Truncate(MaxLabelSize);
                var labels = walletBlobInfo.GetLabels();
                if (!walletTransactionsInfo.TryGetValue(transactionId, out var walletTransactionInfo))
                {
                    walletTransactionInfo = new WalletTransactionInfo();
                }
                if (!labels.Any(l => l.Value.Equals(addlabel, StringComparison.OrdinalIgnoreCase)))
                {
                    List<string> allColors = new List<string>();
                    allColors.AddRange(LabelColorScheme);
                    allColors.AddRange(labels.Select(l => l.Color));
                    var chosenColor =
                        allColors
                        .GroupBy(k => k)
                        .OrderBy(k => k.Count())
                        .ThenBy(k => Array.IndexOf(LabelColorScheme, k.Key))
                        .First().Key;
                    walletBlobInfo.LabelColors.Add(addlabel, chosenColor);
                    await WalletRepository.SetWalletInfo(walletId, walletBlobInfo);
                }
                if (walletTransactionInfo.Labels.Add(addlabel))
                {
                    await WalletRepository.SetWalletTransactionInfo(walletId, transactionId, walletTransactionInfo);
                }
            }
            else if (removelabel != null)
            {
                removelabel = removelabel.Trim().ToLowerInvariant().Truncate(MaxLabelSize);
                if (walletTransactionsInfo.TryGetValue(transactionId, out var walletTransactionInfo))
                {
                    if (walletTransactionInfo.Labels.Remove(removelabel))
                    {
                        var canDelete = !walletTransactionsInfo.SelectMany(txi => txi.Value.Labels).Any(l => l == removelabel);
                        if (canDelete)
                        {
                            walletBlobInfo.LabelColors.Remove(removelabel);
                            await WalletRepository.SetWalletInfo(walletId, walletBlobInfo);
                        }
                        await WalletRepository.SetWalletTransactionInfo(walletId, transactionId, walletTransactionInfo);
                    }
                }
            }
            else if (addcomment != null)
            {
                addcomment = addcomment.Trim().Truncate(MaxCommentSize);
                if (!walletTransactionsInfo.TryGetValue(transactionId, out var walletTransactionInfo))
                {
                    walletTransactionInfo = new WalletTransactionInfo();
                }
                walletTransactionInfo.Comment = addcomment;
                await WalletRepository.SetWalletTransactionInfo(walletId, transactionId, walletTransactionInfo);
            }
            return RedirectToAction(nameof(WalletTransactions), new { walletId = walletId.ToString() });
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ListWallets()
        {
            if (GetUserId() == null)
            {
                return Challenge(AuthenticationSchemes.Cookie);
            }
            var wallets = new ListWalletsViewModel();
            var stores = await Repository.GetStoresByUserId(GetUserId());

            var onChainWallets = stores
                                .SelectMany(s => s.GetSupportedPaymentMethods(NetworkProvider)
                                              .OfType<DerivationSchemeSettings>()
                                              .Select(d => ((Wallet: _walletProvider.GetWallet(d.Network),
                                                            DerivationStrategy: d.AccountDerivation,
                                                            Network: d.Network)))
                                              .Where(_ => _.Wallet != null)
                                              .Select(_ => (Wallet: _.Wallet,
                                                            Store: s,
                                                            Balance: GetBalanceString(_.Wallet, _.DerivationStrategy),
                                                            DerivationStrategy: _.DerivationStrategy,
                                                            Network: _.Network)))
                                              .ToList();

            foreach (var wallet in onChainWallets)
            {
                ListWalletsViewModel.WalletViewModel walletVm = new ListWalletsViewModel.WalletViewModel();
                wallets.Wallets.Add(walletVm);
                walletVm.Balance = await wallet.Balance + " " + wallet.Wallet.Network.CryptoCode;
                walletVm.IsOwner = wallet.Store.Role == StoreRoles.Owner;
                if (!walletVm.IsOwner)
                {
                    walletVm.Balance = "";
                }
                walletVm.CryptoCode = wallet.Network.CryptoCode;
                walletVm.StoreId = wallet.Store.Id;
                walletVm.Id = new WalletId(wallet.Store.Id, wallet.Network.CryptoCode);
                walletVm.StoreName = wallet.Store.StoreName;
            }

            return View(wallets);
        }

        [HttpGet]
        [Route("{walletId}")]
        public async Task<IActionResult> WalletTransactions(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, string labelFilter = null)
        {
            DerivationSchemeSettings paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();

            var wallet = _walletProvider.GetWallet(paymentMethod.Network);
            var walletBlobAsync = WalletRepository.GetWalletInfo(walletId);
            var walletTransactionsInfoAsync = WalletRepository.GetWalletTransactionsInfo(walletId);
            var transactions = await wallet.FetchTransactions(paymentMethod.AccountDerivation);
            var walletBlob = await walletBlobAsync;
            var walletTransactionsInfo = await walletTransactionsInfoAsync;
            var model = new ListTransactionsViewModel();
            foreach (var tx in transactions.UnconfirmedTransactions.Transactions.Concat(transactions.ConfirmedTransactions.Transactions).ToArray())
            {
                var vm = new ListTransactionsViewModel.TransactionViewModel();
                vm.Id = tx.TransactionId.ToString();
                vm.Link = string.Format(CultureInfo.InvariantCulture, paymentMethod.Network.BlockExplorerLink, vm.Id);
                vm.Timestamp = tx.Timestamp;
                vm.Positive = tx.BalanceChange >= Money.Zero;
                vm.Balance = tx.BalanceChange.ToString();
                vm.IsConfirmed = tx.Confirmations != 0;

                if (walletTransactionsInfo.TryGetValue(tx.TransactionId.ToString(), out var transactionInfo))
                {
                    var labels = walletBlob.GetLabels(transactionInfo);
                    vm.Labels.AddRange(labels);
                    model.Labels.AddRange(labels);
                    vm.Comment = transactionInfo.Comment;
                }

                if (labelFilter == null || vm.Labels.Any(l => l.Value.Equals(labelFilter, StringComparison.OrdinalIgnoreCase)))
                    model.Transactions.Add(vm);
            }
            model.Transactions = model.Transactions.OrderByDescending(t => t.Timestamp).ToList();
            return View(model);
        }

        private static string GetLabelTarget(WalletId walletId, uint256 txId)
        {
            return $"{walletId}:{txId}";
        }

        [HttpGet]
        [Route("{walletId}/send")]
        public async Task<IActionResult> WalletSend(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, string defaultDestination = null, string defaultAmount = null)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            var store = await Repository.FindStore(walletId.StoreId, GetUserId());
            DerivationSchemeSettings paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();
            var network = this.NetworkProvider.GetNetwork<BTCPayNetwork>(walletId?.CryptoCode);
            if (network == null)
                return NotFound();
            var storeData = store.GetStoreBlob();
            var rateRules = store.GetStoreBlob().GetRateRules(NetworkProvider);
            rateRules.Spread = 0.0m;
            var currencyPair = new Rating.CurrencyPair(paymentMethod.PaymentId.CryptoCode, GetCurrencyCode(storeData.DefaultLang) ?? "USD");
            double.TryParse(defaultAmount, out var amount);
           var model = new WalletSendModel()
            {
                Outputs = new List<WalletSendModel.TransactionOutput>()
                {
                    new WalletSendModel.TransactionOutput()
                    {
                        Amount = Convert.ToDecimal(amount),
                        DestinationAddress = defaultDestination
                    }
                },
                CryptoCode = walletId.CryptoCode
            };
            

            var feeProvider = _feeRateProvider.CreateFeeProvider(network);
            var recommendedFees = feeProvider.GetFeeRateAsync();
            var balance = _walletProvider.GetWallet(network).GetBalance(paymentMethod.AccountDerivation);
            model.CurrentBalance = (await balance).ToDecimal(MoneyUnit.BTC);
            model.RecommendedSatoshiPerByte = (int)(await recommendedFees).GetFee(1).Satoshi;
            model.FeeSatoshiPerByte = model.RecommendedSatoshiPerByte;
            model.SupportRBF = network.SupportRBF;
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                try
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    var result = await RateFetcher.FetchRate(currencyPair, rateRules, cts.Token).WithCancellation(cts.Token);
                    if (result.BidAsk != null)
                    {
                        model.Rate = result.BidAsk.Center;
                        model.Divisibility = _currencyTable.GetNumberFormatInfo(currencyPair.Right, true).CurrencyDecimalDigits;
                        model.Fiat = currencyPair.Right;
                    }
                    else
                    {
                        model.RateError = $"{result.EvaluatedRule} ({string.Join(", ", result.Errors.OfType<object>().ToArray())})";
                    }
                }
                catch (Exception ex) { model.RateError = ex.Message; }
            }
            return View(model);
        }

        [HttpPost]
        [Route("{walletId}/send")]
        public async Task<IActionResult> WalletSend(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, WalletSendModel vm, string command = "", CancellationToken cancellation = default)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            var store = await Repository.FindStore(walletId.StoreId, GetUserId());
            if (store == null)
                return NotFound();
            var network = this.NetworkProvider.GetNetwork<BTCPayNetwork>(walletId?.CryptoCode);
            if (network == null)
                return NotFound();
            vm.SupportRBF = network.SupportRBF;
            decimal transactionAmountSum  = 0;
            
            if (command == "add-output")
            {
                ModelState.Clear();
                vm.Outputs.Add(new WalletSendModel.TransactionOutput());
                return View(vm);
            }
            if (command.StartsWith("remove-output", StringComparison.InvariantCultureIgnoreCase))
            {
                ModelState.Clear();
                var index = int.Parse(command.Substring(command.IndexOf(":",StringComparison.InvariantCultureIgnoreCase) + 1),  CultureInfo.InvariantCulture);
                vm.Outputs.RemoveAt(index);
                return View(vm);
            }
            

            if (!vm.Outputs.Any())
            {
                ModelState.AddModelError(string.Empty,
                    "Please add at least one transaction output");
                return View(vm);
            }

            var subtractFeesOutputsCount = new List<int>();
            var substractFees = vm.Outputs.Any(o => o.SubtractFeesFromOutput);
            for (var i = 0; i < vm.Outputs.Count; i++)
            {
                var transactionOutput = vm.Outputs[i];
                if (transactionOutput.SubtractFeesFromOutput)
                {
                    subtractFeesOutputsCount.Add(i);
                }
                var destination = ParseDestination(transactionOutput.DestinationAddress, network.NBitcoinNetwork);
                if (destination == null)
                    ModelState.AddModelError(nameof(transactionOutput.DestinationAddress), "Invalid address");

                if (transactionOutput.Amount.HasValue)
                {
                    transactionAmountSum += transactionOutput.Amount.Value;

                    if (vm.CurrentBalance == transactionOutput.Amount.Value &&
                        !transactionOutput.SubtractFeesFromOutput)
                        vm.AddModelError(model => model.Outputs[i].SubtractFeesFromOutput,
                            "You are sending your entire balance to the same destination, you should subtract the fees",
                            this);
                }
            }

            if (subtractFeesOutputsCount.Count > 1)
            {
                foreach (var subtractFeesOutput in subtractFeesOutputsCount)
                {
                    vm.AddModelError(model => model.Outputs[subtractFeesOutput].SubtractFeesFromOutput,
                        "You can only subtract fees from one output", this);
                }
            }else if (vm.CurrentBalance == transactionAmountSum && !substractFees)
            {
                ModelState.AddModelError(string.Empty,
                    "You are sending your entire balance, you should subtract the fees from an output");
            }

            if (vm.CurrentBalance < transactionAmountSum)
            {
                for (var i = 0; i < vm.Outputs.Count; i++)
                {
                    vm.AddModelError(model => model.Outputs[i].Amount,
                        "You are sending more than what you own", this);
                }
            }

            if (!ModelState.IsValid)
                return View(vm);

            DerivationSchemeSettings derivationScheme = GetDerivationSchemeSettings(walletId);

            CreatePSBTResponse psbt = null;
            try
            {
                psbt = await CreatePSBT(network, derivationScheme, vm, cancellation);
            }
            catch (NBXplorerException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Error.Message);
                return View(vm);
            }
            catch (NotSupportedException)
            {
                ModelState.AddModelError(string.Empty, "You need to update your version of NBXplorer");
                return View(vm);
            }
            derivationScheme.RebaseKeyPaths(psbt.PSBT);
            
            switch (command)
            {
                case "ledger":
                    return ViewWalletSendLedger(psbt.PSBT, psbt.ChangeAddress);
                case "seed":
                    return SignWithSeed(walletId, psbt.PSBT.ToBase64());
                case "analyze-psbt":
                    var name =
                        $"Send-{string.Join('_', vm.Outputs.Select(output => $"{output.Amount}->{output.DestinationAddress}{(output.SubtractFeesFromOutput ? "-Fees" : string.Empty)}"))}.psbt";
                    return RedirectToAction(nameof(WalletPSBT), new { walletId = walletId, psbt = psbt.PSBT.ToBase64(), FileName = name });
                default:
                    return View(vm);
            }
            
        }

        private ViewResult ViewWalletSendLedger(PSBT psbt, BitcoinAddress hintChange = null)
        {
            return View("WalletSendLedger", new WalletSendLedgerModel()
            {
                PSBT = psbt.ToBase64(),
                HintChange = hintChange?.ToString(),
                WebsocketPath = this.Url.Action(nameof(LedgerConnection)),
                SuccessPath = this.Url.Action(nameof(WalletPSBTReady))
            });
        }
      
        [HttpGet("{walletId}/psbt/seed")]
        public IActionResult SignWithSeed([ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId,string psbt)
        {
            return View(nameof(SignWithSeed), new SignWithSeedViewModel()
            {
                PSBT = psbt
            });
        }

        [HttpPost("{walletId}/psbt/seed")]
        public async Task<IActionResult> SignWithSeed([ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, SignWithSeedViewModel viewModel)
        {
            if (!ModelState.IsValid)
            {
                return View(viewModel);
            }
            var network = NetworkProvider.GetNetwork<BTCPayNetwork>(walletId.CryptoCode);
            if (network == null)
                throw new FormatException("Invalid value for crypto code");

            ExtKey extKey = viewModel.GetExtKey(network.NBitcoinNetwork);

            if (extKey == null)
            {
                ModelState.AddModelError(nameof(viewModel.SeedOrKey),
                    "Seed or Key was not in a valid format. It is either the 12/24 words or starts with xprv");
            }

            var psbt = PSBT.Parse(viewModel.PSBT, network.NBitcoinNetwork);

            if (!psbt.IsReadyToSign())
            {
                ModelState.AddModelError(nameof(viewModel.PSBT), "PSBT is not ready to be signed");
            }

            if (!ModelState.IsValid)
            {
                return View(viewModel);
            }

            ExtKey signingKey = null;
            var settings = GetDerivationSchemeSettings(walletId);
            var signingKeySettings = settings.GetSigningAccountKeySettings();
            if (signingKeySettings.RootFingerprint is null)
                signingKeySettings.RootFingerprint = extKey.GetPublicKey().GetHDFingerPrint();

            RootedKeyPath rootedKeyPath = signingKeySettings.GetRootedKeyPath();
            // The user gave the root key, let's try to rebase the PSBT, and derive the account private key
            if (rootedKeyPath?.MasterFingerprint == extKey.GetPublicKey().GetHDFingerPrint())
            {
                psbt.RebaseKeyPaths(signingKeySettings.AccountKey, rootedKeyPath);
                signingKey = extKey.Derive(rootedKeyPath.KeyPath);
            }
            // The user maybe gave the account key, let's try to sign with it
            else
            {
                signingKey = extKey;
            }
            var balanceChange = psbt.GetBalance(settings.AccountDerivation, signingKey, rootedKeyPath);
            if (balanceChange == Money.Zero)
            {
                ModelState.AddModelError(nameof(viewModel.SeedOrKey), "This seed is unable to sign this transaction. Either the seed is incorrect, or the account path has not been properly configured in the Wallet Settings.");
                return View(viewModel);
            }
            psbt.SignAll(settings.AccountDerivation, signingKey, rootedKeyPath);
            ModelState.Remove(nameof(viewModel.PSBT));
            return await WalletPSBTReady(walletId, psbt.ToBase64(), signingKey.GetWif(network.NBitcoinNetwork).ToString(), rootedKeyPath?.ToString());
        }

        private string ValueToString(Money v, BTCPayNetworkBase network)
        {
            return v.ToString() + " " + network.CryptoCode;
        }

        private IDestination[] ParseDestination(string destination, Network network)
        {
            try
            {
                destination = destination?.Trim();
                return new IDestination[] { BitcoinAddress.Create(destination, network) };
            }
            catch
            {
                return null;
            }
        }

        private IActionResult RedirectToWalletTransaction(WalletId walletId, Transaction transaction)
        {
            var network = NetworkProvider.GetNetwork<BTCPayNetwork>(walletId.CryptoCode);
            if (transaction != null)
            {
                var wallet = _walletProvider.GetWallet(network);
                var derivationSettings = GetDerivationSchemeSettings(walletId);
                wallet.InvalidateCache(derivationSettings.AccountDerivation);
                StatusMessage = $"Transaction broadcasted successfully ({transaction.GetHash().ToString()})";
            }
            return RedirectToAction(nameof(WalletTransactions));
        }

        [HttpGet]
        [Route("{walletId}/rescan")]
        public async Task<IActionResult> WalletRescan(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            DerivationSchemeSettings paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();

            var vm = new RescanWalletModel();
            vm.IsFullySync = _dashboard.IsFullySynched(walletId.CryptoCode, out var unused);
            vm.IsServerAdmin = (await _authorizationService.AuthorizeAsync(User, Policies.CanModifyServerSettings.Key)).Succeeded;
            vm.IsSupportedByCurrency = _dashboard.Get(walletId.CryptoCode)?.Status?.BitcoinStatus?.Capabilities?.CanScanTxoutSet == true;
            var explorer = ExplorerClientProvider.GetExplorerClient(walletId.CryptoCode);
            var scanProgress = await explorer.GetScanUTXOSetInformationAsync(paymentMethod.AccountDerivation);
            if (scanProgress != null)
            {
                vm.PreviousError = scanProgress.Error;
                if (scanProgress.Status == ScanUTXOStatus.Queued || scanProgress.Status == ScanUTXOStatus.Pending)
                {
                    if (scanProgress.Progress == null)
                    {
                        vm.Progress = 0;
                    }
                    else
                    {
                        vm.Progress = scanProgress.Progress.OverallProgress;
                        vm.RemainingTime = TimeSpan.FromSeconds(scanProgress.Progress.RemainingSeconds).PrettyPrint();
                    }
                }
                if (scanProgress.Status == ScanUTXOStatus.Complete)
                {
                    vm.LastSuccess = scanProgress.Progress;
                    vm.TimeOfScan = (scanProgress.Progress.CompletedAt.Value - scanProgress.Progress.StartedAt).PrettyPrint();
                }
            }
            return View(vm);
        }

        [HttpPost]
        [Route("{walletId}/rescan")]
        [Authorize(Policy = Policies.CanModifyServerSettings.Key, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        public async Task<IActionResult> WalletRescan(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, RescanWalletModel vm)
        {
            if (walletId?.StoreId == null)
                return NotFound();
            DerivationSchemeSettings paymentMethod = GetDerivationSchemeSettings(walletId);
            if (paymentMethod == null)
                return NotFound();
            var explorer = ExplorerClientProvider.GetExplorerClient(walletId.CryptoCode);
            try
            {
                await explorer.ScanUTXOSetAsync(paymentMethod.AccountDerivation, vm.BatchSize, vm.GapLimit, vm.StartingIndex);
            }
            catch (NBXplorerException ex) when (ex.Error.Code == "scanutxoset-in-progress")
            {

            }
            return RedirectToAction();
        }

        private string GetCurrencyCode(string defaultLang)
        {
            if (defaultLang == null)
                return null;
            try
            {
                var ri = new RegionInfo(defaultLang);
                return ri.ISOCurrencySymbol;
            }
            catch (ArgumentException) { }
            return null;
        }

        public StoreData CurrentStore
        {
            get
            {
                return HttpContext.GetStoreData();
            }
        }

        private DerivationSchemeSettings GetDerivationSchemeSettings(WalletId walletId)
        {
            var paymentMethod = CurrentStore
                            .GetSupportedPaymentMethods(NetworkProvider)
                            .OfType<DerivationSchemeSettings>()
                            .FirstOrDefault(p => p.PaymentId.PaymentType == Payments.PaymentTypes.BTCLike && p.PaymentId.CryptoCode == walletId.CryptoCode);
            return paymentMethod;
        }

        private static async Task<string> GetBalanceString(BTCPayWallet wallet, DerivationStrategyBase derivationStrategy)
        {
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    return (await wallet.GetBalance(derivationStrategy, cts.Token)).ToString();
                }
                catch
                {
                    return "--";
                }
            }
        }

        private string GetUserId()
        {
            return _userManager.GetUserId(User);
        }

        [HttpGet]
        [Route("{walletId}/send/ledger/ws")]
        public async Task<IActionResult> LedgerConnection(
            [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId,
            string command,
            // getinfo
            // getxpub
            int account = 0,
            // sendtoaddress
            string psbt = null,
            string hintChange = null
            )
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
                return NotFound();
            var storeData = CurrentStore;
            var network = NetworkProvider.GetNetwork<BTCPayNetwork>(walletId.CryptoCode);
            if (network == null)
                throw new FormatException("Invalid value for crypto code");
            var derivationSettings = GetDerivationSchemeSettings(walletId);

            var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

            using (var normalOperationTimeout = new CancellationTokenSource())
            using (var signTimeout = new CancellationTokenSource())
            {
                normalOperationTimeout.CancelAfter(TimeSpan.FromMinutes(30));
                var hw = new LedgerHardwareWalletService(webSocket);
                var model = new WalletSendLedgerModel();
                object result = null;
                try
                {
                    if (command == "test")
                    {
                        result = await hw.Test(normalOperationTimeout.Token);
                    }
                    if (command == "sendtoaddress")
                    {
                        if (!_dashboard.IsFullySynched(network.CryptoCode, out var summary))
                            throw new Exception($"{network.CryptoCode}: not started or fully synched");

                        var accountKey = derivationSettings.GetSigningAccountKeySettings();
                        // Some deployment does not have the AccountKeyPath set, let's fix this...
                        if (accountKey.AccountKeyPath == null)
                        {
                            // If the saved wallet key path is not present or incorrect, let's scan the wallet to see if it can sign strategy
                            var foundKeyPath = await hw.FindKeyPathFromDerivation(network,
                                                                               derivationSettings.AccountDerivation,
                                                                               normalOperationTimeout.Token);
                            accountKey.AccountKeyPath = foundKeyPath ?? throw new HardwareWalletException($"This store is not configured to use this ledger");
                            storeData.SetSupportedPaymentMethod(derivationSettings);
                            await Repository.UpdateStore(storeData);
                        }
                        // If it has already the AccountKeyPath, we did not looked up for it, so we need to check if we are on the right ledger
                        else
                        {
                            // Checking if ledger is right with the RootFingerprint is faster as it does not need to make a query to the parent xpub, 
                            // but some deployment does not have it, so let's use AccountKeyPath instead
                            if (accountKey.RootFingerprint == null)
                            {

                                var actualPubKey = await hw.GetExtPubKey(network, accountKey.AccountKeyPath, normalOperationTimeout.Token);
                                if (!derivationSettings.AccountDerivation.GetExtPubKeys().Any(p => p.GetPublicKey() == actualPubKey.GetPublicKey()))
                                    throw new HardwareWalletException($"This store is not configured to use this ledger");
                            }
                            // We have the root fingerprint, we can check the root from it
                            else
                            {
                                var actualPubKey = await hw.GetPubKey(network, new KeyPath(), normalOperationTimeout.Token);
                                if (actualPubKey.GetHDFingerPrint() != accountKey.RootFingerprint.Value)
                                    throw new HardwareWalletException($"This store is not configured to use this ledger");
                            }
                        }

                        // Some deployment does not have the RootFingerprint set, let's fix this...
                        if (accountKey.RootFingerprint == null)
                        {
                            accountKey.RootFingerprint = (await hw.GetPubKey(network, new KeyPath(), normalOperationTimeout.Token)).GetHDFingerPrint();
                            storeData.SetSupportedPaymentMethod(derivationSettings);
                            await Repository.UpdateStore(storeData);
                        }

                        var psbtResponse = new CreatePSBTResponse()
                        {
                            PSBT = PSBT.Parse(psbt, network.NBitcoinNetwork),
                            ChangeAddress = string.IsNullOrEmpty(hintChange) ? null : BitcoinAddress.Create(hintChange, network.NBitcoinNetwork)
                        };


                        derivationSettings.RebaseKeyPaths(psbtResponse.PSBT);

                        signTimeout.CancelAfter(TimeSpan.FromMinutes(5));
                        psbtResponse.PSBT = await hw.SignTransactionAsync(psbtResponse.PSBT, accountKey.GetRootedKeyPath(), accountKey.AccountKey, psbtResponse.ChangeAddress?.ScriptPubKey, signTimeout.Token);
                        result = new SendToAddressResult() { PSBT = psbtResponse.PSBT.ToBase64() };
                    }
                }
                catch (OperationCanceledException)
                { result = new LedgerTestResult() { Success = false, Error = "Timeout" }; }
                catch (Exception ex)
                { result = new LedgerTestResult() { Success = false, Error = ex.Message }; }
                finally { hw.Dispose(); }
                try
                {
                    if (result != null)
                    {
                        UTF8Encoding UTF8NOBOM = new UTF8Encoding(false);
                        var bytes = UTF8NOBOM.GetBytes(JsonConvert.SerializeObject(result, _serializerSettings));
                        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, new CancellationTokenSource(2000).Token);
                    }
                }
                catch { }
                finally
                {
                    await webSocket.CloseSocket();
                }
            }
            return new EmptyResult();
        }

        [Route("{walletId}/settings")]
        public async Task<IActionResult> WalletSettings(
             [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId)
        {
            var derivationSchemeSettings = GetDerivationSchemeSettings(walletId);
            if (derivationSchemeSettings == null)
                return NotFound();
            var store = (await Repository.FindStore(walletId.StoreId, GetUserId()));
            var vm = new WalletSettingsViewModel()
            {
                Label = derivationSchemeSettings.Label,
                DerivationScheme = derivationSchemeSettings.AccountDerivation.ToString(),
                DerivationSchemeInput = derivationSchemeSettings.AccountOriginal,
                SelectedSigningKey = derivationSchemeSettings.SigningKey.ToString()
            };
            vm.AccountKeys = derivationSchemeSettings.AccountKeySettings
                            .Select(e => new WalletSettingsAccountKeyViewModel()
                            {
                                AccountKey = e.AccountKey.ToString(),
                                MasterFingerprint = e.RootFingerprint is HDFingerprint fp ? fp.ToString() : null,
                                AccountKeyPath = e.AccountKeyPath == null ? "" : $"m/{e.AccountKeyPath}"
                            }).ToList();
            return View(vm);
        }

        [Route("{walletId}/settings")]
        [HttpPost]
        public async Task<IActionResult> WalletSettings(
             [ModelBinder(typeof(WalletIdModelBinder))]
            WalletId walletId, WalletSettingsViewModel vm)
        {
            if (!ModelState.IsValid)
                return View(vm);
            var derivationScheme = GetDerivationSchemeSettings(walletId);
            if (derivationScheme == null)
                return NotFound();
            derivationScheme.Label = vm.Label;
            derivationScheme.SigningKey = string.IsNullOrEmpty(vm.SelectedSigningKey) ? null : new BitcoinExtPubKey(vm.SelectedSigningKey, derivationScheme.Network.NBitcoinNetwork);
            for (int i = 0; i < derivationScheme.AccountKeySettings.Length; i++)
            {
                derivationScheme.AccountKeySettings[i].AccountKeyPath = string.IsNullOrWhiteSpace(vm.AccountKeys[i].AccountKeyPath) ? null
                                                          : new KeyPath(vm.AccountKeys[i].AccountKeyPath);
                derivationScheme.AccountKeySettings[i].RootFingerprint = string.IsNullOrWhiteSpace(vm.AccountKeys[i].MasterFingerprint) ? (HDFingerprint?)null
                                                          : new HDFingerprint(Encoders.Hex.DecodeData(vm.AccountKeys[i].MasterFingerprint));
            }
            var store = (await Repository.FindStore(walletId.StoreId, GetUserId()));
            store.SetSupportedPaymentMethod(derivationScheme);
            await Repository.UpdateStore(store);
            StatusMessage = "Wallet settings updated";
            return RedirectToAction(nameof(WalletSettings));
        }
    }


    public class GetInfoResult
    {
    }

    public class SendToAddressResult
    {
        [JsonProperty("psbt")]
        public string PSBT { get; set; }
    }
}
