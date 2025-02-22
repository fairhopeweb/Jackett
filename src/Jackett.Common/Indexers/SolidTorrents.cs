using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Jackett.Common.Indexers
{
    [ExcludeFromCodeCoverage]
    public class SolidTorrents : BaseWebIndexer
    {
        private string SearchUrl => SiteLink + "api/v1/search";

        private readonly Dictionary<string, string> APIHeaders = new Dictionary<string, string>
        {
            {"Accept", "application/json, text/plain, */*"}
        };

        private readonly int MAX_RESULTS_PER_PAGE = 20;
        private readonly int MAX_SEARCH_PAGE_LIMIT = 3; // 20 items per page, 60

        private ConfigurationData ConfigData
        {
            get => configData;
            set => configData = value;
        }

        public SolidTorrents(IIndexerConfigurationService configService, WebClient wc, Logger l, IProtectionService ps,
            ICacheService cs)
            : base(id: "solidtorrents",
                   name: "Solid Torrents",
                   description: "Solid Torrents is a Public torrent meta-search engine",
                   link: "https://solidtorrents.net/",
                   caps: new TorznabCapabilities
                   {
                       TvSearchParams = new List<TvSearchParam>
                       {
                           TvSearchParam.Q, TvSearchParam.Season, TvSearchParam.Ep
                       },
                       MovieSearchParams = new List<MovieSearchParam>
                       {
                           MovieSearchParam.Q
                       },
                       MusicSearchParams = new List<MusicSearchParam>
                       {
                           MusicSearchParam.Q
                       },
                       BookSearchParams = new List<BookSearchParam>
                       {
                           BookSearchParam.Q
                       }
                   },
                   configService: configService,
                   client: wc,
                   logger: l,
                   p: ps,
                   cacheService: cs,
                   configData: new ConfigurationData())
        {
            Encoding = Encoding.UTF8;
            Language = "en-US";
            Type = "public";

            // when updating categories also update ParseCategory routine.
            AddCategoryMapping("Audio", TorznabCatType.Audio, "Audio");
            AddCategoryMapping("Video", TorznabCatType.Movies, "Video");
            AddCategoryMapping("Video", TorznabCatType.TV, "Video");
            AddCategoryMapping("Image", TorznabCatType.OtherMisc, "Image");
            AddCategoryMapping("Document", TorznabCatType.BooksComics, "Document");
            AddCategoryMapping("eBook", TorznabCatType.BooksEBook, "eBook");
            AddCategoryMapping("Program", TorznabCatType.PC0day, "Program");
            AddCategoryMapping("Android", TorznabCatType.PCMobileAndroid, "Android");
            AddCategoryMapping("Archive", TorznabCatType.Other, "Archive");
            AddCategoryMapping("Diskimage", TorznabCatType.PCISO, "Diskimage");
            AddCategoryMapping("Sourcecode", TorznabCatType.MoviesOther, "Sourcecode");
            AddCategoryMapping("Database", TorznabCatType.MoviesDVD, "Database");
            AddCategoryMapping("Unknown", TorznabCatType.Other, "Unknown");
        }

        protected virtual List<int> ParseCategory(string query)
        {
            var cats = new List<int>();
            // the json category can be either a category name or an integer !?!
            switch (query)
            {
                case "Audio":
                case "7": // music
                case "8": // audiobook
                    cats.Add(TorznabCatType.Audio.ID);
                    break;
                case "Video":
                case "2": // movies
                case "3": // tv
                case "4": // anime , hentai
                case "10": // 3x
                    cats.Add(TorznabCatType.Movies.ID);
                    cats.Add(TorznabCatType.TV.ID);
                    break;
                case "Image":
                    cats.Add(TorznabCatType.OtherMisc.ID);
                    break;
                case "Document":
                    cats.Add(TorznabCatType.BooksComics.ID);
                    break;
                case "eBook":
                case "9": // books
                    cats.Add(TorznabCatType.BooksEBook.ID);
                    break;
                case "Program":
                case "5": // software
                case "6": // games
                    cats.Add(TorznabCatType.PC0day.ID);
                    break;
                case "Android":
                    cats.Add(TorznabCatType.PCMobileAndroid.ID);
                    break;
                case "Archive":
                    cats.Add(TorznabCatType.Other.ID);
                    break;
                case "Diskimage":
                    cats.Add(TorznabCatType.PCISO.ID);
                    break;
                case "Sourcecode":
                    cats.Add(TorznabCatType.MoviesOther.ID);
                    break;
                case "Database":
                    cats.Add(TorznabCatType.MoviesDVD.ID);
                    break;
                case "Unknown":
                case "1": // other
                    cats.Add(TorznabCatType.Other.ID);
                    break;
                default:
                    // since there is no category table resource on the web site
                    // the numeric categories are a process of discovery.
                    throw new Exception($"Error parsing category={query}!");
            }
            return cats;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            base.LoadValuesFromJson(configJson);
            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Any(), () =>
                                    throw new Exception("Could not find release from this URL."));

            return IndexerConfigurationStatus.Completed;
        }

        private JArray CheckResponse(WebResult result)
        {
            var results = result.ContentString;
            try
            {
                var json = JsonConvert.DeserializeObject<dynamic>(results);
                if (!(json is JObject) || !(json["results"] is JArray) || json["results"] == null)
                    throw new Exception("Server error");
                return (JArray)json["results"];
            }
            catch (Exception e)
            {
                OnParseError(results, e);
            }
            return null;
        }

        private async Task<JArray> SendSearchRequest(string searchString, string category, int page)
        {
            var queryCollection = new NameValueCollection
            {
                {"q", searchString},
                {"category", category},
                {"skip", (page * MAX_RESULTS_PER_PAGE).ToString()},
                {"sort", "date"},
                {"fuv", "no"}
            };
            var fullSearchUrl = SearchUrl + "?" + queryCollection.GetQueryString();
            var result = await RequestWithCookiesAsync(fullSearchUrl, headers: APIHeaders);
            return CheckResponse(result);
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            var searchString = query.GetQueryString();
            var page = 0;

            var cats = MapTorznabCapsToTrackers(query);
            var category = cats.Count > 0 ? string.Join(",", cats) : "all";

            var isLatestSearch = string.IsNullOrWhiteSpace(searchString);
            var isLastPage = false;

            do
            {
                var result = await SendSearchRequest(searchString, category, page);
                try
                {
                    foreach (var torrent in result)
                        releases.Add(MakeRelease(torrent));
                }
                catch (Exception ex)
                {
                    OnParseError(result.ToString(), ex);
                }

                isLastPage = result.Count < MAX_RESULTS_PER_PAGE;
                page++; // update page number

            } while (!isLatestSearch && !isLastPage && page < MAX_SEARCH_PAGE_LIMIT);

            return releases;
        }

        //TODO inline single use function
        private ReleaseInfo MakeRelease(JToken torrent)
        {
            // https://solidtorrents.net/view/5e10885d651df640a70ee826
            var details = new Uri(SiteLink + "view/" + (string)torrent["_id"]);
            var swarm = torrent["swarm"];
            var seeders = (int)swarm["seeders"];
            var publishDate = torrent["imported"] != null ? DateTime.Parse((string)torrent["imported"]) : DateTime.Now;
            var magnetUri = new Uri((string)torrent["magnet"]);
            var category = ParseCategory((string)torrent["category"]);

            return new ReleaseInfo
            {
                Title = (string)torrent["title"],
                Details = details,
                Guid = details,
                PublishDate = publishDate,
                Category = category,
                Size = (long)torrent["size"],
                Seeders = seeders,
                Peers = seeders + (int)swarm["leechers"],
                Grabs = (long)swarm["downloads"],
                InfoHash = (string)torrent["infohash"],
                MagnetUri = magnetUri,
                DownloadVolumeFactor = 0,
                UploadVolumeFactor = 1
            };
        }
    }
}
