(function (SmartLists) {
    'use strict';

    // Initialize namespace if it doesn't exist
    if (!SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }

    // Cache for user ID to name lookups
    var userNameCache = new Map();

    // Helper function to normalize user IDs for consistent cache keys
    // Removes dashes and converts to lowercase for GUID comparison
    var normalizeUserId = function (userId) {
        if (!userId || typeof userId !== 'string') {
            return '';
        }
        return userId.replace(/-/g, '').toLowerCase();
    };

    // ===== USER MANAGEMENT =====
    // Note: loadUsers, loadUsersForRule, and setCurrentUserAsDefault are defined in config-api.js to avoid duplication

    SmartLists.resolveUsername = function (apiClient, playlist) {
        if (!playlist) {
            return Promise.resolve('Unknown User');
        }

        // Check for multi-user playlists (UserPlaylists array)
        if (playlist.UserPlaylists && playlist.UserPlaylists.length > 0) {
            // Resolve all user IDs to names
            const userIds = playlist.UserPlaylists.map(function (up) { return up.UserId; });
            const namePromises = userIds.map(function (userId) {
                return SmartLists.resolveUserIdToName(apiClient, userId).then(function (name) {
                    return name || 'Unknown User';
                });
            });

            return Promise.all(namePromises).then(function (names) {
                // Return comma-separated names without count
                if (names.length > 0) {
                    return names.join(', ');
                } else {
                    return 'Unknown User';
                }
            }).catch(function () {
                return 'Unknown User';
            });
        }

        // Fallback to single UserId (backwards compatibility)
        const userId = playlist.UserId;  // User field contains the user ID (as string)
        if (userId && userId !== '' && userId !== '00000000-0000-0000-0000-000000000000') {
            return SmartLists.resolveUserIdToName(apiClient, userId).then(function (name) {
                return name || 'Unknown User';
            });
        }
        return Promise.resolve('Unknown User');
    };

    SmartLists.resolveUserIdToName = function (apiClient, userId) {
        if (!userId || userId === '' || userId === '00000000-0000-0000-0000-000000000000') {
            return Promise.resolve(null);
        }

        // Normalize userId for cache lookup
        const normalizedId = normalizeUserId(userId);

        // Check cache first
        if (userNameCache.has(normalizedId)) {
            const cachedName = userNameCache.get(normalizedId);
            return Promise.resolve(cachedName);
        }

        // For user pages, don't call admin-only /users endpoint
        // Just return a placeholder since users don't need to see other users' names
        if (SmartLists.IS_USER_PAGE) {
            const fallback = 'My Playlists';
            userNameCache.set(normalizedId, fallback);
            return Promise.resolve(fallback);
        }

        // Load all users and build cache if not already loaded
        return apiClient.ajax({
            type: 'GET',
            url: apiClient.getUrl(SmartLists.ENDPOINTS.users),
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status + ': ' + response.statusText);
            }
            return response.json();
        }).then(function (users) {
            // Build cache from all users
            if (Array.isArray(users)) {
                users.forEach(function (user) {
                    if (user.Id && user.Name) {
                        const normalizedUserId = normalizeUserId(user.Id);
                        userNameCache.set(normalizedUserId, user.Name);
                    }
                });
            }

            // Return the requested user's name or fallback
            const resolvedName = userNameCache.get(normalizedId);
            if (!resolvedName) {
                // User not found, cache the fallback to avoid repeated lookups
                const fallback = 'Unknown User';
                userNameCache.set(normalizedId, fallback);
                return fallback;
            }
            return resolvedName;
        }).catch(function (err) {
            console.error('Error loading users for name resolution:', err);
            const fallback = 'Unknown User';

            // Cache the fallback with normalized ID to avoid repeated failed lookups
            userNameCache.set(normalizedId, fallback);
            return fallback;
        });
    };

    // ===== PLAYLIST CRUD OPERATIONS =====
    SmartLists.createPlaylist = function (page) {
        // Get edit state to determine if we're creating or updating
        const editState = SmartLists.getPageEditState(page);

        // Only scroll to top when creating new playlist (not when updating existing)
        if (!editState.editMode) {
            window.scrollTo({ top: 0, behavior: 'smooth' });
        }

        try {
            const apiClient = SmartLists.getApiClient();
            const playlistName = SmartLists.getElementValue(page, '#playlistName');

            // Get list type to provide appropriate error messages
            const listType = SmartLists.getElementValue(page, '#listType', 'Playlist');
            const isCollection = listType === 'Collection';
            const listTypeName = isCollection ? 'Collection' : 'Playlist';

            if (!playlistName) {
                SmartLists.showNotification(listTypeName + ' name is required.');
                return;
            }

            // Get selected media types early to gate series-only flags
            const selectedMediaTypes = SmartLists.getSelectedMediaTypes(page);
            if (selectedMediaTypes.length === 0) {
                SmartLists.showNotification('At least one media type must be selected.');
                return;
            }

            // Collect rules from form using helper function
            const expressionSets = SmartLists.collectRulesFromForm(page);

            // Collect sorting options from the new sort boxes
            const sortOptions = SmartLists.collectSortsFromForm(page);

            const isPublic = SmartLists.getElementChecked(page, '#playlistIsPublic', false);
            const isEnabled = SmartLists.getElementChecked(page, '#playlistIsEnabled', true); // Default to true
            const autoRefreshMode = SmartLists.getElementValue(page, '#autoRefreshMode', 'Never');

            // Collect schedules from the new schedule boxes
            const schedules = SmartLists.collectSchedulesFromForm(page);

            // Collect visibility schedules from the new visibility schedule boxes
            const visibilitySchedules = SmartLists.collectVisibilitySchedulesFromForm(page);

            // Handle maxItems with validation using helper function
            // Empty string means no limit (0), consistent with UI text "Set to 0 for no limit"
            const maxItemsInput = SmartLists.getElementValue(page, '#playlistMaxItems');
            let maxItems;
            if (maxItemsInput === '') {
                maxItems = 0; // Empty = no limit
            } else {
                const parsedValue = parseInt(maxItemsInput, 10);
                maxItems = (isNaN(parsedValue) || parsedValue < 0) ? 0 : parsedValue;
            }

            // Handle maxPlayTimeMinutes with helper function
            const maxPlayTimeMinutesInput = SmartLists.getElementValue(page, '#playlistMaxPlayTimeMinutes');
            let maxPlayTimeMinutes;
            if (maxPlayTimeMinutesInput === '') {
                maxPlayTimeMinutes = 0;
            } else {
                const parsedValue = parseInt(maxPlayTimeMinutesInput, 10);
                maxPlayTimeMinutes = (isNaN(parsedValue) || parsedValue < 0) ? 0 : parsedValue;
            }

            // Get selected user ID(s) - collections use single select, playlists use multi-select
            let userIds;
            if (SmartLists.IS_USER_PAGE) {
                // User page: always use the current logged-in user
                const apiClient = SmartLists.getApiClient();
                const currentUserId = apiClient.getCurrentUserId();
                // Normalize user ID (remove hyphens and lowercase)
                userIds = currentUserId ? [normalizeUserId(currentUserId)] : [];
            } else if (isCollection) {
                // Admin page - Collections: single user
                const userId = SmartLists.getElementValue(page, '#playlistUser');
                // Normalize user ID (remove hyphens and lowercase)
                userIds = userId ? [normalizeUserId(userId)] : [];
            } else {
                // Admin page - Playlists: potentially multiple users
                const selectedIds = SmartLists.getSelectedUserIds ? SmartLists.getSelectedUserIds(page) : [];
                // Normalize all user IDs (remove hyphens and lowercase)
                userIds = selectedIds.map(function(id) { return normalizeUserId(id); });
            }

            if (!userIds || userIds.length === 0) {
                SmartLists.showNotification('Please select at least one ' + (isCollection ? 'collection user' : 'playlist user') + '.');
                return;
            }

            // Collections are server-wide and don't have library assignments

            // Collect similarity comparison fields from SimilarTo rules
            let similarityComparisonFields = null;
            const allRules = page.querySelectorAll('.rule-row');
            for (var i = 0; i < allRules.length; i++) {
                const ruleRow = allRules[i];
                const fieldSelect = ruleRow.querySelector('.rule-field-select');
                if (fieldSelect && fieldSelect.value === 'SimilarTo') {
                    const fields = SmartLists.getSimilarityComparisonFields(ruleRow);
                    if (fields && fields.length > 0) {
                        similarityComparisonFields = fields;
                        break; // Use the first SimilarTo rule's settings for the entire playlist
                    }
                }
            }

            const playlistDto = {
                Type: listType,
                Name: playlistName,
                ExpressionSets: expressionSets,
                Order: { SortOptions: sortOptions },
                Enabled: isEnabled,
                MediaTypes: selectedMediaTypes,
                MaxItems: maxItems,
                MaxPlayTimeMinutes: maxPlayTimeMinutes,
                AutoRefresh: autoRefreshMode,
                Schedules: schedules.length > 0 ? schedules : [],
                VisibilitySchedules: visibilitySchedules.length > 0 ? visibilitySchedules : []
            };

            // Add type-specific fields
            if (isCollection) {
                // Collections: single UserId
                playlistDto.UserId = userIds[0];
                // Collections are server-wide, no library assignment needed
            } else {
                // Playlists: send UserPlaylists array structure
                playlistDto.UserPlaylists = userIds.map(function (userId) {
                    return {
                        UserId: userId,
                        JellyfinPlaylistId: null  // Backend will populate on creation
                    };
                });
                // Only set Public for single-user playlists (multi-user playlists are always private)
                playlistDto.Public = userIds.length === 1 ? isPublic : false;
            }

            // Add similarity comparison fields if specified
            if (similarityComparisonFields) {
                playlistDto.SimilarityComparisonFields = similarityComparisonFields;
            }

            // Add ID if in edit mode (reuse editState from top of function)
            if (editState.editMode && editState.editingPlaylistId) {
                playlistDto.Id = editState.editingPlaylistId;
                // Preserve CreatedByUserId from the existing list when updating
                if (page._editingPlaylistCreatedByUserId) {
                    playlistDto.CreatedByUserId = page._editingPlaylistCreatedByUserId;
                }
            } else {
                // Only set CreatedByUserId when creating a new list (not when updating)
                const currentUserId = apiClient.getCurrentUserId();
                if (currentUserId) {
                    // Normalize user ID by removing hyphens (consistent with UserIds format)
                    playlistDto.CreatedByUserId = currentUserId.replace(/-/g, '');
                }
            }

            const requestType = editState.editMode ? 'PUT' : 'POST';
            const url = editState.editMode ?
                apiClient.getUrl(SmartLists.ENDPOINTS.base + '/' + editState.editingPlaylistId) :
                apiClient.getUrl(SmartLists.ENDPOINTS.base);

            // Store editingPlaylistId for error recovery
            const editingPlaylistId = editState.editMode ? editState.editingPlaylistId : null;

            // Make API call - wait for response before updating UI
            apiClient.ajax({
                type: requestType,
                url: url,
                data: JSON.stringify(playlistDto),
                contentType: 'application/json'
            }).then(function (response) {
                if (!response.ok) {
                    // Try to parse error message from response
                    return response.text().then(function (errorText) {
                        var errorMessage;
                        try {
                            var parsed = JSON.parse(errorText);
                            // Extract string from parsed object if necessary
                            if (parsed && typeof parsed === 'object') {
                                errorMessage = parsed.message || parsed.error || JSON.stringify(parsed);
                            } else if (typeof parsed === 'string') {
                                errorMessage = parsed;
                            } else {
                                errorMessage = String(parsed);
                            }
                        } catch (e) {
                            errorMessage = errorText || 'Unknown error occurred';
                        }
                        throw new Error(errorMessage);
                    });
                }

                // Success - show success notification first
                let message;
                if (editState.editMode) {
                    message = listTypeName + ' "' + playlistName + '" updated successfully.';
                } else {
                    message = listTypeName + ' "' + playlistName + '" created successfully.';
                }
                SmartLists.showNotification(message, 'success');

                // Only show refresh notification if the list is enabled
                if (isEnabled) {
                    // Then show notification that refresh has started (refresh happens automatically on backend)
                    SmartLists.notifyRefreshQueued(listTypeName, playlistName);
                }

                // Exit edit mode and redirect after successful API call
                if (editState.editMode) {
                    // Exit edit mode silently without showing cancellation message
                    SmartLists.setPageEditState(page, false, null);
                    const editIndicator = page.querySelector('#edit-mode-indicator');
                    if (editIndicator) {
                        editIndicator.style.display = 'none';
                    }
                    const submitBtn = page.querySelector('#submitBtn');
                    if (submitBtn) {
                        const currentListType = SmartLists.getElementValue(page, '#listType', 'Playlist');
                        submitBtn.textContent = 'Create ' + currentListType;
                    }

                    // Restore tab button text
                    const createTabButton = page.querySelector('a[data-tab="create"]');
                    if (createTabButton) {
                        createTabButton.textContent = 'Create List';
                    }

                    // Switch to Manage tab after successful update
                    SmartLists.switchToTab(page, 'manage');
                    window.scrollTo({ top: 0, behavior: 'auto' });
                }

                // Clear form after successful creation/update
                SmartLists.clearForm(page);

                // Reload list to show updated state
                if (SmartLists.loadPlaylistList) {
                    SmartLists.loadPlaylistList(page);
                }
            }).catch(function (err) {
                console.error('Error creating ' + listTypeName.toLowerCase() + ':', err);
                const action = editState.editMode ? 'update' : 'create';

                // For UPDATE operations: restore edit mode by reloading playlist from server
                if (editState.editMode && editingPlaylistId) {
                    // Reload the playlist to restore form state
                    if (SmartLists.editPlaylist) {
                        SmartLists.editPlaylist(page, editingPlaylistId);
                    }
                }
                // For CREATE operations: form remains populated, user can fix and retry
                // Stay on Create tab (already there)

                // Show error notification
                SmartLists.handleApiError(err, 'Failed to ' + action + ' ' + listTypeName.toLowerCase() + ' ' + playlistName);
            });
        } catch (e) {
            console.error('A synchronous error occurred in createPlaylist:', e);
            SmartLists.showNotification('A critical client-side error occurred: ' + e.message);
        }
    };

    SmartLists.clearForm = function (page) {
        // Only handle form clearing - edit mode management should be done by caller

        // Clear stored edit state values
        delete page._editingPlaylistCreatedByUserId;

        SmartLists.setElementValue(page, '#playlistName', '');

        // Clean up all existing event listeners before clearing rules
        const rulesContainer = page.querySelector('#rules-container');
        if (rulesContainer) {
            const allRules = rulesContainer.querySelectorAll('.rule-row');
            allRules.forEach(function (rule) {
                SmartLists.cleanupRuleEventListeners(rule);
            });

            rulesContainer.innerHTML = '';
        }

        // Clear media type selections
        SmartLists.setSelectedItems(page, 'mediaTypesMultiSelect', [], 'media-type-multi-select-checkbox', 'Select media types...');

        // Apply all form defaults using shared helper (DRY)
        // Skip loading config on user pages (admin-only endpoint)
        if (!SmartLists.IS_USER_PAGE) {
            const apiClient = SmartLists.getApiClient();
            apiClient.getPluginConfiguration(SmartLists.getPluginId()).then(function (config) {
                if (SmartLists.applyFormDefaults) {
                    SmartLists.applyFormDefaults(page, config);
                }
            }).catch(function () {
                if (SmartLists.applyFallbackDefaults) {
                    SmartLists.applyFallbackDefaults(page);
                }
            });
        } else {
            // User page: use fallback defaults
            if (SmartLists.applyFallbackDefaults) {
                SmartLists.applyFallbackDefaults(page);
            }
        }

        // Create initial logic group with one rule
        SmartLists.createInitialLogicGroup(page);

        // Update button visibility after initial group is created
        SmartLists.updateRuleButtonVisibility(page);
    };

    SmartLists.editPlaylist = function (page, playlistId) {
        const apiClient = SmartLists.getApiClient();
        Dashboard.showLoadingMsg();

        // Always scroll to top when entering edit mode (auto for instant behavior)
        window.scrollTo({ top: 0, behavior: 'auto' });

        apiClient.ajax({
            type: 'GET',
            url: apiClient.getUrl(SmartLists.ENDPOINTS.base + '/' + playlistId),
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status + ': ' + response.statusText);
            }
            return response.json();
        }).then(function (playlist) {
            Dashboard.hideLoadingMsg();

            if (!playlist) {
                SmartLists.showNotification('No playlist data received from server.');
                return;
            }

            try {
                // Store CreatedByUserId to preserve it during updates
                if (playlist.CreatedByUserId) {
                    page._editingPlaylistCreatedByUserId = playlist.CreatedByUserId;
                }

                // Determine list type
                const listType = playlist.Type || 'Playlist';
                const isCollection = listType === 'Collection';

                // Extract userIds BEFORE calling handleListTypeChange (which triggers loadUsers)
                // This ensures pendingUserIds is set before loadUsers checks for it
                let userIds = [];
                if (!isCollection) {
                    // Playlists can have multiple users
                    if (playlist.UserPlaylists && playlist.UserPlaylists.length > 0) {
                        userIds = playlist.UserPlaylists.map(function (up) { return up.UserId; });
                    } else if (playlist.UserId) {
                        userIds = [String(playlist.UserId)];
                    }
                    // Store userIds to set after users are loaded (loadUsers is async)
                    page._pendingUserIds = userIds;
                } else {
                    // Collections: store the user ID to prevent setCurrentUserAsDefault from overwriting
                    if (playlist.UserId) {
                        page._pendingCollectionUserId = String(playlist.UserId);
                    }
                }

                // Set list type
                SmartLists.setElementValue(page, '#listType', listType);

                // Trigger type change handler to show/hide fields
                SmartLists.handleListTypeChange(page);

                // Populate form with playlist data using helper functions
                SmartLists.setElementValue(page, '#playlistName', playlist.Name || '');

                // Only set public for playlists
                if (!isCollection) {
                    SmartLists.setElementChecked(page, '#playlistIsPublic', playlist.Public || false);
                }

                SmartLists.setElementChecked(page, '#playlistIsEnabled', playlist.Enabled !== false); // Default to true for backward compatibility

                // Handle AutoRefresh with backward compatibility
                const autoRefreshValue = playlist.AutoRefresh !== undefined ? playlist.AutoRefresh : 'Never';
                const autoRefreshElement = page.querySelector('#autoRefreshMode');
                if (autoRefreshElement) {
                    autoRefreshElement.value = autoRefreshValue;
                }

                // Handle schedule settings with backward compatibility
                SmartLists.loadSchedulesIntoUI(page, playlist);

                // Handle visibility schedule settings
                SmartLists.loadVisibilitySchedulesIntoUI(page, playlist);

                // Handle MaxItems with backward compatibility for existing playlists
                // Default to 0 (unlimited) for old playlists that didn't have this setting
                const maxItemsValue = (playlist.MaxItems !== undefined && playlist.MaxItems !== null) ? playlist.MaxItems : 0;
                const maxItemsElement = page.querySelector('#playlistMaxItems');
                if (maxItemsElement) {
                    maxItemsElement.value = maxItemsValue;
                } else {
                    console.warn('Max Items element not found when trying to populate edit form');
                }

                // Handle MaxPlayTimeMinutes with backward compatibility for existing playlists
                // Default to 0 (unlimited) for old playlists that didn't have this setting
                const maxPlayTimeMinutesValue = (playlist.MaxPlayTimeMinutes !== undefined && playlist.MaxPlayTimeMinutes !== null) ? playlist.MaxPlayTimeMinutes : 0;
                const maxPlayTimeMinutesElement = page.querySelector('#playlistMaxPlayTimeMinutes');
                if (maxPlayTimeMinutesElement) {
                    maxPlayTimeMinutesElement.value = maxPlayTimeMinutesValue;
                } else {
                    console.warn('Max Playtime Minutes element not found when trying to populate edit form');
                }

                // Set media types
                // Set flag to skip change event handlers while we programmatically set checkbox states
                page._skipMediaTypeChangeHandlers = true;

                if (playlist.MediaTypes && playlist.MediaTypes.length > 0) {
                    SmartLists.setSelectedItems(page, 'mediaTypesMultiSelect', playlist.MediaTypes, 'media-type-multi-select-checkbox', 'Select media types...');
                } else {
                    SmartLists.setSelectedItems(page, 'mediaTypesMultiSelect', [], 'media-type-multi-select-checkbox', 'Select media types...');
                }

                // Clear flag to re-enable change event handlers
                page._skipMediaTypeChangeHandlers = false;

                // Set the list owner (for both playlists and collections)
                // isCollection is already declared above on line 425
                if (isCollection) {
                    // Collections always have single user
                    const userIdString = playlist.UserId ? String(playlist.UserId) : null;
                    if (userIdString) {
                        SmartLists.setUserIdValueWithRetry(page, userIdString);
                    }
                } else {
                    // Playlists can have multiple users
                    // userIds were already extracted and stored in page._pendingUserIds above
                    // Try to set immediately if users are already loaded, otherwise wait for loadUsers
                    const checkboxes = page.querySelectorAll('#userMultiSelectOptions .user-multi-select-checkbox');
                    if (checkboxes.length > 0 && page._pendingUserIds) {
                        // Users already loaded, set immediately
                        if (SmartLists.setSelectedUserIds) {
                            SmartLists.setSelectedUserIds(page, page._pendingUserIds);
                        }
                        page._pendingUserIds = null; // Clear since we set it
                    }
                    // If checkboxes don't exist yet, loadUsers will set them when it finishes

                    if (SmartLists.updatePublicCheckboxVisibility) {
                        SmartLists.updatePublicCheckboxVisibility(page);
                    }
                }

                // Clear existing rules (applies to both playlists and collections)
                const rulesContainer = page.querySelector('#rules-container');
                rulesContainer.innerHTML = '';

                // Populate logic groups and rules
                if (playlist.ExpressionSets && playlist.ExpressionSets.length > 0 &&
                    playlist.ExpressionSets.some(function (es) { return es.Expressions && es.Expressions.length > 0; })) {
                    playlist.ExpressionSets.forEach(function (expressionSet, groupIndex) {
                        let logicGroup;

                        if (groupIndex === 0) {
                            // Create first logic group
                            logicGroup = SmartLists.createInitialLogicGroup(page);
                            // Remove only the rules, preserve the label
                            const rulesToRemove = logicGroup.querySelectorAll('.rule-row, .rule-within-group-separator');
                            rulesToRemove.forEach(function (rule) {
                                rule.remove();
                            });
                        } else {
                            // Add subsequent logic groups
                            logicGroup = SmartLists.addNewLogicGroup(page);
                            // Remove only the rules, preserve the label
                            const rulesToRemove = logicGroup.querySelectorAll('.rule-row, .rule-within-group-separator');
                            rulesToRemove.forEach(function (rule) {
                                rule.remove();
                            });
                        }

                        // Store similarity comparison fields on page for populateRuleRow to access
                        page._editingPlaylistSimilarityFields = playlist.SimilarityComparisonFields;

                        // Add rules to this logic group
                        if (expressionSet.Expressions && expressionSet.Expressions.length > 0) {
                            expressionSet.Expressions.forEach(function (expression) {
                                SmartLists.addRuleToGroup(page, logicGroup);
                                const ruleRows = logicGroup.querySelectorAll('.rule-row');
                                const currentRule = ruleRows[ruleRows.length - 1];

                                // Use populateRuleRow for consistency with clone flow
                                // populateRuleRow handles all field population including:
                                // - People sub-fields
                                // - User-specific rules
                                // - Value inputs (including relative date operators)
                                // - Per-field option selects (NextUnwatched, Collections, Tags, Studios, Genres, SimilarTo)
                                // - Regex help updates
                                SmartLists.populateRuleRow(currentRule, expression, page);
                            });
                        }

                        // Populate MaxItems for this group if it exists
                        if (expressionSet.MaxItems !== undefined && expressionSet.MaxItems !== null) {
                            const maxItemsInput = logicGroup.querySelector('.group-max-items-input');
                            if (maxItemsInput) {
                                maxItemsInput.value = expressionSet.MaxItems;
                            }
                        }
                    });
                } else {
                    // No rules exist - create an initial logic group with a placeholder rule
                    // This matches the behavior when creating a new playlist
                    SmartLists.createInitialLogicGroup(page);
                }

                // Set sort options AFTER rules are populated so hasSimilarToRuleInForm() can detect them
                SmartLists.loadSortOptionsIntoUI(page, playlist);
                // Update sort options visibility based on populated rules
                SmartLists.updateAllSortOptionsVisibility(page);

                // Update field selects first, then per-field options visibility based on selected media types
                SmartLists.updateAllFieldSelects(page);
                SmartLists.updateAllTagsOptionsVisibility(page);
                SmartLists.updateAllStudiosOptionsVisibility(page);
                SmartLists.updateAllGenresOptionsVisibility(page);
                SmartLists.updateAllAudioLanguagesOptionsVisibility(page);
                SmartLists.updateAllCollectionsOptionsVisibility(page);
                SmartLists.updateAllNextUnwatchedOptionsVisibility(page);

                // Update button visibility
                SmartLists.updateRuleButtonVisibility(page);

                // Set edit mode state
                SmartLists.setPageEditState(page, true, playlistId);

                // Update UI to show edit mode
                const editIndicator = page.querySelector('#edit-mode-indicator');
                if (editIndicator) {
                    editIndicator.style.display = 'block';
                }
                const submitBtn = page.querySelector('#submitBtn');
                if (submitBtn) {
                    const currentListType = SmartLists.getElementValue(page, '#listType', 'Playlist');
                    submitBtn.textContent = 'Update ' + currentListType;
                }

                // Update tab button text
                const createTabButton = page.querySelector('a[data-tab="create"]');
                if (createTabButton) {
                    createTabButton.textContent = 'Edit List';
                }

                // Switch to Create tab to show edit form
                SmartLists.switchToTab(page, 'create');

            } catch (formError) {
                console.error('Error populating form for edit:', formError);
                SmartLists.showNotification('Error loading playlist data for editing: ' + formError.message);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            console.error('Error loading playlist for edit:', err);
            SmartLists.handleApiError(err, 'Failed to load playlist for editing');
        });
    };

    SmartLists.clonePlaylist = function (page, playlistId, playlistName) {
        const apiClient = SmartLists.getApiClient();
        Dashboard.showLoadingMsg();

        // Always scroll to top when entering clone mode (auto for instant behavior)
        window.scrollTo({ top: 0, behavior: 'auto' });

        apiClient.ajax({
            type: 'GET',
            url: apiClient.getUrl(SmartLists.ENDPOINTS.base + '/' + playlistId),
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status + ': ' + response.statusText);
            }
            return response.json();
        }).then(function (playlist) {
            Dashboard.hideLoadingMsg();

            if (!playlist) {
                SmartLists.showNotification('No playlist data received from server.');
                return;
            }

            try {
                // Determine list type
                const listType = playlist.Type || 'Playlist';
                const isCollection = listType === 'Collection';

                // Extract userIds BEFORE calling handleListTypeChange (which triggers loadUsers)
                // This ensures pendingUserIds is set before loadUsers checks for it
                let userIds = [];
                if (!isCollection) {
                    // Playlists can have multiple users
                    if (playlist.UserPlaylists && playlist.UserPlaylists.length > 0) {
                        userIds = playlist.UserPlaylists.map(function (up) { return up.UserId; });
                    } else if (playlist.UserId) {
                        userIds = [String(playlist.UserId)];
                    }
                    // Store userIds to set after users are loaded (loadUsers is async)
                    page._pendingUserIds = userIds;
                } else {
                    // Collections: store the source user ID to prevent setCurrentUserAsDefault from overwriting
                    if (playlist.UserId) {
                        page._pendingCollectionUserId = String(playlist.UserId);
                    }
                }

                // Set playlist name FIRST (before switchToTab) to prevent populateFormDefaults from being called
                // switchToTab checks if name is empty and calls populateFormDefaults if so, which would regenerate checkboxes
                SmartLists.setElementValue(page, '#playlistName', (playlist.Name || '') + ' (Copy)');

                // Switch to Create tab
                SmartLists.switchToTab(page, 'create');

                // Clear any existing edit state
                SmartLists.setPageEditState(page, false, null);

                // Set list type
                SmartLists.setElementValue(page, '#listType', listType);

                // Set flag to prevent media type change handlers from interfering during cloning setup
                page._skipMediaTypeChangeHandlers = true;

                // Trigger type change handler to show/hide fields
                SmartLists.handleListTypeChange(page);

                // Only set public for playlists
                if (!isCollection) {
                    SmartLists.setElementChecked(page, '#playlistIsPublic', playlist.Public || false);
                }

                SmartLists.setElementChecked(page, '#playlistIsEnabled', playlist.Enabled !== false);

                // Handle AutoRefresh
                const autoRefreshValue = playlist.AutoRefresh !== undefined ? playlist.AutoRefresh : 'Never';
                const autoRefreshElement = page.querySelector('#autoRefreshMode');
                if (autoRefreshElement) {
                    autoRefreshElement.value = autoRefreshValue;
                }

                // Handle schedule settings with backward compatibility (same as editPlaylist)
                SmartLists.loadSchedulesIntoUI(page, playlist);

                // Handle visibility schedule settings (same as editPlaylist)
                SmartLists.loadVisibilitySchedulesIntoUI(page, playlist);

                // Handle MaxItems
                const maxItemsValue = (playlist.MaxItems !== undefined && playlist.MaxItems !== null) ? playlist.MaxItems : 0;
                const maxItemsElement = page.querySelector('#playlistMaxItems');
                if (maxItemsElement) {
                    maxItemsElement.value = maxItemsValue;
                }

                // Handle MaxPlayTimeMinutes
                const maxPlayTimeMinutesValue = (playlist.MaxPlayTimeMinutes !== undefined && playlist.MaxPlayTimeMinutes !== null) ? playlist.MaxPlayTimeMinutes : 0;
                const maxPlayTimeMinutesElement = page.querySelector('#playlistMaxPlayTimeMinutes');
                if (maxPlayTimeMinutesElement) {
                    maxPlayTimeMinutesElement.value = maxPlayTimeMinutesValue;
                }

                // Store media types to set later (after all updates are complete)
                const clonedMediaTypes = playlist.MediaTypes && playlist.MediaTypes.length > 0 ? playlist.MediaTypes : [];

                // Set media types BEFORE populating rules so that rule population can check selected media types
                // Flag was already set at the beginning of clone process to prevent interference
                // Set the media types from the cloned playlist
                SmartLists.setSelectedItems(page, 'mediaTypesMultiSelect', clonedMediaTypes, 'media-type-multi-select-checkbox', 'Select media types...');

                // Set the list owner (for both playlists and collections)
                // isCollection is already declared above on line 650
                if (isCollection) {
                    // Collections always have single user
                    const userIdString = playlist.UserId ? String(playlist.UserId) : null;
                    if (userIdString) {
                        SmartLists.setUserIdValueWithRetry(page, userIdString);
                    }
                } else {
                    // Playlists can have multiple users
                    // userIds were already extracted and stored in page._pendingUserIds above
                    // Try to set immediately if users are already loaded, otherwise wait for loadUsers
                    const checkboxes = page.querySelectorAll('#userMultiSelectOptions .user-multi-select-checkbox');
                    if (checkboxes.length > 0 && page._pendingUserIds) {
                        // Users already loaded, set immediately
                        if (SmartLists.setSelectedUserIds) {
                            SmartLists.setSelectedUserIds(page, page._pendingUserIds);
                        }
                        page._pendingUserIds = null; // Clear since we set it
                    }
                    // If checkboxes don't exist yet, loadUsers will set them when it finishes

                    if (SmartLists.updatePublicCheckboxVisibility) {
                        SmartLists.updatePublicCheckboxVisibility(page);
                    }
                }

                // Clear existing rules and populate with cloned rules (applies to both playlists and collections)
                const rulesContainer = page.querySelector('#rules-container');
                if (rulesContainer) {
                    rulesContainer.innerHTML = '';
                }

                // Populate rules from cloned playlist
                if (playlist.ExpressionSets && playlist.ExpressionSets.length > 0) {
                    playlist.ExpressionSets.forEach(function (expressionSet, setIndex) {
                        const logicGroup = setIndex === 0 ? SmartLists.createInitialLogicGroup(page) : SmartLists.addNewLogicGroup(page);

                        // Store similarity comparison fields on page for populateRuleRow to access
                        page._cloningPlaylistSimilarityFields = playlist.SimilarityComparisonFields;

                        if (expressionSet.Expressions && expressionSet.Expressions.length > 0) {
                            expressionSet.Expressions.forEach(function (expression, expIndex) {
                                if (expIndex === 0) {
                                    // Use the first rule row that's already in the group
                                    const firstRuleRow = logicGroup.querySelector('.rule-row');
                                    if (firstRuleRow) {
                                        SmartLists.populateRuleRow(firstRuleRow, expression, page);
                                    }
                                } else {
                                    // Add additional rule rows
                                    SmartLists.addRuleToGroup(page, logicGroup);
                                    const newRuleRow = logicGroup.querySelector('.rule-row:last-child');
                                    if (newRuleRow) {
                                        SmartLists.populateRuleRow(newRuleRow, expression, page);
                                    }
                                }
                            });
                        }

                        // Populate MaxItems for this group if it exists
                        if (expressionSet.MaxItems !== undefined && expressionSet.MaxItems !== null) {
                            const maxItemsInput = logicGroup.querySelector('.group-max-items-input');
                            if (maxItemsInput) {
                                maxItemsInput.value = expressionSet.MaxItems;
                            }
                        }
                    });
                } else {
                    // If no rules, create initial empty group
                    SmartLists.createInitialLogicGroup(page);
                }

                // Update button visibility
                SmartLists.updateRuleButtonVisibility(page);

                // Update field selects first, then per-field options visibility based on selected media types
                SmartLists.updateAllFieldSelects(page);
                SmartLists.updateAllTagsOptionsVisibility(page);
                SmartLists.updateAllStudiosOptionsVisibility(page);
                SmartLists.updateAllGenresOptionsVisibility(page);
                SmartLists.updateAllAudioLanguagesOptionsVisibility(page);
                SmartLists.updateAllCollectionsOptionsVisibility(page);
                SmartLists.updateAllNextUnwatchedOptionsVisibility(page);

                // Set sort options AFTER rules are populated so hasSimilarToRuleInForm() can detect them
                SmartLists.loadSortOptionsIntoUI(page, playlist);
                // Update sort options visibility based on populated rules
                SmartLists.updateAllSortOptionsVisibility(page);



                // Clear flag to re-enable change event handlers
                page._skipMediaTypeChangeHandlers = false;

                // Clear any pending media type update timers just in case
                if (page._mediaTypeUpdateTimer) {
                    clearTimeout(page._mediaTypeUpdateTimer);
                    page._mediaTypeUpdateTimer = null;
                }

                // Show success message
                SmartLists.showNotification('List "' + playlistName + '" cloned successfully! You can now modify and create the new list.', 'success');

            } catch (formError) {
                console.error('Error populating form for clone:', formError);
                SmartLists.showNotification('Error loading list data for cloning: ' + formError.message);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            console.error('Error loading list for clone:', err);
            SmartLists.handleApiError(err, 'Failed to load list for cloning');
        });
    };

    SmartLists.cancelEdit = function (page) {
        SmartLists.setPageEditState(page, false, null);

        // Update UI to show create mode
        const editIndicator = page.querySelector('#edit-mode-indicator');
        if (editIndicator) {
            editIndicator.style.display = 'none';
        }
        const submitBtn = page.querySelector('#submitBtn');
        if (submitBtn) {
            submitBtn.textContent = 'Create List';
        }

        // Restore tab button text
        const createTabButton = page.querySelector('a[data-tab="create"]');
        if (createTabButton) {
            createTabButton.textContent = 'Create List';
        }

        // Clear form
        SmartLists.clearForm(page);

        // Switch to Manage tab after canceling edit
        SmartLists.switchToTab(page, 'manage');
        window.scrollTo({ top: 0, behavior: 'auto' });

        SmartLists.showNotification('Edit mode cancelled.', 'success');
    };

    SmartLists.notifyRefreshQueued = function (listTypeName, playlistName) {
        // Show single notification with status page link (or simple message for user pages)
        var statusLink = SmartLists.createStatusPageLink('status page');
        var message = 'Refresh started';
        if (playlistName) {
            message += ' for ' + (listTypeName || 'list') + ' "' + playlistName + '"';
        }
        
        if (SmartLists.IS_USER_PAGE) {
            message += '. Your list will be updated in the background.';
        } else {
            message += '. Check the ' + statusLink + ' for progress.';
        }
        
        SmartLists.showNotification(message, 'info', { html: true });

        // Start aggressive polling on status page to catch the operation (admin only)
        if (!SmartLists.IS_USER_PAGE && window.SmartLists && window.SmartLists.Status && window.SmartLists.Status.startAggressivePolling) {
            window.SmartLists.Status.startAggressivePolling();
        }
    };

    SmartLists.refreshPlaylist = function (playlistId, playlistName) {
        const apiClient = SmartLists.getApiClient();

        // Show notification that refresh has started and start aggressive polling
        SmartLists.notifyRefreshQueued('List', playlistName);

        // Make API call (fire and forget - notification already shown)
        apiClient.ajax({
            type: 'POST',
            url: apiClient.getUrl(SmartLists.ENDPOINTS.base + '/' + playlistId + '/refresh'),
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                // Try to parse error message from response
                return response.text().then(function (errorText) {
                    var errorMessage;
                    try {
                        var parsed = JSON.parse(errorText);
                        // Extract string from parsed object if necessary
                        if (parsed && typeof parsed === 'object') {
                            errorMessage = parsed.message || parsed.error || JSON.stringify(parsed);
                        } else if (typeof parsed === 'string') {
                            errorMessage = parsed;
                        } else {
                            errorMessage = String(parsed);
                        }
                    } catch (e) {
                        errorMessage = errorText || 'Unknown error occurred';
                    }
                    throw new Error(errorMessage);
                });
            }

            // Success - operation is queued and will be processed in the background
            // No success notification needed since status page shows progress

            // Auto-refresh the playlist list to show updated LastRefreshed timestamp
            // Commented out to prevent page jump/scroll to top
            /*
            const page = document.querySelector('.SmartListsConfigurationPage');
            if (page) {
                SmartLists.loadPlaylistList(page);
            }
            */
        }).catch(async function (err) {

            // Extract error message using utility function
            const errorMessage = await SmartLists.extractErrorMessage(
                err,
                'An unexpected error occurred, check the logs for more details.'
            );

            const fullMessage = 'Failed to refresh list "' + (playlistName || 'Unknown') + '": ' + errorMessage;
            console.error('List refresh error:', fullMessage, err);
            SmartLists.showNotification(fullMessage, 'error');
        });
    };

    SmartLists.deletePlaylist = async function (page, listId, listName) {
        await SmartLists.performListAction(page, listId, listName, {
            actionType: 'delete',
            apiPath: '',
            httpMethod: 'DELETE',
            getQueryParams: function (page) {
                const checkbox = page.querySelector('#delete-jellyfin-playlist-checkbox');
                const deleteJellyfinList = checkbox ? checkbox.checked : false;
                return 'deleteJellyfinList=' + deleteJellyfinList;
            },
            formatSuccessMessage: function (name, page) {
                const checkbox = page.querySelector('#delete-jellyfin-playlist-checkbox');
                const deleteJellyfinList = checkbox ? checkbox.checked : false;
                const action = deleteJellyfinList ? 'deleted' : 'suffix/prefix removed (if any) and configuration deleted';
                return 'List "' + name + '" ' + action + ' successfully.';
            }
        });
    };

    SmartLists.enablePlaylist = async function (page, listId, listName) {
        await SmartLists.performListAction(page, listId, listName, {
            actionType: 'enable',
            apiPath: '/enable',
            httpMethod: 'POST',
            formatSuccessMessage: function (name) {
                return 'List "' + name + '" has been refreshed successfully.';
            }
        });
    };

    SmartLists.disablePlaylist = async function (page, listId, listName) {
        await SmartLists.performListAction(page, listId, listName, {
            actionType: 'disable',
            apiPath: '/disable',
            httpMethod: 'POST',
            formatSuccessMessage: function (name) {
                return 'List "' + name + '" has been disabled.';
            }
        });
    };

    SmartLists.showDeleteConfirm = function (page, listId, listName) {
        const confirmText = 'Are you sure you want to delete the smart list "' + listName + '"? This cannot be undone.';

        SmartLists.showDeleteModal(page, confirmText, function () {
            SmartLists.deletePlaylist(page, listId, listName);
        });
    };

    // Note: formatPlaylistDisplayValues is defined in config-formatters.js to avoid duplication

    // ===== SEARCH INPUT STATE MANAGEMENT =====
    SmartLists.setSearchInputState = function (page, disabled, placeholder) {
        placeholder = placeholder !== undefined ? placeholder : 'Search lists...';
        try {
            const searchInput = page.querySelector('#playlistSearchInput');
            const clearSearchBtn = page.querySelector('#clearSearchBtn');

            if (searchInput) {
                searchInput.disabled = disabled;
                searchInput.placeholder = placeholder;

                // Store original state to restore later if needed
                if (!page._originalSearchState) {
                    page._originalSearchState = {
                        disabled: false,
                        placeholder: 'Search lists...'
                    };
                }
            }

            // Only hide clear button when disabling, let updateClearButtonVisibility handle showing it
            if (clearSearchBtn && disabled) {
                clearSearchBtn.style.display = 'none';
            }
        } catch (err) {
            console.error('Error setting search input state:', err);
        }
    };

    // Note: getPeopleFieldDisplayName is defined in config-formatters.js to avoid duplication

    // ===== GENERATE RULES HTML =====
    SmartLists.generateRulesHtml = async function (playlist, apiClient) {
        let rulesHtml = '';
        if (playlist.ExpressionSets && playlist.ExpressionSets.length > 0) {
            for (let groupIndex = 0; groupIndex < playlist.ExpressionSets.length; groupIndex++) {
                const expressionSet = playlist.ExpressionSets[groupIndex];
                if (groupIndex > 0) {
                    rulesHtml += '<strong style="color: #888;">OR</strong><br>';
                }

                if (expressionSet.Expressions && expressionSet.Expressions.length > 0) {
                    rulesHtml += '<div style="padding: 0.6em; background: rgba(255,255,255,0.02); border-radius: 4px; margin: 0.3em 0;">';

                    for (let ruleIndex = 0; ruleIndex < expressionSet.Expressions.length; ruleIndex++) {
                        const rule = expressionSet.Expressions[ruleIndex];
                        if (ruleIndex > 0) {
                            rulesHtml += '<br><em style="color: #888; font-size: 0.9em;">AND</em><br>';
                        }

                        let fieldName = rule.MemberName;
                        if (fieldName === 'ItemType') fieldName = 'Media Type';

                        // Map people field names to friendly display names
                        const displayName = SmartLists.getPeopleFieldDisplayName(fieldName);
                        if (displayName !== fieldName) {
                            fieldName = displayName;
                        }
                        let operator = rule.Operator;
                        switch (operator) {
                            case 'Equal': operator = 'equals'; break;
                            case 'NotEqual': operator = 'not equals'; break;
                            case 'Contains': operator = 'contains'; break;
                            case 'NotContains': operator = "not contains"; break;
                            case 'IsIn': operator = 'is in'; break;
                            case 'IsNotIn': operator = 'is not in'; break;
                            case 'GreaterThan': operator = '>'; break;
                            case 'LessThan': operator = '<'; break;
                            case 'After': operator = 'after'; break;
                            case 'Before': operator = 'before'; break;
                            case 'GreaterThanOrEqual': operator = '>='; break;
                            case 'LessThanOrEqual': operator = '<='; break;
                            case 'MatchRegex': operator = 'matches regex'; break;
                        }
                        let value = rule.TargetValue;
                        if (rule.MemberName === 'PlaybackStatus') {
                            if (value === 'Played') { value = 'Played'; }
                            else if (value === 'InProgress') { value = 'In Progress'; }
                            else if (value === 'Unplayed') { value = 'Unplayed'; }
                        }
                        if (rule.MemberName === 'NextUnwatched') { value = value === 'true' ? 'Yes (Next to Watch)' : 'No (Not Next)'; }

                        // Format weekday operator value to show day name instead of number
                        if (rule.Operator === 'Weekday') {
                            value = SmartLists.getDayNameFromValue(value);
                        }

                        // Check if this rule has a specific user and resolve username
                        // Skip showing user info on user pages since users only see their own playlists
                        let userInfo = '';
                        if (!SmartLists.IS_USER_PAGE && rule.UserId && rule.UserId !== '00000000-0000-0000-0000-000000000000') {
                            try {
                                const userName = await SmartLists.resolveUserIdToName(apiClient, rule.UserId);
                                userInfo = ' for ' + (userName || 'Unknown User');
                            } catch (err) {
                                console.error('Error resolving username for rule:', err);
                                userInfo = ' for specific user';
                            }
                        }

                        // Add NextUnwatched configuration info
                        let nextUnwatchedInfo = '';
                        if (rule.MemberName === 'NextUnwatched' && rule.IncludeUnwatchedSeries !== undefined) {
                            nextUnwatchedInfo = rule.IncludeUnwatchedSeries ? ' (including unwatched series)' : ' (excluding unwatched series)';
                        }

                        // Add Collections configuration info
                        let collectionsInfo = '';
                        if (rule.MemberName === 'Collections') {
                            if (rule.IncludeCollectionOnly === true) {
                                collectionsInfo = ' (collection only)';
                            } else if (rule.IncludeEpisodesWithinSeries === true) {
                                collectionsInfo = ' (including episodes within series)';
                            }
                        }

                        // Add Playlists configuration info
                        let playlistsInfo = '';
                        if (rule.MemberName === 'Playlists' && rule.IncludePlaylistOnly === true) {
                            playlistsInfo = ' (playlist only)';
                        }

                        // Add Tags configuration info
                        let tagsInfo = '';
                        if (rule.MemberName === 'Tags' && rule.IncludeParentSeriesTags === true) {
                            tagsInfo = ' (including parent series tags)';
                        }

                        // Add Studios configuration info
                        let studiosInfo = '';
                        if (rule.MemberName === 'Studios' && rule.IncludeParentSeriesStudios === true) {
                            studiosInfo = ' (including parent series studios)';
                        }

                        // Add Genres configuration info
                        let genresInfo = '';
                        if (rule.MemberName === 'Genres' && rule.IncludeParentSeriesGenres === true) {
                            genresInfo = ' (including parent series genres)';
                        }

                        // Add AudioLanguages configuration info
                        let audioLanguagesInfo = '';
                        if (rule.MemberName === 'AudioLanguages' && rule.OnlyDefaultAudioLanguage === true) {
                            audioLanguagesInfo = ' (default only)';
                        }

                        // Add SimilarTo comparison fields info
                        let similarityInfo = '';
                        if (rule.MemberName === 'SimilarTo') {
                            if (playlist.SimilarityComparisonFields && playlist.SimilarityComparisonFields.length > 0) {
                                similarityInfo = ' (comparing: ' + playlist.SimilarityComparisonFields.join(', ') + ')';
                            } else {
                                similarityInfo = ' (comparing: Genre, Tags)'; // Default
                            }
                        }

                        rulesHtml += '<span style="font-family: monospace; background: #232323; padding: 4px 4px; border-radius: 3px;">';
                        rulesHtml += SmartLists.escapeHtml(fieldName) + ' ' + SmartLists.escapeHtml(operator) + ' "' + SmartLists.escapeHtml(value) + '"' + SmartLists.escapeHtml(userInfo) + SmartLists.escapeHtml(nextUnwatchedInfo) + SmartLists.escapeHtml(collectionsInfo) + SmartLists.escapeHtml(playlistsInfo) + SmartLists.escapeHtml(tagsInfo) + SmartLists.escapeHtml(studiosInfo) + SmartLists.escapeHtml(genresInfo) + SmartLists.escapeHtml(audioLanguagesInfo) + SmartLists.escapeHtml(similarityInfo);
                        rulesHtml += '</span>';
                    }

                    // Add MaxItems indicator if this group has a limit
                    if (expressionSet.MaxItems !== undefined && expressionSet.MaxItems !== null) {
                        rulesHtml += '<br><span style="color: #888; font-size: 0.9em;">↳ Max ' + SmartLists.escapeHtml(expressionSet.MaxItems.toString()) + ' items from this group</span>';
                    }

                    rulesHtml += '</div>';
                }
            }
        } else {
            rulesHtml = 'No rules defined';
        }
        return rulesHtml;
    };

    // ===== GENERATE PLAYLIST CARD HTML =====
    SmartLists.generatePlaylistCardHtml = function (playlist, rulesHtml, resolvedUserName, createdByUserName) {
        // Determine list type
        const listType = playlist.Type || 'Playlist';
        const isCollection = listType === 'Collection';

        const isPublic = playlist.Public ? 'Public' : 'Private';
        const isEnabled = playlist.Enabled !== false; // Default to true for backward compatibility
        const enabledStatus = isEnabled ? '' : 'Disabled';
        const enabledStatusColor = isEnabled ? '#4CAF50' : '#f44336';
        const statusDisplayText = isEnabled ? 'Enabled' : 'Disabled';
        const autoRefreshMode = playlist.AutoRefresh || 'Never';
        const autoRefreshDisplay = autoRefreshMode === 'Never' ? 'Manual/scheduled only' :
            autoRefreshMode === 'OnLibraryChanges' ? 'On library changes - When new items are added' :
                autoRefreshMode === 'OnAllChanges' ? 'On all changes - Including playback status' : autoRefreshMode;
        const scheduleDisplay = SmartLists.formatScheduleDisplay(playlist);
        const visibilityScheduleDisplay = SmartLists.formatVisibilityScheduleDisplay(playlist);

        // Format last scheduled refresh display
        const lastRefreshDisplay = SmartLists.formatRelativeTimeFromIso(playlist.LastRefreshed, 'N/A') || 'N/A';
        const dateCreatedDisplay = SmartLists.formatRelativeTimeFromIso(playlist.DateCreated, 'Unknown');
        const sortName = SmartLists.formatSortDisplay(playlist);

        // Use the resolved username passed as parameter (for playlists) or libraries (for collections)
        const userName = resolvedUserName || 'Unknown User';
        const createdBy = createdByUserName || 'Unknown';
        const playlistId = playlist.Id || 'NO_ID';

        // Collections are server-wide, no library assignment needed
        // Create individual media type labels - filter out Series for playlists only (not supported due to Jellyfin limitations)
        let mediaTypesArray = [];
        if (playlist.MediaTypes && playlist.MediaTypes.length > 0) {
            // Only filter Series for Playlists (Collections support Series)
            const isPlaylist = playlist.Type === 'Playlist' || !playlist.Type; // Default to Playlist if Type not set
            const validTypes = isPlaylist
                ? playlist.MediaTypes.filter(function (type) { return type !== 'Series'; })
                : playlist.MediaTypes; // Collections: show all types including Series
            mediaTypesArray = validTypes.length > 0 ? validTypes : ['Unknown'];
        } else {
            mediaTypesArray = ['Unknown'];
        }

        const displayValues = SmartLists.formatPlaylistDisplayValues(playlist);
        const maxItemsDisplay = displayValues.maxItemsDisplay;
        const maxPlayTimeDisplay = displayValues.maxPlayTimeDisplay;

        // Format media types for display in Properties table
        const mediaTypesDisplayText = mediaTypesArray.join(', ');

        // Format playlist statistics for header display
        const itemCount = playlist.ItemCount !== undefined && playlist.ItemCount !== null ? playlist.ItemCount : null;
        const totalRuntime = playlist.TotalRuntimeMinutes ? SmartLists.formatRuntime(playlist.TotalRuntimeMinutes) : null;
        const totalRuntimeLong = playlist.TotalRuntimeMinutes ? SmartLists.formatRuntimeLong(playlist.TotalRuntimeMinutes) : null;

        // Build stats display string for header
        const statsElements = [];
        if (itemCount !== null) {
            statsElements.push(itemCount + ' item' + (itemCount === 1 ? '' : 's'));
        }
        if (totalRuntime) {
            statsElements.push(totalRuntime);
        }
        const statsDisplay = statsElements.length > 0 ? statsElements.join(' | ') : '';

        // Escape all dynamic content to prevent XSS
        const eName = SmartLists.escapeHtml(playlist.Name || '');
        const eFileName = SmartLists.escapeHtml(playlist.FileName || '');
        const eUserName = SmartLists.escapeHtml(userName || '');
        const eCreatedBy = SmartLists.escapeHtml(createdBy || 'Unknown');
        const eSortName = SmartLists.escapeHtml(sortName);
        const eMaxItems = SmartLists.escapeHtml(maxItemsDisplay);
        const eMaxPlayTime = SmartLists.escapeHtml(maxPlayTimeDisplay);
        const eAutoRefreshDisplay = SmartLists.escapeHtml(autoRefreshDisplay);
        const eScheduleDisplay = SmartLists.escapeHtml(scheduleDisplay);
        const eVisibilityScheduleDisplay = SmartLists.escapeHtml(visibilityScheduleDisplay);
        const eLastRefreshDisplay = SmartLists.escapeHtml(lastRefreshDisplay);
        const eDateCreatedDisplay = SmartLists.escapeHtml(dateCreatedDisplay);
        const eStatusDisplayText = SmartLists.escapeHtml(statusDisplayText);
        const eMediaTypesDisplayText = SmartLists.escapeHtml(mediaTypesDisplayText);
        const eStatsDisplay = SmartLists.escapeHtml(statsDisplay);
        const eTotalRuntimeLong = totalRuntimeLong ? SmartLists.escapeHtml(totalRuntimeLong) : null;
        const eListType = SmartLists.escapeHtml(listType);

        // Helper function to build Jellyfin URL from ID
        const buildJellyfinUrl = function (jellyfinId) {
            if (!jellyfinId || jellyfinId === '' || jellyfinId === '00000000-0000-0000-0000-000000000000') {
                return null;
            }
            try {
                const apiClient = SmartLists.getApiClient();
                const serverId = apiClient.serverId();
                const baseUrl = apiClient.serverAddress();
                return baseUrl + '/web/#/details?id=' + encodeURIComponent(jellyfinId) + '&serverId=' + encodeURIComponent(serverId);
            } catch (err) {
                console.error('Error building Jellyfin URL:', err);
                // Fallback: try without serverId
                try {
                    const apiClient = SmartLists.getApiClient();
                    const baseUrl = apiClient.serverAddress();
                    return baseUrl + '/web/#/details?id=' + encodeURIComponent(jellyfinId);
                } catch (fallbackErr) {
                    console.error('Error building Jellyfin URL (fallback):', fallbackErr);
                    return null;
                }
            }
        };

        // Build Jellyfin playlist/collection link info
        // For playlists: show current user's link (if any) + count of others
        // For collections: show single link
        let jellyfinLinkHtml = '';

        if (isEnabled) {
            if (isCollection) {
                // Collections: single ID, show simple link
                const jellyfinId = playlist.JellyfinCollectionId;
                const url = buildJellyfinUrl(jellyfinId);
                if (url) {
                    jellyfinLinkHtml = ' - <a href="' + SmartLists.escapeHtmlAttribute(url) + '" target="_blank" rel="noopener noreferrer" style="color: #00a4dc; text-decoration: none;">View in Jellyfin</a>';
                }
            } else {
                // Playlists: show current user's playlist (if they have one) + count of others
                if (playlist.UserPlaylists && playlist.UserPlaylists.length > 0) {
                    try {
                        const apiClient = SmartLists.getApiClient();
                        const currentUserId = apiClient.getCurrentUserId();
                        const normalizedCurrentUserId = normalizeUserId(currentUserId);

                        // Find current user's playlist
                        let currentUserPlaylist = null;
                        let otherUsersCount = 0;

                        playlist.UserPlaylists.forEach(function (userPlaylist) {
                            const normalizedUserId = normalizeUserId(userPlaylist.UserId);
                            if (normalizedUserId === normalizedCurrentUserId) {
                                currentUserPlaylist = userPlaylist;
                            } else {
                                otherUsersCount++;
                            }
                        });

                        if (currentUserPlaylist) {
                            const url = buildJellyfinUrl(currentUserPlaylist.JellyfinPlaylistId);
                            if (url) {
                                jellyfinLinkHtml = ' - <a href="' + SmartLists.escapeHtmlAttribute(url) + '" target="_blank" rel="noopener noreferrer" style="color: #00a4dc; text-decoration: none;">View in Jellyfin</a>';

                                // Add count of other users if any
                                if (otherUsersCount > 0) {
                                    jellyfinLinkHtml += ' <span style="color: #888; font-size: 0.9em;">(+' + otherUsersCount + ' other' + (otherUsersCount === 1 ? '' : 's') + ')</span>';
                                }
                            }
                        } else if (otherUsersCount > 0) {
                            // Current user doesn't have this playlist, just show count
                            jellyfinLinkHtml = ' - <span style="color: #888; font-style: italic;">(' + playlist.UserPlaylists.length + ' user' + (playlist.UserPlaylists.length === 1 ? '' : 's') + ')</span>';
                        }
                    } catch (err) {
                        console.error('Error getting current user ID:', err);
                        // Fallback: show count only
                        jellyfinLinkHtml = ' - <span style="color: #888; font-style: italic;">(' + playlist.UserPlaylists.length + ' user' + (playlist.UserPlaylists.length === 1 ? '' : 's') + ')</span>';
                    }
                } else {
                    // Fallback: single playlist (backwards compatibility)
                    const jellyfinId = playlist.JellyfinPlaylistId;
                    const url = buildJellyfinUrl(jellyfinId);
                    if (url) {
                        jellyfinLinkHtml = ' - <a href="' + SmartLists.escapeHtmlAttribute(url) + '" target="_blank" rel="noopener noreferrer" style="color: #00a4dc; text-decoration: none;">View in Jellyfin</a>';
                    }
                }
            }
        }

        // Generate collapsible playlist card with improved styling
        return '<div class="inputContainer playlist-card" data-playlist-id="' + SmartLists.escapeHtmlAttribute(playlistId) + '" data-enabled="' + (isEnabled ? 'true' : 'false') + '" style="border: none; border-radius: 2px; margin-bottom: 1em; background: #202020;">' +
            // Compact header (always visible)
            '<div class="playlist-header" style="padding: 0.75em; cursor: pointer; display: flex; align-items: center; justify-content: space-between;">' +
            '<div class="playlist-header-left" style="display: flex; align-items: center; flex: 1; min-width: 0;">' +
            '<label class="emby-checkbox-label" style="width: auto; min-width: auto; margin-right: 0.3em; margin-left: 0.3em; flex-shrink: 0;">' +
            '<input type="checkbox" is="emby-checkbox" data-embycheckbox="true" class="emby-checkbox playlist-checkbox" data-playlist-id="' + SmartLists.escapeHtmlAttribute(playlistId) + '">' +
            '<span class="checkboxLabel" style="display: none;"></span>' +
            '<span class="checkboxOutline">' +
            '<span class="material-icons checkboxIcon checkboxIcon-checked check" aria-hidden="true"></span>' +
            '<span class="material-icons checkboxIcon checkboxIcon-unchecked" aria-hidden="true"></span>' +
            '</span>' +
            '</label>' +
            '<span class="playlist-expand-icon" style="margin-right: 0.5em; font-family: monospace; font-size: 1.2em; color: #999; flex-shrink: 0;">▶</span>' +
            '<h3 style="margin: 0; flex: 1.5; min-width: 0; word-wrap: break-word; padding-right: 0.5em;">' + eName + '</h3>' +
            (enabledStatus ? '<span class="playlist-status" style="color: ' + enabledStatusColor + '; font-weight: bold; margin-right: 0.75em; flex-shrink: 0; line-height: 1.5; align-self: center;">' + enabledStatus + '</span>' : '') +
            (eStatsDisplay ? '<span class="playlist-stats" style="color: #888; font-size: 0.85em; margin-right: 0.5em; flex-shrink: 0; font-weight: normal; line-height: 1.5; align-self: center;">' + eStatsDisplay + '</span>' : '') +
            '</div>' +
            '<div class="playlist-header-right" style="display: flex; align-items: center; margin-left: 1em; margin-right: 0.5em;">' +
            '<div class="playlist-type-container" style="display: flex; flex-wrap: wrap; gap: 0.25em; flex-shrink: 0; max-width: 160px; justify-content: flex-end;">' +
            '<span class="playlist-type-label" style="padding: 0.4em 0.6em; background: #333; border-radius: 3px; font-size: 0.8em; color: #ccc; white-space: nowrap;">' + SmartLists.escapeHtml(listType) + '</span>' +
            '</div>' +
            '</div>' +
            '</div>' +

            // Detailed content (initially hidden)
            '<div class="playlist-details" style="display: none; padding: 0 0.75em 0.75em 0.75em; background: #202020;">' +
            // Rules section
            '<div class="rules-section" style="margin-bottom: 1em; margin-left: 0.5em;">' +
            '<h4 style="margin: 0 0 0.5em 0; color: #fff; font-size: 1em;">Rules</h4>' +
            rulesHtml +
            '</div>' +

            // Properties table
            '<div class="properties-section" style="margin-bottom: 1em; margin-left: 0.5em;">' +
            '<h4 style="margin: 0 0 0.5em 0; color: #fff; font-size: 1em;">Properties</h4>' +
            '<table style="width: 100%; border-collapse: collapse; background: rgba(255,255,255,0.02); border-radius: 4px; overflow: hidden;">' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Type</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eListType +
            jellyfinLinkHtml +
            '</td>' +
            '</tr>' +
            // Hide File property on user pages
            (!SmartLists.IS_USER_PAGE ?
                '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
                '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">File</td>' +
                '<td style="padding: 0.5em 0.75em; color: #fff;">' + eFileName + '</td>' +
                '</tr>' :
                ''
            ) +
            // Hide User(s) property on user pages
            (!SmartLists.IS_USER_PAGE ?
                '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
                '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">User(s)</td>' +
                '<td style="padding: 0.5em 0.75em; color: #fff;">' + eUserName + '</td>' +
                '</tr>' :
                ''
            ) +
            // Hide Created By property on user pages
            (!SmartLists.IS_USER_PAGE ?
                '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
                '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Created By</td>' +
                '<td style="padding: 0.5em 0.75em; color: #fff;">' + eCreatedBy + '</td>' +
                '</tr>' :
                ''
            ) +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Status</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eStatusDisplayText + '</td>' +
            '</tr>' +
            (!isCollection ?
                '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
                '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Visibility</td>' +
                '<td style="padding: 0.5em 0.75em; color: #fff;">' + SmartLists.escapeHtml(isPublic) + '</td>' +
                '</tr>' :
                ''
            ) +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Media Types</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eMediaTypesDisplayText + '</td>' +
            '</tr>' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Sort</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eSortName + '</td>' +
            '</tr>' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Max Items</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eMaxItems + '</td>' +
            '</tr>' +
            (!isCollection ?
                '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
                '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Max Playtime</td>' +
                '<td style="padding: 0.5em 0.75em; color: #fff;">' + eMaxPlayTime + '</td>' +
                '</tr>' : ''
            ) +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Auto Refresh</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eAutoRefreshDisplay + '</td>' +
            '</tr>' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Refresh Schedule</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eScheduleDisplay + '</td>' +
            '</tr>' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Visibility Schedule</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eVisibilityScheduleDisplay + '</td>' +
            '</tr>' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Created</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eDateCreatedDisplay + '</td>' +
            '</tr>' +
            '</table>' +
            '</div>' +

            // Statistics table
            '<div class="statistics-section" style="margin-bottom: 1em; margin-left: 0.5em;">' +
            '<h4 style="margin: 0 0 0.5em 0; color: #fff; font-size: 1em;">Statistics</h4>' +
            '<table style="width: 100%; border-collapse: collapse; background: rgba(255,255,255,0.02); border-radius: 4px; overflow: hidden;">' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Item Count</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + (itemCount !== null ? itemCount : 'N/A') + '</td>' +
            '</tr>' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Total Playtime</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + (eTotalRuntimeLong && playlist.TotalRuntimeMinutes && playlist.TotalRuntimeMinutes > 0 ? eTotalRuntimeLong : 'N/A') + '</td>' +
            '</tr>' +
            '<tr style="border-bottom: 1px solid rgba(255,255,255,0.1);">' +
            '<td style="padding: 0.5em 0.75em; font-weight: bold; color: #ccc; width: 40%; border-right: 1px solid rgba(255,255,255,0.1);">Last Refreshed</td>' +
            '<td style="padding: 0.5em 0.75em; color: #fff;">' + eLastRefreshDisplay + '</td>' +
            '</tr>' +
            '</table>' +
            '</div>' +

            // Action buttons
            '<div class="playlist-actions" style="margin-top: 1em; margin-left: 0.5em;">' +
            '<button is="emby-button" type="button" class="emby-button raised edit-playlist-btn" data-playlist-id="' + SmartLists.escapeHtmlAttribute(playlistId) + '" data-playlist-name="' + SmartLists.escapeHtmlAttribute(playlist.Name || '') + '">Edit</button>' +
            '<button is="emby-button" type="button" class="emby-button raised clone-playlist-btn" data-playlist-id="' + SmartLists.escapeHtmlAttribute(playlistId) + '" data-playlist-name="' + SmartLists.escapeHtmlAttribute(playlist.Name || '') + '">Clone</button>' +
            '<button is="emby-button" type="button" class="emby-button raised refresh-playlist-btn" data-playlist-id="' + SmartLists.escapeHtmlAttribute(playlistId) + '" data-playlist-name="' + SmartLists.escapeHtmlAttribute(playlist.Name || '') + '">Refresh</button>' +
            (isEnabled ?
                '<button is="emby-button" type="button" class="emby-button raised disable-playlist-btn" data-playlist-id="' + SmartLists.escapeHtmlAttribute(playlistId) + '" data-playlist-name="' + SmartLists.escapeHtmlAttribute(playlist.Name || '') + '">Disable</button>' :
                '<button is="emby-button" type="button" class="emby-button raised enable-playlist-btn" data-playlist-id="' + SmartLists.escapeHtmlAttribute(playlistId) + '" data-playlist-name="' + SmartLists.escapeHtmlAttribute(playlist.Name || '') + '">Enable</button>'
            ) +
            '<button is="emby-button" type="button" class="emby-button raised danger delete-playlist-btn" data-playlist-id="' + SmartLists.escapeHtmlAttribute(playlistId) + '" data-playlist-name="' + SmartLists.escapeHtmlAttribute(playlist.Name || '') + '">Delete</button>' +
            '</div>' +
            '</div>' +
            '</div>';
    };

    // ===== LOAD PLAYLIST LIST =====
    SmartLists.loadPlaylistList = async function (page) {
        const apiClient = SmartLists.getApiClient();
        const container = page.querySelector('#playlist-list-container');

        // Guard: Check if container exists
        if (!container) {
            console.error('SmartLists: Container #playlist-list-container not found');
            return;
        }

        // Prevent multiple simultaneous requests
        if (page._loadingPlaylists) {
            return;
        }

        // Set loading state BEFORE any async operations
        page._loadingPlaylists = true;

        // Disable search input while loading
        SmartLists.setSearchInputState(page, true, 'Loading lists...');

        container.innerHTML = '<p>Loading lists...</p>';

        try {
            // Note: apiClient.ajax() returns a fetch Response object (not parsed JSON)
            // So we need to check .ok and call .json() to get the actual data
            const response = await apiClient.ajax({
                type: "GET",
                url: apiClient.getUrl(SmartLists.ENDPOINTS.base),
                contentType: 'application/json'
            });

            if (!response.ok) {
                throw new Error('HTTP ' + response.status + ': ' + response.statusText);
            }

            const playlists = await response.json();
            let processedPlaylists = playlists;
            // Ensure playlists is an array
            if (!Array.isArray(processedPlaylists)) {
                console.warn('API returned non-array playlists data, converting to empty array');
                processedPlaylists = [];
            }

            // On user pages, filter out multi-user playlists to prevent users from
            // editing playlists that would affect other users
            if (SmartLists.IS_USER_PAGE) {
                processedPlaylists = processedPlaylists.filter(function (playlist) {
                    // Only show playlists that have 0 or 1 user
                    // Multi-user playlists (created by admins) should only be visible/editable by admins
                    return !playlist.UserPlaylists || playlist.UserPlaylists.length <= 1;
                });
            }

            // Store playlists data for filtering
            page._allPlaylists = processedPlaylists;

            // Preload all users to populate cache for user name resolution
            // Skip for user pages (admin-only endpoint)
            if (!SmartLists.IS_USER_PAGE) {
                try {
                    // Note: apiClient.ajax() returns a fetch Response object (not parsed JSON)
                    const usersResponse = await apiClient.ajax({
                        type: 'GET',
                        url: apiClient.getUrl(SmartLists.ENDPOINTS.users),
                        contentType: 'application/json'
                    });
                    
                    if (!usersResponse.ok) {
                        throw new Error('HTTP ' + usersResponse.status + ': ' + usersResponse.statusText);
                    }
                    
                    const users = await usersResponse.json();

                    // Build cache from all users for user name resolution
                    if (Array.isArray(users)) {
                        users.forEach(function (user) {
                            if (user.Id && user.Name) {
                                // Normalize GUID format when storing in cache
                                const normalizedId = normalizeUserId(user.Id);
                                userNameCache.set(normalizedId, user.Name);
                            }
                        });
                    }
                } catch (err) {
                    console.error('Error preloading users:', err);
                    // Continue even if user preload fails
                }
            }

            try {
                // Populate user filter dropdown (skip on user pages - users only see their own lists)
                if (SmartLists.populateUserFilter && !SmartLists.IS_USER_PAGE) {
                    await SmartLists.populateUserFilter(page, processedPlaylists);
                }
            } catch (err) {
                console.error('Error populating user filter:', err);
                // Continue even if user filter fails
            }

            if (processedPlaylists && processedPlaylists.length > 0) {
                // Apply all filters and sorting and display results
                const filteredPlaylists = SmartLists.applyAllFiltersAndSort ? SmartLists.applyAllFiltersAndSort(page, processedPlaylists) : processedPlaylists;

                // Count playlists and collections based on Type
                const totalPlaylists = processedPlaylists.length;
                const filteredCount = filteredPlaylists.length;
                const playlistCount = processedPlaylists.filter(function (p) { return p.Type === 'Playlist' || !p.Type; }).length;
                const collectionCount = processedPlaylists.filter(function (p) { return p.Type === 'Collection'; }).length;

                let html = '';

                // Add bulk actions container after summary
                const summaryText = SmartLists.generateSummaryText ? SmartLists.generateSummaryText(totalPlaylists, playlistCount, collectionCount, filteredCount, null) : '';
                html += SmartLists.generateBulkActionsHTML ? SmartLists.generateBulkActionsHTML(summaryText) : '';

                // Process filtered playlists sequentially to resolve usernames
                for (let i = 0; i < filteredPlaylists.length; i++) {
                    const playlist = filteredPlaylists[i];
                    // Resolve username first
                    // Determine list type

                    // Resolve user name (both playlists and collections have a User/owner)
                    let resolvedUserName = await SmartLists.resolveUsername(apiClient, playlist);

                    // Resolve CreatedBy username
                    let createdByUserName = 'Unknown';
                    if (playlist.CreatedByUserId) {
                        createdByUserName = await SmartLists.resolveUserIdToName(apiClient, playlist.CreatedByUserId) || 'Unknown';
                    }

                    // Generate detailed rules display using helper function
                    const rulesHtml = await SmartLists.generateRulesHtml(playlist, apiClient);

                    // Use helper function to generate playlist HTML (DRY)
                    html += SmartLists.generatePlaylistCardHtml(playlist, rulesHtml, resolvedUserName, createdByUserName);
                }
                container.innerHTML = html;

                // Restore expand states from localStorage
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
            } else {
                container.innerHTML = '<div class="inputContainer"><p>No lists found.</p></div>';
            }

        } catch (err) {
            const errorMessage = SmartLists.displayApiError ? SmartLists.displayApiError(err, 'Failed to load playlists') : (err.message || 'Failed to load playlists');
            container.innerHTML = '<div class="inputContainer"><p style="color: #ff6b6b;">' + SmartLists.escapeHtml(errorMessage) + '</p></div>';
        } finally {
            // Always re-enable search input and clear loading flag
            SmartLists.setSearchInputState(page, false);
            page._loadingPlaylists = false;
        }
    };

})(window.SmartLists = window.SmartLists || {});

