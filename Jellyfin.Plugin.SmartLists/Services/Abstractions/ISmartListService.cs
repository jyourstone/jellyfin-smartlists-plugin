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
        /// Deletes a smart list
        /// </summary>
        Task DeleteAsync(TDto dto, CancellationToken cancellationToken = default);

        /// <summary>
        /// Disables a smart list (deletes the underlying Jellyfin entity)
        /// </summary>
        Task DisableAsync(TDto dto, CancellationToken cancellationToken = default);
    }
}

