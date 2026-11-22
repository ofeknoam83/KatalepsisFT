using System.Text.Json;
using Binance.Net.Objects.Models.Spot;
using Bybit.Net.Enums;
using DepositStatus = Binance.Net.Enums.DepositStatus;
using WithdrawalStatus = Binance.Net.Enums.WithdrawalStatus;
using Bybit.Net.Objects.Models.V5;
using FluentAssertions;
using KatalepsisFT.Application.Connections;
using KatalepsisFT.Venues.Binance;
using KatalepsisFT.Venues.Bybit;
using KatalepsisFT.Venues.Deribit;
using KatalepsisFT.Venues.Okx;
using OKX.Net.Objects.Account;

namespace KatalepsisFT.Venues.Tests;

/// <summary>
/// How each venue's deposits, withdrawals and transfers are signed and filtered. As with positions, the inputs are
/// built by hand from the venues' documentation and are not recordings of a real account.
/// </summary>
public class TransferTests
{
    private static readonly DateTimeOffset Since = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTime After = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
    private static readonly long AfterMs = new DateTimeOffset(After).ToUnixTimeMilliseconds();

    private static JsonElement Entries(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Deribit_Deposits_CountOnlyWhenCompleted_AndAfterTheStart()
    {
        JsonElement entries = Entries($$"""
            [{"amount":0.5,"state":"completed","transaction_id":"tx-a","received_timestamp":{{AfterMs}}},
             {"amount":9.0,"state":"pending","transaction_id":"tx-b","received_timestamp":{{AfterMs}}},
             {"amount":9.0,"state":"completed","transaction_id":"tx-c","received_timestamp":1000}]
            """);

        DeribitMapper.Transfers("get_deposits", "BTC", entries, Since).Should().Equal(new VenueTransfer("dep:tx-a", After, "BTC", 0.5m));
    }

    [Fact]
    public void Deribit_Withdrawal_LeavesWithItsFee()
    {
        JsonElement entries = Entries($$"""[{"amount":2.0,"fee":0.0005,"state":"completed","id":41,"confirmed_timestamp":{{AfterMs}}}]""");

        DeribitMapper.Transfers("get_withdrawals", "ETH", entries, Since).Should().Equal(new VenueTransfer("wd:41", After, "ETH", -2.0005m));
    }

    [Fact]
    public void Deribit_TransfersBetweenAccounts_AreSignedByDirection()
    {
        JsonElement entries = Entries($$"""
            [{"amount":100.0,"direction":"payment","state":"confirmed","id":7,"updated_timestamp":{{AfterMs}}},
             {"amount":40.0,"direction":"income","state":"confirmed","id":8,"updated_timestamp":{{AfterMs}}},
             {"amount":5.0,"direction":"income","state":"prepared","id":9,"updated_timestamp":{{AfterMs}}}]
            """);

        DeribitMapper.Transfers("get_transfers", "USDC", entries, Since).Select(t => (t.Id, t.Quantity)).Should().Equal(("tr:7", -100m), ("tr:8", 40m));
    }

    [Fact]
    public void Okx_TransferBills_KeepTheirSign()
    {
        OKXAccountBill[] bills = [new() { BillId = 11, Asset = "usdt", BalanceChange = 2_500m, Time = After }, new() { BillId = 12, Asset = "BTC", BalanceChange = -0.2m, Time = After }, new() { BillId = 13, Asset = "ETH", BalanceChange = 0m, Time = After }];

        OkxMapper.Transfers(bills).Should().Equal(new VenueTransfer("11", After, "USDT", 2_500m), new VenueTransfer("12", After, "BTC", -0.2m));
    }

    [Fact]
    public void Binance_CountsFinishedDepositsAndWithdrawals_WithdrawalsWithTheirFee()
    {
        BinanceDeposit[] deposits = [new() { Id = "d1", Asset = "USDT", Quantity = 1_000m, Status = DepositStatus.Success, InsertTime = After }, new() { Id = "d2", Asset = "USDT", Quantity = 7m, Status = DepositStatus.Pending, InsertTime = After }];
        BinanceWithdrawal[] withdrawals = [new() { Id = "w1", Asset = "BTC", Quantity = 0.3m, TransactionFee = 0.0002m, Status = WithdrawalStatus.Completed, ApplyTime = After }, new() { Id = "w2", Asset = "BTC", Quantity = 5m, Status = WithdrawalStatus.Rejected, ApplyTime = After }];

        BinanceMapper.Transfers(deposits, withdrawals).Should().Equal(new VenueTransfer("dep:d1", After, "USDT", 1_000m), new VenueTransfer("wd:w1", After, "BTC", -0.3002m));
    }

    [Fact]
    public void Bybit_TransferLog_IsSignedByItsType_HoweverTheAmountIsWritten()
    {
        BybitTransactionLog[] entries =
        [
            new() { Id = "a", Asset = "USDT", Type = TransactionLogType.TransferIn, Change = 800m, TransactionTime = After },
            new() { Id = "b", Asset = "USDT", Type = TransactionLogType.TransferOut, Change = -300m, TransactionTime = After },
            new() { Id = "c", Asset = "USDT", Type = TransactionLogType.TransferOut, Change = 50m, TransactionTime = After },
            new() { Id = "d", Asset = "USDT", Type = TransactionLogType.Trade, Change = 999m, TransactionTime = After },
        ];

        BybitMapper.Transfers(entries).Select(t => (t.Id, t.Quantity)).Should().Equal(("a", 800m), ("b", -300m), ("c", -50m));
    }
}
