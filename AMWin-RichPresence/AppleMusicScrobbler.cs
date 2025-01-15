using IF.Lastfm.Core.Api;
using IF.Lastfm.Core.Objects;
using IF.Lastfm.Core.Scrobblers;
using MetaBrainz.ListenBrainz;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace AMWin_RichPresence {
    internal interface IScrobblerCredentials { }

    public struct LastFmCredentials : IScrobblerCredentials {
        public string apiKey;
        public string apiSecret;
        public string username;
        public string password;
    }

    public struct ListenBrainzCredentials : IScrobblerCredentials {
        public string userToken;
    }

    internal class AlbumCleaner {

        private static readonly Regex AlbumCleanerRegex = new Regex(@"\s-\s((Single)|(EP))$", RegexOptions.Compiled);

        public static string CleanAlbumName(string songName) {
            // Remove " - Single" and " - EP"
            return AlbumCleanerRegex.Replace(songName, new MatchEvaluator((m) => { return ""; }));
        }

    }

    internal abstract class AppleMusicScrobbler<C> where C : IScrobblerCredentials
    {
        protected int elapsedSeconds;
        protected string? lastSongID;
        protected bool hasScrobbled;
        protected double lastSongProgress;
        protected Logger? logger;
        protected string serviceName;
        protected string region;

        public AppleMusicScrobbler(string serviceName, string region, Logger? logger = null)
        {
            this.serviceName = serviceName;
            this.logger = logger;
            this.region = region;
        }

        protected bool IsTimeToScrobble(AppleMusicInfo info)
        {
            double duration = info.SongDuration.HasValue ? info.SongDuration.Value : 120; // Default to 2 minutes if missing
            string durationSource = info.SongDuration.HasValue ? "Scraper/Last.fm" : "Fallback (Default)";

            // Allow very short songs to scrobble based on their actual duration
            if (duration < 75)
            {
                durationSource = "Short Song (Valid)";
                logger?.Log($"[IsTimeToScrobble] Short song detected: elapsedSeconds: {elapsedSeconds}, halfDuration: {duration / 2}, SongDuration: {duration}, Source: {durationSource}");
                return elapsedSeconds >= (duration / 2); // Scrobble after 50% playback
            }

            // Override invalid durations
            if (duration > 600)
            {
                duration = 210; // Fallback for absurdly long durations
                durationSource = "Fallback (Invalid)";
                logger?.Log($"[IsTimeToScrobble] Overridden invalid SongDuration to {duration}, Source: {durationSource}");
            }

            // Default logic for other songs
            double halfSongDuration = duration / 2;
            bool conditionMet = elapsedSeconds >= halfSongDuration || elapsedSeconds >= 120;

            logger?.Log($"[IsTimeToScrobble] elapsedSeconds: {elapsedSeconds}, halfSongDuration: {halfSongDuration}, " +
                        $"SongDuration: {duration}, Condition Met: {conditionMet}, Source: {durationSource}");

            return conditionMet;
        }








        protected bool IsRepeating(AppleMusicInfo info)
        {
            if (info.CurrentTime.HasValue && info.SongDuration.HasValue)
            {
                double currentTime = info.CurrentTime.Value;
                double songDuration = info.SongDuration.Value;
                double repeatThreshold = 1.5 * Constants.RefreshPeriod;
                return currentTime <= repeatThreshold && lastSongProgress >= (songDuration - repeatThreshold);
            }

            return false;
        }

        public abstract Task<bool> init(C credentials);

        public abstract Task<bool> UpdateCredsAsync(C credentials);

        protected abstract Task UpdateNowPlaying(string artist, string album, string song);

        protected abstract Task ScrobbleSong(string artist, string album, string song);

        public async void Scrobbleit(AppleMusicInfo info)
        {
            try
            {
                string durationSource = "Unknown"; // Track the source of the duration

                // Log song information
                logger?.Log($"[{serviceName} scrobbler] Info: " +
                    $"Artist: {info.SongArtist}, Name: {info.SongName}, Album: {info.SongAlbum}, " +
                    $"Duration: {info.SongDuration}, CurrentTime: {info.CurrentTime}");

                // Determine the source of the duration
                if (!info.SongDuration.HasValue)
                {
                    info.SongDuration = 120; // Default fallback duration
                    durationSource = "Fallback (Default)";
                }
                else if (info.SongDuration > 600)
                {
                    info.SongDuration = 210; // Override with default duration
                    durationSource = "Fallback (Invalid)";
                }
                else
                {
                    durationSource = "Valid (Scraper or Last.fm)";
                }

                logger?.Log($"[{serviceName} scrobbler] Duration source: {durationSource}, Final Duration: {info.SongDuration} seconds");

                var thisSongID = info.SongArtist + info.SongName + info.SongAlbum;
                var webScraper = new AppleMusicWebScraper(info.SongName, info.SongAlbum, info.SongArtist, region);
                var artist = Properties.Settings.Default.LastfmScrobblePrimaryArtist ? (await webScraper.GetArtistList()).FirstOrDefault(info.SongArtist) : info.SongArtist;
                var album = Properties.Settings.Default.LastfmCleanAlbumName ? AlbumCleaner.CleanAlbumName(info.SongAlbum) : info.SongAlbum;

                if (thisSongID != lastSongID)
                {
                    lastSongID = thisSongID;
                    elapsedSeconds = 0;
                    hasScrobbled = false;
                    logger?.Log($"[{serviceName} scrobbler] New Song: {lastSongID}");

                    await UpdateNowPlaying(artist, album, info.SongName);
                    logger?.Log($"[{serviceName} scrobbler] Updated now playing: {lastSongID}");
                }
                else
                {
                    elapsedSeconds += Constants.RefreshPeriod;

                    logger?.Log($"[{serviceName} scrobbler] elapsedSeconds: {elapsedSeconds}, hasScrobbled: {hasScrobbled}");
                    logger?.Log($"[{serviceName} scrobbler] IsTimeToScrobble: {IsTimeToScrobble(info)}");

                    if (hasScrobbled && IsRepeating(info))
                    {
                        if (elapsedSeconds > Constants.RefreshPeriod)
                        {
                            hasScrobbled = false;
                            elapsedSeconds = 0;
                            logger?.Log($"[{serviceName} scrobbler] Repeating Song: {lastSongID}");
                        }
                    }

                    if (IsTimeToScrobble(info) && !hasScrobbled)
                    {
                        logger?.Log($"[{serviceName} scrobbler] Scrobbling: {lastSongID}");
                        try
                        {
                            await ScrobbleSong(artist, album, info.SongName);
                            hasScrobbled = true;
                            logger?.Log($"[{serviceName} scrobbler] Successfully scrobbled: {info.SongName}");
                        }
                        catch (Exception ex)
                        {
                            logger?.Log($"[{serviceName} scrobbler] Failed to scrobble: {ex}");
                        }
                    }

                    lastSongProgress = info.CurrentTime ?? 0.0;
                }
            }
            catch (Exception ex)
            {
                logger?.Log($"[{serviceName} scrobbler] An error occurred while scrobbling: {ex}");
            }
        }
    }


        internal class AppleMusicLastFmScrobbler : AppleMusicScrobbler<LastFmCredentials> {
        private LastAuth? lastfmAuth;
        private IScrobbler? lastFmScrobbler;
        private ITrackApi? trackApi;

        public AppleMusicLastFmScrobbler(string region, Logger? logger = null) : base("Last.FM", region, logger) { }

        public async override Task<bool> init(LastFmCredentials credentials) {
            if (string.IsNullOrEmpty(credentials.apiKey)
                || string.IsNullOrEmpty(credentials.apiSecret)
                || string.IsNullOrEmpty(credentials.username)) {
                return false;
            }
            // Use the four pieces of information (API Key, API Secret, Username, Password) to log into Last.FM for Scrobbling
            lastfmAuth = new LastAuth(credentials.apiKey, credentials.apiSecret);
            await lastfmAuth.GetSessionTokenAsync(credentials.username, credentials.password);

            lastFmScrobbler = new MemoryScrobbler(lastfmAuth, Constants.HttpClient);
            trackApi = new TrackApi(lastfmAuth, Constants.HttpClient);

            if (lastfmAuth.Authenticated) {
                logger?.Log("Last.FM authentication succeeded");
            } else {
                logger?.Log("Last.FM authentication failed");
            }

            return lastfmAuth.Authenticated;
        }

        public async override Task<bool> UpdateCredsAsync(LastFmCredentials credentials) {
            logger?.Log("[Last.FM scrobbler] Updating credentials");
            lastfmAuth = null;
            lastFmScrobbler = null;
            trackApi = null;
            return await init(credentials);
        }

        protected async override Task ScrobbleSong(string artist, string album, string song) {
            if (lastFmScrobbler == null || lastfmAuth?.Authenticated != true) {
                return;
            }

            var scrobble = new Scrobble(artist, album, song, DateTime.UtcNow);
            await lastFmScrobbler.ScrobbleAsync(scrobble);
        }

        protected async override Task UpdateNowPlaying(string artist, string album, string song) {
            if (trackApi == null || lastfmAuth?.Authenticated != true) {
                return;
            }

            var scrobble = new Scrobble(artist, album, song, DateTime.UtcNow);
            await trackApi.UpdateNowPlayingAsync(scrobble);
        }
    }

    internal class AppleMusicListenBrainzScrobbler : AppleMusicScrobbler<ListenBrainzCredentials> {
        private ListenBrainz? listenBrainzClient;

        public AppleMusicListenBrainzScrobbler(string region, Logger? logger = null) : base("ListenBrainz", region, logger) { }

        public async override Task<bool> init(ListenBrainzCredentials credentials) {
            listenBrainzClient = new();

            if (string.IsNullOrEmpty(credentials.userToken)) {
                logger?.Log("No ListenBrainz user token found");
                return false;
            }

            var tokenValidation = await listenBrainzClient.ValidateTokenAsync(credentials.userToken);

            if (tokenValidation.Valid == true) {
                logger?.Log("ListenBrainz authentication succeeded");
                listenBrainzClient.UserToken = credentials.userToken;
            } else {
                logger?.Log("ListenBrainz authentication failed");
                listenBrainzClient.UserToken = null;
            }

            return tokenValidation.Valid ?? false;
        }

        public async override Task<bool> UpdateCredsAsync(ListenBrainzCredentials credentials) {
            logger?.Log("[ListenBrainz] Updating credentials");
            return await init(credentials);
        }

        protected async override Task ScrobbleSong(string artist, string album, string song) {
            if (string.IsNullOrEmpty(listenBrainzClient?.UserToken)) {
                return;
            }

            await listenBrainzClient.SubmitSingleListenAsync(song, artist, album);
        }

        protected async override Task UpdateNowPlaying(string artist, string album, string song) {
            if (string.IsNullOrEmpty(listenBrainzClient?.UserToken)) {
                return;
            }

            await listenBrainzClient.SetNowPlayingAsync(song, artist, album);
        }
    }
}
