using Biletix.Shared.Contracts;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace Biletix.Modules.Search;

internal sealed class EsEventCatalogReadModel(ElasticsearchClient es) : IEventCatalogReadModel
{
    public async Task<IReadOnlyList<EventDto>> ListAsync(int pageNumber, int pageSize, bool upcoming, CancellationToken ct = default)
    {
        var must = new List<Query>();
        if (upcoming)
            must.Add(new DateRangeQuery("starts_at") { Gte = DateTime.UtcNow.ToString("o") });
        if (must.Count == 0)
            must.Add(new MatchAllQuery());

        var resp = await es.SearchAsync<EsEventDoc>(s => s
            .Indices("events")
            .From((pageNumber - 1) * pageSize)
            .Size(pageSize)
            .Query(q => q.Bool(b => b.Must(must.ToArray())))
            .Sort(so => so.Field(f => f.StartsAt, sf => sf.Order(Elastic.Clients.Elasticsearch.SortOrder.Asc))), ct);

        if (!resp.IsValidResponse)
            return Array.Empty<EventDto>();

        return resp.Documents.Select(d => new EventDto(
            d.Id, d.Title ?? "", d.StartsAt, d.TotalTickets,
            new VenueDto(d.VenueId, d.VenueName ?? "", d.City ?? ""),
            new PerformerDto(d.PerformerId, d.PerformerName ?? ""))).ToList();
    }
}
