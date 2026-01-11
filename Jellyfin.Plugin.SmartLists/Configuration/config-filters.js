(function (SmartLists) {
    'use strict';

    // Initialize namespace if it doesn't exist
    if (!SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }

    // Normalize user ID for consistent cache lookups (remove dashes, lowercase)
    const normalizeUserId = function (userId) {
        if (!userId || typeof userId !== 'string') return '';
        return userId.replace(/-/g, '').toLowerCase();
    };

    // Centralized filter configuration - eliminates DRY violations
    SmartLists.PLAYLIST_FILTER_CONFIGS = {
        search: {
            selector: '#playlistSearchInput',
            defaultValue: '',
            getValue: function (element) {
                return element ? element.value.trim().toLowerCase() : '';
            },
            filterFn: function (playlists, searchTerm, page) {
                if (!searchTerm) return playlists;
                return SmartLists.filterPlaylists(playlists, searchTerm, page);
            }
        },
        type: {
            selector: '#typeFilter',
            defaultValue: 'all',
            getValue: function (element) {
                return element ? element.value : 'all';
            },
            filterFn: function (playlists, typeFilter) {
                if (!typeFilter || typeFilter === 'all') return playlists;

                return playlists.filter(function (list) {
                    const listType = list.Type || 'Playlist'; // Default to Playlist for backward compatibility
                    return listType === typeFilter;
                });
            }
        },
        mediaType: {
            selector: '#mediaTypeFilter',
            defaultValue: 'all',
            getValue: function (element) {
                return element ? element.value : 'all';
            },
            filterFn: function (playlists, mediaTypeFilter) {
                if (!mediaTypeFilter || mediaTypeFilter === 'all') return playlists;

                return playlists.filter(function (playlist) {
                    const mediaTypes = playlist.MediaTypes || [];
                    return mediaTypes.indexOf(mediaTypeFilter) !== -1;
                });
            }
        },
        user: {
            selector: '#userFilter',
            defaultValue: 'all',
            getValue: function (element) {
                return element ? element.value : 'all';
            },
            filterFn: function (playlists, userFilter) {
                if (!userFilter || userFilter === 'all') return playlists;

                const normalizedFilter = normalizeUserId(userFilter);

                return playlists.filter(function (playlist) {
                    // User filter applies to both playlists (owner) and collections (rule context user)
                    // Check UserPlaylists array (multi-user playlists)
                    if (playlist.UserPlaylists && playlist.UserPlaylists.length > 0) {
                        const hasUserInPlaylists = playlist.UserPlaylists.some(function(mapping) {
                            return normalizeUserId(mapping.UserId) === normalizedFilter;
                        });
                        if (hasUserInPlaylists) return true;
                    }
                    // Check CreatedByUserId (creator/owner)
                    if (playlist.CreatedByUserId && normalizeUserId(playlist.CreatedByUserId) === normalizedFilter) {
                        return true;
                    }
                    // Fallback to UserId (backwards compatibility)
                    const normalizedPlaylistUserId = normalizeUserId(playlist.UserId);
                    return normalizedPlaylistUserId === normalizedFilter;
                });
            }
        },
        sort: {
            selector: '#playlistSortSelect',
            defaultValue: 'name-asc',
            getValue: function (element) {
                return element ? element.value : 'name-asc';
            }
            // Note: sorting is handled by sortPlaylists function, not as a filter
        }
    };

    SmartLists.filterPlaylists = function (playlists, searchTerm, page) {
        if (!searchTerm) return playlists;

        return playlists.filter(function (playlist) {
            // Search in playlist name
            if (playlist.Name && playlist.Name.toLowerCase().indexOf(searchTerm) !== -1) {
                return true;
            }

            // Search in ID
            if (playlist.Id && playlist.Id.toLowerCase().indexOf(searchTerm) !== -1) {
                return true;
            }

            // Search in filename
            if (playlist.FileName && playlist.FileName.toLowerCase().indexOf(searchTerm) !== -1) {
                return true;
            }

            // Search in list type
            if (playlist.Type && playlist.Type.toLowerCase().indexOf(searchTerm) !== -1) {
                return true;
            }

            // Search in media types
            if (playlist.MediaTypes && playlist.MediaTypes.some(function (type) {
                return type.toLowerCase().indexOf(searchTerm) !== -1;
            })) {
                return true;
            }

            // Search in rules (field names, operators, and values)
            if (playlist.ExpressionSets) {
                for (var i = 0; i < playlist.ExpressionSets.length; i++) {
                    const expressionSet = playlist.ExpressionSets[i];
                    if (expressionSet.Expressions) {
                        for (var j = 0; j < expressionSet.Expressions.length; j++) {
                            const expression = expressionSet.Expressions[j];
                            // Search in field name
                            if (expression.MemberName && expression.MemberName.toLowerCase().indexOf(searchTerm) !== -1) {
                                return true;
                            }

                            // Search in operator
                            if (expression.Operator && expression.Operator.toLowerCase().indexOf(searchTerm) !== -1) {
                                return true;
                            }

                            // Search in target value (coerce to string to handle numbers/booleans)
                            // Check for null/undefined explicitly to allow searching for 0 or false
                            if (expression.TargetValue != null && String(expression.TargetValue).toLowerCase().indexOf(searchTerm) !== -1) {
                                return true;
                            }
                        }
                    }
                }
            }

            // Search in sort order (both legacy and new multi-sort formats)
            if (playlist.Order && playlist.Order.Name && playlist.Order.Name.toLowerCase().indexOf(searchTerm) !== -1) {
                return true;
            }
            if (playlist.Order && Array.isArray(playlist.Order.SortOptions)) {
                const sortText = playlist.Order.SortOptions
                    .map(function (o) {
                        return ((o.SortBy || '') + ' ' + (o.SortOrder || '')).toLowerCase();
                    })
                    .join(' ');
                if (sortText.indexOf(searchTerm) !== -1) {
                    return true;
                }
            }

            // Search in public/private status
            if (searchTerm === 'public' && playlist.Public) {
                return true;
            }
            if (searchTerm === 'private' && !playlist.Public) {
                return true;
            }

            // Search in enabled/disabled status
            if (searchTerm === 'enabled' && playlist.Enabled !== false) {
                return true;
            }
            if (searchTerm === 'disabled' && playlist.Enabled === false) {
                return true;
            }

            // Search in auto-refresh mode
            if (playlist.AutoRefresh && playlist.AutoRefresh.toLowerCase().indexOf(searchTerm) !== -1) {
                return true;
            }
            // Search for "never" when AutoRefresh is not set or is default
            if (searchTerm === 'never' && (!playlist.AutoRefresh || playlist.AutoRefresh === 'Never')) {
                return true;
            }

            // Search in MaxItems (numeric search)
            if (playlist.MaxItems != null && playlist.MaxItems !== 0 && String(playlist.MaxItems).indexOf(searchTerm) !== -1) {
                return true;
            }
            // Search for "unlimited" when MaxItems is not set or is 0
            if ((searchTerm === 'unlimited' || searchTerm === 'none') && (playlist.MaxItems === undefined || playlist.MaxItems === null || playlist.MaxItems === 0)) {
                return true;
            }

            // Search in MaxPlayTimeMinutes (numeric search)
            if (playlist.MaxPlayTimeMinutes != null && playlist.MaxPlayTimeMinutes !== 0 && String(playlist.MaxPlayTimeMinutes).indexOf(searchTerm) !== -1) {
                return true;
            }
            // Search for "unlimited" when MaxPlayTimeMinutes is not set or is 0
            if ((searchTerm === 'unlimited' || searchTerm === 'none') && (playlist.MaxPlayTimeMinutes === undefined || playlist.MaxPlayTimeMinutes === null || playlist.MaxPlayTimeMinutes === 0)) {
                return true;
            }

            // Search in ItemCount statistics (numeric search)
            if (playlist.ItemCount != null && String(playlist.ItemCount).indexOf(searchTerm) !== -1) {
                return true;
            }

            // Search in TotalRuntimeMinutes statistics (numeric search)
            if (playlist.TotalRuntimeMinutes != null && String(playlist.TotalRuntimeMinutes).indexOf(searchTerm) !== -1) {
                return true;
            }

            // Search in similarity comparison fields
            if (playlist.SimilarityComparisonFields && playlist.SimilarityComparisonFields.some(function (field) {
                return field && field.toLowerCase().indexOf(searchTerm) !== -1;
            })) {
                return true;
            }

            // Search in schedules (both Schedules and VisibilitySchedules)
            const allSchedules = (playlist.Schedules || []).concat(playlist.VisibilitySchedules || []);
            if (allSchedules.length > 0) {
                for (var s = 0; s < allSchedules.length; s++) {
                    const schedule = allSchedules[s];
                    // Search in trigger type
                    if (schedule.Trigger && schedule.Trigger.toLowerCase().indexOf(searchTerm) !== -1) {
                        return true;
                    }
                    // Search in action (Enable/Disable for visibility schedules)
                    if (schedule.Action && schedule.Action.toLowerCase().indexOf(searchTerm) !== -1) {
                        return true;
                    }
                    // Search for day names (convert DayOfWeek number to name)
                    if (schedule.DayOfWeek != null) {
                        const dayNames = ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'];
                        if (dayNames[schedule.DayOfWeek].indexOf(searchTerm) !== -1) {
                            return true;
                        }
                    }
                    // Search for month names
                    if (schedule.Month != null) {
                        const monthNames = ['january', 'february', 'march', 'april', 'may', 'june', 
                                          'july', 'august', 'september', 'october', 'november', 'december'];
                        if (monthNames[schedule.Month - 1] && monthNames[schedule.Month - 1].indexOf(searchTerm) !== -1) {
                            return true;
                        }
                    }
                }
            } else {
                // Search for "none" or "unscheduled" when there are no schedules
                if (searchTerm === 'none' || searchTerm === 'unscheduled' || searchTerm === 'noscheduled' || searchTerm === 'manual') {
                    return true;
                }
            }

            // Search in username (resolved from User ID or UserPlaylists)
            if (page && page._usernameCache) {
                // Check UserPlaylists array (multi-user playlists)
                if (playlist.UserPlaylists && playlist.UserPlaylists.length > 0) {
                    for (let j = 0; j < playlist.UserPlaylists.length; j++) {
                        const username = page._usernameCache.get(normalizeUserId(playlist.UserPlaylists[j].UserId));
                        if (username && username.toLowerCase().indexOf(searchTerm) !== -1) {
                            return true;
                        }
                    }
                }
                // Fallback to UserId (backwards compatibility)
                if (playlist.UserId) {
                    const username = page._usernameCache.get(normalizeUserId(playlist.UserId));
                    if (username && username.toLowerCase().indexOf(searchTerm) !== -1) {
                        return true;
                    }
                }
                // Search in CreatedBy username
                if (playlist.CreatedByUserId) {
                    const createdByUsername = page._usernameCache.get(normalizeUserId(playlist.CreatedByUserId));
                    if (createdByUsername && createdByUsername.toLowerCase().indexOf(searchTerm) !== -1) {
                        return true;
                    }
                }
            }

            return false;
        });
    };

    // Generic DOM query helper - eliminates repetitive querySelector patterns
    SmartLists.getFilterValue = function (page, filterKey) {
        const config = SmartLists.PLAYLIST_FILTER_CONFIGS[filterKey];
        if (!config) {
            return '';
        }

        const element = page.querySelector(config.selector);
        return config.getValue(element);
    };

    // Generic filter application function - replaces all individual filter functions
    SmartLists.applyFilter = function (playlists, filterKey, filterValue, page) {
        const config = SmartLists.PLAYLIST_FILTER_CONFIGS[filterKey];
        if (!config || !config.filterFn) return playlists;

        return config.filterFn(playlists, filterValue, page);
    };

    // Initialize page-level AbortController for better event listener management
    SmartLists.initializePageEventListeners = function (page) {
        // Create page-level AbortController if it doesn't exist
        if (!page._pageAbortController) {
            page._pageAbortController = SmartLists.createAbortController();
        }
        return page._pageAbortController ? page._pageAbortController.signal : null;
    };

    // Generic event listener setup - eliminates repetitive filter change handlers
    SmartLists.setupFilterEventListeners = function (page, pageSignal) {
        pageSignal = pageSignal || SmartLists.initializePageEventListeners(page);
        const filterKeys = ['sort', 'type', 'mediaType', 'user'];

        filterKeys.forEach(function (filterKey) {
            const config = SmartLists.PLAYLIST_FILTER_CONFIGS[filterKey];
            if (!config) return;

            const element = page.querySelector(config.selector);
            if (element) {
                element.addEventListener('change', function () {
                    SmartLists.savePlaylistFilterPreferences(page);
                    SmartLists.applySearchFilter(page).catch(function (err) {
                        console.error('Error during ' + filterKey + ' filter:', err);
                        SmartLists.showNotification('Filter error: ' + err.message);
                    });
                }, SmartLists.getEventListenerOptions(pageSignal));
            }
        });
    };

    SmartLists.clearAllFilters = function (page) {
        // Clear search
        const searchInput = page.querySelector('#playlistSearchInput');
        if (searchInput) {
            searchInput.value = '';
        }

        // Reset filters to default
        const typeFilter = page.querySelector('#typeFilter');
        if (typeFilter) {
            typeFilter.value = 'all';
        }

        const mediaTypeFilter = page.querySelector('#mediaTypeFilter');
        if (mediaTypeFilter) {
            mediaTypeFilter.value = 'all';
        }


        const userFilter = page.querySelector('#userFilter');
        if (userFilter) {
            userFilter.value = 'all';
        }

        // Reset sort to default
        const sortSelect = page.querySelector('#playlistSortSelect');
        if (sortSelect) {
            sortSelect.value = 'name-asc';
        }

        // Save preferences
        SmartLists.savePlaylistFilterPreferences(page);

        // Apply filters
        SmartLists.applySearchFilter(page).catch(function (err) {
            console.error('Error during clear filters:', err);
            SmartLists.showNotification('Filter error: ' + err.message);
        });

        // Update clear button visibility
        const clearSearchBtn = page.querySelector('#clearSearchBtn');
        if (clearSearchBtn) {
            clearSearchBtn.style.display = 'none';
        }
    };

    // Enhanced preferences system with validation and error recovery
    SmartLists.savePlaylistFilterPreferences = function (page) {
        try {
            const preferences = {};

            // Get preferences for all filters except search (session-specific)
            const persistentFilters = ['sort', 'type', 'mediaType', 'user'];

            for (var i = 0; i < persistentFilters.length; i++) {
                const filterKey = persistentFilters[i];
                const config = SmartLists.PLAYLIST_FILTER_CONFIGS[filterKey];
                if (config) {
                    const element = page.querySelector(config.selector);
                    if (element) {
                        const value = config.getValue(element);
                        // Only save non-default values to reduce storage
                        if (value !== config.defaultValue) {
                            preferences[filterKey] = value;
                        }
                    }
                }
            }

            localStorage.setItem('smartListsFilterPreferences', JSON.stringify(preferences));
            console.debug('Saved filter preferences:', preferences);
        } catch (err) {
            console.warn('Failed to save playlist filter preferences:', err);
        }
    };

    SmartLists.loadPlaylistFilterPreferences = function (page) {
        try {
            const saved = localStorage.getItem('smartListsFilterPreferences');
            if (!saved) {
                console.debug('No saved filter preferences found, using defaults');
                return;
            }

            const preferences = JSON.parse(saved);
            console.debug('Loading filter preferences:', preferences);

            // Apply saved preferences using the generic system with validation
            Object.keys(preferences).forEach(function (filterKey) {
                const value = preferences[filterKey];
                const config = SmartLists.PLAYLIST_FILTER_CONFIGS[filterKey];
                if (config && value !== undefined) {
                    const element = page.querySelector(config.selector);
                    if (element) {
                        // Validate that the saved value is still valid for this element
                        const options = Array.prototype.slice.call(element.options || []);
                        
                        // Special handling for user filter: if it only has the "all" option,
                        // skip validation as the user options haven't been loaded yet
                        // (they will be loaded asynchronously by populateUserFilter)
                        if (filterKey === 'user' && options.length === 1 && options[0].value === 'all') {
                            console.debug('Skipping user filter validation - options not yet loaded');
                            return; // Skip this filter, will be restored after populateUserFilter
                        }
                        
                        const isValidOption = options.length === 0 || options.some(function (opt) {
                            return opt.value === value;
                        });

                        if (isValidOption) {
                            element.value = value;
                            console.debug('Restored ' + filterKey + ' filter to:', value);
                        } else {
                            console.warn('Invalid saved value for ' + filterKey + ':', value, 'Available options:', options.map(function (o) {
                                return o.value;
                            }));
                            // Fall back to default value
                            element.value = config.defaultValue;
                        }
                    } else {
                        console.warn('Filter element not found for ' + filterKey + ':', config.selector);
                    }
                } else {
                    console.warn('Invalid filter configuration for ' + filterKey);
                }
            });

            // Ensure all filters have valid values, even if not in saved preferences
            const persistentFilters = ['sort', 'type', 'mediaType', 'user'];
            persistentFilters.forEach(function (filterKey) {
                if (!preferences.hasOwnProperty(filterKey)) {
                    const config = SmartLists.PLAYLIST_FILTER_CONFIGS[filterKey];
                    if (config) {
                        const element = page.querySelector(config.selector);
                        if (element && !element.value) {
                            element.value = config.defaultValue;
                            console.debug('Set default value for ' + filterKey + ':', config.defaultValue);
                        }
                    }
                }
            });

        } catch (err) {
            console.warn('Failed to load playlist filter preferences:', err);
            // Reset to defaults on error
            SmartLists.resetFiltersToDefaults(page);
        }
    };

    SmartLists.resetFiltersToDefaults = function (page) {
        try {
            const persistentFilters = ['sort', 'type', 'mediaType', 'user'];
            persistentFilters.forEach(function (filterKey) {
                const config = SmartLists.PLAYLIST_FILTER_CONFIGS[filterKey];
                if (config) {
                    const element = page.querySelector(config.selector);
                    if (element) {
                        element.value = config.defaultValue;
                    }
                }
            });
            console.debug('Reset all filters to defaults');
        } catch (err) {
            console.error('Failed to reset filters to defaults:', err);
        }
    };

    SmartLists.populateUserFilter = async function (page, playlists) {
        const userFilter = page.querySelector('#userFilter');
        if (!userFilter || !playlists) return Promise.resolve();

        try {
            // Ensure playlists is an array
            if (!Array.isArray(playlists)) {
                console.warn('Playlists is not an array:', typeof playlists, playlists);
                return Promise.resolve();
            }

            // Initialize username cache with size limit if it doesn't exist
            if (!page._usernameCache) {
                page._usernameCache = new Map();
                page._usernameCacheMaxSize = 100; // Limit cache size
            }

            // Clear cache if it gets too large (simple LRU-like behavior)
            if (page._usernameCache.size > page._usernameCacheMaxSize) {
                console.log('Username cache size exceeded, clearing old entries');
                const entries = Array.from(page._usernameCache.entries());
                // Keep only the last 50% of entries (rough LRU approximation)
                page._usernameCache.clear();
                const keepCount = Math.floor(page._usernameCacheMaxSize / 2);
                entries.slice(-keepCount).forEach(function (entry) {
                    page._usernameCache.set(entry[0], entry[1]);
                });
            }

            // Get unique user IDs from playlists
            // Normalize IDs to prevent duplicates when both dashed and non-dashed formats exist
            const userIds = [];
            const seenIds = {};

            for (var i = 0; i < playlists.length; i++) {
                const playlist = playlists[i];
                // Check UserPlaylists array (multi-user playlists)
                if (playlist.UserPlaylists && playlist.UserPlaylists.length > 0) {
                    for (var j = 0; j < playlist.UserPlaylists.length; j++) {
                        const userId = playlist.UserPlaylists[j].UserId;
                        if (userId) {
                            const normalizedId = normalizeUserId(userId);
                            if (!seenIds[normalizedId]) {
                                userIds.push(userId);
                                seenIds[normalizedId] = true;
                            }
                        }
                    }
                } else if (playlist.UserId) {
                    // Fallback to UserId (backwards compatibility)
                    const userId = playlist.UserId;
                    const normalizedId = normalizeUserId(userId);
                    if (!seenIds[normalizedId]) {
                        userIds.push(userId);
                        seenIds[normalizedId] = true;
                    }
                }
                // Also collect CreatedByUserId for username cache
                if (playlist.CreatedByUserId) {
                    const userId = playlist.CreatedByUserId;
                    const normalizedId = normalizeUserId(userId);
                    if (!seenIds[normalizedId]) {
                        userIds.push(userId);
                        seenIds[normalizedId] = true;
                    }
                }
            }

            // Clear existing options except "All Users"
            const allUsersOption = userFilter.querySelector('option[value="all"]');
            userFilter.innerHTML = '';
            if (allUsersOption) {
                userFilter.appendChild(allUsersOption);
            } else {
                const defaultOption = document.createElement('option');
                defaultOption.value = 'all';
                defaultOption.textContent = 'All Users';
                userFilter.appendChild(defaultOption);
            }

            // Fetch user names and populate dropdown
            const apiClient = SmartLists.getApiClient();
            const promises = [];

            for (var j = 0; j < userIds.length; j++) {
                const userId = userIds[j];
                promises.push(
                    SmartLists.resolveUserIdToName(apiClient, userId).then(function (userName) {
                        if (userName) {
                            const option = document.createElement('option');
                            option.value = userId;
                            option.textContent = userName;
                            userFilter.appendChild(option);
                            // Cache the username (store both raw and normalized for robustness)
                            page._usernameCache.set(userId, userName);
                            page._usernameCache.set(normalizeUserId(userId), userName);
                        }
                    }).catch(function (err) {
                        console.error('Error resolving user ID ' + userId + ':', err);
                        // Add option with fallback name
                        const option = document.createElement('option');
                        option.value = userId;
                        option.textContent = 'Unknown User';
                        userFilter.appendChild(option);
                        // Cache the username (store both raw and normalized for robustness)
                        page._usernameCache.set(userId, 'Unknown User');
                        page._usernameCache.set(normalizeUserId(userId), 'Unknown User');
                    })
                );
            }

            await Promise.all(promises);
            // Restore user filter preference now that options are populated
            // This fixes the issue where the preference is loaded before options exist and gets rejected
            if (SmartLists.loadPlaylistFilterPreferences) {
                SmartLists.loadPlaylistFilterPreferences(page);
            }
        } catch (err) {
            console.error('Error populating user filter:', err);
            return Promise.resolve();
        }
    };

    SmartLists.applySearchFilter = function (page) {
        const searchInput = page.querySelector('#playlistSearchInput');
        if (!searchInput || !page._allPlaylists) {
            return Promise.resolve();
        }

        // Don't search while loading playlists
        if (page._loadingPlaylists) {
            return Promise.resolve();
        }

        // Read the search input value
        const searchTerm = searchInput.value.trim();

        // Apply all filters and sorting
        const filteredPlaylists = SmartLists.applyAllFiltersAndSort(page, page._allPlaylists);

        // Display the filtered results with the actual search term
        return SmartLists.displayFilteredPlaylists(page, filteredPlaylists, searchTerm);
    };

    SmartLists.displayFilteredPlaylists = async function (page, filteredPlaylists, searchTerm) {
        const container = page.querySelector('#playlist-list-container');
        const apiClient = SmartLists.getApiClient();

        // Calculate summary statistics for filtered results
        const totalPlaylists = page._allPlaylists.length;
        const filteredCount = filteredPlaylists.length;
        // Count playlists and collections from the total unfiltered list
        const playlistCount = page._allPlaylists.filter(function (p) { return p.Type === 'Playlist' || !p.Type; }).length;
        const collectionCount = page._allPlaylists.filter(function (p) { return p.Type === 'Collection'; }).length;

        let html = '';

        // Add bulk actions container after summary
        let summaryText;
        summaryText = SmartLists.generateSummaryText(totalPlaylists, playlistCount, collectionCount, filteredCount, searchTerm);
        html += SmartLists.generateBulkActionsHTML(summaryText);

        // Process filtered playlists using the helper function
        const promises = [];
        for (var i = 0; i < filteredPlaylists.length; i++) {
            const playlist = filteredPlaylists[i];
            promises.push(
                SmartLists.resolveUsername(apiClient, playlist).then(function (resolvedUserName) {
                    return SmartLists.generateRulesHtml(playlist, apiClient).then(function (rulesHtml) {
                        // Resolve CreatedBy username
                        const createdByPromise = playlist.CreatedByUserId
                            ? SmartLists.resolveUserIdToName(apiClient, playlist.CreatedByUserId)
                            : Promise.resolve('Unknown');
                        
                        return createdByPromise.then(function (createdByUserName) {
                            return {
                                playlist: playlist,
                                rulesHtml: rulesHtml,
                                resolvedUserName: resolvedUserName,
                                createdByUserName: createdByUserName || 'Unknown'
                            };
                        });
                    });
                })
            );
        }

        const results = await Promise.all(promises);
        for (var j = 0; j < results.length; j++) {
            const result = results[j];
            html += SmartLists.generatePlaylistCardHtml(result.playlist, result.rulesHtml, result.resolvedUserName, result.createdByUserName);
        }
        container.innerHTML = html;
        // Restore expand states from localStorage after regenerating HTML
        if (SmartLists.restorePlaylistExpandStates) {
            SmartLists.restorePlaylistExpandStates(page);
        }
        // Update expand all button text based on current states
        if (SmartLists.updateExpandAllButtonText) {
            SmartLists.updateExpandAllButtonText(page);
        }
        // Update bulk actions visibility and state
        if (SmartLists.updateBulkActionsVisibility) {
            SmartLists.updateBulkActionsVisibility(page);
        }
    };

    // Note: getPeopleFieldDisplayName is defined in config-formatters.js to avoid duplication

})(window.SmartLists = window.SmartLists || {});

