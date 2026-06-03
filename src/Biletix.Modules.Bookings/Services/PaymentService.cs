using Biletix.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Biletix.Modules.Bookings.Services;

public class PaymentService
{
    private readonly ILogger<PaymentService> _log;
    public PaymentService(ILogger<PaymentService> log) => _log = log;

    public Task<bool> ChargeAsync(PaymentDetails details, decimal amount)
    {
        _log.LogInformation("Stub charge {Amount} on {Card}", amount, details.CardNumberMasked);
        return Task.FromResult(true);
    }
}
