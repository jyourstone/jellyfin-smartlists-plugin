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

        // Don't overwrite if we have a pending collection user ID (from edit/clone mode for collections)
        if (page._pendingCollectionUserId) {
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

        // Normalize user ID by removing hyphens (API returns IDs without hyphens, but stored IDs may have them)
        const normalizedUserId = userIdString.replace(/-/g, '');

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
                return opt.value === normalizedUserId;
            });
            if (optionExists) {
                SmartLists.setElementValue(page, '#playlistUser', normalizedUserId);
                userSelect.value = normalizedUserId;
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
     * Get list of available backups
     */
    SmartLists.getBackups = function () {
        const apiClient = SmartLists.getApiClient();
        const url = apiClient.getUrl(SmartLists.ENDPOINTS.backups);

        return fetch(url, {
            method: 'GET',
            headers: {
                'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
            }
        })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error('Failed to get backup list');
                }
                return response.json();
            });
    };

    /**
     * Create a new backup and download it
     */
    SmartLists.createBackup = function () {
        try {
            const apiClient = SmartLists.getApiClient();
            const url = apiClient.getUrl(SmartLists.ENDPOINTS.backups);

            SmartLists.showNotification('Creating backup...', 'info');

            fetch(url, {
                method: 'POST',
                headers: {
                    'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"',
                    'Content-Type': 'application/json'
                }
            })
                .then(async function (response) {
                    if (!response.ok) {
                        let errorMessage = 'Backup creation failed';
                        try {
                            const errorData = await response.json();
                            errorMessage = errorData.message || errorData.detail || errorMessage;
                        } catch (e) {
                            try {
                                const errorText = await response.text();
                                if (errorText && errorText.trim()) {
                                    errorMessage = errorText;
                                }
                            } catch (textError) {
                                // Ignore text parsing errors
                            }
                        }
                        throw new Error(errorMessage);
                    }

                    // Get filename from Content-Disposition header
                    const contentDisposition = response.headers.get('Content-Disposition');
                    let filename = 'smartlists_backup.zip';
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
                    SmartLists.showNotification('Backup created successfully!', 'success');

                    // Refresh backup list if function exists
                    if (SmartLists.loadBackupList) {
                        SmartLists.loadBackupList();
                    }
                })
                .catch(function (error) {
                    console.error('Backup creation error:', error);
                    SmartLists.showNotification('Backup creation failed: ' + (error.message || 'Unknown error'), 'error');
                });
        } catch (error) {
            console.error('Backup creation error:', error);
            SmartLists.showNotification('Backup creation failed: ' + (error.message || 'Unknown error'), 'error');
        }
    };

    /**
     * Download a specific backup file
     */
    SmartLists.downloadBackup = function (filename) {
        const apiClient = SmartLists.getApiClient();
        const url = apiClient.getUrl(SmartLists.ENDPOINTS.backups + '/' + encodeURIComponent(filename));

        fetch(url, {
            method: 'GET',
            headers: {
                'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
            }
        })
            .then(async function (response) {
                if (!response.ok) {
                    throw new Error('Failed to download backup');
                }
                const blob = await response.blob();
                const blobUrl = window.URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = blobUrl;
                a.download = filename;
                document.body.appendChild(a);
                a.click();
                window.URL.revokeObjectURL(blobUrl);
                document.body.removeChild(a);
            })
            .catch(function (error) {
                console.error('Download backup error:', error);
                SmartLists.showNotification('Failed to download backup: ' + (error.message || 'Unknown error'), 'error');
            });
    };

    /**
     * Restore from a server-side backup
     * @param {string} filename - The backup filename to restore from
     * @param {Element} page - The page element
     * @param {boolean} overwrite - If true, overwrite existing lists with the same ID
     */
    SmartLists.restoreFromBackup = function (filename, page, overwrite) {
        const apiClient = SmartLists.getApiClient();
        var urlPath = SmartLists.ENDPOINTS.backups + '/' + encodeURIComponent(filename) + '/restore';
        if (overwrite) {
            urlPath += '?overwrite=true';
        }
        const url = apiClient.getUrl(urlPath);

        SmartLists.showNotification('Restoring from backup...', 'info');

        fetch(url, {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
            }
        })
            .then(async function (response) {
                if (!response.ok) {
                    let errorMessage = 'Restore failed';
                    try {
                        const errorData = await response.json();
                        errorMessage = errorData.message || errorData.detail || errorMessage;
                    } catch (e) {
                        // Ignore
                    }
                    throw new Error(errorMessage);
                }
                return response.json();
            })
            .then(function (result) {
                SmartLists._handleRestoreResult(result, page);
            })
            .catch(function (error) {
                console.error('Restore error:', error);
                SmartLists.showNotification('Restore failed: ' + (error.message || 'Unknown error'), 'error');
            });
    };

    /**
     * Delete a backup file
     */
    SmartLists.deleteBackup = function (filename) {
        const apiClient = SmartLists.getApiClient();
        const url = apiClient.getUrl(SmartLists.ENDPOINTS.backups + '/' + encodeURIComponent(filename));

        return fetch(url, {
            method: 'DELETE',
            headers: {
                'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
            }
        })
            .then(async function (response) {
                if (!response.ok) {
                    let errorMessage = 'Delete failed';
                    try {
                        const errorData = await response.json();
                        errorMessage = errorData.message || errorData.detail || errorMessage;
                    } catch (e) {
                        // Ignore
                    }
                    throw new Error(errorMessage);
                }
                SmartLists.showNotification('Backup deleted successfully', 'success');
                // Refresh backup list
                if (SmartLists.loadBackupList) {
                    SmartLists.loadBackupList();
                }
            })
            .catch(function (error) {
                console.error('Delete backup error:', error);
                SmartLists.showNotification('Failed to delete backup: ' + (error.message || 'Unknown error'), 'error');
            });
    };

    /**
     * Preview an uploaded backup file to get metadata like list count
     * @param {File} file - The file to preview
     * @returns {Promise<{filename: string, sizeBytes: number, listCount: number}>}
     */
    SmartLists.previewBackupFile = function (file) {
        const formData = new FormData();
        formData.append('file', file);

        const apiClient = SmartLists.getApiClient();
        const url = apiClient.getUrl(SmartLists.ENDPOINTS.backupPreview);

        return fetch(url, {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
            },
            body: formData
        })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error('Failed to preview backup');
                }
                return response.json();
            });
    };

    /**
     * Upload and restore from a file
     */
    SmartLists.uploadAndRestore = function (page) {
        const fileInput = page.querySelector('#restoreFile');

        if (!fileInput) {
            SmartLists.showNotification('Restore file input not found on page', 'error');
            return;
        }

        const file = fileInput.files[0];

        if (!file) {
            SmartLists.showNotification('Please select a file to restore', 'error');
            return;
        }

        // File size limit: 1GB
        const MAX_FILE_SIZE = 1 * 1024 * 1024 * 1024;
        if (file.size > MAX_FILE_SIZE) {
            SmartLists.showNotification('File is too large (max 1GB)', 'error');
            return;
        }

        // Extension check
        if (!file.name.toLowerCase().endsWith('.zip')) {
            SmartLists.showNotification('Please select a ZIP file', 'error');
            return;
        }

        // Get overwrite checkbox value
        var overwriteCheckbox = page.querySelector('#restoreOverwriteCheckbox');
        var overwrite = overwriteCheckbox && overwriteCheckbox.checked;

        const formData = new FormData();
        formData.append('file', file);

        const apiClient = SmartLists.getApiClient();
        var urlPath = SmartLists.ENDPOINTS.backupUpload;
        if (overwrite) {
            urlPath += '?overwrite=true';
        }
        const url = apiClient.getUrl(urlPath);

        SmartLists.showNotification('Restoring from file...', 'info');

        fetch(url, {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
            },
            body: formData
        })
            .then(async function (response) {
                if (!response.ok) {
                    let errorMessage = 'Restore failed';
                    try {
                        const errorData = await response.json();
                        errorMessage = errorData.message || errorData.detail || errorMessage;
                    } catch (e) {
                        try {
                            const errorText = await response.text();
                            if (errorText && errorText.trim()) {
                                errorMessage = errorText;
                            }
                        } catch (textError) {
                            // Ignore
                        }
                    }
                    throw new Error(errorMessage);
                }
                return response.json();
            })
            .then(function (result) {
                // Reset the drop zone UI
                if (SmartLists.resetRestoreDropZone) {
                    SmartLists.resetRestoreDropZone();
                }
                SmartLists._handleRestoreResult(result, page);
            })
            .catch(function (error) {
                console.error('Restore error:', error);
                SmartLists.showNotification('Restore failed: ' + (error.message || 'Unknown error'), 'error');
            });
    };

    /**
     * Handle restore result (shared by restoreFromBackup and uploadAndRestore)
     */
    SmartLists._handleRestoreResult = function (result, page) {
        // Backend returns: restored, overwritten, skipped, errors, details
        var restoredCount = result.restored || 0;
        var overwrittenCount = result.overwritten || 0;
        var skippedCount = result.skipped || 0;
        var errorCount = result.errors || 0;
        var details = result.details || [];

        // Build detailed message
        var message = 'Restore completed: ';
        var parts = [];

        if (restoredCount > 0) {
            parts.push(restoredCount + ' restored');
        }
        if (overwrittenCount > 0) {
            parts.push(overwrittenCount + ' overwritten');
        }
        if (skippedCount > 0) {
            parts.push(skippedCount + ' skipped');
        }
        if (errorCount > 0) {
            parts.push(errorCount + ' errors');
        }

        if (parts.length === 0) {
            message = 'Restore completed with no lists processed.';
        } else {
            message += parts.join(', ') + '.';
        }

        // Show appropriate notification type
        var totalProcessed = restoredCount + overwrittenCount;
        var notificationType = errorCount > 0 ? 'warning' : (totalProcessed > 0 ? 'success' : 'info');
        SmartLists.showNotification(message, notificationType);

        // Log detailed results to console
        if (details.length > 0) {
            console.log('Restore details:', details);
        }

        // Clear all checkbox selections
        if (page) {
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
        }
    };

})(window.SmartLists = window.SmartLists || {});

