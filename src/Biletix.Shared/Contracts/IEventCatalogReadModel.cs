namespace Biletix.Shared.Contracts;

public interface IEventCatalogReadModel
{
    Task<IReadOnlyList<EventDto>> ListAsync(int pageNumber, int pageSize, bool upcoming, CancellationToken ct = default);
}
