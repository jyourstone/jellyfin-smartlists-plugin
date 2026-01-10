(function (SmartLists) {
    'use strict';

    SmartLists.handleApiError = function (err, defaultMessage) {
        // Guard against null/undefined defaultMessage
        const baseMessage = defaultMessage || 'An error occurred';

        // Try to extract meaningful error message from server response
        if (err && typeof err.text === 'function') {
            return err.text().then(function (serverMessage) {
                let friendlyMessage = baseMessage;
                try {
                    const parsedMessage = JSON.parse(serverMessage);

                    // Handle ValidationProblemDetails format (has 'errors' object with field-level errors)
                    if (parsedMessage && parsedMessage.errors && typeof parsedMessage.errors === 'object') {
                        const fieldErrors = [];
                        for (const field in parsedMessage.errors) {
                            if (parsedMessage.errors.hasOwnProperty(field)) {
                                const fieldErrorMessages = Array.isArray(parsedMessage.errors[field])
                                    ? parsedMessage.errors[field].join(', ')
                                    : parsedMessage.errors[field];
                                fieldErrors.push(field + ': ' + fieldErrorMessages);
                            }
                        }
                        if (fieldErrors.length > 0) {
                            friendlyMessage = baseMessage.replace(/\.$/, '') + ': ' + fieldErrors.join('; ');
                        } else if (parsedMessage.detail) {
                            friendlyMessage = baseMessage.replace(/\.$/, '') + ': ' + parsedMessage.detail;
                        }
                    }
                    // Handle ProblemDetails format (has 'detail' property)
                    else if (parsedMessage && parsedMessage.detail) {
                        friendlyMessage = baseMessage.replace(/\.$/, '') + ': ' + parsedMessage.detail;
                    }
                    // Handle other JSON error formats
                    else if (parsedMessage && parsedMessage.message) {
                        friendlyMessage = baseMessage.replace(/\.$/, '') + ': ' + parsedMessage.message;
                    } else if (parsedMessage && parsedMessage.title) {
                        friendlyMessage = baseMessage.replace(/\.$/, '') + ': ' + parsedMessage.title;
                    } else if (serverMessage && serverMessage.trim()) {
                        // Remove quotes and Unicode escapes, then add context
                        const cleanMessage = serverMessage
                            .replace(/"/g, '')
                            .replace(/\\u0027/g, "'")
                            .replace(/\\u0022/g, '"');
                        friendlyMessage = baseMessage.replace(/\.$/, '') + ': ' + cleanMessage;
                    }
                } catch (e) {
                    if (serverMessage && serverMessage.trim()) {
                        // Remove quotes and Unicode escapes, then add context
                        const cleanMessage = serverMessage
                            .replace(/"/g, '')
                            .replace(/\\u0027/g, "'")
                            .replace(/\\u0022/g, '"');
                        friendlyMessage = baseMessage.replace(/\.$/, '') + ': ' + cleanMessage;
                    }
                }
                SmartLists.showNotification(friendlyMessage);
                return Promise.resolve();
            }).catch(function () {
                SmartLists.showNotification(baseMessage + ' HTTP ' + (err.status || 'Unknown'));
                return Promise.resolve();
            });
        } else {
            SmartLists.showNotification(baseMessage + ' ' + ((err && err.message) ? err.message : 'Unknown error'));
            return Promise.resolve();
        }
    };

    SmartLists.loadUsers = function (page) {
        const apiClient = SmartLists.getApiClient();
        const userSelect = page.querySelector('#playlistUser');

        if (!userSelect) {
            console.warn('SmartLists.loadUsers: #playlistUser element not found');
            return Promise.resolve();
        }

        return apiClient.ajax({
            type: "GET",
            url: apiClient.getUrl(SmartLists.ENDPOINTS.users),
            contentType: 'application/json'
        }).then(function (response) {
            return response.json();
        }).then(function (users) {
            // Clear existing options
            userSelect.innerHTML = '';

            // Add user options
            users.forEach(function (user) {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                userSelect.appendChild(option);
            });

            // Set current user as default
            return SmartLists.setCurrentUserAsDefault(page);
        }).catch(function (err) {
            console.error('Error loading users:', err);
            userSelect.innerHTML = '<option value="">Error loading users</option>';
            SmartLists.showNotification('Failed to load users. Using fallback.');
            return Promise.resolve();
        });
    };

    SmartLists.setCurrentUserAsDefault = function (page) {
        const apiClient = SmartLists.getApiClient();
        const userSelect = page.querySelector('#playlistUser');

        if (!userSelect) {
            console.warn('SmartLists.setCurrentUserAsDefault: #playlistUser element not found');
            return Promise.resolve();
        }

        // Check if we're in edit/clone mode
        const editState = SmartLists.getPageEditState(page);

        // Don't overwrite if we have pending user IDs (from edit/clone mode)
        if (page._pendingUserIds && Array.isArray(page._pendingUserIds) && page._pendingUserIds.length > 0) {
            return Promise.resolve();
        }

        // Don't overwrite if a value is already set AND we're editing/cloning
        if (userSelect && userSelect.value && (editState.editMode || editState.cloneMode)) {
            return Promise.resolve();
        }

        // Don't overwrite if multi-select already has selections (playlists only)
        // Note: Collections use single-select, so skip this check for collections
        const listType = SmartLists.getElementValue(page, '#listType', 'Playlist');
        const isPlaylist = listType !== 'Collection';
        if (isPlaylist) {
            const checkboxes = page.querySelectorAll('#userMultiSelectOptions .user-multi-select-checkbox:checked');
            if (checkboxes.length > 0) {
                return Promise.resolve();
            }
        }

        // Clear the value first (it might have been auto-selected by the browser)
        if (userSelect && userSelect.value && !editState.editMode && !editState.cloneMode) {
            userSelect.value = '';
        }

        try {
            // Use client-side method to get current user
            let userId = apiClient.getCurrentUserId();

            if (!userId) {
                return apiClient.getCurrentUser().then(function (user) {
                    userId = user ? user.Id : null;
                    if (userId) {
                        userSelect.value = userId;
                        // Also set multi-select for playlists
                        if (SmartLists.setSelectedUserIds) {
                            SmartLists.setSelectedUserIds(page, [userId]);
                        }
                    }
                });
            } else {
                userSelect.value = userId;
                // Also set multi-select for playlists
                if (SmartLists.setSelectedUserIds) {
                    SmartLists.setSelectedUserIds(page, [userId]);
                }
                return Promise.resolve();
            }
        } catch (err) {
            console.error('Error setting current user as default:', err);
            return Promise.resolve();
        }
    };

    SmartLists.loadUsersForRule = function (userSelect, isOptional) {
        if (!userSelect) {
            console.warn('SmartLists.loadUsersForRule: userSelect element not provided');
            return Promise.resolve();
        }

        // On user pages, skip loading users (admin-only endpoint)
        if (SmartLists.IS_USER_PAGE) {
            // User pages don't show user selectors in rules
            return Promise.resolve();
        }

        isOptional = isOptional !== undefined ? isOptional : false;
        const apiClient = SmartLists.getApiClient();

        return apiClient.ajax({
            type: "GET",
            url: apiClient.getUrl(SmartLists.ENDPOINTS.users),
            contentType: 'application/json'
        }).then(function (response) {
            return response.json();
        }).then(function (users) {
            if (!isOptional) {
                userSelect.innerHTML = '';
            } else {
                // Remove all options except the first (default) if present
                while (userSelect.options.length > 1) {
                    userSelect.remove(1);
                }
            }

            // Add user options
            users.forEach(function (user) {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                userSelect.appendChild(option);
            });
        }).catch(function (err) {
            console.error('Error loading users for rule:', err);
            if (!isOptional) {
                userSelect.innerHTML = '<option value="">Error loading users</option>';
            }
            throw err;
        });
    };

    // Note: resolveUsername and resolveUserIdToName are defined in config-lists.js
    // Do not duplicate them here to avoid overwriting the implementation

    /**
     * Sets the user ID value in the playlist user dropdown, waiting for options to load if needed.
     * This function handles the case where users may not be loaded yet when setting the value.
     * @param {Object} page - The page DOM element
     * @param {string} userIdString - The user ID string to set
     */
    SmartLists.setUserIdValueWithRetry = function (page, userIdString) {
        if (!userIdString || userIdString === '00000000-0000-0000-0000-000000000000') {
            return;
        }

        // Check if element exists before proceeding
        const userSelect = page.querySelector('#playlistUser');
        if (!userSelect) {
            console.warn('SmartLists.setUserIdValueWithRetry: #playlistUser element not found');
            // Nothing to set on this page; stop immediately
            return;
        }

        // Function to set the User value
        const setUserIdValue = function () {
            const userSelect = page.querySelector('#playlistUser');

            if (!userSelect || !userSelect.options) {
                // Element doesn't exist or options not loaded yet
                return false;
            }

            // Check if the option exists in the dropdown
            const optionExists = Array.from(userSelect.options).some(function (opt) {
                return opt.value === userIdString;
            });
            if (optionExists) {
                SmartLists.setElementValue(page, '#playlistUser', userIdString);
                userSelect.value = userIdString;
                return true;
            }
            return false;
        };

        // Try to set immediately if users are loaded
        if (!setUserIdValue()) {
            // Users not loaded yet, wait for them to load
            const checkUsersLoaded = setInterval(function () {
                if (setUserIdValue()) {
                    clearInterval(checkUsersLoaded);
                }
            }, 50);
            // Timeout after 3 seconds
            setTimeout(function () {
                clearInterval(checkUsersLoaded);
            }, 3000);
        }
    };

    /**
     * Export all playlists as a ZIP file
     */
    SmartLists.exportPlaylists = function () {
        try {
            const apiClient = SmartLists.getApiClient();
            const url = apiClient.getUrl(SmartLists.ENDPOINTS.export);

            fetch(url, {
                method: 'POST',
                headers: {
                    'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"',
                    'Content-Type': 'application/json'
                }
            })
                .then(async function (response) {
                    if (!response.ok) {
                        let errorMessage = 'Export failed';
                        try {
                            const errorData = await response.json();
                            errorMessage = errorData.message || errorData.detail || errorMessage;
                        } catch (e) {
                            // Fallback to text if JSON parsing fails
                            try {
                                const errorText = await response.text();
                                if (errorText && errorText.trim()) {
                                    errorMessage = errorText;
                                }
                            } catch (textError) {
                                // Ignore text parsing errors, use default message
                            }
                        }
                        throw new Error(errorMessage);
                    }

                    // Get filename from Content-Disposition header BEFORE consuming the blob
                    const contentDisposition = response.headers.get('Content-Disposition');
                    let filename = 'smartlists_export.zip';
                    if (contentDisposition) {
                        const matches = contentDisposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
                        if (matches && matches[1]) {
                            filename = matches[1].replace(/['"]/g, '');
                        }
                    }

                    // Get the blob from response
                    const blob = await response.blob();
                    // Create download link
                    const blobUrl = window.URL.createObjectURL(blob);
                    const a = document.createElement('a');
                    a.href = blobUrl;
                    a.download = filename;
                    document.body.appendChild(a);
                    a.click();
                    window.URL.revokeObjectURL(blobUrl);
                    document.body.removeChild(a);
                    SmartLists.showNotification('Export completed successfully!', 'success');
                })
                .catch(function (error) {
                    console.error('Export error:', error);
                    SmartLists.showNotification('Export failed: ' + (error.message || 'Unknown error'), 'error');
                });
        } catch (error) {
            console.error('Export error:', error);
            SmartLists.showNotification('Export failed: ' + (error.message || 'Unknown error'), 'error');
        }
    };

    /**
     * Import playlists from selected ZIP file
     */
    SmartLists.importPlaylists = function (page) {
        const fileInput = page.querySelector('#importPlaylistsFile');

        if (!fileInput) {
            SmartLists.showNotification('Import file input not found on page', 'error');
            return;
        }

        const file = fileInput.files[0];

        if (!file) {
            SmartLists.showNotification('Please select a file to import', 'error');
            return;
        }

        // File size limit: 10MB
        const MAX_FILE_SIZE = 10 * 1024 * 1024;
        if (file.size > MAX_FILE_SIZE) {
            SmartLists.showNotification('File is too large (max 10MB)', 'error');
            return;
        }

        // Extension check as safety net (accept attribute already filters in dialog)
        if (!file.name.toLowerCase().endsWith('.zip')) {
            SmartLists.showNotification('Please select a ZIP file', 'error');
            return;
        }

        const formData = new FormData();
        formData.append('file', file);

        const apiClient = SmartLists.getApiClient();
        const url = apiClient.getUrl(SmartLists.ENDPOINTS.import);

        // Show loading indicator
        SmartLists.showNotification('Importing playlists...', 'info');

        fetch(url, {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
            },
            body: formData
        })
            .then(async function (response) {
                if (!response.ok) {
                    let errorMessage = 'Import failed';
                    try {
                        const errorData = await response.json();
                        errorMessage = errorData.message || errorData.detail || errorMessage;
                    } catch (e) {
                        // Fallback to text if JSON parsing fails
                        try {
                            const errorText = await response.text();
                            if (errorText && errorText.trim()) {
                                errorMessage = errorText;
                            }
                        } catch (textError) {
                            // Ignore text parsing errors, use default message
                        }
                    }
                    throw new Error(errorMessage);
                }
                return response.json();
            })
            .then(function (result) {
                // Clear file input and reset UI
                fileInput.value = '';

                // Hide import button and clear selected filename display
                const importBtn = page.querySelector('#importPlaylistsBtn');
                const selectedFileName = page.querySelector('#selectedFileName');
                if (importBtn) {
                    importBtn.style.display = 'none';
                    importBtn.disabled = true;
                }
                if (selectedFileName) {
                    selectedFileName.textContent = '';
                }

                // Backend returns: imported, skipped, errors, details
                const importedCount = result.imported || result.importedCount || 0;
                const skippedCount = result.skipped || result.skippedCount || 0;
                const errorCount = result.errors || result.errorCount || 0;
                const details = result.details || [];

                // Build detailed message
                let message = 'Import completed: ';
                const parts = [];

                if (importedCount > 0) {
                    parts.push(importedCount + ' imported');
                }
                if (skippedCount > 0) {
                    parts.push(skippedCount + ' skipped');
                }
                if (errorCount > 0) {
                    parts.push(errorCount + ' errors');
                }

                if (parts.length === 0) {
                    message = 'Import completed with no playlists processed.';
                } else {
                    message += parts.join(', ') + '.';
                }

                // Show appropriate notification type
                const notificationType = errorCount > 0 ? 'warning' : (importedCount > 0 ? 'success' : 'info');
                SmartLists.showNotification(message, notificationType);

                // Log detailed results to console for debugging
                if (details.length > 0) {
                    console.log('Import details:', details);
                }

                // Clear all checkbox selections
                const playlistCheckboxes = page.querySelectorAll('.playlist-checkbox');
                playlistCheckboxes.forEach(function (checkbox) {
                    checkbox.checked = false;
                });
                const selectAllCheckbox = page.querySelector('#selectAllCheckbox');
                if (selectAllCheckbox) {
                    selectAllCheckbox.checked = false;
                }
                // Update selected count display if function exists
                if (SmartLists.updateSelectedCount) {
                    SmartLists.updateSelectedCount(page);
                }

                // Switch to manage tab and scroll to top
                SmartLists.switchToTab(page, 'manage');
                window.scrollTo({ top: 0, behavior: 'auto' });

                // Refresh the playlist list
                if (SmartLists.loadPlaylistList) {
                    SmartLists.loadPlaylistList(page);
                }
            })
            .catch(function (error) {
                console.error('Import error:', error);
                SmartLists.showNotification('Import failed: ' + (error.message || 'Unknown error'), 'error');
            });
    };

})(window.SmartLists = window.SmartLists || {});

