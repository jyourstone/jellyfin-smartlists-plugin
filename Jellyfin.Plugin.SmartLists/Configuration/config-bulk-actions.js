(function (SmartLists) {
    'use strict';

    // Initialize namespace if it doesn't exist
    if (!SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }

    // ===== HELPER FUNCTIONS FOR ACTION OPERATIONS =====

    /**
     * Generic helper for performing bulk list actions
     * @param {Object} page - The page element
     * @param {Object} options - Configuration options
     * @param {string} options.actionType - The action type (e.g., 'enable', 'disable', 'delete')
     * @param {string} options.apiPath - The API path (e.g., '/enable', '/disable', or '' for delete)
     * @param {string} options.httpMethod - HTTP method ('POST' or 'DELETE')
     * @param {Function} [options.filterFunction] - Optional function to filter which lists to act on
     * @param {Function} [options.getQueryParams] - Optional function to get query parameters
     * @param {Function} [options.formatSuccessMessage] - Custom success message formatter (successCount, page) => string
     * @param {Function} [options.formatErrorMessage] - Custom error message formatter (errorCount, successCount) => string
     */
    SmartLists.performBulkListAction = async function (page, options) {
        const selectedCheckboxes = page.querySelectorAll('.playlist-checkbox:checked');
        const listIds = Array.prototype.slice.call(selectedCheckboxes).map(function (cb) {
            return cb.getAttribute('data-playlist-id');
        });

        if (listIds.length === 0) {
            SmartLists.showNotification('No lists selected', 'error');
            return;
        }

        // Apply filter function if provided (e.g., to skip already enabled/disabled items)
        let listsToProcess = listIds;
        if (options.filterFunction) {
            const filterResult = options.filterFunction(selectedCheckboxes);
            listsToProcess = filterResult.filtered;

            if (listsToProcess.length === 0) {
                SmartLists.showNotification(filterResult.message, 'info');
                return;
            }
        }

        const apiClient = SmartLists.getApiClient();

        // If enabling, show notification about refresh starting
        if (options.actionType === 'enable' && listsToProcess.length > 0) {
            var statusLink = SmartLists.createStatusPageLink('status page');
            var enableListWord = listsToProcess.length === 1 ? 'List has' : 'Lists have';
            var refreshMessage = SmartLists.IS_USER_PAGE
                ? enableListWord + ' been enabled. A refresh will be triggered automatically in the background.'
                : enableListWord + ' been enabled. A refresh will be triggered automatically, check the ' + statusLink + ' for progress.';
            SmartLists.showNotification(refreshMessage, 'info', { html: true });
        }

        // If disabling, show notification about Jellyfin list removal
        if (options.actionType === 'disable' && listsToProcess.length > 0) {
            var disableListWord = listsToProcess.length === 1 ? 'list' : 'lists';
            SmartLists.showNotification('Disabling ' + disableListWord + ' and removing Jellyfin ' + disableListWord + '...', 'info', { html: true });
        }

        // Process sequentially in background
        // Enable/disable operations enqueue refresh operations through the queue system
        // Processing sequentially ensures each operation completes before the next starts
        let successCount = 0;
        let errorCount = 0;

        // Clear selections immediately (before API calls)
        const selectAllCheckbox = page.querySelector('#selectAllCheckbox');
        if (selectAllCheckbox) {
            selectAllCheckbox.checked = false;
        }

        // Process sequentially in background
        (async function () {
            for (const listId of listsToProcess) {
                let url = SmartLists.ENDPOINTS.base + '/' + listId + options.apiPath;
                if (options.getQueryParams) {
                    url += '?' + options.getQueryParams(page);
                }

                try {
                    const response = await apiClient.ajax({
                        type: options.httpMethod,
                        url: apiClient.getUrl(url),
                        contentType: 'application/json'
                    });

                    if (!response.ok) {
                        const errorMessage = await SmartLists.extractErrorMessage(response, 'HTTP ' + response.status + ': ' + response.statusText);
                        console.error('Error ' + options.actionType + ' list:', listId, errorMessage);
                        errorCount++;
                    } else {
                        successCount++;
                    }
                } catch (err) {
                    console.error('Error ' + options.actionType + ' list:', listId, err);
                    errorCount++;
                }
            }

            // Show success notification after all API calls complete
            // Skip success notification for enable actions (info notification already shown)
            if (successCount > 0 && options.actionType !== 'enable') {
                const successListWord = successCount === 1 ? 'list' : 'lists';
                const message = options.formatSuccessMessage
                    ? options.formatSuccessMessage(successCount, page)
                    : 'Successfully ' + options.actionType + ' ' + successCount + ' ' + successListWord + '.';

                if (message) {
                    SmartLists.showNotification(message, 'success');
                }
            }

            // If there were errors, show error notification
            if (errorCount > 0) {
                const errorListWord = errorCount === 1 ? 'list' : 'lists';
                const message = options.formatErrorMessage
                    ? options.formatErrorMessage(errorCount, successCount)
                    : 'Failed to ' + options.actionType + ' ' + errorCount + ' ' + errorListWord + '.';
                SmartLists.showNotification(message, 'error');
            }

            // Reload list to show updated state
            if (SmartLists.loadPlaylistList) {
                SmartLists.loadPlaylistList(page);
            }
        })();
    };

    /**
     * Generic helper for performing individual list actions
     * @param {Object} page - The page element
     * @param {string} listId - The list ID
     * @param {string} listName - The list name
     * @param {Object} options - Configuration options
     * @param {string} options.actionType - The action type (e.g., 'enable', 'disable', 'delete')
     * @param {string} options.apiPath - The API path (e.g., '/enable', '/disable', or '' for delete)
     * @param {string} options.httpMethod - HTTP method ('POST' or 'DELETE')
     * @param {Function} [options.getQueryParams] - Optional function to get query parameters
     * @param {Function} [options.formatSuccessMessage] - Custom success message formatter
     */
    SmartLists.performListAction = async function (page, listId, listName, options) {
        const apiClient = SmartLists.getApiClient();

        let url = SmartLists.ENDPOINTS.base + '/' + listId + options.apiPath;
        if (options.getQueryParams) {
            url += '?' + options.getQueryParams(page);
        }

        // If enabling, show notification about refresh starting
        if (options.actionType === 'enable') {
            var statusLink = SmartLists.createStatusPageLink('status page');
            var refreshMessage = SmartLists.IS_USER_PAGE
                ? 'List has been enabled. A refresh will be triggered automatically in the background.'
                : 'List has been enabled. A refresh will be triggered automatically, check the ' + statusLink + ' for progress.';
            SmartLists.showNotification(refreshMessage, 'info', { html: true });
        }

        // If disabling, show notification about Jellyfin list removal
        if (options.actionType === 'disable') {
            SmartLists.showNotification('Disabling list and removing Jellyfin list...', 'info', { html: true });
        }

        // Make API call
        try {
            const response = await apiClient.ajax({
                type: options.httpMethod,
                url: apiClient.getUrl(url),
                contentType: 'application/json'
            });

            if (!response.ok) {
                const errorMessage = await SmartLists.extractErrorMessage(response, 'HTTP ' + response.status + ': ' + response.statusText);
                throw new Error(errorMessage);
            }

            // Show success notification after API call completes
            // Skip success notification for enable actions (info notification already shown)
            if (options.actionType !== 'enable') {
                const message = options.formatSuccessMessage
                    ? options.formatSuccessMessage(listName, page)
                    : 'List "' + listName + '" ' + options.actionType + ' successfully.';
                SmartLists.showNotification(message, 'success');
            }

            // Reload list after API call completes to show accurate updated values
            if (SmartLists.loadPlaylistList) {
                SmartLists.loadPlaylistList(page);
            }
        } catch (err) {
            // Reload list on error to show correct state
            if (SmartLists.loadPlaylistList) {
                SmartLists.loadPlaylistList(page);
            }
            SmartLists.displayApiError(err, 'Failed to ' + options.actionType + ' list "' + listName + '"');
        }
    };

    SmartLists.getBulkActionElements = function (page, forceRefresh) {
        forceRefresh = forceRefresh !== undefined ? forceRefresh : false;
        if (!page._bulkActionElements || forceRefresh) {
            page._bulkActionElements = {
                bulkContainer: page.querySelector('#bulkActionsContainer'),
                countDisplay: page.querySelector('#selectedCountDisplay'),
                bulkActionSelect: page.querySelector('#bulkActionSelect'),
                bulkApplyBtn: page.querySelector('#bulkApplyBtn'),
                selectAllCheckbox: page.querySelector('#selectAllCheckbox')
            };
        }
        return page._bulkActionElements;
    };

    // Setup change listener for the bulk action dropdown to enable/disable Apply button
    SmartLists.setupBulkActionDropdownListener = function (page, pageSignal) {
        const elements = SmartLists.getBulkActionElements(page, true);
        if (elements.bulkActionSelect) {
            elements.bulkActionSelect.addEventListener('change', function () {
                const hasAction = this.value !== '';
                const selectedCheckboxes = page.querySelectorAll('.playlist-checkbox:checked');
                const hasSelection = selectedCheckboxes.length > 0;

                if (elements.bulkApplyBtn) {
                    const isDisabled = !hasSelection || !hasAction;
                    elements.bulkApplyBtn.disabled = isDisabled;
                    // Update tooltip based on state
                    if (isDisabled) {
                        if (!hasSelection) {
                            elements.bulkApplyBtn.title = 'Select items first';
                        } else {
                            elements.bulkApplyBtn.title = 'Choose an action from the dropdown';
                        }
                    } else {
                        elements.bulkApplyBtn.title = 'Apply the selected action';
                    }
                }
            }, SmartLists.getEventListenerOptions(pageSignal));
        }
    };

    // Bulk operations functionality
    SmartLists.updateBulkActionsVisibility = function (page) {
        const elements = SmartLists.getBulkActionElements(page, true); // Force refresh after HTML changes
        const checkboxes = page.querySelectorAll('.playlist-checkbox');

        // Show bulk actions if any playlists exist
        if (elements.bulkContainer) {
            elements.bulkContainer.style.display = checkboxes.length > 0 ? 'block' : 'none';
        }

        // Setup dropdown change listener (safe to call multiple times since HTML is regenerated)
        SmartLists.setupBulkActionDropdownListener(page);

        // Update selected count and button states
        SmartLists.updateSelectedCount(page);
    };

    SmartLists.updateSelectedCount = function (page) {
        const selectedCheckboxes = page.querySelectorAll('.playlist-checkbox:checked');
        const selectedCount = selectedCheckboxes.length;
        const elements = SmartLists.getBulkActionElements(page);

        // Update count display
        if (elements.countDisplay) {
            elements.countDisplay.textContent = '(' + selectedCount + ')';
        }

        // Update apply button state (dropdown always enabled for better UX)
        const hasSelection = selectedCount > 0;
        if (elements.bulkActionSelect) {
            // Reset dropdown selection when no items are selected
            if (!hasSelection) {
                elements.bulkActionSelect.value = '';
            }
        }
        if (elements.bulkApplyBtn) {
            // Apply button is enabled only when there's a selection AND an action is chosen
            const hasAction = elements.bulkActionSelect && elements.bulkActionSelect.value !== '';
            const isDisabled = !hasSelection || !hasAction;
            elements.bulkApplyBtn.disabled = isDisabled;
            // Update tooltip based on state
            if (isDisabled) {
                if (!hasSelection) {
                    elements.bulkApplyBtn.title = 'Select items first';
                } else {
                    elements.bulkApplyBtn.title = 'Choose an action from the dropdown';
                }
            } else {
                elements.bulkApplyBtn.title = 'Apply the selected action';
            }
        }

        // Update Select All checkbox state
        if (elements.selectAllCheckbox) {
            const totalCheckboxes = page.querySelectorAll('.playlist-checkbox').length;
            if (totalCheckboxes > 0) {
                elements.selectAllCheckbox.checked = selectedCount === totalCheckboxes;
                elements.selectAllCheckbox.indeterminate = selectedCount > 0 && selectedCount < totalCheckboxes;
            }
        }
    };

    SmartLists.toggleSelectAll = function (page) {
        const elements = SmartLists.getBulkActionElements(page);
        const playlistCheckboxes = page.querySelectorAll('.playlist-checkbox');

        const shouldSelect = elements.selectAllCheckbox ? elements.selectAllCheckbox.checked : false;

        playlistCheckboxes.forEach(function (checkbox) {
            checkbox.checked = shouldSelect;
        });

        SmartLists.updateSelectedCount(page);
    };

    SmartLists.bulkEnablePlaylists = async function (page) {
        await SmartLists.performBulkListAction(page, {
            actionType: 'enable',
            apiPath: '/enable',
            httpMethod: 'POST',
            filterFunction: function (selectedCheckboxes) {
                const listsToEnable = [];

                for (var i = 0; i < selectedCheckboxes.length; i++) {
                    const checkbox = selectedCheckboxes[i];
                    const listId = checkbox.getAttribute('data-playlist-id');
                    const playlistCard = checkbox.closest('.playlist-card');
                    const isCurrentlyEnabled = playlistCard ? playlistCard.dataset.enabled === 'true' : true;

                    if (!isCurrentlyEnabled) {
                        listsToEnable.push(listId);
                    }
                }

                return {
                    filtered: listsToEnable,
                    message: 'All selected lists are already enabled'
                };
            },
            formatSuccessMessage: function (count) {
                var listWord = count === 1 ? 'list' : 'lists';
                return count + ' ' + listWord + ' enabled successfully';
            },
            formatErrorMessage: function (errorCount, successCount) {
                return (successCount || 0) + ' enabled, ' + errorCount + ' failed';
            }
        });
    };

    SmartLists.bulkDisablePlaylists = async function (page) {
        await SmartLists.performBulkListAction(page, {
            actionType: 'disable',
            apiPath: '/disable',
            httpMethod: 'POST',
            filterFunction: function (selectedCheckboxes) {
                const listsToDisable = [];

                for (var i = 0; i < selectedCheckboxes.length; i++) {
                    const checkbox = selectedCheckboxes[i];
                    const listId = checkbox.getAttribute('data-playlist-id');
                    const playlistCard = checkbox.closest('.playlist-card');
                    const isCurrentlyEnabled = playlistCard ? playlistCard.dataset.enabled === 'true' : true;

                    if (isCurrentlyEnabled) {
                        listsToDisable.push(listId);
                    }
                }

                return {
                    filtered: listsToDisable,
                    message: 'All selected lists are already disabled'
                };
            },
            formatSuccessMessage: function (count) {
                var listWord = count === 1 ? 'list' : 'lists';
                return count + ' ' + listWord + ' disabled successfully';
            },
            formatErrorMessage: function (errorCount, successCount) {
                return (successCount || 0) + ' disabled, ' + errorCount + ' failed';
            }
        });
    };

    SmartLists.bulkRefreshPlaylists = async function (page) {
        const selectedCheckboxes = page.querySelectorAll('.playlist-checkbox:checked');
        const allListIds = Array.prototype.slice.call(selectedCheckboxes).map(function (cb) {
            return cb.getAttribute('data-playlist-id');
        });

        if (allListIds.length === 0) {
            SmartLists.showNotification('No lists selected', 'error');
            return;
        }

        // Filter out disabled lists - check against page._allPlaylists data
        const enabledListIds = [];
        const disabledListIds = [];
        
        allListIds.forEach(function (listId) {
            if (page._allPlaylists) {
                const playlist = page._allPlaylists.find(function (p) { return p.Id === listId; });
                if (playlist) {
                    // Default to true for backward compatibility (if Enabled property doesn't exist)
                    const isEnabled = playlist.Enabled !== false;
                    if (isEnabled) {
                        enabledListIds.push(listId);
                    } else {
                        disabledListIds.push(listId);
                    }
                } else {
                    // If playlist not found in data, assume it's enabled (fallback)
                    enabledListIds.push(listId);
                }
            } else {
                // If no playlist data available, assume all are enabled (fallback)
                enabledListIds.push(listId);
            }
        });

        // If all selected lists are disabled, show error and return
        if (enabledListIds.length === 0) {
            SmartLists.showNotification('All selected lists are disabled. Enable them first before refreshing.', 'error');
            return;
        }

        // Show notification about what we're doing
        var statusLink = SmartLists.createStatusPageLink('status page');
        var refreshMessage;

        if (disabledListIds.length > 0) {
            // Some lists were skipped
            var skippedWord = disabledListIds.length === 1 ? 'list' : 'lists';
            var refreshingWord = enabledListIds.length === 1 ? 'list' : 'lists';
            refreshMessage = SmartLists.IS_USER_PAGE
                ? 'Skipped ' + disabledListIds.length + ' disabled ' + skippedWord + '. Refreshing ' + enabledListIds.length + ' enabled ' + refreshingWord + ' in the background.'
                : 'Skipped ' + disabledListIds.length + ' disabled ' + skippedWord + '. Refreshing ' + enabledListIds.length + ' enabled ' + refreshingWord + '. Check the ' + statusLink + ' for progress.';
        } else {
            // All lists are enabled
            var selectedWord = enabledListIds.length === 1 ? 'list' : 'lists';
            refreshMessage = SmartLists.IS_USER_PAGE
                ? 'Refresh started for ' + enabledListIds.length + ' ' + selectedWord + '. Your ' + selectedWord + ' will be updated in the background.'
                : 'Refresh started for ' + enabledListIds.length + ' ' + selectedWord + '. Check the ' + statusLink + ' for progress.';
        }
        
        SmartLists.showNotification(refreshMessage, 'info', { html: true });

        // Start aggressive polling on status page to catch the operation (admin only)
        if (!SmartLists.IS_USER_PAGE && window.SmartLists && window.SmartLists.Status && window.SmartLists.Status.startAggressivePolling) {
            window.SmartLists.Status.startAggressivePolling();
        }

        // Process only enabled lists
        const apiClient = SmartLists.getApiClient();
        let successCount = 0;
        let errorCount = 0;

        // Clear selections immediately (before API calls)
        const selectAllCheckbox = page.querySelector('#selectAllCheckbox');
        if (selectAllCheckbox) {
            selectAllCheckbox.checked = false;
        }

        // Process sequentially in background
        (async function () {
            for (const listId of enabledListIds) {
                const url = SmartLists.ENDPOINTS.base + '/' + listId + '/refresh';

                try {
                    const response = await apiClient.ajax({
                        type: 'POST',
                        url: apiClient.getUrl(url),
                        contentType: 'application/json'
                    });

                    if (!response.ok) {
                        const errorMessage = await SmartLists.extractErrorMessage(response, 'HTTP ' + response.status + ': ' + response.statusText);
                        console.error('Error refreshing list:', listId, errorMessage);
                        errorCount++;
                    } else {
                        successCount++;
                    }
                } catch (err) {
                    console.error('Error refreshing list:', listId, err);
                    errorCount++;
                }
            }

            // If there were errors, show error notification
            if (errorCount > 0) {
                var refreshErrorWord = errorCount === 1 ? 'list' : 'lists';
                SmartLists.showNotification('Failed to trigger refresh for ' + errorCount + ' ' + refreshErrorWord + '.', 'error');
            }

            // Reload list to show updated state
            if (SmartLists.loadPlaylistList) {
                SmartLists.loadPlaylistList(page);
            }
        })();
    };

    // Refresh confirmation modal function
    SmartLists.showRefreshConfirmModal = function (page, onConfirm) {
        const modal = page.querySelector('#refresh-confirm-modal');
        if (!modal) return;

        // Clean up any existing modal listeners
        SmartLists.cleanupModalListeners(modal);

        // Apply modal styles using centralized configuration
        const modalContainer = modal.querySelector('.custom-modal-container');
        SmartLists.applyStyles(modalContainer, SmartLists.STYLES.modal.container);
        SmartLists.applyStyles(modal, SmartLists.STYLES.modal.backdrop);

        // Show the modal
        modal.classList.remove('hide');

        // Create AbortController for modal event listeners
        const modalAbortController = SmartLists.createAbortController();
        const modalSignal = modalAbortController ? modalAbortController.signal : null;

        // Clean up function to close modal and remove all listeners
        const cleanupAndClose = function () {
            modal.classList.add('hide');
            SmartLists.cleanupModalListeners(modal);
        };

        // Handle confirm button
        const confirmBtn = modal.querySelector('.modal-confirm-btn');
        confirmBtn.addEventListener('click', function () {
            cleanupAndClose();
            onConfirm();
        }, SmartLists.getEventListenerOptions(modalSignal));

        // Handle cancel button
        const cancelBtn = modal.querySelector('.modal-cancel-btn');
        cancelBtn.addEventListener('click', function () {
            cleanupAndClose();
        }, SmartLists.getEventListenerOptions(modalSignal));

        // Handle backdrop click
        modal.addEventListener('click', function (e) {
            if (e.target === modal) {
                cleanupAndClose();
            }
        }, SmartLists.getEventListenerOptions(modalSignal));

        // Store abort controller for cleanup
        modal._modalAbortController = modalAbortController;
    };

    // Generic delete modal function to reduce duplication
    SmartLists.showDeleteModal = function (page, confirmText, onConfirm) {
        const modal = page.querySelector('#delete-confirm-modal');
        if (!modal) return;

        // Clean up any existing modal listeners
        SmartLists.cleanupModalListeners(modal);

        // Apply modal styles using centralized configuration
        const modalContainer = modal.querySelector('.custom-modal-container');
        SmartLists.applyStyles(modalContainer, SmartLists.STYLES.modal.container);
        SmartLists.applyStyles(modal, SmartLists.STYLES.modal.backdrop);

        // Set the confirmation text with proper line break handling
        const confirmTextElement = modal.querySelector('#delete-confirm-text');
        confirmTextElement.textContent = confirmText;
        confirmTextElement.style.whiteSpace = 'pre-line';

        // Reset checkbox to checked by default
        const checkbox = modal.querySelector('#delete-jellyfin-playlist-checkbox');
        if (checkbox) {
            checkbox.checked = true;
        }

        // Show the modal
        modal.classList.remove('hide');

        // Create AbortController for modal event listeners
        const modalAbortController = SmartLists.createAbortController();
        const modalSignal = modalAbortController ? modalAbortController.signal : null;

        // Clean up function to close modal and remove all listeners
        const cleanupAndClose = function () {
            modal.classList.add('hide');
            SmartLists.cleanupModalListeners(modal);
        };

        // Handle confirm button
        const confirmBtn = modal.querySelector('#delete-confirm-btn');
        confirmBtn.addEventListener('click', function () {
            cleanupAndClose();
            onConfirm();
        }, SmartLists.getEventListenerOptions(modalSignal));

        // Handle cancel button
        const cancelBtn = modal.querySelector('#delete-cancel-btn');
        cancelBtn.addEventListener('click', function () {
            cleanupAndClose();
        }, SmartLists.getEventListenerOptions(modalSignal));

        // Handle backdrop click
        modal.addEventListener('click', function (e) {
            if (e.target === modal) {
                cleanupAndClose();
            }
        }, SmartLists.getEventListenerOptions(modalSignal));

        // Store abort controller for cleanup
        modal._modalAbortController = modalAbortController;
    };

    SmartLists.showBulkDeleteConfirm = function (page, listIds, listNames) {
        const listList = listNames.length > 5
            ? listNames.slice(0, 5).join('\n') + '\n... and ' + (listNames.length - 5) + ' more'
            : listNames.join('\n');

        const isPlural = listNames.length !== 1;
        const confirmText = 'Are you sure you want to delete the following ' + (isPlural ? 'lists' : 'list') + '?\n\n' + listList + '\n\nThis action cannot be undone.';

        SmartLists.showDeleteModal(page, confirmText, function () {
            SmartLists.performBulkDelete(page, listIds);
        });
    };

    SmartLists.performBulkDelete = async function (page, listIds) {
        // For bulk delete, we need to pass the listIds directly since they come from the confirm modal
        // Instead of getting them from checkboxes again
        const apiClient = SmartLists.getApiClient();
        const deleteCheckbox = page.querySelector('#delete-jellyfin-playlist-checkbox');
        const deleteJellyfinList = deleteCheckbox ? deleteCheckbox.checked : false;
        let successCount = 0;
        let errorCount = 0;

        Dashboard.showLoadingMsg();

        const promises = listIds.map(function (listId) {
            const url = SmartLists.ENDPOINTS.base + '/' + listId + '?deleteJellyfinList=' + deleteJellyfinList;
            return apiClient.ajax({
                type: 'DELETE',
                url: apiClient.getUrl(url),
                contentType: 'application/json'
            }).then(function (response) {
                if (!response.ok) {
                    return SmartLists.extractErrorMessage(response, 'HTTP ' + response.status + ': ' + response.statusText)
                        .then(function (errorMessage) {
                            console.error('Error deleting list:', listId, errorMessage);
                            errorCount++;
                            const err = new Error(errorMessage);
                            err._smartListsHttpError = true;
                            throw err;
                        });
                } else {
                    successCount++;
                }
            }).catch(function (err) {
                // Only increment errorCount for non-HTTP/transport errors
                if (!err._smartListsHttpError) {
                    console.error('Error deleting list:', listId, err);
                    errorCount++;
                }
            });
        });

        await Promise.all(promises);
        Dashboard.hideLoadingMsg();

        if (successCount > 0) {
            const action = deleteJellyfinList ? 'deleted' : 'suffix/prefix removed (if any) and configuration deleted';
            const deleteSuccessWord = successCount === 1 ? 'list' : 'lists';
            SmartLists.showNotification('Successfully ' + action + ' ' + successCount + ' ' + deleteSuccessWord + '.', 'success');
        }
        if (errorCount > 0) {
            const deleteErrorWord = errorCount === 1 ? 'list' : 'lists';
            SmartLists.showNotification('Failed to delete ' + errorCount + ' ' + deleteErrorWord + '.', 'error');
        }

        // Clear selections and reload
        const selectAllCheckbox = page.querySelector('#selectAllCheckbox');
        if (selectAllCheckbox) {
            selectAllCheckbox.checked = false;
        }
        if (SmartLists.loadPlaylistList) {
            SmartLists.loadPlaylistList(page);
        }
    };

    SmartLists.bulkDeletePlaylists = function (page) {
        const selectedCheckboxes = page.querySelectorAll('.playlist-checkbox:checked');
        const listIds = Array.prototype.slice.call(selectedCheckboxes).map(function (cb) {
            return cb.getAttribute('data-playlist-id');
        });

        if (listIds.length === 0) {
            SmartLists.showNotification('No lists selected', 'error');
            return;
        }

        const listNames = Array.prototype.slice.call(selectedCheckboxes).map(function (cb) {
            const playlistCard = cb.closest('.playlist-card');
            const nameElement = playlistCard ? playlistCard.querySelector('.playlist-header-left h3') : null;
            return nameElement ? nameElement.textContent : 'Unknown';
        });

        // Show the custom modal instead of browser confirm
        SmartLists.showBulkDeleteConfirm(page, listIds, listNames);
    };

    // Handler for the bulk action Apply button
    SmartLists.handleBulkApply = function (page) {
        const elements = SmartLists.getBulkActionElements(page);
        const action = elements.bulkActionSelect ? elements.bulkActionSelect.value : '';

        if (!action) {
            SmartLists.showNotification('Please select an action', 'error');
            return;
        }

        switch (action) {
            case 'enable':
                SmartLists.bulkEnablePlaylists(page);
                break;
            case 'disable':
                SmartLists.bulkDisablePlaylists(page);
                break;
            case 'refresh':
                SmartLists.bulkRefreshPlaylists(page);
                break;
            case 'delete':
                SmartLists.bulkDeletePlaylists(page);
                break;
            case 'convertToPlaylist':
                SmartLists.bulkConvertToPlaylist(page);
                break;
            case 'convertToCollection':
                SmartLists.bulkConvertToCollection(page);
                break;
            default:
                SmartLists.showNotification('Unknown action: ' + action, 'error');
        }

        // Reset dropdown after action
        if (elements.bulkActionSelect) {
            elements.bulkActionSelect.value = '';
        }
        if (elements.bulkApplyBtn) {
            elements.bulkApplyBtn.disabled = true;
        }
    };

    // Bulk convert selected lists to playlists
    SmartLists.bulkConvertToPlaylist = async function (page) {
        const selectedCheckboxes = page.querySelectorAll('.playlist-checkbox:checked');
        const listsToConvert = [];

        // Filter to only collections (can't convert playlists to playlists)
        for (var i = 0; i < selectedCheckboxes.length; i++) {
            const checkbox = selectedCheckboxes[i];
            const listId = checkbox.getAttribute('data-playlist-id');
            const playlistCard = checkbox.closest('.playlist-card');
            const listType = playlistCard ? playlistCard.dataset.listType : null;

            if (listType === 'Collection') {
                listsToConvert.push(listId);
            }
        }

        if (listsToConvert.length === 0) {
            SmartLists.showNotification('No collections selected. Only collections can be converted to playlists.', 'info');
            return;
        }

        await SmartLists.performBulkConversion(page, listsToConvert, 'Playlist');
    };

    // Bulk convert selected lists to collections
    SmartLists.bulkConvertToCollection = async function (page) {
        const selectedCheckboxes = page.querySelectorAll('.playlist-checkbox:checked');
        const listsToConvert = [];

        // Filter to only playlists (can't convert collections to collections)
        for (var i = 0; i < selectedCheckboxes.length; i++) {
            const checkbox = selectedCheckboxes[i];
            const listId = checkbox.getAttribute('data-playlist-id');
            const playlistCard = checkbox.closest('.playlist-card');
            const listType = playlistCard ? playlistCard.dataset.listType : null;

            if (listType === 'Playlist') {
                listsToConvert.push(listId);
            }
        }

        if (listsToConvert.length === 0) {
            SmartLists.showNotification('No playlists selected. Only playlists can be converted to collections.', 'info');
            return;
        }

        await SmartLists.performBulkConversion(page, listsToConvert, 'Collection');
    };

    // Perform bulk conversion to target type
    SmartLists.performBulkConversion = async function (page, listIds, targetType) {
        const apiClient = SmartLists.getApiClient();
        let successCount = 0;
        let errorCount = 0;
        let enabledSuccessCount = 0;

        // Clear selections immediately
        const selectAllCheckbox = page.querySelector('#selectAllCheckbox');
        if (selectAllCheckbox) {
            selectAllCheckbox.checked = false;
        }

        // Process sequentially - need to fetch each list, modify type, and PUT back
        for (const listId of listIds) {
            try {
                // First, get the current list data
                const getUrl = SmartLists.ENDPOINTS.base + '/' + listId;
                const getResponse = await apiClient.ajax({
                    type: 'GET',
                    url: apiClient.getUrl(getUrl),
                    contentType: 'application/json'
                });

                if (!getResponse.ok) {
                    console.error('Error fetching list for conversion:', listId);
                    errorCount++;
                    continue;
                }

                const listData = await getResponse.json();
                const isEnabled = listData.Enabled !== false;

                // Reconstruct object with Type FIRST - required for System.Text.Json polymorphic deserialization
                // The discriminator property must appear early in the JSON for proper type resolution
                const { Type: _oldType, ...rest } = listData;
                const convertedData = { Type: targetType, ...rest };

                // PUT the updated list back
                const putUrl = SmartLists.ENDPOINTS.base + '/' + listId;
                const putResponse = await apiClient.ajax({
                    type: 'PUT',
                    url: apiClient.getUrl(putUrl),
                    contentType: 'application/json',
                    data: JSON.stringify(convertedData)
                });

                if (!putResponse.ok) {
                    const errorMessage = await SmartLists.extractErrorMessage(putResponse, 'HTTP ' + putResponse.status);
                    console.error('Error converting list:', listId, errorMessage);
                    errorCount++;
                } else {
                    successCount++;
                    if (isEnabled) {
                        enabledSuccessCount++;
                    }
                }
            } catch (err) {
                console.error('Error converting list:', listId, err);
                errorCount++;
            }
        }

        // Show results
        if (successCount > 0) {
            var listWord = successCount === 1 ? 'list' : 'lists';
            var targetWord = successCount === 1 ? targetType.toLowerCase() : targetType.toLowerCase() + 's';
            var successMessage = 'Converted ' + successCount + ' ' + listWord + ' to ' + targetWord + '.';

            // Only show status page link if at least one enabled list was successfully converted (will refresh)
            // Note: html option should only be true when we actually include HTML content (the statusLink)
            var includeStatusLink = !SmartLists.IS_USER_PAGE && enabledSuccessCount > 0;
            if (includeStatusLink) {
                var statusLink = SmartLists.createStatusPageLink('status page');
                successMessage += ' Check the ' + statusLink + ' for progress.';
            }
            SmartLists.showNotification(successMessage, 'success', { html: includeStatusLink });
        }
        if (errorCount > 0) {
            var errorListWord = errorCount === 1 ? 'list' : 'lists';
            SmartLists.showNotification('Failed to convert ' + errorCount + ' ' + errorListWord + '.', 'error');
        }

        // Reload list to show updated state
        if (SmartLists.loadPlaylistList) {
            SmartLists.loadPlaylistList(page);
        }
    };

    // Collapsible playlist functionality
    SmartLists.togglePlaylistCard = function (playlistCard) {
        const details = playlistCard.querySelector('.playlist-details');
        const actions = playlistCard.querySelector('.playlist-actions');
        const icon = playlistCard.querySelector('.playlist-expand-icon');

        if (!details || !actions || !icon) {
            return;
        }

        if (details.style.display === 'none' || details.style.display === '') {
            // Expand
            details.style.display = 'block';
            actions.style.display = 'block';
            icon.textContent = '▼';
            playlistCard.setAttribute('data-expanded', 'true');
        } else {
            // Collapse
            details.style.display = 'none';
            actions.style.display = 'none';
            icon.textContent = '▶';
            playlistCard.setAttribute('data-expanded', 'false');
        }

        // Save state to localStorage
        SmartLists.savePlaylistExpandStates();
    };

    SmartLists.toggleAllPlaylists = function (page) {
        const expandAllBtn = page.querySelector('#expandAllBtn');
        const playlistCards = page.querySelectorAll('.playlist-card');

        if (!playlistCards.length || !expandAllBtn) return;

        // Base action on current button text, not on current state
        const shouldExpand = expandAllBtn.textContent.trim() === 'Expand All';

        // Preserve scroll position when expanding to prevent unwanted scrolling
        const currentScrollTop = window.pageYOffset || document.documentElement.scrollTop;

        if (shouldExpand) {
            // Expand all
            for (var i = 0; i < playlistCards.length; i++) {
                const card = playlistCards[i];
                const details = card.querySelector('.playlist-details');
                const actions = card.querySelector('.playlist-actions');
                const icon = card.querySelector('.playlist-expand-icon');
                if (details && actions && icon) {
                    details.style.display = 'block';
                    actions.style.display = 'block';
                    icon.textContent = '▼';
                    card.setAttribute('data-expanded', 'true');
                }
            }
            if (expandAllBtn) {
                expandAllBtn.textContent = 'Collapse All';
            }

            // Restore scroll position after DOM changes to prevent unwanted scrolling
            if (window.requestAnimationFrame) {
                requestAnimationFrame(function () {
                    window.scrollTo(0, currentScrollTop);
                });
            } else {
                setTimeout(function () {
                    window.scrollTo(0, currentScrollTop);
                }, 0);
            }
        } else {
            // Collapse all
            for (var j = 0; j < playlistCards.length; j++) {
                const card = playlistCards[j];
                const details = card.querySelector('.playlist-details');
                const actions = card.querySelector('.playlist-actions');
                const icon = card.querySelector('.playlist-expand-icon');
                if (details && actions && icon) {
                    details.style.display = 'none';
                    actions.style.display = 'none';
                    icon.textContent = '▶';
                    card.setAttribute('data-expanded', 'false');
                }
            }
            if (expandAllBtn) {
                expandAllBtn.textContent = 'Expand All';
            }
        }

        // Save state to localStorage
        SmartLists.savePlaylistExpandStates();
    };

    SmartLists.savePlaylistExpandStates = function () {
        try {
            const playlistCards = document.querySelectorAll('.playlist-card');
            const states = {};

            for (var i = 0; i < playlistCards.length; i++) {
                const card = playlistCards[i];
                const playlistId = card.getAttribute('data-playlist-id');
                const isExpanded = card.getAttribute('data-expanded') === 'true';
                if (playlistId) {
                    states[playlistId] = isExpanded;
                }
            }

            localStorage.setItem('smartListsExpandStates', JSON.stringify(states));
        } catch (err) {
            console.warn('Failed to save playlist expand states:', err);
        }
    };

    SmartLists.loadPlaylistExpandStates = function () {
        try {
            const saved = localStorage.getItem('smartListsExpandStates');
            if (!saved) return {};

            return JSON.parse(saved);
        } catch (err) {
            console.warn('Failed to load playlist expand states:', err);
            return {};
        }
    };

    SmartLists.restorePlaylistExpandStates = function (page) {
        const savedStates = SmartLists.loadPlaylistExpandStates();
        const playlistCards = page.querySelectorAll('.playlist-card');

        for (var i = 0; i < playlistCards.length; i++) {
            const card = playlistCards[i];
            const playlistId = card.getAttribute('data-playlist-id');
            const shouldExpand = savedStates[playlistId] === true;

            if (shouldExpand) {
                const details = card.querySelector('.playlist-details');
                const actions = card.querySelector('.playlist-actions');
                const icon = card.querySelector('.playlist-expand-icon');
                if (details) {
                    details.style.display = 'block';
                }
                if (actions) {
                    actions.style.display = 'block';
                }
                if (icon) {
                    icon.textContent = '▼';
                }
                card.setAttribute('data-expanded', 'true');
            } else {
                // Ensure collapsed state (default)
                card.setAttribute('data-expanded', 'false');
            }
        }
    };

    SmartLists.updateExpandAllButtonText = function (page) {
        const expandAllBtn = page.querySelector('#expandAllBtn');
        const playlistCards = page.querySelectorAll('.playlist-card');

        if (!expandAllBtn || !playlistCards.length) return;

        // Count how many playlists are currently expanded
        let expandedCount = 0;
        for (var i = 0; i < playlistCards.length; i++) {
            if (playlistCards[i].getAttribute('data-expanded') === 'true') {
                expandedCount++;
            }
        }
        const totalCount = playlistCards.length;

        // Update button text based on current state
        if (expandedCount === totalCount) {
            expandAllBtn.textContent = 'Collapse All';
        } else {
            expandAllBtn.textContent = 'Expand All';
        }
    };

})(window.SmartLists = window.SmartLists || {});

