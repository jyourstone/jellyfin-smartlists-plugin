using System.Text;
using Jellyfin.Extensions;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Normalization helpers for matching rule values against server-side name dumps.
    ///
    /// Background: the ItemValues-backed name dumps (GetGenreNames/GetStudioNames/...) hold
    /// ONE representative raw variant per server-cleaned group (GetItemValueNames groups by
    /// CleanValue and returns the first Value). Matching a rule against a representative
    /// with the plugin's per-item semantics (OrdinalIgnoreCase on raw strings) would miss
    /// groups whose OTHER variants match - a false negative. These helpers instead compare
    /// on normalized forms whose granularity is chosen per use case relative to the two
    /// server cleans (10.11: RemoveDiacritics + lowercase; Jellyfin 12: the same plus
    /// punctuation stripping and whitespace collapsing).
    /// </summary>
    internal static class PrefilterValueCleaner
    {
        /// <summary>
        /// The 10.11 server's GetCleanValue: RemoveDiacritics (the server's own
        /// implementation, so character folding matches exactly) + ToLowerInvariant.
        /// This is the FINEST clean either ABI applies to ItemValues - Jellyfin 12's
        /// clean only removes more - so equality on this key implies equality of the
        /// stored CleanValue on BOTH ABIs. That implication is what the studio
        /// materialization coverage check relies on.
        /// </summary>
        /// <param name="value">The raw value.</param>
        /// <returns>The cleaned value.</returns>
        internal static string CleanValue(string value)
        {
            return value.RemoveDiacritics().ToLowerInvariant();
        }

        /// <summary>
        /// <see cref="CleanValue"/> with every non-alphanumeric character removed. This is
        /// COARSER than both ABIs' server cleans: any two raw variants the server stores
        /// under one cleaned group normalize to the same string here (the extra steps of
        /// either server clean only delete/collapse non-alphanumerics, never touch letters
        /// or digits). Matching a rule against one dumped representative on this form can
        /// therefore never miss a group whose other variant the per-item path would match.
        /// The per-character mapping also preserves substring containment, so Contains/IsIn
        /// matching stays a superset too. Extra matches only add candidates (harmless).
        /// </summary>
        /// <param name="value">The raw value.</param>
        /// <returns>The normalized comparison form.</returns>
        internal static string MatchNormalize(string value)
        {
            var cleaned = CleanValue(value);
            var builder = new StringBuilder(cleaned.Length);
            foreach (var c in cleaned)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }
}
