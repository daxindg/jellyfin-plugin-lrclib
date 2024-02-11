using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.LrcLib.Models;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LrcLib;

/// <summary>
/// Lyric provider for LrcLib.
/// </summary>
public class LrcLibProvider : ILyricProvider
{
    private const string BaseUrl = "https://lrclib.net";
    private const string SyncedSuffix = "synced";
    private const string PlainSuffix = "plain";
    private const string SyncedFormat = "lrc";
    private const string PlainFormat = "txt";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LrcLibProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LrcLibProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LrcLibProvider}"/>.</param>
    public LrcLibProvider(IHttpClientFactory httpClientFactory, ILogger<LrcLibProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => LrcLibPlugin.Instance!.Name;

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteLyricInfo>> SearchAsync(
        LyricSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string artist;
        if (request.ArtistNames is not null
            && request.ArtistNames.Count > 0)
        {
            artist = request.ArtistNames[0];
        }
        else
        {
            _logger.LogInformation("Artist name is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        if (string.IsNullOrEmpty(request.SongName))
        {
            _logger.LogInformation("Song name is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        if (string.IsNullOrEmpty(request.AlbumName))
        {
            _logger.LogInformation("Album name is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        if (request.Duration is null)
        {
            _logger.LogInformation("Duration is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        try
        {
            var queryStringBuilder = new StringBuilder()
                .Append("track_name=")
                .Append(HttpUtility.UrlEncode(request.SongName))
                .Append("&artist_name=")
                .Append(HttpUtility.UrlEncode(artist))
                .Append("&album_name=")
                .Append(HttpUtility.UrlEncode(request.AlbumName))
                .Append("&duration=")
                .Append(TimeSpan.FromTicks(request.Duration.Value).TotalSeconds.ToString(CultureInfo.InvariantCulture));

            var requestUri = new UriBuilder(BaseUrl)
            {
                Path = "/api/get",
                Query = queryStringBuilder.ToString()
            };

            var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .GetFromJsonAsync<LrcLibSearchResponse>(requestUri.Uri, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (response is null)
            {
                return Enumerable.Empty<RemoteLyricInfo>();
            }

            var results = new List<RemoteLyricInfo>();
            if (!string.IsNullOrEmpty(response.PlainLyrics))
            {
                results.Add(new RemoteLyricInfo
                {
                    Id = $"{response.Id}_{PlainSuffix}",
                    ProviderName = Name,
                    Metadata = new LyricMetadata
                    {
                        Album = response.AlbumName,
                        Artist = response.ArtistName,
                        Title = response.TrackName,
                        Length = TimeSpan.FromSeconds(response.Duration ?? 0).Ticks,
                        IsSynced = false
                    }
                });
            }

            if (!string.IsNullOrEmpty(response.SyncedLyrics))
            {
                results.Add(new RemoteLyricInfo
                {
                    Id = $"{response.Id}_{SyncedSuffix}",
                    ProviderName = Name,
                    Metadata = new LyricMetadata
                    {
                        Album = response.AlbumName,
                        Artist = response.ArtistName,
                        Title = response.TrackName,
                        Length = TimeSpan.FromSeconds(response.Duration ?? 0).Ticks,
                        IsSynced = true
                    }
                });
            }

            return results;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(
                ex,
                "Unable to get results for {Artist} - {Album} - {Song}",
                artist,
                request.AlbumName,
                request.SongName);
            return Enumerable.Empty<RemoteLyricInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<LyricResponse> GetLyricsAsync(string id, CancellationToken cancellationToken)
    {
        var splitId = id.Split('_', 2);

        try
        {
            var requestUri = new UriBuilder(BaseUrl)
            {
                Path = $"/api/get/{splitId[0]}"
            };

            var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .GetFromJsonAsync<LrcLibSearchResponse>(requestUri.Uri, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (response is null)
            {
                throw new ResourceNotFoundException("Unable to get results for id {Id}");
            }

            if (string.Equals(splitId[1], SyncedSuffix, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(response.SyncedLyrics))
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(response.SyncedLyrics));
                return new LyricResponse
                {
                    Format = SyncedFormat,
                    Stream = stream
                };
            }

            if (string.Equals(splitId[1], PlainSuffix, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(response.PlainLyrics))
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(response.PlainLyrics));
                return new LyricResponse
                {
                    Format = PlainFormat,
                    Stream = stream
                };
            }

            throw new ResourceNotFoundException("Unable to get results for id {Id}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(
                ex,
                "Unable to get results for id {Id}",
                id);
            throw new ResourceNotFoundException("Unable to get results for id {Id}");
        }
    }
}