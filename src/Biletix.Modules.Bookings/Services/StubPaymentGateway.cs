using Biletix.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Biletix.Modules.Bookings.Services;

public class StubPaymentGateway(ILogger<StubPaymentGateway> log) : IPaymentGateway
{
    public Task<bool> ChargeAsync(PaymentDetails details, decimal amount, CancellationToken ct = default)
    {
        log.LogInformation("Stub charge {Amount} on {Card}", amount, details.CardNumberMasked);
        return Task.FromResult(true);
    }

    public Task RefundAsync(PaymentDetails details, decimal amount, CancellationToken ct = default)
    {
        log.LogInformation("Stub refund {Amount} on {Card}", amount, details.CardNumberMasked);
        return Task.CompletedTask;
    }
}
