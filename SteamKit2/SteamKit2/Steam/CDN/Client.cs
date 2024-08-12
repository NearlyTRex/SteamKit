﻿/*
 * This file is subject to the terms and conditions defined in
 * file 'license.txt', which is part of this source code package.
 */

using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SteamKit2.CDN
{
    /// <summary>
    /// The <see cref="Client"/> class is used for downloading game content from the Steam servers.
    /// </summary>
    public sealed class Client : IDisposable
    {
        HttpClient httpClient;

        /// <summary>
        /// Default timeout to use when making requests
        /// </summary>
        public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds( 10 );
        /// <summary>
        /// Default timeout to use when reading the response body
        /// </summary>
        public static TimeSpan ResponseBodyTimeout { get; set; } = TimeSpan.FromSeconds( 60 );


        /// <summary>
        /// Initializes a new instance of the <see cref="Client"/> class.
        /// </summary>
        /// <param name="steamClient">
        /// The <see cref="SteamClient"/> this instance will be associated with.
        /// The SteamClient instance must be connected and logged onto Steam.</param>
        public Client( SteamClient steamClient )
        {
            ArgumentNullException.ThrowIfNull( steamClient );

            this.httpClient = steamClient.Configuration.HttpClientFactory();
        }

        /// <summary>
        /// Disposes of this object.
        /// </summary>
        public void Dispose()
        {
            httpClient.Dispose();
        }

        /// <summary>
        /// Downloads the depot manifest specified by the given manifest ID, and optionally decrypts the manifest's filenames if the depot decryption key has been provided.
        /// </summary>
        /// <param name="depotId">The id of the depot being accessed.</param>
        /// <param name="manifestId">The unique identifier of the manifest to be downloaded.</param>
        /// <param name="manifestRequestCode">The manifest request code for the manifest that is being downloaded.</param>
        /// <param name="server">The content server to connect to.</param>
        /// <param name="depotKey">
        /// The depot decryption key for the depot that will be downloaded.
        /// This is used for decrypting filenames (if needed) in depot manifests, and processing depot chunks.
        /// </param>
        /// <param name="proxyServer">Optional content server marked as UseAsProxy which transforms the request.</param>
        /// <returns>A <see cref="DepotManifest"/> instance that contains information about the files present within a depot.</returns>
        /// <exception cref="System.ArgumentNullException"><see ref="server"/> was null.</exception>
        /// <exception cref="HttpRequestException">An network error occurred when performing the request.</exception>
        /// <exception cref="SteamKitWebRequestException">A network error occurred when performing the request.</exception>
        public async Task<DepotManifest> DownloadManifestAsync( uint depotId, ulong manifestId, ulong manifestRequestCode, Server server, byte[]? depotKey = null, Server? proxyServer = null )
        {
            ArgumentNullException.ThrowIfNull( server );

            const uint MANIFEST_VERSION = 5;
            string url;

            if ( manifestRequestCode > 0 )
            {
                url = $"depot/{depotId}/manifest/{manifestId}/{MANIFEST_VERSION}/{manifestRequestCode}";
            }
            else
            {
                url = $"depot/{depotId}/manifest/{manifestId}/{MANIFEST_VERSION}";
            }

            using var request = new HttpRequestMessage( HttpMethod.Get, BuildCommand( server, url, proxyServer ) );

            using var cts = new CancellationTokenSource();
            cts.CancelAfter( RequestTimeout );

            DepotManifest depotManifest;

            try
            {
                using var response = await httpClient.SendAsync( request, HttpCompletionOption.ResponseHeadersRead, cts.Token ).ConfigureAwait( false );

                if ( !response.IsSuccessStatusCode )
                {
                    throw new SteamKitWebRequestException( $"Response status code does not indicate success: {response.StatusCode:D} ({response.ReasonPhrase}).", response );
                }

                if ( !response.Content.Headers.ContentLength.HasValue )
                {
                    throw new SteamKitWebRequestException( "Response does not have Content-Length", response );
                }

                cts.CancelAfter( ResponseBodyTimeout );

                var contentLength = ( int )response.Content.Headers.ContentLength;
                var buffer = ArrayPool<byte>.Shared.Rent( contentLength );

                try
                {
                    using var ms = new MemoryStream( buffer, 0, contentLength );

                    // Stream the http response into the rented buffer
                    await response.Content.CopyToAsync( ms, cts.Token );

                    if ( ms.Position != contentLength )
                    {
                        throw new InvalidDataException( $"Length mismatch after downloading depot manifest! (was {ms.Position}, but should be {contentLength})" );
                    }

                    ms.Position = 0;

                    // Decompress the zipped manifest data
                    using var zip = new ZipArchive( ms );
                    var entries = zip.Entries;

                    DebugLog.Assert( entries.Count == 1, nameof( CDN ), "Expected the zip to contain only one file" );

                    using var zipEntryStream = entries[ 0 ].Open();
                    depotManifest = DepotManifest.Deserialize( zipEntryStream );
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return( buffer );
                }
            }
            catch ( Exception ex )
            {
                DebugLog.WriteLine( nameof( CDN ), $"Failed to download manifest {request.RequestUri}: {ex.Message}" );
                throw;
            }

            if ( depotKey != null )
            {
                // if we have the depot key, decrypt the manifest filenames
                depotManifest.DecryptFilenames( depotKey );
            }

            return depotManifest;
        }

        /// <summary>
        /// Downloads the specified depot chunk, and optionally processes the chunk and verifies the checksum if the depot decryption key has been provided.
        /// </summary>
        /// <remarks>
        /// This function will also validate the length of the downloaded chunk with the value of <see cref="DepotManifest.ChunkData.CompressedLength"/>,
        /// if it has been assigned a value.
        /// </remarks>
        /// <param name="depotId">The id of the depot being accessed.</param>
        /// <param name="chunk">
        /// A <see cref="DepotManifest.ChunkData"/> instance that represents the chunk to download.
        /// This value should come from a manifest downloaded with <see cref="o:DownloadManifestAsync"/>.
        /// </param>
        /// <returns>A <see cref="DepotChunk"/> instance that contains the data for the given chunk.</returns>
        /// <param name="server">The content server to connect to.</param>
        /// <param name="depotKey">
        /// The depot decryption key for the depot that will be downloaded.
        /// This is used for decrypting filenames (if needed) in depot manifests, and processing depot chunks.
        /// </param>
        /// <param name="proxyServer">Optional content server marked as UseAsProxy which transforms the request.</param>
        /// <exception cref="System.ArgumentNullException">chunk's <see cref="DepotManifest.ChunkData.ChunkID"/> was null.</exception>
        /// <exception cref="System.IO.InvalidDataException">Thrown if the downloaded data does not match the expected length.</exception>
        /// <exception cref="HttpRequestException">An network error occurred when performing the request.</exception>
        /// <exception cref="SteamKitWebRequestException">A network error occurred when performing the request.</exception>
        public async Task<DepotChunk> DownloadDepotChunkAsync( uint depotId, DepotManifest.ChunkData chunk, Server server, byte[]? depotKey = null, Server? proxyServer = null )
        {
            ArgumentNullException.ThrowIfNull( server );
            ArgumentNullException.ThrowIfNull( chunk );

            if ( chunk.ChunkID == null )
            {
                throw new ArgumentException( "Chunk must have a ChunkID.", nameof( chunk ) );
            }

            var chunkID = Utils.EncodeHexString( chunk.ChunkID );
            var url = $"depot/{depotId}/chunk/{chunkID}";

            using var request = new HttpRequestMessage( HttpMethod.Get, BuildCommand( server, url, proxyServer ) );

            using var cts = new CancellationTokenSource();
            cts.CancelAfter( RequestTimeout );

            try
            {
                using var response = await httpClient.SendAsync( request, HttpCompletionOption.ResponseHeadersRead, cts.Token ).ConfigureAwait( false );

                if ( !response.IsSuccessStatusCode )
                {
                    throw new SteamKitWebRequestException( $"Response status code does not indicate success: {response.StatusCode:D} ({response.ReasonPhrase}).", response );
                }

                if ( !response.Content.Headers.ContentLength.HasValue )
                {
                    throw new SteamKitWebRequestException( "Response does not have Content-Length", response );
                }

                var contentLength = ( int )response.Content.Headers.ContentLength;

                // assert that lengths match only if the chunk has a length assigned.
                if ( chunk.CompressedLength > 0 && contentLength != chunk.CompressedLength )
                {
                    throw new InvalidDataException( $"Content-Length mismatch for depot chunk! (was {contentLength}, but should be {chunk.CompressedLength})" );
                }

                cts.CancelAfter( ResponseBodyTimeout );
                var buffer = ArrayPool<byte>.Shared.Rent( contentLength );

                try
                {
                    using var ms = new MemoryStream( buffer, 0, contentLength );

                    // Stream the http response into the rented buffer
                    await response.Content.CopyToAsync( ms, cts.Token );

                    if ( ms.Position != contentLength )
                    {
                        throw new InvalidDataException( $"Length mismatch after downloading depot chunk! (was {ms.Position}, but should be {contentLength})" );
                    }

                    if ( depotKey != null )
                    {
                        // if we have the depot key, we can process the chunk immediately
                        return DepotChunk.ProcessFromData( chunk, buffer.AsSpan()[ ..contentLength ], depotKey );
                    }
                    else
                    {
                        // we have to perform a copy
                        return new DepotChunk( chunk, ms.ToArray() );
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return( buffer );
                }
            }
            catch ( Exception ex )
            {
                DebugLog.WriteLine( nameof( CDN ), $"Failed to download a depot chunk {request.RequestUri}: {ex.Message}" );
                throw;
            }
        }

        static Uri BuildCommand( Server server, string command, Server? proxyServer )
        {
            var uriBuilder = new UriBuilder
            {
                Scheme = server.Protocol == Server.ConnectionProtocol.HTTP ? "http" : "https",
                Host = server.VHost,
                Port = server.Port,
                Path = command,
            };

            if ( proxyServer != null && proxyServer.UseAsProxy && proxyServer.ProxyRequestPathTemplate != null )
            {
                var pathTemplate = proxyServer.ProxyRequestPathTemplate;
                pathTemplate = pathTemplate.Replace( "%host%", uriBuilder.Host, StringComparison.Ordinal );
                pathTemplate = pathTemplate.Replace( "%path%", $"/{uriBuilder.Path}", StringComparison.Ordinal );
                uriBuilder.Scheme = proxyServer.Protocol == Server.ConnectionProtocol.HTTP ? "http" : "https";
                uriBuilder.Host = proxyServer.VHost;
                uriBuilder.Port = proxyServer.Port;
                uriBuilder.Path = pathTemplate;
            }

            return uriBuilder.Uri;
        }
    }
}
