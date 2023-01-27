using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Organizer
{
    public interface IBuildFileNames
    {
        string BuildTrackFileName(List<Track> tracks, Artist artist, Album album, TrackFile trackFile, NamingConfig namingConfig = null, List<CustomFormat> customFormats = null);
        string BuildTrackFilePath(Artist artist, string fileName, string extension);
        BasicNamingConfig GetBasicNamingConfig(NamingConfig nameSpec);
        string GetArtistFolder(Artist artist, NamingConfig namingConfig = null);
    }

    public class FileNameBuilder : IBuildFileNames
    {
        private readonly INamingConfigService _namingConfigService;
        private readonly IQualityDefinitionService _qualityDefinitionService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly ICached<TrackFormat[]> _trackFormatCache;
        private readonly ICached<AbsoluteTrackFormat[]> _absoluteTrackFormatCache;
        private readonly Logger _logger;

        private static readonly Regex TitleRegex = new Regex(@"\{(?<prefix>[- ._\[(]*)(?<token>(?:[a-z0-9]+)(?:(?<separator>[- ._]+)(?:[a-z0-9]+))?)(?::(?<customFormat>[a-z0-9]+))?(?<suffix>[- ._)\]]*)\}",
                                                             RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex TrackRegex = new Regex(@"(?<track>\{track(?:\:0+)?})",
                                                               RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MediumRegex = new Regex(@"(?<medium>\{medium(?:\:0+)?})",
                                                               RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex SeasonEpisodePatternRegex = new Regex(@"(?<separator>(?<=})[- ._]+?)?(?<seasonEpisode>s?{season(?:\:0+)?}(?<episodeSeparator>[- ._]?[ex])(?<episode>{episode(?:\:0+)?}))(?<separator>[- ._]+?(?={))?",
                                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex ReleaseDateRegex = new Regex(@"\{Release(\s|\W|_)Year\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex ArtistNameRegex = new Regex(@"(?<token>\{(?:Artist)(?<separator>[- ._])(Clean)?Name(The)?\})",
                                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex AlbumTitleRegex = new Regex(@"(?<token>\{(?:Album)(?<separator>[- ._])(Clean)?Title(The)?\})",
                                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex TrackTitleRegex = new Regex(@"(?<token>\{(?:Track)(?<separator>[- ._])(Clean)?Title(The)?\})",
                                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FileNameCleanupRegex = new Regex(@"([- ._])(\1)+", RegexOptions.Compiled);
        private static readonly Regex TrimSeparatorsRegex = new Regex(@"[- ._]$", RegexOptions.Compiled);

        private static readonly Regex ScenifyRemoveChars = new Regex(@"(?<=\s)(,|<|>|\/|\\|;|:|'|""|\||`|~|!|\?|@|$|%|^|\*|-|_|=){1}(?=\s)|('|:|\?|,)(?=(?:(?:s|m)\s)|\s|$)|(\(|\)|\[|\]|\{|\})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ScenifyReplaceChars = new Regex(@"[\/]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // TODO: Support Written numbers (One, Two, etc) and Roman Numerals (I, II, III etc)
        private static readonly Regex MultiPartCleanupRegex = new Regex(@"(?:\(\d+\)|(Part|Pt\.?)\s?\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly char[] TrackTitleTrimCharacters = new[] { ' ', '.', '?' };

        private static readonly Regex TitlePrefixRegex = new Regex(@"^(The|An|A) (.*?)((?: *\([^)]+\))*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public FileNameBuilder(INamingConfigService namingConfigService,
                               IQualityDefinitionService qualityDefinitionService,
                               ICacheManager cacheManager,
                               ICustomFormatCalculationService formatCalculator,
                               Logger logger)
        {
            _namingConfigService = namingConfigService;
            _qualityDefinitionService = qualityDefinitionService;
            _formatCalculator = formatCalculator;
            _trackFormatCache = cacheManager.GetCache<TrackFormat[]>(GetType(), "trackFormat");
            _absoluteTrackFormatCache = cacheManager.GetCache<AbsoluteTrackFormat[]>(GetType(), "absoluteTrackFormat");
            _logger = logger;
        }

        public string BuildTrackFileName(List<Track> tracks, Artist artist, Album album, TrackFile trackFile, NamingConfig namingConfig = null, List<CustomFormat> customFormats = null)
        {
            if (namingConfig == null)
            {
                namingConfig = _namingConfigService.GetConfig();
            }

            if (!namingConfig.RenameTracks)
            {
                return GetOriginalFileName(trackFile);
            }

            if (namingConfig.StandardTrackFormat.IsNullOrWhiteSpace() || namingConfig.MultiDiscTrackFormat.IsNullOrWhiteSpace())
            {
                throw new NamingFormatException("Standard and Multi track formats cannot be empty");
            }

            var pattern = namingConfig.StandardTrackFormat;

            if (tracks.First().AlbumRelease.Value.Media.Count() > 1)
            {
                pattern = namingConfig.MultiDiscTrackFormat;
            }

            var tokenHandlers = new Dictionary<string, Func<TokenMatch, string>>(FileNameBuilderTokenEqualityComparer.Instance);

            tracks = tracks.OrderBy(e => e.AlbumReleaseId).ThenBy(e => e.TrackNumber).ToList();

            AddArtistTokens(tokenHandlers, artist);
            AddAlbumTokens(tokenHandlers, album);
            AddMediumTokens(tokenHandlers, tracks.First().AlbumRelease.Value.Media.SingleOrDefault(m => m.Number == tracks.First().MediumNumber));
            AddTrackTokens(tokenHandlers, tracks, artist);
            AddTrackFileTokens(tokenHandlers, trackFile);
            AddQualityTokens(tokenHandlers, artist, trackFile);
            AddMediaInfoTokens(tokenHandlers, trackFile);
            AddCustomFormats(tokenHandlers, artist, trackFile, customFormats);

            var splitPatterns = pattern.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var components = new List<string>();

            foreach (var s in splitPatterns)
            {
                var splitPattern = s;

                splitPattern = FormatTrackNumberTokens(splitPattern, "", tracks);
                splitPattern = FormatMediumNumberTokens(splitPattern, "", tracks);

                var component = ReplaceTokens(splitPattern, tokenHandlers, namingConfig).Trim();

                component = FileNameCleanupRegex.Replace(component, match => match.Captures[0].Value[0].ToString());
                component = TrimSeparatorsRegex.Replace(component, string.Empty);

                if (component.IsNotNullOrWhiteSpace())
                {
                    components.Add(component);
                }
            }

            return Path.Combine(components.ToArray());
        }

        public string BuildTrackFilePath(Artist artist, string fileName, string extension)
        {
            Ensure.That(extension, () => extension).IsNotNullOrWhiteSpace();

            var path = artist.Path;

            return Path.Combine(path, fileName + extension);
        }

        public BasicNamingConfig GetBasicNamingConfig(NamingConfig nameSpec)
        {
            var trackFormat = GetTrackFormat(nameSpec.StandardTrackFormat).LastOrDefault();

            if (trackFormat == null)
            {
                return new BasicNamingConfig();
            }

            var basicNamingConfig = new BasicNamingConfig
            {
                Separator = trackFormat.Separator
            };

            var titleTokens = TitleRegex.Matches(nameSpec.StandardTrackFormat);

            foreach (Match match in titleTokens)
            {
                var separator = match.Groups["separator"].Value;
                var token = match.Groups["token"].Value;

                if (!separator.Equals(" "))
                {
                    basicNamingConfig.ReplaceSpaces = true;
                }

                if (token.StartsWith("{Artist", StringComparison.InvariantCultureIgnoreCase))
                {
                    basicNamingConfig.IncludeArtistName = true;
                }

                if (token.StartsWith("{Album", StringComparison.InvariantCultureIgnoreCase))
                {
                    basicNamingConfig.IncludeAlbumTitle = true;
                }

                if (token.StartsWith("{Quality", StringComparison.InvariantCultureIgnoreCase))
                {
                    basicNamingConfig.IncludeQuality = true;
                }
            }

            return basicNamingConfig;
        }

        public string GetArtistFolder(Artist artist, NamingConfig namingConfig = null)
        {
            if (namingConfig == null)
            {
                namingConfig = _namingConfigService.GetConfig();
            }

            var pattern = namingConfig.ArtistFolderFormat;
            var tokenHandlers = new Dictionary<string, Func<TokenMatch, string>>(FileNameBuilderTokenEqualityComparer.Instance);

            AddArtistTokens(tokenHandlers, artist);

            var splitPatterns = pattern.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var components = new List<string>();

            foreach (var s in splitPatterns)
            {
                var splitPattern = s;

                var component = ReplaceTokens(splitPattern, tokenHandlers, namingConfig);
                component = CleanFolderName(component);

                if (component.IsNotNullOrWhiteSpace())
                {
                    components.Add(component);
                }
            }

            return Path.Combine(components.ToArray());
        }

        public static string CleanTitle(string title)
        {
            title = title.Replace("&", "and");
            title = ScenifyReplaceChars.Replace(title, " ");
            title = ScenifyRemoveChars.Replace(title, string.Empty);

            return title;
        }

        public static string TitleThe(string title)
        {
            return TitlePrefixRegex.Replace(title, "$2, $1$3");
        }

        public static string CleanFileName(string name, bool replace = true)
        {
            string result = name;
            string[] badCharacters = { "\\", "/", "<", ">", "?", "*", ":", "|", "\"" };
            string[] goodCharacters = { "+", "+", "", "", "!", "-", "-", "", "" };

            // Replace a colon followed by a space with space dash space for a better appearance
            if (replace)
            {
                result = result.Replace(": ", " - ");
            }

            for (int i = 0; i < badCharacters.Length; i++)
            {
                result = result.Replace(badCharacters[i], replace ? goodCharacters[i] : string.Empty);
            }

            return result.Trim();
        }

        public static string CleanFolderName(string name)
        {
            name = FileNameCleanupRegex.Replace(name, match => match.Captures[0].Value[0].ToString());
            return name.Trim(' ', '.');
        }

        private void AddArtistTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Artist artist)
        {
            tokenHandlers["{Artist Name}"] = m => artist.Name;
            tokenHandlers["{Artist CleanName}"] = m => CleanTitle(artist.Name);
            tokenHandlers["{Artist NameThe}"] = m => TitleThe(artist.Name);
            tokenHandlers["{Artist Genre}"] = m => artist.Metadata.Value.Genres?.FirstOrDefault() ?? string.Empty;
            tokenHandlers["{Artist NameFirstCharacter}"] = m => TitleThe(artist.Name).Substring(0, 1).FirstCharToUpper();

            if (artist.Metadata.Value.Disambiguation != null)
            {
                tokenHandlers["{Artist Disambiguation}"] = m => artist.Metadata.Value.Disambiguation;
            }
        }

        private void AddAlbumTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Album album)
        {
            tokenHandlers["{Album Title}"] = m => album.Title;
            tokenHandlers["{Album CleanTitle}"] = m => CleanTitle(album.Title);
            tokenHandlers["{Album TitleThe}"] = m => TitleThe(album.Title);
            tokenHandlers["{Album Type}"] = m => album.AlbumType;
            tokenHandlers["{Album Genre}"] = m => album.Genres.FirstOrDefault() ?? string.Empty;

            if (album.Disambiguation != null)
            {
                tokenHandlers["{Album Disambiguation}"] = m => album.Disambiguation;
            }

            if (album.ReleaseDate.HasValue)
            {
                tokenHandlers["{Release Year}"] = m => album.ReleaseDate.Value.Year.ToString();
            }
            else
            {
                tokenHandlers["{Release Year}"] = m => "Unknown";
            }
        }

        private void AddMediumTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Medium medium)
        {
            tokenHandlers["{Medium Format}"] = m => medium.Format;
        }

        private void AddTrackTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, List<Track> tracks, Artist artist)
        {
            tokenHandlers["{Track Title}"] = m => GetTrackTitle(tracks, "+");
            tokenHandlers["{Track CleanTitle}"] = m => CleanTitle(GetTrackTitle(tracks, "and"));

            // Use the track's ArtistMetadata by default, as it will handle the "Various Artists" case
            // (where the album artist is "Various Artists" but each track has its own artist). Fall back
            // to the album artist if we don't have any track ArtistMetadata for whatever reason.
            var firstArtist = tracks.Select(t => t.ArtistMetadata?.Value).FirstOrDefault() ?? artist.Metadata;
            if (firstArtist != null)
            {
                tokenHandlers["{Track ArtistName}"] = m => firstArtist.Name;
                tokenHandlers["{Track ArtistCleanName}"] = m => CleanTitle(firstArtist.Name);
                tokenHandlers["{Track ArtistNameThe}"] = m => TitleThe(firstArtist.Name);
            }
        }

        private void AddTrackFileTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, TrackFile trackFile)
        {
            tokenHandlers["{Original Title}"] = m => GetOriginalTitle(trackFile);
            tokenHandlers["{Original Filename}"] = m => GetOriginalFileName(trackFile);
            tokenHandlers["{Release Group}"] = m => trackFile.ReleaseGroup ?? m.DefaultValue("Lidarr");
        }

        private void AddQualityTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Artist artist, TrackFile trackFile)
        {
            var qualityTitle = _qualityDefinitionService.Get(trackFile.Quality.Quality).Title;
            var qualityProper = GetQualityProper(trackFile.Quality);

            // var qualityReal = GetQualityReal(artist, trackFile.Quality);
            tokenHandlers["{Quality Full}"] = m => string.Format("{0}", qualityTitle);
            tokenHandlers["{Quality Title}"] = m => qualityTitle;
            tokenHandlers["{Quality Proper}"] = m => qualityProper;

            // tokenHandlers["{Quality Real}"] = m => qualityReal;
        }

        private void AddMediaInfoTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, TrackFile trackFile)
        {
            if (trackFile.MediaInfo == null)
            {
                _logger.Trace("Media info is unavailable for {0}", trackFile);

                return;
            }

            var audioCodec = MediaInfoFormatter.FormatAudioCodec(trackFile.MediaInfo);
            var audioChannels = MediaInfoFormatter.FormatAudioChannels(trackFile.MediaInfo);
            var audioChannelsFormatted = audioChannels > 0 ?
                                audioChannels.ToString("F1", CultureInfo.InvariantCulture) :
                                string.Empty;

            tokenHandlers["{MediaInfo AudioCodec}"] = m => audioCodec;
            tokenHandlers["{MediaInfo AudioChannels}"] = m => audioChannelsFormatted;
            tokenHandlers["{MediaInfo AudioBitRate}"] = m => MediaInfoFormatter.FormatAudioBitrate(trackFile.MediaInfo);
            tokenHandlers["{MediaInfo AudioBitsPerSample}"] = m => MediaInfoFormatter.FormatAudioBitsPerSample(trackFile.MediaInfo);
            tokenHandlers["{MediaInfo AudioSampleRate}"] = m => MediaInfoFormatter.FormatAudioSampleRate(trackFile.MediaInfo);
        }

        private void AddCustomFormats(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Artist series, TrackFile episodeFile, List<CustomFormat> customFormats = null)
        {
            if (customFormats == null)
            {
                episodeFile.Artist = series;
                customFormats = _formatCalculator.ParseCustomFormat(episodeFile, series);
            }

            tokenHandlers["{Custom Formats}"] = m => string.Join(" ", customFormats.Where(x => x.IncludeCustomFormatWhenRenaming));
        }

        private string ReplaceTokens(string pattern, Dictionary<string, Func<TokenMatch, string>> tokenHandlers, NamingConfig namingConfig)
        {
            return TitleRegex.Replace(pattern, match => ReplaceToken(match, tokenHandlers, namingConfig));
        }

        private string ReplaceToken(Match match, Dictionary<string, Func<TokenMatch, string>> tokenHandlers, NamingConfig namingConfig)
        {
            var tokenMatch = new TokenMatch
            {
                RegexMatch = match,
                Prefix = match.Groups["prefix"].Value,
                Separator = match.Groups["separator"].Value,
                Suffix = match.Groups["suffix"].Value,
                Token = match.Groups["token"].Value,
                CustomFormat = match.Groups["customFormat"].Value
            };

            if (tokenMatch.CustomFormat.IsNullOrWhiteSpace())
            {
                tokenMatch.CustomFormat = null;
            }

            var tokenHandler = tokenHandlers.GetValueOrDefault(tokenMatch.Token, m => string.Empty);

            var replacementText = tokenHandler(tokenMatch).Trim();

            if (tokenMatch.Token.All(t => !char.IsLetter(t) || char.IsLower(t)))
            {
                replacementText = replacementText.ToLower();
            }
            else if (tokenMatch.Token.All(t => !char.IsLetter(t) || char.IsUpper(t)))
            {
                replacementText = replacementText.ToUpper();
            }

            if (!tokenMatch.Separator.IsNullOrWhiteSpace())
            {
                replacementText = replacementText.Replace(" ", tokenMatch.Separator);
            }

            replacementText = CleanFileName(replacementText, namingConfig.ReplaceIllegalCharacters);

            if (!replacementText.IsNullOrWhiteSpace())
            {
                replacementText = tokenMatch.Prefix + replacementText + tokenMatch.Suffix;
            }

            return replacementText;
        }

        private string FormatTrackNumberTokens(string basePattern, string formatPattern, List<Track> tracks)
        {
            var pattern = string.Empty;

            for (int i = 0; i < tracks.Count; i++)
            {
                var patternToReplace = i == 0 ? basePattern : formatPattern;

                pattern += TrackRegex.Replace(patternToReplace, match => ReplaceNumberToken(match.Groups["track"].Value, tracks[i].AbsoluteTrackNumber));
            }

            return pattern;
        }

        private string FormatMediumNumberTokens(string basePattern, string formatPattern, List<Track> tracks)
        {
            var pattern = string.Empty;

            for (int i = 0; i < tracks.Count; i++)
            {
                var patternToReplace = i == 0 ? basePattern : formatPattern;

                pattern += MediumRegex.Replace(patternToReplace, match => ReplaceNumberToken(match.Groups["medium"].Value, tracks[i].MediumNumber));
            }

            return pattern;
        }

        private string ReplaceNumberToken(string token, int value)
        {
            var split = token.Trim('{', '}').Split(':');
            if (split.Length == 1)
            {
                return value.ToString("0");
            }

            return value.ToString(split[1]);
        }

        private TrackFormat[] GetTrackFormat(string pattern)
        {
            return _trackFormatCache.Get(pattern, () => SeasonEpisodePatternRegex.Matches(pattern).OfType<Match>()
                .Select(match => new TrackFormat
                {
                    TrackSeparator = match.Groups["episodeSeparator"].Value,
                    Separator = match.Groups["separator"].Value,
                    TrackPattern = match.Groups["episode"].Value,
                }).ToArray());
        }

        private string GetTrackTitle(List<Track> tracks, string separator)
        {
            separator = string.Format(" {0} ", separator.Trim());

            if (tracks.Count == 1)
            {
                return tracks.First().Title.TrimEnd(TrackTitleTrimCharacters);
            }

            var titles = tracks.Select(c => c.Title.TrimEnd(TrackTitleTrimCharacters))
                                 .Select(CleanupTrackTitle)
                                 .Distinct()
                                 .ToList();

            if (titles.All(t => t.IsNullOrWhiteSpace()))
            {
                titles = tracks.Select(c => c.Title.TrimEnd(TrackTitleTrimCharacters))
                                 .Distinct()
                                 .ToList();
            }

            return string.Join(separator, titles);
        }

        private string CleanupTrackTitle(string title)
        {
            // this will remove (1),(2) from the end of multi part episodes.
            return MultiPartCleanupRegex.Replace(title, string.Empty).Trim();
        }

        private string GetQualityProper(QualityModel quality)
        {
            if (quality.Revision.Version > 1)
            {
                if (quality.Revision.IsRepack)
                {
                    return "Repack";
                }

                return "Proper";
            }

            return string.Empty;
        }

        // private string GetQualityReal(Series series, QualityModel quality)
        // {
        //    if (quality.Revision.Real > 0)
        //    {
        //        return "REAL";
        //    }

        // return string.Empty;
        // }
        private string GetOriginalTitle(TrackFile trackFile)
        {
            if (trackFile.SceneName.IsNullOrWhiteSpace())
            {
                return GetOriginalFileName(trackFile);
            }

            return trackFile.SceneName;
        }

        private string GetOriginalFileName(TrackFile trackFile)
        {
            return Path.GetFileNameWithoutExtension(trackFile.Path);
        }
    }

    internal sealed class TokenMatch
    {
        public Match RegexMatch { get; set; }
        public string Prefix { get; set; }
        public string Separator { get; set; }
        public string Suffix { get; set; }
        public string Token { get; set; }
        public string CustomFormat { get; set; }

        public string DefaultValue(string defaultValue)
        {
            if (string.IsNullOrEmpty(Prefix) && string.IsNullOrEmpty(Suffix))
            {
                return defaultValue;
            }
            else
            {
                return string.Empty;
            }
        }
    }

    public enum MultiEpisodeStyle
    {
        Extend = 0,
        Duplicate = 1,
        Repeat = 2,
        Scene = 3,
        Range = 4,
        PrefixedRange = 5
    }
}
