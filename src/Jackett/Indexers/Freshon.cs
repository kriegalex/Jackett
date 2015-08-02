﻿using CsQuery;
using Jackett.Indexers;
using Jackett.Models;
using Jackett.Services;
using Jackett.Utils;
using Jackett.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.UI.WebControls;

namespace Jackett.Indexers
{
    public class Freshon : BaseIndexer, IIndexer
    {
        private string LoginUrl { get { return SiteLink + "login.php"; } }
        private string LoginPostUrl { get { return SiteLink + "login.php?action=makelogin"; } }
        private string SearchUrl { get { return SiteLink + "browse.php"; } }

        public Freshon(IIndexerManagerService i, Logger l, IWebClient c)
            : base(name: "FreshOnTV",
                description: "Our goal is to provide the latest stuff in the TV show domain",
                link: "https://freshon.tv/",
                caps: TorznabCapsUtil.CreateDefaultTorznabTVCaps(),
                manager: i,
                client: c,
                logger: l)
        {
        }

        public async Task<ConfigurationData> GetConfigurationForSetup()
        {
            return await Task.FromResult< ConfigurationData>(new ConfigurationDataBasicLogin());
        }

        public async Task ApplyConfiguration(JToken configJson)
        {
            var incomingConfig = new ConfigurationDataBasicLogin();
            incomingConfig.LoadValuesFromJson(configJson);
            var pairs = new Dictionary<string, string> {
				{ "username", incomingConfig.Username.Value },
				{ "password", incomingConfig.Password.Value }
			};

            // Get inital cookies
            cookieHeader = string.Empty;
            var response = await RequestLoginAndFollowRedirect(LoginPostUrl, pairs, cookieHeader, true, null, LoginUrl);

            await ConfigureIfOK(response.Cookies, response.Content != null && response.Content.Contains("/logout.php"), () =>
            {
                CQ dom = response.Content;
                var messageEl = dom[".error_text"];
                var errorMessage = messageEl.Text().Trim();
                throw new ExceptionWithConfigData(errorMessage, (ConfigurationData)incomingConfig);
            });
        }

        public async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();
            string episodeSearchUrl;

            if (string.IsNullOrEmpty(query.SanitizedSearchTerm))
                episodeSearchUrl = SearchUrl;
            else
            {
                var searchString = query.SanitizedSearchTerm + " " + query.GetEpisodeSearchString();
                episodeSearchUrl = string.Format("{0}?search={1}&cat=0", SearchUrl, HttpUtility.UrlEncode(searchString));
            }

            var results = await RequestStringWithCookiesAndRetry(episodeSearchUrl);
            try
            {
                CQ dom = results.Content;

                var rows = dom["#highlight > tbody > tr"];

                foreach (var row in rows.Skip(1))
                {
                    var release = new ReleaseInfo();

                    var qRow = row.Cq();
                    var qLink = qRow.Find("a.torrent_name_link").First();

                    release.MinimumRatio = 1;
                    release.MinimumSeedTime = 172800;
                    release.Title = qLink.Attr("title");
                    release.Description = release.Title;
                    release.Guid = new Uri(SiteLink + qLink.Attr("href"));
                    release.Comments = release.Guid;
                    release.Link = new Uri(SiteLink + qRow.Find("td.table_links > a").First().Attr("href"));

                    DateTime pubDate;
                    var dateString = qRow.Find("td.table_added").Text().Trim();
                    if (dateString.StartsWith("Today "))
                        pubDate = (DateTime.UtcNow + TimeSpan.Parse(dateString.Split(' ')[1])).ToLocalTime();
                    else if (dateString.StartsWith("Yesterday "))
                        pubDate = (DateTime.UtcNow + TimeSpan.Parse(dateString.Split(' ')[1]) - TimeSpan.FromDays(1)).ToLocalTime();
                    else
                        pubDate = DateTime.ParseExact(dateString, "d-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToLocalTime();
                    release.PublishDate = pubDate;

                    release.Seeders = ParseUtil.CoerceInt(qRow.Find("td.table_seeders").Text().Trim());
                    release.Peers = ParseUtil.CoerceInt(qRow.Find("td.table_leechers").Text().Trim()) + release.Seeders;

                    var sizeStr = qRow.Find("td.table_size")[0].Cq().Text();
                    release.Size = ReleaseInfo.GetBytes(sizeStr);

                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(results.Content, ex);
            }

            return releases;
        }
    }
}
