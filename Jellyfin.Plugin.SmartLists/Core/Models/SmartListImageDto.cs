namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Data transfer object for custom smart list images.
    /// </summary>
    public class SmartListImageDto
    {
        /// <summary>
        /// Gets or sets the image type (Primary, Backdrop, Banner, etc.).
        /// </summary>
        public required string ImageType { get; set; }

        /// <summary>
        /// Gets or sets the stored filename.
        /// </summary>
        public required string FileName { get; set; }

        /// <summary>
        /// Gets or sets the MIME content type (e.g., "image/jpeg").
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Gets or sets the file size in bytes.
        /// </summary>
        public long? FileSize { get; set; }
    }
}
