﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeslaMateAgile.Data;
using TeslaMateAgile.Data.Options;
using TeslaMateAgile.Services.Interfaces;

namespace TeslaMateAgile.Services;

public class HomeAssistantService : IPriceDataService
{
    private readonly HomeAssistantOptions _options;
    private readonly HttpClient _client;

    public HomeAssistantService(HttpClient client, IOptions<HomeAssistantOptions> options, IOptions<TeslaMateOptions> teslaMateOptions, ILogger<HomeAssistantService> logger)
    {
        _options = options.Value;
        _client = client;
        if (!teslaMateOptions.Value.LookbackDays.HasValue)
        {
            logger.LogWarning($"Configuring {nameof(TeslaMateOptions.LookbackDays)} is recommended when using Home Assistant as there is usually a rolling data range and older charges may not be able to be calculated");
        }
    }

    public async Task<IEnumerable<Price>> GetPriceData(DateTimeOffset from, DateTimeOffset to)
    {
        var url = $"api/history/period/{from.UtcDateTime:o}?end={to.UtcDateTime:o}&filter_entity_id={_options.EntityId}";
        var resp = await _client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var homeAssistantResponse = await JsonSerializer.DeserializeAsync<List<List<HomeAssistantResponse>>>(await resp.Content.ReadAsStreamAsync()) ?? throw new Exception($"Deserialization of Home Assistant API response failed");
        var history = homeAssistantResponse.SingleOrDefault();
        if (history == null || !history.Any())
        {
            throw new Exception($"No data from Home Assistant for entity id {_options.EntityId}, ensure it and {nameof(TeslaMateOptions.LookbackDays)} are set correctly");
        }
        if (history.First().LastUpdated != from)
        {
            throw new Exception($"Home Assistant has incomplete data for date range {from.UtcDateTime:o}?end={to.UtcDateTime:o}, ensure entity and {nameof(TeslaMateOptions.LookbackDays)} are set correctly");
        }
        var prices = new List<Price>();
        string previous_val = "";
        for (var i = 0; i < history.Count; i++)
        {
            var state = history[i];

            // Work around ha truncating / otherwise missing price data.
            if (state.State == "unknown" || state.State == "unavailable")
            {
                Console.WriteLine("Unknown price data!");
                if (previous_val != "") {
                    Console.WriteLine($"Old data found, will use it! {previous_val}");
                } else {
                    Console.WriteLine("No old data found, this will probably fail! Trying to get next data!");
                    if (history.Count-1 >= i+1 ){
                        var next_value = history[i+1].State;
                        if (next_value != "unknown" && next_value != "unavailable") 
                        {
                            Console.WriteLine($"Next data found, using: {next_value}");
                            previous_val = next_value;
                        } else {
                            Console.WriteLine("Next data also unavaialble, maybe write a loop. Failing");
                        }
                    } else {
                        Console.WriteLine("This is last value in list, no next value to read. Failing");
                    }
                }
            } else {
                previous_val = state.State;
            }
            
            var price = decimal.Parse(previous_val);
            var validFrom = state.LastUpdated;
            var validTo = (i < history.Count - 1) ? history[i + 1].LastUpdated : to;

            prices.Add(new Price
            {
                Value = price,
                ValidFrom = validFrom,
                ValidTo = validTo
            });
        }
        return prices;
    }

    public class HomeAssistantResponse
    {
        [JsonPropertyName("entity_id")]
        public string EntityId { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonPropertyName("last_changed")]
        public DateTimeOffset LastChanged { get; set; }

        [JsonPropertyName("last_updated")]
        public DateTimeOffset LastUpdated { get; set; }
    }
}
