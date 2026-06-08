using Biletix.Shared.Contracts;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Biletix.Modules.Search;

public static class SearchEndpoints
{
    public static IServiceCollection AddSearchModule(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<ElasticsearchClient>(_ =>
        {
            var url = cfg["Elasticsearch:Url"] ?? "http://elasticsearch:9200";
            var settings = new ElasticsearchClientSettings(new Uri(url))
                .DefaultFieldNameInferrer(name =>
                    string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString())));
            return new ElasticsearchClient(settings);
        });
       
        services.AddScoped<IEventCatalogReadModel, EsEventCatalogReadModel>();
        return services;
    }

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/search", async (
            ElasticsearchClient es,
            string? keyword = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            int pageSize = 20,
            int pageNumber = 1) =>
        {
            var must = new List<Query>();
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                must.Add(new MultiMatchQuery
                {
                    Query = keyword,
                    Fields = Fields.FromStrings(new[] { "title^2", "performer_name^3", "venue_name", "city" }),
                    Fuzziness = new Fuzziness("AUTO"),
                });
            }
            if (startDate.HasValue || endDate.HasValue)
            {
                must.Add(new DateRangeQuery("starts_at")
                {
                    Gte = startDate?.ToString("o"),
                    Lte = endDate?.ToString("o"),
                });
            }
            if (must.Count == 0) must.Add(new MatchAllQuery());

            var resp = await es.SearchAsync<EsEventDoc>(s => s
                .Indices("events")
                .From((pageNumber - 1) * pageSize)
                .Size(pageSize)
                .Query(q => q.Bool(bq => bq.Must(must.ToArray())))
                .Sort(so => so.Field(f => f.StartsAt, sf => sf.Order(Elastic.Clients.Elasticsearch.SortOrder.Asc))));

            if (!resp.IsValidResponse)
                return Results.Problem(resp.DebugInformation, statusCode: 500);

            var hits = resp.Documents.Select(d => new SearchHit(
                d.Id, d.Title ?? "", d.PerformerName ?? "", d.VenueName ?? "", d.City ?? "",
                d.StartsAt, d.TotalTickets)).ToList();

            return Results.Ok(new { total = resp.Total, items = hits });
        });

        return app;
    }
}

public class EsEventDoc
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public Guid PerformerId { get; set; }
    public string? PerformerName { get; set; }
    public Guid VenueId { get; set; }
    public string? VenueName { get; set; }
    public string? City { get; set; }
    public DateTime StartsAt { get; set; }
    public int TotalTickets { get; set; }
}
