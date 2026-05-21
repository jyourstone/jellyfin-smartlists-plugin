using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Models;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Resolves the effective user mappings for smart playlists.
    /// </summary>
    internal static class PlaylistUserResolver
    {
        public static string NormalizeUserId(Guid userId) => userId.ToString("N");

        public static string NormalizeUserId(string userId)
        {
            return Guid.TryParse(userId, out var guid) ? guid.ToString("N") : userId;
        }

        public static List<User> GetAllUsers(IUserManager userManager)
        {
            ArgumentNullException.ThrowIfNull(userManager);

            return GetUsers(userManager)
                .Where(u => u != null && u.Id != Guid.Empty)
                .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<User> GetUsers(IUserManager userManager)
        {
            var userManagerType = userManager.GetType();
            var getUsersMethod = userManagerType.GetMethod("GetUsers", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes)
                ?? typeof(IUserManager).GetMethod("GetUsers", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
            if (TryGetUsers(getUsersMethod?.Invoke(userManager, null), out var users))
            {
                return users;
            }

            var usersProperty = userManagerType.GetProperty("Users", BindingFlags.Instance | BindingFlags.Public)
                ?? typeof(IUserManager).GetProperty("Users", BindingFlags.Instance | BindingFlags.Public);
            if (TryGetUsers(usersProperty?.GetValue(userManager), out users))
            {
                return users;
            }

            throw new MissingMethodException(
                userManagerType.FullName,
                "GetUsers/Users");
        }

        private static bool TryGetUsers(object? value, out IEnumerable<User> users)
        {
            switch (value)
            {
                case IEnumerable<User> typedUsers:
                    users = typedUsers;
                    return true;
                case IEnumerable enumerable:
                    users = enumerable.OfType<User>();
                    return true;
                default:
                    users = [];
                    return false;
            }
        }

        public static void ExpandAllUsers(SmartPlaylistDto playlist, IUserManager userManager)
        {
            ArgumentNullException.ThrowIfNull(playlist);
            ArgumentNullException.ThrowIfNull(userManager);

            if (!playlist.AllUsers)
            {
                return;
            }

            var existingByUserId = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (playlist.UserPlaylists != null)
            {
                foreach (var mapping in playlist.UserPlaylists)
                {
                    if (!string.IsNullOrEmpty(mapping.UserId) &&
                        Guid.TryParse(mapping.UserId, out var userId) &&
                        userId != Guid.Empty &&
                        !existingByUserId.ContainsKey(userId.ToString("N")))
                    {
                        existingByUserId[userId.ToString("N")] = mapping.JellyfinPlaylistId;
                    }
                }
            }

            playlist.UserPlaylists = GetAllUsers(userManager)
                .Select(user =>
                {
                    var normalizedUserId = user.Id.ToString("N");
                    existingByUserId.TryGetValue(normalizedUserId, out var jellyfinPlaylistId);
                    return new SmartPlaylistDto.UserPlaylistMapping
                    {
                        UserId = normalizedUserId,
                        JellyfinPlaylistId = jellyfinPlaylistId
                    };
                })
                .ToList();

            playlist.Public = false;
        }

        public static HashSet<string> GetEffectiveUserIds(SmartPlaylistDto playlist, IUserManager? userManager = null)
        {
            ArgumentNullException.ThrowIfNull(playlist);

            var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (playlist.AllUsers && userManager != null)
            {
                foreach (var user in GetAllUsers(userManager))
                {
                    userIds.Add(user.Id.ToString("N"));
                }

                return userIds;
            }

            if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
            {
                foreach (var mapping in playlist.UserPlaylists)
                {
                    if (!string.IsNullOrEmpty(mapping.UserId) &&
                        Guid.TryParse(mapping.UserId, out var userId) &&
                        userId != Guid.Empty)
                    {
                        userIds.Add(userId.ToString("N"));
                    }
                }
            }
            else if (!string.IsNullOrEmpty(playlist.UserId) &&
                     Guid.TryParse(playlist.UserId, out var parsedUserId) &&
                     parsedUserId != Guid.Empty)
            {
                userIds.Add(parsedUserId.ToString("N"));
            }

            return userIds;
        }

        public static SmartPlaylistDto.UserPlaylistMapping? FindMapping(SmartPlaylistDto playlist, string userId)
        {
            if (playlist.UserPlaylists == null)
            {
                return null;
            }

            var normalizedUserId = NormalizeUserId(userId);
            return playlist.UserPlaylists.FirstOrDefault(mapping =>
                !string.IsNullOrEmpty(mapping.UserId) &&
                string.Equals(NormalizeUserId(mapping.UserId), normalizedUserId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
