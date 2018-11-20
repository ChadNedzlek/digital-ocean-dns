using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace dns_update
{
    static class Program
    {
        static async Task<int> Main(string[] args)
        {
            Configuration config = GetConfiguration();
            (string ip4, string ip6) = await GetCurrentIpAddressAsync();


            if (!config.Domains.Any())
            {
                Console.Error.WriteLine("No domains specified on configuration");
                return 1;
            }

            bool success = true;
            foreach (var (domain, domainConfig) in config.Domains)
            {
                if (!await ProcessDomainAsync(domain, domainConfig, ip4, ip6))
                {
                    success = false;
                }
            }

            return success ? 0 : 2;
        }

        private static async Task<(string v4, string v6)> GetCurrentIpAddressAsync()
        {
            using (HttpClient ipReflectionClient = new HttpClient())
            {
                string ip4 = await ipReflectionClient.GetStringAsync("http://ip4.seeip.org");
                string ip6;
                try
                {
                    ip6 = await ipReflectionClient.GetStringAsync("http://ip6.seeip.org");
                }
                catch (HttpRequestException e) when (e.InnerException is SocketException sock &&
                    sock.SocketErrorCode == SocketError.NoData)
                {
                    // We failed to connect to the ip 6 address, we probably don't have an ip6 addres;
                    ip6 = null;
                }

                return (ip4.Trim(), ip6?.Trim());
            }
        }

        private static async Task<bool> ProcessDomainAsync(
            string domain,
            DomainConfiguration domainConfig,
            string ip4,
            string ip6)
        {
            if (String.IsNullOrEmpty(domainConfig.ApiKey))
            {
                Console.Error.WriteLine($"No apiKey specified, skipping {domain}");
                return false;
            }

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", domainConfig.ApiKey);
            Console.WriteLine($"Fetching current records for {domain}...");
            RecordList records = await GetJsonAsync<RecordList>(client, $"{domain}/records");
            foreach (RecordConfiguration record in domainConfig.Records)
            {
                DomainRecord existing = records.Records.FirstOrDefault(r => r.Type == record.Type && r.Name == record.Name);
                string data = record.CurrentIp4 ? ip4 :
                    record.CurrentIp6 ? ip6 : 
                    record.Data;


                if (existing == null)
                {
                    if (String.IsNullOrEmpty(data))
                    {
                        // It doesn't exist, and we cannot route it, so good
                    }
                    else
                    {
                        DomainRecord newRecord = new DomainRecord
                        {
                            Type = record.Type,
                            Name = record.Name,
                            Data = data
                        };
                        Console.WriteLine($"Creating new {record.Type} record for {record.Name}.{domain}...");
                        DomainRecord response = await PostJsonAsync<DomainRecord>(
                            client,
                            $"{domain}/records",
                            newRecord);
                        Console.WriteLine($"... new record id is {response.Id}");
                    }
                }
                else
                {
                    if (String.IsNullOrEmpty(data))
                    {
                        // It already exists, but we don't want it, time to toast it
                        Console.WriteLine(
                            $"Removing unroutable record {existing.Id} {record.Type} for {record.Name}.{domain}");
                        var response = await client.DeleteAsync(new Uri(s_baseUri, $"{domain}/records/{existing.Id}"));
                        response.EnsureSuccessStatusCode();
                    }
                    else if (existing.Data == data)
                    {
                        // Already exists, already right, do nothing.
                    }
                    else
                    {
                        existing.Data = data;
                        existing.Id = null;
                        Console.WriteLine($"Updating record {existing.Id}, {record.Type} record for {record.Name}.{domain}...");
                        await PutJsonAsync<DomainRecord>(client, $"{domain}/records", existing);
                    }
                }
            }

            return true;
        }

        private static readonly JsonSerializer s_serializer = new JsonSerializer();
        private static readonly Uri s_baseUri = new Uri("https://api.digitalocean.com/v2/domains/");

        private static async Task<T> GetJsonAsync<T>(HttpClient client, string path)
        {
            using (Stream stream =
                await client.GetStreamAsync(new Uri(s_baseUri, path)))
            {
                return HandleJsonResponse<T>(stream);
            }
        }
        
        private static async Task<T> PostJsonAsync<T>(HttpClient client, string path, object input)
        {
            StringContent content;
            using (StringWriter writer = new StringWriter())
            {
                s_serializer.Serialize(writer, input);
                content = new StringContent(writer.ToString(), Encoding.UTF8, "application/json");
            }

            using (HttpResponseMessage response = await client.PostAsync(new Uri(s_baseUri, path), content))
            using (Stream stream = await response.Content.ReadAsStreamAsync())
            {
                return HandleJsonResponse<T>(stream);
            }
        }

        private static async Task<T> PutJsonAsync<T>(HttpClient client, string path, object input)
        {
            StringContent content;
            using (StringWriter writer = new StringWriter())
            {
                s_serializer.Serialize(writer, input);
                content = new StringContent(writer.ToString(), Encoding.UTF8, "application/json");
            }

            using (HttpResponseMessage response = await client.PutAsync(new Uri(s_baseUri, path), content))
            using (Stream stream = await response.Content.ReadAsStreamAsync())
            {
                return HandleJsonResponse<T>(stream);
            }
        }

        private static T HandleJsonResponse<T>(Stream stream)
        {
            using (MemoryStream mem = new MemoryStream())
            {
                stream.CopyTo(mem);
                var body = Encoding.UTF8.GetString(mem.ToArray());
                mem.Position = 0;
                using (var sr = new StreamReader(mem))
                using (var reader = new JsonTextReader(sr))
                {
                    return s_serializer.Deserialize<T>(reader);
                }
            }
        }

        private static Configuration GetConfiguration()
        {
            Configuration config;
            var executingLocation = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            using (var file = File.OpenText(Path.Combine(executingLocation, "config.json")))
            using (var reader = new JsonTextReader(file))
            {
                var serializer = new JsonSerializer();
                config = serializer.Deserialize<Configuration>(reader);
            }

            return config;
        }
    }

    public class Configuration
    {
        public IImmutableDictionary<string, DomainConfiguration> Domains { get; set; }
    }

    public class DomainConfiguration
    {
        public string ApiKey { get; set; }
        public IImmutableList<RecordConfiguration> Records { get; set; }
    }

    public class RecordConfiguration
    {
        public string Type { get; set; }
        public string Name { get; set; }

        public string Data { get; set; }
        public bool CurrentIp4 { get; set; }
        public bool CurrentIp6 { get; set; }
    }

    public class RecordList
    {
        [JsonProperty("domain_records")]
        public IImmutableList<DomainRecord> Records { get; set; }
    }

    public class DomainRecord
    {
        [JsonProperty("id")]
        public int? Id { get; set; }
        [JsonProperty("type")]
        public string Type { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonProperty("data")]
        public string Data { get; set; }
        [JsonProperty("priority")]
        public int? Priority { get; set; }
        [JsonProperty("port")]
        public int? Port { get; set; }
        [JsonProperty("ttl")]
        public int? Ttl { get; set; }
        [JsonProperty("weight")]
        public int? Weight { get; set; }
        [JsonProperty("flags")]
        public byte? Flags { get; set; }
        [JsonProperty("tag")]
        public string Tag { get; set; }
    }
}
