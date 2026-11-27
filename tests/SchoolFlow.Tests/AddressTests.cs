using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SchoolFlow.Web.Services;
using Xunit;

namespace SchoolFlow.Tests;

public class AddressTests
{
    [Fact]
    public void Photon_answers_become_one_line_addresses()
    {
        const string json = """
        {"type":"FeatureCollection","features":[
          {"properties":{"housenumber":"540","street":"Main Street","city":"Hackensack","state":"New Jersey","postcode":"07601","countrycode":"US"}},
          {"properties":{"name":"Liberty Science Center","street":"Liberty Science Center Drive","city":"Jersey City","state":"New Jersey","postcode":"07305","countrycode":"US"}},
          {"properties":{"name":"Hackensack","county":"Bergen County","state":"New Jersey","countrycode":"US"}},
          {"properties":{"housenumber":"540","street":"Main Street","city":"Toronto","countrycode":"CA"}},
          {"properties":{"housenumber":"540","street":"Main Street","city":"Hackensack","state":"New Jersey","postcode":"07601","countrycode":"US"}}
        ]}
        """;
        var list = PhotonAddressSource.Parse(json).Select(a => a.Label).ToList();
        Assert.Equal(new[]
        {
            "540 Main Street, Hackensack, NJ 07601",
            "Liberty Science Center Drive, Jersey City, NJ 07305",   // the name is already in the street
            "Hackensack, Bergen County, NJ",
        }, list);   // other countries and repeats are left out
    }

    [Fact]
    public void Photon_answer_without_features_is_an_empty_list()
    {
        Assert.Empty(PhotonAddressSource.Parse("{}"));
    }

    private sealed class FailingSource : IAddressSource
    {
        public int Calls;
        public bool IsConfigured => true;
        public Task<IReadOnlyList<AddressSuggestion>> SearchAsync(string query, CancellationToken ct)
        {
            Calls++;
            throw new HttpRequestException("no network");
        }
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_reports_not_connected()
    {
        var lookup = new AddressLookup(new FailingSource(), new MemoryCache(new MemoryCacheOptions()), NullLogger<AddressLookup>.Instance);
        Assert.Null(await lookup.SearchAsync("540 Main", CancellationToken.None));
    }

    [Fact]
    public async Task Repeated_searches_are_remembered()
    {
        var lookup = new AddressLookup(new SampleAddressSource(), new MemoryCache(new MemoryCacheOptions()), NullLogger<AddressLookup>.Instance);
        var first = await lookup.SearchAsync("540", CancellationToken.None);
        var again = await lookup.SearchAsync(" 540 ", CancellationToken.None);
        Assert.Equal(2, first!.Count);
        Assert.Same(first, again);
    }
}
