using Biletix.Shared.Contracts;

namespace Biletix.Modules.Bookings.Services;

public interface IPaymentGateway
{
    Task<bool> ChargeAsync(PaymentDetails details, decimal amount, CancellationToken ct = default);
    Task RefundAsync(PaymentDetails details, decimal amount, CancellationToken ct = default);
}
