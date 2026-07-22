using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Builds normalized title|artist match keys for music tracks. Shared by external-list
    /// providers (list side) and the query engine (library-item side) so both sides produce
    /// identical keys for fallback matching when no MusicBrainz recording MBID is available.
    /// </summary>
    public static partial class MusicMatchKey
    {
        /// <summary>
        /// Builds the combined match key for a track title and a single artist name.
        /// </summary>
        /// <param name="title">The track title.</param>
        /// <param name="artist">The artist credit name.</param>
        /// <returns>The normalized "title|artist" key.</returns>
        public static string TitleArtistKey(string? title, string? artist)
        {
            return NormalizeTitle(title) + "|" + NormalizeArtist(artist);
        }

        /// <summary>
        /// Normalizes a track title for matching: lowercases, strips diacritics and
        /// featured-artist segments, strips trailing parenthetical/bracket groups
        /// (remaster/live suffixes), and keeps only letters, digits and spaces.
        /// </summary>
        /// <param name="s">The raw title.</param>
        /// <returns>The normalized title, or an empty string when nothing remains.</returns>
        public static string NormalizeTitle(string? s)
        {
            return Normalize(s, stripTrailingGroups: true);
        }

        /// <summary>
        /// Normalizes an artist name for matching. Same pipeline as <see cref="NormalizeTitle"/>
        /// but keeps trailing parenthetical/bracket groups (they can be part of the artist name).
        /// </summary>
        /// <param name="s">The raw artist name.</param>
        /// <returns>The normalized artist name, or an empty string when nothing remains.</returns>
        public static string NormalizeArtist(string? s)
        {
            return Normalize(s, stripTrailingGroups: false);
        }

        private static string Normalize(string? s, bool stripTrailingGroups)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            // Trim and lowercase (invariant), then decompose (NFKD) and drop combining marks
            // so accented characters compare equal to their base characters.
            var value = RemoveDiacritics(s.Trim().ToLowerInvariant());

            // Remove featured-artist segments: "(feat. X)", "[ft X]" or "feat. X" to end of string.
            value = FeatSegmentPattern().Replace(value, string.Empty);

            // Titles only: repeatedly strip a trailing "(...)" or "[...]" group (remaster/live
            // suffixes). Leading/mid groups stay — "(I Can't Get No) Satisfaction" keeps its prefix.
            if (stripTrailingGroups)
            {
                string stripped;
                while ((stripped = TrailingGroupPattern().Replace(value, string.Empty)) != value)
                {
                    value = stripped;
                }
            }

            // "&" and "and" compare equal.
            value = value.Replace("&", " and ", StringComparison.Ordinal);

            // Keep only letters, digits and spaces; collapse whitespace runs to a single space; trim.
            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (pendingSpace && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    pendingSpace = false;
                    builder.Append(c);
                }
                else if (char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                }
            }

            return builder.ToString();
        }

        private static string RemoveDiacritics(string value)
        {
            var decomposed = value.Normalize(NormalizationForm.FormKD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Matches featured-artist segments in an already-lowercased string: a "(...)"/"[...]"
        /// group starting with feat/ft/featuring (optional trailing dot), or the keyword to end of string.
        /// </summary>
        [GeneratedRegex(@"\(\s*(?:featuring|feat|ft)\b\.?[^)]*\)|\[\s*(?:featuring|feat|ft)\b\.?[^\]]*\]|\b(?:featuring|feat|ft)\b\.?\s.*$", RegexOptions.None)]
        private static partial Regex FeatSegmentPattern();

        /// <summary>
        /// Matches a trailing "(...)" or "[...]" group, e.g. "(2011 remaster)" or "[live]".
        /// </summary>
        [GeneratedRegex(@"\s*(?:\([^()]*\)|\[[^\[\]]*\])\s*$", RegexOptions.None)]
        private static partial Regex TrailingGroupPattern();
    }
}
