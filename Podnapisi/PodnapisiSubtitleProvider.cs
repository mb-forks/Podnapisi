﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Common;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Xml;
using System.Xml;
using System.IO.Compression;

namespace Podnapisi
{
    public class PodnapisiSubtitleProvider : ISubtitleProvider, IHasOrder
    {
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly IApplicationHost _appHost;
        private readonly IXmlReaderSettingsFactory _xmlSettings;

        public PodnapisiSubtitleProvider(ILogger logger, IHttpClient httpClient, IFileSystem fileSystem,
            IApplicationHost appHost, IXmlReaderSettingsFactory xmlSettings)
        {
            _logger = logger;
            _httpClient = httpClient;
            _fileSystem = fileSystem;
            _appHost = appHost;
            _xmlSettings = xmlSettings;
        }

        private HttpRequestOptions BaseRequestOptions => new HttpRequestOptions
        {
            UserAgent = $"Emby/{_appHost.ApplicationVersion}"
        };

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            var pid = id.Split(',')[0];
            var title = id.Split(',')[1];
            var lang = id.Split(',')[2];
            var opts = BaseRequestOptions;
            opts.Url = $"https://www.podnapisi.net/{lang}/subtitles/{title}/{pid}/download";
            _logger.Debug("Requesting {0}", opts.Url);

            using (var response = await _httpClient.GetResponse(opts).ConfigureAwait(false))
            {
                var ms = new MemoryStream();
                var archive = new ZipArchive(response.Content);

                await archive.Entries.FirstOrDefault().Open().CopyToAsync(ms).ConfigureAwait(false);
                ms.Position = 0;

                var fileExt = archive.Entries.FirstOrDefault().FullName.Split('.').LastOrDefault();

                if (string.IsNullOrWhiteSpace(fileExt))
                {
                    fileExt = "srt";
                }

                return new SubtitleResponse
                {
                    Format = fileExt,
                    Language = lang,
                    Stream = ms
                };

            }
        }

        public string Name => "Podnapisi";

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request,
            CancellationToken cancellationToken)
        {
            var url = new StringBuilder("https://www.podnapisi.net/subtitles/search/old?sXML=1");
            url.Append($"&sL={request.TwoLetterISOLanguageName}");
            if (request.SeriesName == null)
            {
                url.Append($"&sK={request.Name}");
            }
            else
            {
                url.Append($"&sK={request.SeriesName}");
            }
            if (request.ParentIndexNumber.HasValue)
            {
                url.Append($"&sTS={request.ParentIndexNumber}");
            }
            if (request.IndexNumber.HasValue)
            {
                url.Append($"&sTE={request.IndexNumber}");
            }
            if (request.ProductionYear.HasValue)
            {
                url.Append($"&sY={request.ProductionYear}");
            }

            var opts = BaseRequestOptions;
            opts.Url = url.ToString();
            _logger.Debug("Requesting {0}", opts.Url);

            try
            {
                using (var response = await _httpClient.GetResponse(opts).ConfigureAwait(false))
                {
                    using (var reader = new StreamReader(response.Content))
                    {
                        var settings = _xmlSettings.Create(false);
                        settings.CheckCharacters = false;
                        settings.IgnoreComments = true;
                        settings.DtdProcessing = DtdProcessing.Parse;
                        settings.MaxCharactersFromEntities = 1024;

                        using (var result = XmlReader.Create(reader, settings))
                        {
                            return ParseSearch(result);
                        }
                    }
                }
            }
            catch (HttpException ex)
            {
                if (ex.StatusCode.HasValue && ex.StatusCode.Value == HttpStatusCode.NotFound)
                {
                    return new List<RemoteSubtitleInfo>();
                }

                throw;
            }
        }

        public IEnumerable<VideoContentType> SupportedMediaTypes =>
            new List<VideoContentType> { VideoContentType.Episode, VideoContentType.Movie };

        public int Order => 2;

        private List<RemoteSubtitleInfo> ParseSearch(XmlReader reader)
        {
            var list = new List<RemoteSubtitleInfo>();
            reader.MoveToContent();
            reader.Read();

            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "subtitle":
                            {
                                if (reader.IsEmptyElement)
                                {
                                    reader.Read();
                                    continue;
                                }
                                using (var subReader = reader.ReadSubtree())
                                {
                                    list.Add(ParseSubtitleList(subReader));
                                }
                                break;
                            }
                        default:
                            {
                                reader.Skip();
                                break;
                            }
                    }
                }
                else
                {
                    reader.Read();
                }
            }
            return list;
        }

        private RemoteSubtitleInfo ParseSubtitleList(XmlReader reader)
        {
            var SubtitleInfo = new RemoteSubtitleInfo
            {
                ProviderName = Name
            };
            reader.MoveToContent();
            reader.Read();

            var id = new StringBuilder();

            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "pid":
                            {
                                SubtitleInfo.Id = reader.ReadElementContentAsString();
                                break;
                            }
                        case "title":
                            {
                                SubtitleInfo.Name = reader.ReadElementContentAsString();
                                break;
                            }
                        case "url":
                            {
                                SubtitleInfo.Id = $"{SubtitleInfo.Id},{reader.ReadElementContentAsString().Split('/')[5]}";
                                break;
                            }
                        case "language":
                            {
                                SubtitleInfo.Id = $"{SubtitleInfo.Id},{reader.ReadElementContentAsString()}";
                                break;
                            }
                        case "rating":
                            {
                                SubtitleInfo.CommunityRating = reader.ReadElementContentAsFloat();
                                break;
                            }
                        case "downloads":
                            {
                                SubtitleInfo.DownloadCount = reader.ReadElementContentAsInt();
                                break;
                            }
                        default:
                            {
                                reader.Skip();
                                break;
                            }
                    }
                }
                else
                {
                    reader.Read();
                }
            }
            return SubtitleInfo;
        }

    }

}