using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Core.Models;

namespace Jellyfin.Plugin.SmartLists.Services.Abstractions
{
    /// <summary>
    /// Generic service interface for smart list operations (Playlists and Collections)
    /// </summary>
    /// <typeparam name="TDto">The DTO type (SmartPlaylistDto or SmartCollectionDto)</typeparam>
    public interface ISmartListService<TDto> where TDto : SmartListDto
    {
        /// <summary>
        /// Refreshes a single smart list
        /// This method is called by the queue processor and assumes no locking is needed.
        /// </summary>
        Task<(bool Success, string Message, string Id)> RefreshAsync(
            TDto dto, Action<int, int>? progressCallback = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a smart list
        /// </summary>
        Task DeleteAsync(TDto dto, CancellationToken cancellationToken = default);

        /// <summary>
        /// Disables a smart list (deletes the underlying Jellyfin entity)
        /// </summary>
        Task DisableAsync(TDto dto, CancellationToken cancellationToken = default);
    }
}

