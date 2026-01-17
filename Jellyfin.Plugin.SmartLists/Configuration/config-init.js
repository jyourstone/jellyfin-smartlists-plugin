(function (SmartLists) {
    'use strict';

    // Initialize namespace if it doesn't exist
    if (!window.SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }

    // ===== SAFE SORT BOX ADDITION =====
    // Helper to safely add a sort box, handling race conditions with config-sorts.js loading
    SmartLists.safeAddSortBox = function(page, sortData, retryCount) {
        retryCount = retryCount || 0;
        const maxRetries = 10;
        const retryDelay = 100; // ms

        if (typeof SmartLists.addSortBox === 'function') {
            SmartLists.addSortBox(page, sortData);
        } else if (retryCount < maxRetries) {
            // config-sorts.js hasn't loaded yet, retry after a short delay
            setTimeout(function() {
                SmartLists.safeAddSortBox(page, sortData, retryCount + 1);
            }, retryDelay);
        } else {
            console.warn('SmartLists: addSortBox function not available after retries. config-sorts.js may not have loaded.');
        }
    };

    // ===== PAGE INITIALIZATION =====
    SmartLists.initPage = function (page) {
        // Check if this specific page is already initialized
        if (page._pageInitialized) {
            return;
        }
        page._pageInitialized = true;

        // Reset loading state to prevent stuck "Loading..." from previous page loads
        page._loadingPlaylists = false;

        SmartLists.applyCustomStyles(page);

        // Show loading state
        const userSelect = page.querySelector('#playlistUser');
        if (userSelect) {
            userSelect.innerHTML = '<option value="">Loading users...</option>';
        }

        // Disable form submission until initialization is complete
        const submitBtn = page.querySelector('#submitBtn');
        if (submitBtn) {
            submitBtn.disabled = true;
            submitBtn.textContent = 'Loading...';
        }

        // Build array of async initialization tasks
        var initTasks = [
            SmartLists.populateStaticSelects(page), // Make synchronous function async
            SmartLists.loadAndPopulateFields()
        ];
        
        // Only load users and configuration on admin pages
        if (!SmartLists.IS_USER_PAGE) {
            initTasks.push(SmartLists.loadUsers(page));
            initTasks.push(SmartLists.loadAllowedUsersMultiSelect(page));
        } else {
            // On user pages, load capabilities to determine what they can create
            if (SmartLists.loadUserCapabilities) {
                initTasks.push(SmartLists.loadUserCapabilities());
            }
        }

        // Coordinate all async initialization
        Promise.all(initTasks).then(function () {
            // All async operations completed successfully
            const rulesContainer = page.querySelector('#rules-container');
            if (rulesContainer.children.length === 0) {
                SmartLists.createInitialLogicGroup(page);
            } else {
                // Re-initialize existing rules to ensure event listeners are properly attached
                SmartLists.reinitializeExistingRules(page);
            }

            // Enable form submission
            const editState = SmartLists.getPageEditState(page);
            const submitBtn = page.querySelector('#submitBtn');
            if (submitBtn) {
                submitBtn.disabled = false;
                submitBtn.textContent = editState.editMode ? 'Update Playlist' : 'Create Playlist';
            }

            // Populate form defaults if we're on the create tab and not in edit mode
            const currentTab = SmartLists.getCurrentTab();
            if (currentTab === 'create' && !editState.editMode) {
                // On user pages, always use Playlist type (no configuration needed)
                if (SmartLists.IS_USER_PAGE) {
                    SmartLists.setElementValue(page, '#listType', 'Playlist');
                    SmartLists.handleListTypeChange(page);
                    SmartLists.populateFormDefaults(page);
                } else {
                    // Admin page: Set default list type from configuration
                    const apiClient = SmartLists.getApiClient();
                    apiClient.getPluginConfiguration(SmartLists.getPluginId()).then(function (config) {
                        SmartLists.setElementValue(page, '#listType', config.DefaultListType || 'Playlist');
                        SmartLists.handleListTypeChange(page);
                        SmartLists.populateFormDefaults(page);
                    }).catch(function () {
                        SmartLists.setElementValue(page, '#listType', 'Playlist');
                        SmartLists.handleListTypeChange(page);
                        SmartLists.populateFormDefaults(page);
                    });
                }
            }
        }).catch(function (error) {
            console.error('Error during page initialization:', error);
            SmartLists.showNotification('Some configuration options failed to load. Please refresh the page.');

            // Still enable form submission even if some things failed
            const editState = SmartLists.getPageEditState(page);
            const submitBtn = page.querySelector('#submitBtn');
            if (submitBtn) {
                submitBtn.disabled = false;
                submitBtn.textContent = editState.editMode ? 'Update Playlist' : 'Create Playlist';
            }
        });

        // Set up event listeners (these don't depend on async operations)
        SmartLists.setupEventListeners(page);

        // Set up navigation functionality
        SmartLists.setupNavigation(page);

        // Load configuration (only on admin pages - user pages don't have settings tab)
        if (!SmartLists.IS_USER_PAGE) {
            SmartLists.loadConfiguration(page);
        }
    };

    // ===== STATIC SELECTS POPULATION =====
    SmartLists.populateStaticSelects = function (page) {
        // Initialize page elements (media types, schedules, sorts)
        SmartLists.initializePageElements(page);

        // Populate all common selectors dynamically (DRY principle) - using safe DOM manipulation
        const scheduleTimeElement = page.querySelector('#scheduleTime');
        if (scheduleTimeElement) {
            SmartLists.populateSelectElement(scheduleTimeElement, SmartLists.generateTimeOptions('00:00')); // Default to midnight
        }

        const defaultScheduleTimeElement = page.querySelector('#defaultScheduleTime');
        if (defaultScheduleTimeElement) {
            SmartLists.populateSelectElement(defaultScheduleTimeElement, SmartLists.generateTimeOptions('00:00')); // Default to midnight
        }

        const autoRefreshElement = page.querySelector('#autoRefreshMode');
        if (autoRefreshElement) {
            SmartLists.populateSelectElement(autoRefreshElement, SmartLists.generateAutoRefreshOptions('OnLibraryChanges'));
        }

        const defaultAutoRefreshElement = page.querySelector('#defaultAutoRefresh');
        if (defaultAutoRefreshElement) {
            SmartLists.populateSelectElement(defaultAutoRefreshElement, SmartLists.generateAutoRefreshOptions('OnLibraryChanges'));
        }

        const scheduleTriggerElement = page.querySelector('#scheduleTrigger');
        if (scheduleTriggerElement) {
            SmartLists.populateSelectElement(scheduleTriggerElement, SmartLists.generateScheduleTriggerOptions('', true)); // Include "No schedule"
        }

        const defaultScheduleTriggerElement = page.querySelector('#defaultScheduleTrigger');
        if (defaultScheduleTriggerElement) {
            SmartLists.populateSelectElement(defaultScheduleTriggerElement, SmartLists.generateScheduleTriggerOptions('', true)); // Include "No schedule"

            // Add event listener to update containers when trigger changes
            defaultScheduleTriggerElement.addEventListener('change', function () {
                SmartLists.updateDefaultScheduleContainers(page, this.value);
            });
        }

        // Populate default schedule option dropdowns
        const defaultScheduleDayOfWeekElement = page.querySelector('#defaultScheduleDayOfWeek');
        if (defaultScheduleDayOfWeekElement) {
            SmartLists.populateSelectElement(defaultScheduleDayOfWeekElement, SmartLists.generateDayOfWeekOptions('0')); // Default Sunday
        }

        const defaultScheduleDayOfMonthElement = page.querySelector('#defaultScheduleDayOfMonth');
        if (defaultScheduleDayOfMonthElement) {
            SmartLists.populateSelectElement(defaultScheduleDayOfMonthElement, SmartLists.generateDayOfMonthOptions('1')); // Default 1st
        }

        const defaultScheduleMonthElement = page.querySelector('#defaultScheduleMonth');
        if (defaultScheduleMonthElement) {
            SmartLists.populateSelectElement(defaultScheduleMonthElement, SmartLists.generateMonthOptions('1')); // Default January
        }

        const defaultScheduleIntervalElement = page.querySelector('#defaultScheduleInterval');
        if (defaultScheduleIntervalElement) {
            SmartLists.populateSelectElement(defaultScheduleIntervalElement, SmartLists.generateIntervalOptions('00:15:00')); // Default 15 minutes
        }

        // Populate sort options (legacy format for backward compatibility)
        const SORT_OPTIONS_LEGACY = SmartLists.SORT_OPTIONS.map(function (opt) { return { Value: opt.value, Label: opt.label }; });
        const SORT_ORDER_OPTIONS_LEGACY = SmartLists.SORT_ORDER_OPTIONS.map(function (opt) { return { Value: opt.value, Label: opt.label }; });

        const defaultSortBySetting = page.querySelector('#defaultSortBy');
        const defaultSortOrderSetting = page.querySelector('#defaultSortOrder');
        const defaultIgnoreArticlesContainer = page.querySelector('#defaultIgnoreArticlesContainer');
        const defaultIgnoreArticlesCheckbox = page.querySelector('#defaultIgnoreArticles');

        if (defaultSortBySetting && defaultSortBySetting.children.length === 0) {
            SmartLists.populateSelect(defaultSortBySetting, SORT_OPTIONS_LEGACY, 'Name');
        }
        if (defaultSortOrderSetting && defaultSortOrderSetting.children.length === 0) {
            SmartLists.populateSelect(defaultSortOrderSetting, SORT_ORDER_OPTIONS_LEGACY, 'Ascending');
        }

        // Add event listener to show/hide ignore articles checkbox based on sort selection
        if (defaultSortBySetting && defaultIgnoreArticlesContainer) {
            defaultSortBySetting.addEventListener('change', function () {
                const showCheckbox = (this.value === 'Name' || this.value === 'SeriesName');
                defaultIgnoreArticlesContainer.style.display = showCheckbox ? '' : 'none';
                if (!showCheckbox && defaultIgnoreArticlesCheckbox) {
                    defaultIgnoreArticlesCheckbox.checked = false;
                }
            });
        }

        // Add default sort option when creating a new playlist (not in edit mode)
        const editState = SmartLists.getPageEditState(page);
        if (!editState.editMode) {
            // On user pages, skip loading plugin configuration (admin-only)
            if (SmartLists.IS_USER_PAGE) {
                // Use default values for user pages
                const sortsContainer = page.querySelector('#sorts-container');
                if (sortsContainer && sortsContainer.querySelectorAll('.sort-box').length === 0) {
                    SmartLists.safeAddSortBox(page, { SortBy: 'Name', SortOrder: 'Ascending' });
                }
                return Promise.resolve();
            }

            const apiClient = SmartLists.getApiClient();

            // Suffix initialization removed - allow blank values

            return apiClient.getPluginConfiguration(SmartLists.getPluginId()).then(function (config) {
                const sortsContainer = page.querySelector('#sorts-container');
                if (sortsContainer && sortsContainer.querySelectorAll('.sort-box').length === 0) {
                    SmartLists.safeAddSortBox(page, { SortBy: config.DefaultSortBy || 'Name', SortOrder: config.DefaultSortOrder || 'Ascending' });
                }
            }).catch(function () {
                // Fallback to default values if config load fails
                const sortsContainer = page.querySelector('#sorts-container');
                if (sortsContainer && sortsContainer.querySelectorAll('.sort-box').length === 0) {
                    SmartLists.safeAddSortBox(page, { SortBy: 'Name', SortOrder: 'Ascending' });
                }
            });
        }

        // Suffix initialization removed - allow blank values

        // Return resolved promise (synchronous path when not adding default sort)
        return Promise.resolve();
    };

    // ===== PAGE ELEMENTS INITIALIZATION =====
    SmartLists.initializePageElements = function (page) {
        // Generate media type checkboxes from the mediaTypes array
        SmartLists.generateMediaTypeCheckboxes(page);

        // Initialize schedule system
        if (SmartLists.initializeScheduleSystem) {
            SmartLists.initializeScheduleSystem(page);
        }

        // Initialize sort system
        if (SmartLists.initializeSortSystem) {
            SmartLists.initializeSortSystem(page);
        }
    };

    // ===== MEDIA TYPE CHECKBOXES GENERATION =====
    SmartLists.generateMediaTypeCheckboxes = function (page) {
        const container = page.querySelector('#mediaTypesMultiSelect');
        if (!container) return;

        // Debounce timer and AbortController for media type updates (shared per page)
        page._mediaTypeUpdateTimer = page._mediaTypeUpdateTimer || null;

        // Create AbortController for media type checkbox listeners
        if (page._mediaTypeAbortController) {
            page._mediaTypeAbortController.abort();
        }
        page._mediaTypeAbortController = SmartLists.createAbortController();

        // Batch update function for all media type changes
        // Order matters: repopulate fields first (may invalidate), then sync dependent UI
        const batchUpdateMediaTypeChanges = function () {
            // 1) Re-populate fields (may invalidate current selections)
            if (SmartLists.updateAllFieldSelects) {
                SmartLists.updateAllFieldSelects(page);
            }

            // 2) Re-sync dependent UI for all rules
            if (SmartLists.updateAllUserSelectorVisibility) {
                SmartLists.updateAllUserSelectorVisibility(page);
            }
            if (SmartLists.updateAllNextUnwatchedOptionsVisibility) {
                SmartLists.updateAllNextUnwatchedOptionsVisibility(page);
            }
            if (SmartLists.updateAllCollectionsOptionsVisibility) {
                SmartLists.updateAllCollectionsOptionsVisibility(page);
            }
            if (SmartLists.updateAllTagsOptionsVisibility) {
                SmartLists.updateAllTagsOptionsVisibility(page);
            }
            if (SmartLists.updateAllStudiosOptionsVisibility) {
                SmartLists.updateAllStudiosOptionsVisibility(page);
            }
            if (SmartLists.updateAllGenresOptionsVisibility) {
                SmartLists.updateAllGenresOptionsVisibility(page);
            }
            if (SmartLists.updateAllAudioLanguagesOptionsVisibility) {
                SmartLists.updateAllAudioLanguagesOptionsVisibility(page);
            }

            // 3) Update sort options visibility based on media types
            if (SmartLists.updateAllSortOptionsVisibility) {
                SmartLists.updateAllSortOptionsVisibility(page);
            }
        };

        // Generate media types array filtered by list type
        if (!SmartLists.mediaTypes || !Array.isArray(SmartLists.mediaTypes)) {
            console.error('SmartLists.mediaTypes is not available');
            return;
        }

        // Get list type to filter media types
        const listType = SmartLists.getElementValue(page, '#listType', 'Playlist');
        const isCollection = listType === 'Collection';

        // Filter media types based on list type
        const availableMediaTypes = SmartLists.mediaTypes.filter(function (mediaType) {
            // Skip collection-only media types for playlists
            return !(mediaType.CollectionOnly && !isCollection);
        });

        // Initialize multi-select component
        SmartLists.initializeMultiSelect(page, {
            containerId: 'mediaTypesMultiSelect',
            displayId: 'mediaTypesMultiSelectDisplay',
            dropdownId: 'mediaTypesMultiSelectDropdown',
            optionsId: 'mediaTypesMultiSelectOptions',
            placeholderText: 'Select media types...',
            checkboxClass: 'media-type-multi-select-checkbox',
            onChange: function (selectedValues) {
                // Skip processing if we're programmatically setting media types (e.g., during clone/edit)
                if (page._skipMediaTypeChangeHandlers) {
                    return;
                }

                // Clear any pending update
                if (page._mediaTypeUpdateTimer) {
                    clearTimeout(page._mediaTypeUpdateTimer);
                }

                // Schedule batched update after debounce delay
                page._mediaTypeUpdateTimer = setTimeout(function () {
                    batchUpdateMediaTypeChanges();
                    page._mediaTypeUpdateTimer = null;
                }, SmartLists.MEDIA_TYPE_UPDATE_DEBOUNCE_MS || 200);
            }
        });

        // Load media types into multi-select
        SmartLists.loadItemsIntoMultiSelect(
            page,
            'mediaTypesMultiSelect',
            availableMediaTypes,
            'media-type-multi-select-checkbox',
            function (item) { return item.Label; },
            function (item) { return item.Value; }
        );

        // Update display after loading
        SmartLists.updateMultiSelectDisplay(page, 'mediaTypesMultiSelect', 'Select media types...', 'media-type-multi-select-checkbox');
    };

    // ===== FORM DEFAULTS POPULATION (DRY) =====
    // Helper function to apply all form defaults from config
    SmartLists.applyFormDefaults = function (page, config) {
        // Set default list type
        SmartLists.setElementValue(page, '#listType', config.DefaultListType || 'Playlist');
        SmartLists.handleListTypeChange(page);

        // Set default values for Max Items and Max Playtime
        const defaultMaxItems = config.DefaultMaxItems !== undefined && config.DefaultMaxItems !== null ? config.DefaultMaxItems : 500;
        SmartLists.setElementValue(page, '#playlistMaxItems', defaultMaxItems);

        const defaultMaxPlayTimeMinutes = config.DefaultMaxPlayTimeMinutes !== undefined && config.DefaultMaxPlayTimeMinutes !== null ? config.DefaultMaxPlayTimeMinutes : 0;
        SmartLists.setElementValue(page, '#playlistMaxPlayTimeMinutes', defaultMaxPlayTimeMinutes);

        // Set default auto refresh mode
        SmartLists.setElementValue(page, '#autoRefreshMode', config.DefaultAutoRefresh || 'OnLibraryChanges');

        // Set default public/enabled checkboxes
        SmartLists.setElementChecked(page, '#playlistIsPublic', config.DefaultMakePublic || false);
        SmartLists.setElementChecked(page, '#playlistIsEnabled', true); // Default to enabled

        // Reinitialize schedule system
        if (typeof SmartLists.initializeScheduleSystem === 'function') {
            SmartLists.initializeScheduleSystem(page);
        }

        // Reinitialize visibility schedule system
        if (typeof SmartLists.initializeVisibilityScheduleSystem === 'function') {
            SmartLists.initializeVisibilityScheduleSystem(page);
        }

        // Apply default schedule if configured
        if (SmartLists.applyDefaultScheduleFromConfig) {
            SmartLists.applyDefaultScheduleFromConfig(page, config);
        }

        // Reinitialize sort system with defaults
        if (typeof SmartLists.initializeSortSystem === 'function') {
            SmartLists.initializeSortSystem(page);
        }
        const sortsContainer = page.querySelector('#sorts-container');
        if (sortsContainer && sortsContainer.querySelectorAll('.sort-box').length === 0) {
            SmartLists.safeAddSortBox(page, { SortBy: config.DefaultSortBy || 'Name', SortOrder: config.DefaultSortOrder || 'Ascending' });
        }

        // Reset user dropdown to currently logged-in user
        const userSelect = page.querySelector('#playlistUser');
        if (userSelect) {
            userSelect.value = '';
            SmartLists.setCurrentUserAsDefault(page);
        }
    };

    // Fallback defaults when config fails to load
    SmartLists.applyFallbackDefaults = function (page) {
        SmartLists.setElementValue(page, '#listType', 'Playlist');
        SmartLists.handleListTypeChange(page);
        SmartLists.setElementValue(page, '#playlistMaxItems', 500);
        SmartLists.setElementValue(page, '#playlistMaxPlayTimeMinutes', 0);
        SmartLists.setElementValue(page, '#autoRefreshMode', 'OnLibraryChanges');
        SmartLists.setElementChecked(page, '#playlistIsPublic', false);
        SmartLists.setElementChecked(page, '#playlistIsEnabled', true);

        // Reinitialize schedule system with fallback defaults
        if (typeof SmartLists.initializeScheduleSystem === 'function') {
            SmartLists.initializeScheduleSystem(page);
        }

        // Reinitialize visibility schedule system with fallback defaults
        if (typeof SmartLists.initializeVisibilityScheduleSystem === 'function') {
            SmartLists.initializeVisibilityScheduleSystem(page);
        }

        // Reinitialize sort system with fallback defaults
        if (typeof SmartLists.initializeSortSystem === 'function') {
            SmartLists.initializeSortSystem(page);
        }
        const sortsContainer = page.querySelector('#sorts-container');
        if (sortsContainer && sortsContainer.querySelectorAll('.sort-box').length === 0) {
            SmartLists.safeAddSortBox(page, { SortBy: 'Name', SortOrder: 'Ascending' });
        }
    };

    SmartLists.populateFormDefaults = function (page) {
        // On user pages, skip loading plugin configuration (admin-only)
        if (SmartLists.IS_USER_PAGE) {
            SmartLists.applyFallbackDefaults(page);
            return;
        }

        const apiClient = SmartLists.getApiClient();
        apiClient.getPluginConfiguration(SmartLists.getPluginId()).then(function (config) {
            SmartLists.applyFormDefaults(page, config);
        }).catch(function () {
            SmartLists.applyFallbackDefaults(page);
        });
    };

    // ===== USER LOADING =====
    SmartLists.loadUsers = async function (page) {
        const apiClient = SmartLists.getApiClient();
        const userSelect = page.querySelector('#playlistUser');
        const multiSelectContainer = page.querySelector('#playlistUserMultiSelect');

        if (!userSelect && !multiSelectContainer) {
            console.warn('SmartLists.loadUsers: #playlistUser or #playlistUserMultiSelect element not found');
            return;
        }

        try {
            const response = await apiClient.ajax({
                type: "GET",
                url: apiClient.getUrl(SmartLists.ENDPOINTS.users),
                contentType: 'application/json'
            });

            if (!response.ok) {
                const errorText = await response.text();
                throw new Error(`Failed to load users: ${errorText || response.statusText}`);
            }

            const users = await response.json();

            // Detect list type
            const listType = SmartLists.getElementValue(page, '#listType', 'Playlist');
            const isCollection = listType === 'Collection';

            if (isCollection) {
                // Collections: populate single select
                if (userSelect) {
                    userSelect.innerHTML = '';
                    users.forEach(function (user) {
                        const option = document.createElement('option');
                        option.value = user.Id;
                        option.textContent = user.Name;
                        userSelect.appendChild(option);
                    });
                    // Check for pending collection user ID (from edit/clone mode)
                    if (page._pendingCollectionUserId) {
                        SmartLists.setUserIdValueWithRetry(page, page._pendingCollectionUserId);
                        page._pendingCollectionUserId = null; // Clear after use
                    } else {
                        // Set current user as default for new collections
                        SmartLists.setCurrentUserAsDefault(page);
                    }
                }
            } else {
                // Playlists: populate multi-select component
                if (multiSelectContainer && SmartLists.loadUsersIntoMultiSelect) {
                    SmartLists.loadUsersIntoMultiSelect(page, users);
                    // Initialize multi-select component
                    if (SmartLists.initializeUserMultiSelect) {
                        SmartLists.initializeUserMultiSelect(page);
                    }

                    // Check if there are pending userIds to set (from edit/clone mode)
                    if (page._pendingUserIds && Array.isArray(page._pendingUserIds) && page._pendingUserIds.length > 0) {
                        // Use setTimeout to ensure checkboxes are fully rendered
                        setTimeout(function () {
                            if (SmartLists.setSelectedUserIds) {
                                SmartLists.setSelectedUserIds(page, page._pendingUserIds);
                            }
                            // Don't clear pending userIds immediately - keep it for subsequent loadUsers calls
                            // It will be cleared when form is submitted or reset
                        }, 0);
                    } else {
                        // Check if checkboxes already have selections before defaulting to current user
                        const checkboxes = page.querySelectorAll('#userMultiSelectOptions .user-multi-select-checkbox:checked');
                        if (checkboxes.length === 0) {
                            // Set current user as default (only if not in edit/clone mode)
                            SmartLists.setCurrentUserAsDefault(page);
                        }
                    }
                }
            }
        } catch (err) {
            console.error('Error loading users:', err);
            const errorMessage = err.message || 'Failed to load users. Please refresh the page.';

            // Show error in single-select (collections)
            if (userSelect) {
                userSelect.innerHTML = '<option value="">Error loading users</option>';
            }

            // Show error in multi-select (playlists)
            const multiSelectContainer = page.querySelector('#playlistUserMultiSelect');
            if (multiSelectContainer) {
                const options = page.querySelector('#userMultiSelectOptions');
                if (options) {
                    options.innerHTML = '<div class="multi-select-option" style="padding: 0.5em; color: #BB3932;">Error: ' + errorMessage + '</div>';
                }
                const display = page.querySelector('#userMultiSelectDisplay');
                if (display) {
                    const placeholder = display.querySelector('.multi-select-placeholder');
                    if (placeholder) {
                        placeholder.textContent = 'Error loading users';
                        placeholder.style.color = '#BB3932';
                        placeholder.style.display = 'inline';
                    }
                }
            }

            if (SmartLists.showNotification) {
                SmartLists.showNotification('Failed to load users. Please refresh the page.', 'error');
            }
        }
    };

    /**
     * Load users for the allowed users multi-select in Settings tab
     */
    SmartLists.loadAllowedUsersMultiSelect = async function (page) {
        const apiClient = SmartLists.getApiClient();
        const container = page.querySelector('#allowedUsersMultiSelect');

        if (!container) {
            return;
        }

        try {
            const response = await apiClient.ajax({
                type: "GET",
                url: apiClient.getUrl(SmartLists.ENDPOINTS.users),
                contentType: 'application/json'
            });

            if (!response.ok) {
                const errorText = await response.text();
                throw new Error(`Failed to load users: ${errorText || response.statusText}`);
            }

            const users = await response.json();

            // Initialize the multi-select component
            if (SmartLists.initializeMultiSelect) {
                SmartLists.initializeMultiSelect(page, {
                    containerId: 'allowedUsersMultiSelect',
                    displayId: 'allowedUsersMultiSelectDisplay',
                    dropdownId: 'allowedUsersMultiSelectDropdown',
                    optionsId: 'allowedUsersMultiSelectOptions',
                    placeholderText: 'All users (default)',
                    checkboxClass: 'allowed-users-checkbox',
                    onChange: null
                });
            }

            // Load users into the multi-select
            if (SmartLists.loadItemsIntoMultiSelect) {
                SmartLists.loadItemsIntoMultiSelect(
                    page,
                    'allowedUsersMultiSelect',
                    users,
                    'allowed-users-checkbox',
                    function (user) { return user.Name || user.Username || user.Id; },
                    function (user) { return user.Id; }
                );
            }
            
            // Apply any pending selections that were stored before the multi-select was ready
            if (page._pendingAllowedUserPageUsers && SmartLists.setSelectedItems) {
                SmartLists.setSelectedItems(
                    page, 
                    'allowedUsersMultiSelect', 
                    page._pendingAllowedUserPageUsers, 
                    'allowed-users-checkbox', 
                    'All users (default)'
                );
                // Clear the pending data once applied
                delete page._pendingAllowedUserPageUsers;
            }
        } catch (err) {
            console.error('Error loading allowed users:', err);
            const errorMessage = err.message || 'Failed to load users. Please refresh the page.';
            
            const options = page.querySelector('#allowedUsersMultiSelectOptions');
            if (options) {
                options.innerHTML = '<div class="multi-select-option" style="padding: 0.5em; color: #BB3932;">Error: ' + errorMessage + '</div>';
            }
            const display = page.querySelector('#allowedUsersMultiSelectDisplay');
            if (display) {
                const placeholder = display.querySelector('.multi-select-placeholder');
                if (placeholder) {
                    placeholder.textContent = 'Error loading users';
                    placeholder.style.color = '#BB3932';
                }
            }
        }
    };

    // ===== NAVIGATION =====
    SmartLists.getCurrentTab = function () {
        const hash = window.location.hash;
        const match = hash.match(/[?&]tab=([^&]*)/);
        return match ? decodeURIComponent(match[1]) : 'create';
    };

    SmartLists.updateUrl = function (tabId) {
        let hash = window.location.hash;
        let newHash;

        // Ensure hash starts with # for proper parsing by getCurrentTab
        if (!hash) {
            hash = '#';
        }

        if (hash.includes('tab=')) {
            // Replace existing tab parameter
            newHash = hash.replace(/([?&])tab=[^&]*/, '$1tab=' + encodeURIComponent(tabId));
        } else {
            // Add tab parameter
            const separator = hash.includes('?') ? '&' : '?';
            newHash = hash + separator + 'tab=' + encodeURIComponent(tabId);
        }

        window.history.replaceState({}, '', window.location.pathname + window.location.search + newHash);
    };

    SmartLists.switchToTab = function (page, tabId) {
        var navContainer = page.querySelector('.localnav');
        var navButtons = navContainer ? navContainer.querySelectorAll('a[data-tab]') : [];
        var tabContents = page.querySelectorAll('[data-tab-content]');

        // Update navigation buttons
        navButtons.forEach(function (btn) {
            if (btn.getAttribute('data-tab') === tabId) {
                btn.classList.add('ui-btn-active');
            } else {
                btn.classList.remove('ui-btn-active');
            }
        });

        // Update tab content visibility
        tabContents.forEach(function (content) {
            var contentTabId = content.getAttribute('data-tab-content');
            if (contentTabId === tabId) {
                content.classList.remove('hide');
                // If this is the status tab, ensure status page loads after tab becomes visible
            } else {
                content.classList.add('hide');
            }
        });

        // Handle Status Page Polling (Centralized Logic)
        if (window.SmartLists && window.SmartLists.Status) {
            if (tabId === 'status') {
                // Use requestAnimationFrame to ensure DOM update is complete
                requestAnimationFrame(function () {
                    window.SmartLists.Status.initializeStatusPage();
                    window.SmartLists.Status.loadStatusPage();
                });
            } else {
                // Stop polling when leaving status tab
                window.SmartLists.Status.stopPolling();
                // Also stop aggressive polling if it's running
                if (window.SmartLists.Status.stopAggressivePolling) {
                    window.SmartLists.Status.stopAggressivePolling();
                }
            }
        }

        // Load playlist list when switching to manage tab
        if (tabId === 'manage') {
            // Load saved filter preferences first
            if (SmartLists.loadPlaylistFilterPreferences) {
                SmartLists.loadPlaylistFilterPreferences(page);
            }
            
            // Load playlist list with retry logic to handle script loading race conditions
            var loadListWithRetry = function(retryCount) {
                retryCount = retryCount || 0;
                const maxRetries = 10;
                if (SmartLists.loadPlaylistList) {
                    SmartLists.loadPlaylistList(page);
                } else if (retryCount < maxRetries) {
                    setTimeout(function() {
                        loadListWithRetry(retryCount + 1);
                    }, 100); // 100ms delay between retries
                } else {
                    console.error('SmartLists: Failed to load playlist list - loadPlaylistList function not available after retries');
                    // Show error message to user
                    var container = page.querySelector('#playlist-list-container');
                    if (container) {
                        container.innerHTML = '<p style="color: #ff6b6b;">Failed to load playlist list. Please refresh the page.</p>';
                    }
                }
            };
            loadListWithRetry(0);
        }

        // Populate defaults when switching to create tab
        if (tabId === 'create') {
            // Check if form is empty (not in edit mode) and populate defaults
            const editState = SmartLists.getPageEditState(page);
            if (!editState.editMode) {
                // Only populate defaults if form fields are empty
                const playlistName = page.querySelector('#playlistName');
                if (playlistName && !playlistName.value) {
                    SmartLists.populateFormDefaults(page);
                }
            }
        }

        // Note: Status page loading is now handled in the tab visibility update above
        // to ensure it happens after the tab is visible

        // Update URL
        SmartLists.updateUrl(tabId);
    };

    SmartLists.setupNavigation = function (page) {
        var navContainer = page.querySelector('.localnav');
        if (!navContainer) {
            return;
        }

        // Prevent multiple setups on the same navigation
        if (navContainer._navInitialized) return;
        navContainer._navInitialized = true;

        // Apply Jellyfin's native styling to the navigation container
        SmartLists.applyStyles(navContainer, {
            marginBottom: '0.5em'
        });

        // Set initial active tab - use requestAnimationFrame to ensure DOM is ready
        var initialTab = SmartLists.getCurrentTab();
        requestAnimationFrame(function() {
            SmartLists.switchToTab(page, initialTab);
        });

        // Use shared tab switching helper
        function setActiveTab(tabId) {
            SmartLists.switchToTab(page, tabId);
        }

        // Create AbortController for navigation click listeners
        var navAbortController = SmartLists.createAbortController();
        var navSignal = navAbortController.signal;

        // Store controller for cleanup
        navContainer._navAbortController = navAbortController;

        // Handle navigation clicks
        var navButtons = navContainer.querySelectorAll('a[data-tab]');
        navButtons.forEach(function (button) {
            button.addEventListener('click', function (e) {
                e.preventDefault();
                var tabId = button.getAttribute('data-tab');

                // Hide any open modals when switching tabs
                var deleteModal = page.querySelector('#delete-confirm-modal');
                if (deleteModal && !deleteModal.classList.contains('hide')) {
                    deleteModal.classList.add('hide');
                    SmartLists.cleanupModalListeners(deleteModal);
                }
                var refreshModal = page.querySelector('#refresh-confirm-modal');
                if (refreshModal && !refreshModal.classList.contains('hide')) {
                    refreshModal.classList.add('hide');
                    SmartLists.cleanupModalListeners(refreshModal);
                }

                // Use shared tab switching helper (includes URL update)
                setActiveTab(tabId);

                // Initialize status page when status tab is clicked
                // Logic moved to switchToTab to handle all navigation methods (clicks, hashchange, etc.)
            }, SmartLists.getEventListenerOptions(navSignal));
        });

        // Handle browser back/forward navigation via hashchange
        // This ensures status page data loads when navigating via browser buttons
        window.addEventListener('hashchange', function () {
            // Get the current tab from the URL hash
            const currentTab = SmartLists.getCurrentTab();
            // Switch to that tab, which will trigger data loading for status page
            SmartLists.switchToTab(page, currentTab);
        }, SmartLists.getEventListenerOptions(navSignal));

        // Note: No popstate handler needed - hashchange handles browser navigation

        // Initial tab already set above to prevent flash
    };

    // ===== EVENT LISTENERS SETUP =====
    SmartLists.setupEventListeners = function (page) {
        // Create AbortController for page event listeners
        const pageAbortController = SmartLists.createAbortController();
        const pageSignal = pageAbortController.signal;

        // Store controller on the page for cleanup
        page._pageAbortController = pageAbortController;

        // Setup playlist naming event listeners
        SmartLists.setupPlaylistNamingListeners(page, pageSignal);

        // Setup list type change handler
        const listTypeSelect = page.querySelector('#listType');
        if (listTypeSelect) {
            listTypeSelect.addEventListener('change', function () {
                SmartLists.handleListTypeChange(page);
            }, SmartLists.getEventListenerOptions(pageSignal));
        }

        page.addEventListener('click', function (e) {
            const target = e.target;

            // Handle rule action buttons
            const andBtn = target.closest('.and-btn');
            if (andBtn) {
                const ruleRow = andBtn.closest('.rule-row');
                const logicGroup = ruleRow ? ruleRow.closest('.logic-group') : null;
                if (logicGroup && SmartLists.addRuleToGroup) {
                    SmartLists.addRuleToGroup(page, logicGroup);
                }
            }

            const orBtn = target.closest('.or-btn');
            if (orBtn) {
                if (SmartLists.addNewLogicGroup) {
                    SmartLists.addNewLogicGroup(page);
                }
            }

            const deleteBtn = target.closest('.delete-btn');
            if (deleteBtn) {
                const ruleRow = deleteBtn.closest('.rule-row');
                if (ruleRow && SmartLists.removeRule) {
                    SmartLists.removeRule(page, ruleRow);
                }
            }

            const cloneRuleBtn = target.closest('.clone-rule-btn');
            if (cloneRuleBtn) {
                const ruleRow = cloneRuleBtn.closest('.rule-row');
                if (ruleRow && SmartLists.cloneRule) {
                    SmartLists.cloneRule(page, ruleRow);
                }
            }

            const cloneGroupBtn = target.closest('.clone-group-btn');
            if (cloneGroupBtn) {
                const logicGroup = cloneGroupBtn.closest('.logic-group');
                if (logicGroup && SmartLists.cloneLogicGroup) {
                    SmartLists.cloneLogicGroup(page, logicGroup);
                }
            }

            const deleteGroupBtn = target.closest('.delete-group-btn');
            if (deleteGroupBtn) {
                const logicGroup = deleteGroupBtn.closest('.logic-group');
                if (logicGroup && SmartLists.removeLogicGroup) {
                    SmartLists.removeLogicGroup(page, logicGroup);
                }
            }

            // Handle other buttons
            if (target.closest('#clearFormBtn')) {
                if (SmartLists.clearForm) {
                    SmartLists.clearForm(page);
                }
            }
            if (target.closest('#saveSettingsBtn')) {
                SmartLists.saveConfiguration(page);
            }
            if (target.closest('#refreshPlaylistsBtn')) {
                if (SmartLists.showRefreshConfirmModal) {
                    SmartLists.showRefreshConfirmModal(page, SmartLists.refreshAllPlaylists);
                }
            }
            if (target.closest('#refreshPlaylistListBtn')) {
                if (SmartLists.loadPlaylistList) {
                    SmartLists.loadPlaylistList(page);
                }
            }
            if (target.closest('#exportPlaylistsBtn')) {
                if (SmartLists.exportPlaylists) {
                    SmartLists.exportPlaylists();
                }
            }
            if (target.closest('#importPlaylistsBtn')) {
                if (SmartLists.importPlaylists) {
                    SmartLists.importPlaylists(page);
                }
            }
            if (target.closest('.delete-playlist-btn')) {
                const button = target.closest('.delete-playlist-btn');
                if (SmartLists.showDeleteConfirm) {
                    SmartLists.showDeleteConfirm(page, button.getAttribute('data-playlist-id'), button.getAttribute('data-playlist-name'));
                }
            }
            if (target.closest('.edit-playlist-btn')) {
                const button = target.closest('.edit-playlist-btn');
                if (SmartLists.editPlaylist) {
                    SmartLists.editPlaylist(page, button.getAttribute('data-playlist-id'));
                }
            }
            if (target.closest('.clone-playlist-btn')) {
                const button = target.closest('.clone-playlist-btn');
                if (SmartLists.clonePlaylist) {
                    SmartLists.clonePlaylist(page, button.getAttribute('data-playlist-id'), button.getAttribute('data-playlist-name'));
                }
            }
            if (target.closest('.refresh-playlist-btn')) {
                const button = target.closest('.refresh-playlist-btn');
                if (SmartLists.refreshPlaylist) {
                    SmartLists.refreshPlaylist(button.getAttribute('data-playlist-id'), button.getAttribute('data-playlist-name'));
                }
            }
            if (target.closest('.enable-playlist-btn')) {
                const button = target.closest('.enable-playlist-btn');
                if (SmartLists.enablePlaylist) {
                    SmartLists.enablePlaylist(page, button.getAttribute('data-playlist-id'), button.getAttribute('data-playlist-name'));
                }
            }
            if (target.closest('.disable-playlist-btn')) {
                const button = target.closest('.disable-playlist-btn');
                if (SmartLists.disablePlaylist) {
                    SmartLists.disablePlaylist(page, button.getAttribute('data-playlist-id'), button.getAttribute('data-playlist-name'));
                }
            }
            if (target.closest('#cancelEditBtn')) {
                if (SmartLists.cancelEdit) {
                    SmartLists.cancelEdit(page);
                }
            }
            if (target.closest('#expandAllBtn')) {
                if (SmartLists.toggleAllPlaylists) {
                    SmartLists.toggleAllPlaylists(page);
                }
            }
            if (target.closest('.playlist-header')) {
                const playlistCard = target.closest('.playlist-card');
                if (playlistCard && SmartLists.togglePlaylistCard) {
                    SmartLists.togglePlaylistCard(playlistCard);
                }
            }

            // Bulk operations
            if (target.closest('#selectAllCheckbox')) {
                if (SmartLists.toggleSelectAll) {
                    SmartLists.toggleSelectAll(page);
                }
            }
            if (target.closest('#bulkEnableBtn')) {
                if (SmartLists.bulkEnablePlaylists) {
                    SmartLists.bulkEnablePlaylists(page);
                }
            }
            if (target.closest('#bulkDisableBtn')) {
                if (SmartLists.bulkDisablePlaylists) {
                    SmartLists.bulkDisablePlaylists(page);
                }
            }
            if (target.closest('#bulkDeleteBtn')) {
                if (SmartLists.bulkDeletePlaylists) {
                    SmartLists.bulkDeletePlaylists(page);
                }
            }
            if (target.closest('#bulkRefreshBtn')) {
                if (SmartLists.bulkRefreshPlaylists) {
                    SmartLists.bulkRefreshPlaylists(page);
                }
            }
            if (target.classList.contains('playlist-checkbox')) {
                e.stopPropagation(); // Prevent triggering playlist header click
                if (SmartLists.updateSelectedCount) {
                    SmartLists.updateSelectedCount(page);
                }
            }
            if (target.closest('.emby-checkbox-label') && target.closest('.playlist-header')) {
                const label = target.closest('.emby-checkbox-label');
                const checkbox = label.querySelector('.playlist-checkbox');
                if (checkbox && target !== checkbox) {
                    e.stopPropagation(); // Prevent triggering playlist header click
                    // Let the label's default behavior handle the checkbox toggle
                }
            }
        }, SmartLists.getEventListenerOptions(pageSignal));

        const playlistForm = page.querySelector('#playlistForm');
        if (playlistForm) {
            playlistForm.addEventListener('submit', function (e) {
                e.preventDefault();
                if (SmartLists.createPlaylist) {
                    SmartLists.createPlaylist(page);
                }
            }, SmartLists.getEventListenerOptions(pageSignal));
        }

        // Add search input event listener
        const searchInput = page.querySelector('#playlistSearchInput');
        const clearSearchBtn = page.querySelector('#clearSearchBtn');
        if (searchInput) {
            // Store search timeout on the page for cleanup
            page._searchTimeout = null;

            // Function to update clear button visibility
            const updateClearButtonVisibility = function () {
                if (clearSearchBtn) {
                    clearSearchBtn.style.display = searchInput.value.trim() ? 'flex' : 'none';
                }
            };

            // Use debounced search to avoid too many re-renders
            searchInput.addEventListener('input', function () {
                updateClearButtonVisibility();
                clearTimeout(page._searchTimeout);
                page._searchTimeout = setTimeout(async function () {
                    try {
                        if (SmartLists.applySearchFilter) {
                            await SmartLists.applySearchFilter(page);
                        }
                    } catch (err) {
                        console.error('Error during search:', err);
                        SmartLists.showNotification('Search error: ' + err.message);
                    }
                }, 300); // 300ms delay
            }, SmartLists.getEventListenerOptions(pageSignal));

            // Also search on Enter key
            searchInput.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') {
                    clearTimeout(page._searchTimeout);
                    if (SmartLists.applySearchFilter) {
                        SmartLists.applySearchFilter(page).catch(function (err) {
                            console.error('Error during search:', err);
                            SmartLists.showNotification('Search error: ' + err.message);
                        });
                    }
                }
            }, SmartLists.getEventListenerOptions(pageSignal));

            // Handle clear button click
            if (clearSearchBtn) {
                clearSearchBtn.addEventListener('click', function () {
                    searchInput.value = '';
                    updateClearButtonVisibility();
                    clearTimeout(page._searchTimeout);
                    if (SmartLists.applySearchFilter) {
                        SmartLists.applySearchFilter(page).catch(function (err) {
                            console.error('Error during search:', err);
                            SmartLists.showNotification('Search error: ' + err.message);
                        });
                    }
                    searchInput.focus(); // Return focus to search input
                }, SmartLists.getEventListenerOptions(pageSignal));
            }

            // Initialize clear button visibility
            updateClearButtonVisibility();
        }

        // Generic event listener setup - eliminates DRY violations
        if (SmartLists.setupFilterEventListeners) {
            SmartLists.setupFilterEventListeners(page, pageSignal);
        }

        const clearFiltersBtn = page.querySelector('#clearFiltersBtn');
        if (clearFiltersBtn) {
            clearFiltersBtn.addEventListener('click', function () {
                if (SmartLists.clearAllFilters) {
                    SmartLists.clearAllFilters(page);
                }
            }, SmartLists.getEventListenerOptions(pageSignal));
        }

        // Add import file input event listener
        const importFileInput = page.querySelector('#importPlaylistsFile');
        const importBtn = page.querySelector('#importPlaylistsBtn');
        const selectedFileName = page.querySelector('#selectedFileName');
        if (importFileInput && importBtn) {
            importFileInput.addEventListener('change', function () {
                const hasFile = this.files && this.files.length > 0;

                // Show/hide and enable/disable import button based on file selection
                if (hasFile) {
                    importBtn.style.display = 'inline-block';
                    importBtn.disabled = false;
                } else {
                    importBtn.style.display = 'none';
                    importBtn.disabled = true;
                }

                // Update filename display
                if (selectedFileName) {
                    if (hasFile) {
                        selectedFileName.textContent = 'Selected: ' + this.files[0].name;
                        selectedFileName.style.fontStyle = 'italic';
                    } else {
                        selectedFileName.textContent = '';
                    }
                }
            }, SmartLists.getEventListenerOptions(pageSignal));
        }
    };

    // ===== PLAYLIST NAMING =====
    SmartLists.updatePlaylistNamePreview = function (page) {
        const prefixElement = page.querySelector('#playlistNamePrefix');
        const suffixElement = page.querySelector('#playlistNameSuffix');
        const previewText = page.querySelector('#previewText');

        // Return early if preview text element is missing
        if (!previewText) {
            return;
        }

        // Use safe defaults: empty string for prefix, "[Smart]" for suffix
        const prefix = prefixElement ? prefixElement.value : '';
        const suffix = suffixElement ? suffixElement.value : '[Smart]';

        const exampleName = 'My Awesome List';
        let finalName = '';

        if (prefix) {
            finalName += prefix + ' ';
        }
        finalName += exampleName;
        if (suffix) {
            finalName += ' ' + suffix;
        }

        previewText.textContent = finalName;
    };

    SmartLists.setupPlaylistNamingListeners = function (page, signal) {
        const prefixInput = page.querySelector('#playlistNamePrefix');
        const suffixInput = page.querySelector('#playlistNameSuffix');

        if (prefixInput) {
            prefixInput.addEventListener('input', function () {
                SmartLists.updatePlaylistNamePreview(page);
            }, SmartLists.getEventListenerOptions(signal));
        }

        if (suffixInput) {
            suffixInput.addEventListener('input', function () {
                SmartLists.updatePlaylistNamePreview(page);
            }, SmartLists.getEventListenerOptions(signal));
        }
    };

    // ===== CONFIGURATION MANAGEMENT =====
    SmartLists.loadConfiguration = function (page) {
        Dashboard.showLoadingMsg();
        SmartLists.getApiClient().getPluginConfiguration(SmartLists.getPluginId()).then(function (config) {
            const defaultSortByEl = page.querySelector('#defaultSortBy');
            const defaultSortOrderEl = page.querySelector('#defaultSortOrder');
            const defaultIgnoreArticlesCheckbox = page.querySelector('#defaultIgnoreArticles');
            const defaultIgnoreArticlesContainer = page.querySelector('#defaultIgnoreArticlesContainer');
            const defaultListTypeEl = page.querySelector('#defaultListType');
            const defaultMakePublicEl = page.querySelector('#defaultMakePublic');
            const defaultMaxItemsEl = page.querySelector('#defaultMaxItems');
            const defaultMaxPlayTimeMinutesEl = page.querySelector('#defaultMaxPlayTimeMinutes');
            const defaultAutoRefreshEl = page.querySelector('#defaultAutoRefresh');
            const playlistNamePrefixEl = page.querySelector('#playlistNamePrefix');
            const playlistNameSuffixEl = page.querySelector('#playlistNameSuffix');

            // Handle backwards compatibility for DefaultSortBy with "(Ignore Articles)"
            let sortBy = config.DefaultSortBy || 'Name';
            let ignoreArticles = false;

            if (sortBy === 'Name (Ignore Articles)') {
                sortBy = 'Name';
                ignoreArticles = true;
            } else if (sortBy === 'SeriesName (Ignore Articles)') {
                sortBy = 'SeriesName';
                ignoreArticles = true;
            }

            if (defaultSortByEl) defaultSortByEl.value = sortBy;
            if (defaultSortOrderEl) defaultSortOrderEl.value = config.DefaultSortOrder || 'Ascending';
            if (defaultIgnoreArticlesCheckbox) defaultIgnoreArticlesCheckbox.checked = ignoreArticles;

            // Show/hide ignore articles checkbox based on current sort selection
            if (defaultIgnoreArticlesContainer) {
                const showCheckbox = (sortBy === 'Name' || sortBy === 'SeriesName');
                defaultIgnoreArticlesContainer.style.display = showCheckbox ? '' : 'none';
            }

            if (defaultListTypeEl) defaultListTypeEl.value = config.DefaultListType || 'Playlist';
            if (defaultMakePublicEl) defaultMakePublicEl.checked = config.DefaultMakePublic || false;
            if (defaultMaxItemsEl) defaultMaxItemsEl.value = config.DefaultMaxItems !== undefined && config.DefaultMaxItems !== null ? config.DefaultMaxItems : 500;
            if (defaultMaxPlayTimeMinutesEl) defaultMaxPlayTimeMinutesEl.value = config.DefaultMaxPlayTimeMinutes !== undefined && config.DefaultMaxPlayTimeMinutes !== null ? config.DefaultMaxPlayTimeMinutes : 0;
            if (defaultAutoRefreshEl) defaultAutoRefreshEl.value = config.DefaultAutoRefresh || 'OnLibraryChanges';

            if (playlistNamePrefixEl) playlistNamePrefixEl.value = config.PlaylistNamePrefix !== undefined && config.PlaylistNamePrefix !== null ? config.PlaylistNamePrefix : '';
            if (playlistNameSuffixEl) playlistNameSuffixEl.value = config.PlaylistNameSuffix !== undefined && config.PlaylistNameSuffix !== null ? config.PlaylistNameSuffix : '[Smart]';

            // Load processing batch size setting
            const processingBatchSizeEl = page.querySelector('#processingBatchSize');
            if (processingBatchSizeEl) {
                processingBatchSizeEl.value = config.ProcessingBatchSize !== undefined && config.ProcessingBatchSize !== null && config.ProcessingBatchSize > 0 ? config.ProcessingBatchSize : 300;
            }

            // Load enable user page setting
            const enableUserPageEl = page.querySelector('#enableUserPage');
            if (enableUserPageEl) {
                enableUserPageEl.checked = config.EnableUserPage !== undefined && config.EnableUserPage !== null ? config.EnableUserPage : true;
            }

            // Load allowed users for user page access
            if (config.AllowedUserPageUsers && Array.isArray(config.AllowedUserPageUsers) && config.AllowedUserPageUsers.length > 0) {
                // Store allowed users to be applied when multi-select is initialized
                // (loadAllowedUsersMultiSelect will apply these after loading users)
                page._pendingAllowedUserPageUsers = config.AllowedUserPageUsers;
            }

            // Load schedule configuration values
            const defaultScheduleTriggerElement = page.querySelector('#defaultScheduleTrigger');
            if (defaultScheduleTriggerElement) {
                // DefaultScheduleTrigger is nullable enum - null means no schedule
                defaultScheduleTriggerElement.value = config.DefaultScheduleTrigger || '';

                // Event listener is already added in populateStaticSelects to avoid duplication

                // Update containers based on current value
                SmartLists.updateDefaultScheduleContainers(page, defaultScheduleTriggerElement.value);
            }

            const defaultScheduleTimeElement = page.querySelector('#defaultScheduleTime');
            if (defaultScheduleTimeElement && config.DefaultScheduleTime) {
                // Parse time from "HH:mm:ss" format and convert to "HH:mm" for the select
                const timeParts = config.DefaultScheduleTime.split(':');
                if (timeParts.length >= 2) {
                    const timeValue = timeParts[0] + ':' + timeParts[1];
                    defaultScheduleTimeElement.value = timeValue;
                }
            }

            const defaultScheduleDayOfWeekElement = page.querySelector('#defaultScheduleDayOfWeek');
            if (defaultScheduleDayOfWeekElement && config.DefaultScheduleDayOfWeek !== undefined) {
                defaultScheduleDayOfWeekElement.value = SmartLists.convertDayOfWeekToValue(config.DefaultScheduleDayOfWeek);
            }

            const defaultScheduleDayOfMonthElement = page.querySelector('#defaultScheduleDayOfMonth');
            if (defaultScheduleDayOfMonthElement && config.DefaultScheduleDayOfMonth !== undefined) {
                defaultScheduleDayOfMonthElement.value = config.DefaultScheduleDayOfMonth.toString();
            }

            const defaultScheduleMonthElement = page.querySelector('#defaultScheduleMonth');
            if (defaultScheduleMonthElement && config.DefaultScheduleMonth !== undefined) {
                defaultScheduleMonthElement.value = config.DefaultScheduleMonth.toString();
            }

            const defaultScheduleIntervalElement = page.querySelector('#defaultScheduleInterval');
            if (defaultScheduleIntervalElement && config.DefaultScheduleInterval) {
                defaultScheduleIntervalElement.value = config.DefaultScheduleInterval;
            }

            // Update preview after loading configuration
            SmartLists.updatePlaylistNamePreview(page);

            Dashboard.hideLoadingMsg();
        }).catch(function (err) {
            console.error('Error loading configuration:', err);
            Dashboard.hideLoadingMsg();
        });
    };

    SmartLists.saveConfiguration = function (page) {
        Dashboard.showLoadingMsg();
        const apiClient = SmartLists.getApiClient();
        apiClient.getPluginConfiguration(SmartLists.getPluginId()).then(function (config) {
            // Handle DefaultSortBy with ignore articles checkbox
            let sortBy = page.querySelector('#defaultSortBy').value;
            const ignoreArticlesCheckbox = page.querySelector('#defaultIgnoreArticles');

            if ((sortBy === 'Name' || sortBy === 'SeriesName') && ignoreArticlesCheckbox && ignoreArticlesCheckbox.checked) {
                sortBy = sortBy + ' (Ignore Articles)';
            }

            config.DefaultSortBy = sortBy;
            config.DefaultSortOrder = page.querySelector('#defaultSortOrder').value;
            config.DefaultListType = page.querySelector('#defaultListType').value;
            config.DefaultMakePublic = page.querySelector('#defaultMakePublic').checked;
            const defaultMaxItemsInput = page.querySelector('#defaultMaxItems').value;
            if (defaultMaxItemsInput === '') {
                config.DefaultMaxItems = 500;
            } else {
                const parsedValue = parseInt(defaultMaxItemsInput);
                config.DefaultMaxItems = isNaN(parsedValue) ? 500 : parsedValue;
            }

            const defaultMaxPlayTimeMinutesInput = page.querySelector('#defaultMaxPlayTimeMinutes').value;
            if (defaultMaxPlayTimeMinutesInput === '') {
                config.DefaultMaxPlayTimeMinutes = 0;
            } else {
                const parsedValue = parseInt(defaultMaxPlayTimeMinutesInput);
                config.DefaultMaxPlayTimeMinutes = isNaN(parsedValue) ? 0 : parsedValue;
            }

            config.DefaultAutoRefresh = page.querySelector('#defaultAutoRefresh').value || 'OnLibraryChanges';

            // Save default schedule settings
            // DefaultScheduleTrigger is nullable ScheduleTrigger enum - send null for empty, otherwise the enum value
            const defaultScheduleTriggerValue = page.querySelector('#defaultScheduleTrigger').value;
            config.DefaultScheduleTrigger = defaultScheduleTriggerValue === '' ? null : (defaultScheduleTriggerValue || null);

            // DefaultScheduleTime is TimeSpan - send in format "HH:mm:ss" or "HH:mm:ss.fffffff"
            const defaultScheduleTimeValue = page.querySelector('#defaultScheduleTime').value;
            if (defaultScheduleTimeValue) {
                // Parse HH:mm format and convert to HH:mm:ss
                const timeParts = defaultScheduleTimeValue.split(':');
                const hours = parseInt(timeParts[0] || '0', 10);
                const minutes = parseInt(timeParts[1] || '0', 10);
                // Manual padding for ES5 compatibility
                const hoursStr = hours < 10 ? '0' + hours : hours.toString();
                const minutesStr = minutes < 10 ? '0' + minutes : minutes.toString();
                config.DefaultScheduleTime = hoursStr + ':' + minutesStr + ':00';
            } else {
                config.DefaultScheduleTime = '00:00:00';
            }

            // DefaultScheduleDayOfWeek is DayOfWeek enum (not nullable) - default to 0 (Sunday) if empty
            const defaultScheduleDayOfWeekValue = page.querySelector('#defaultScheduleDayOfWeek').value;
            config.DefaultScheduleDayOfWeek = defaultScheduleDayOfWeekValue ? parseInt(defaultScheduleDayOfWeekValue, 10) : 0;

            // DefaultScheduleDayOfMonth is int (not nullable) - default to 1 if empty
            const defaultScheduleDayOfMonthValue = page.querySelector('#defaultScheduleDayOfMonth').value;
            config.DefaultScheduleDayOfMonth = defaultScheduleDayOfMonthValue ? parseInt(defaultScheduleDayOfMonthValue, 10) : 1;

            // DefaultScheduleMonth is int (not nullable) - default to 1 (January) if empty
            const defaultScheduleMonthValue = page.querySelector('#defaultScheduleMonth').value;
            config.DefaultScheduleMonth = defaultScheduleMonthValue ? parseInt(defaultScheduleMonthValue, 10) : 1;

            // DefaultScheduleInterval is TimeSpan - send in format "HH:mm:ss" or "d.HH:mm:ss"
            const defaultScheduleIntervalValue = page.querySelector('#defaultScheduleInterval').value;
            if (defaultScheduleIntervalValue) {
                // Parse interval format (e.g., "15" = minutes, "1:00" = hours:minutes)
                // Convert to TimeSpan format "HH:mm:ss"
                const intervalParts = defaultScheduleIntervalValue.split(':');
                if (intervalParts.length === 1) {
                    // Just minutes (e.g., "15")
                    const minutes = parseInt(intervalParts[0], 10);
                    const hours = Math.floor(minutes / 60);
                    const remainingMinutes = minutes % 60;
                    // Manual padding for ES5 compatibility
                    const hoursStr = hours < 10 ? '0' + hours : hours.toString();
                    const minutesStr = remainingMinutes < 10 ? '0' + remainingMinutes : remainingMinutes.toString();
                    config.DefaultScheduleInterval = hoursStr + ':' + minutesStr + ':00';
                } else if (intervalParts.length === 2) {
                    // Hours:minutes format (e.g., "1:00")
                    const hours = parseInt(intervalParts[0], 10);
                    const minutes = parseInt(intervalParts[1], 10);
                    // Manual padding for ES5 compatibility
                    const hoursStr = hours < 10 ? '0' + hours : hours.toString();
                    const minutesStr = minutes < 10 ? '0' + minutes : minutes.toString();
                    config.DefaultScheduleInterval = hoursStr + ':' + minutesStr + ':00';
                } else {
                    // Already in correct format or invalid - use as-is
                    config.DefaultScheduleInterval = defaultScheduleIntervalValue;
                }
            } else {
                // Default to 15 minutes if empty
                config.DefaultScheduleInterval = '00:15:00';
            }

            // Allow empty strings for prefix and suffix
            const prefixValue = page.querySelector('#playlistNamePrefix').value;
            const suffixValue = page.querySelector('#playlistNameSuffix').value;
            config.PlaylistNamePrefix = prefixValue;
            config.PlaylistNameSuffix = suffixValue;

            // Save processing batch size setting
            const processingBatchSizeInput = page.querySelector('#processingBatchSize').value;
            if (processingBatchSizeInput === '') {
                config.ProcessingBatchSize = 300;
            } else {
                const parsedValue = parseInt(processingBatchSizeInput, 10);
                config.ProcessingBatchSize = (isNaN(parsedValue) || parsedValue <= 0) ? 300 : parsedValue;
            }

            // Save enable user page setting
            const enableUserPageCheckbox = page.querySelector('#enableUserPage');
            config.EnableUserPage = enableUserPageCheckbox ? enableUserPageCheckbox.checked : true;

            // Save allowed users for user page access
            const allowedUserIds = SmartLists.getSelectedItems ? SmartLists.getSelectedItems(page, 'allowedUsersMultiSelect', 'allowed-users-checkbox') : [];
            config.AllowedUserPageUsers = allowedUserIds && allowedUserIds.length > 0 ? allowedUserIds : null;

            apiClient.updatePluginConfiguration(SmartLists.getPluginId(), config).then(function () {
                Dashboard.hideLoadingMsg();
                SmartLists.showNotification('Configuration saved successfully.', 'success');
                window.scrollTo({ top: 0, behavior: 'smooth' });
            }).catch(function (err) {
                console.error('Error saving configuration:', err);
                Dashboard.hideLoadingMsg();
                SmartLists.showNotification('Failed to save configuration: ' + err.message, 'error');
            });
        }).catch(function (err) {
            console.error('Error loading configuration for save:', err);
            Dashboard.hideLoadingMsg();
            SmartLists.showNotification('Failed to load configuration: ' + err.message, 'error');
        });
    };

    SmartLists.refreshAllPlaylists = function () {
        // Show notification that refresh has started
        var statusLink = SmartLists.createStatusPageLink('status page');
        var refreshMessage = SmartLists.IS_USER_PAGE 
            ? 'List refresh started. Your lists will be updated in the background.'
            : 'List refresh started, check the ' + statusLink + ' for progress.';
        SmartLists.showNotification(refreshMessage, 'info', { html: true });

        // Start the refresh operation (fire and forget - status page will show progress)
        SmartLists.getApiClient().ajax({
            type: "POST",
            url: SmartLists.getApiClient().getUrl(SmartLists.ENDPOINTS.refreshDirect),
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
            // Success - operations are queued and will be processed in the background
            // No success notification needed since status page shows progress
        }).catch(async function (err) {
            // Extract error message using utility function
            const errorMessage = await SmartLists.extractErrorMessage(
                err,
                'An unexpected error occurred, check the logs for more details.'
            );

            SmartLists.showNotification('Failed to refresh all lists: ' + errorMessage, 'error');
        });
    };

    // ===== STYLING =====
    SmartLists.applyCustomStyles = function () {
        // Check if styles are already added
        if (document.getElementById('smartlists-custom-styles')) {
            return;
        }

        // Load multi-select CSS file
        const multiSelectLink = document.createElement('link');
        multiSelectLink.rel = 'stylesheet';
        multiSelectLink.href = 'configurationpage?name=config-multi-select.css';
        document.head.appendChild(multiSelectLink);

        const style = document.createElement('style');
        style.id = 'smartlists-custom-styles';
        style.textContent = `
            select.emby-select, select[is="emby-select"] {
                -webkit-appearance: none;
                -moz-appearance: none;
                appearance: none;
                background-image: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' fill='%23e0e0e0' viewBox='0 0 24 24'%3e%3cpath d='M7 10l5 5 5-5z'/%3e%3c/svg%3e");
                background-repeat: no-repeat;
                background-position: right 0.7em top 50%;
                background-size: 1.2em auto;
                padding-right: 1em !important;
            }
            
            /* Field group styling */
            optgroup {
                font-weight: bold;
                font-size: 0.9em;
                background: var(--jf-palette-background-paper);
                padding: 0.2em 0;
                margin-top: 0.3em;
            }

            optgroup option {
                font-weight: normal;
                font-size: 1em;
                background: inherit;
                padding-left: 1em;
            }
            
            /* Hide native search input clear button to avoid double X with our custom clear button */
            #playlistSearchInput::-webkit-search-cancel-button,
            #playlistSearchInput::-webkit-search-cancel-decoration,
            #playlistSearchInput::-webkit-search-results-button,
            #playlistSearchInput::-webkit-search-results-decoration {
                -webkit-appearance: none !important;
                appearance: none !important;
                display: none !important;
            }
            
            /* Style danger/delete buttons to be red */
            .SmartListsConfigurationPage .emby-button.danger,
            .SmartListsConfigurationPage .emby-button.button-delete {
                background-color: #BB3932 !important;
                border-color: #BB3932 !important;
            }
        `;
        document.head.appendChild(style);
    };

    // ===== CLEANUP =====
    SmartLists.cleanupAllEventListeners = function (page) {
        // Clean up rule event listeners
        const allRules = page.querySelectorAll('.rule-row');
        allRules.forEach(function (rule) {
            if (SmartLists.cleanupRuleEventListeners) {
                SmartLists.cleanupRuleEventListeners(rule);
            }
        });

        // Clean up modal listeners
        const deleteModal = page.querySelector('#delete-confirm-modal');
        if (deleteModal) {
            SmartLists.cleanupModalListeners(deleteModal);
        }
        const refreshModal = page.querySelector('#refresh-confirm-modal');
        if (refreshModal) {
            SmartLists.cleanupModalListeners(refreshModal);
        }

        // Cleanup user multi-select listeners
        if (SmartLists.cleanupUserMultiSelect) {
            SmartLists.cleanupUserMultiSelect(page);
        }

        // Clean up page event listeners
        if (page._pageAbortController) {
            page._pageAbortController.abort();
            page._pageAbortController = null;
        }

        // Clean up tab listeners
        if (page._tabAbortController) {
            page._tabAbortController.abort();
            page._tabAbortController = null;
        }

        // Clean up search timeout
        if (page._searchTimeout) {
            clearTimeout(page._searchTimeout);
            page._searchTimeout = null;
        }

        // Clean up media type debounce timer
        if (page._mediaTypeUpdateTimer) {
            clearTimeout(page._mediaTypeUpdateTimer);
            page._mediaTypeUpdateTimer = null;
        }

        // Abort media type checkbox listeners
        if (page._mediaTypeAbortController) {
            page._mediaTypeAbortController.abort();
            page._mediaTypeAbortController = null;
        }

        // Stop status polling timers
        if (window.SmartLists && window.SmartLists.Status) {
            window.SmartLists.Status.stopPolling();
            if (window.SmartLists.Status.stopAggressivePolling) {
                window.SmartLists.Status.stopAggressivePolling();
            }
        }

        // Clean up notification timer
        if (SmartLists.clearNotification) {
            SmartLists.clearNotification();
        }

        // Clean up navigation listeners
        const navContainer = page.querySelector('.localnav');
        if (navContainer) {
            // Clean up navigation click listeners via AbortController
            if (navContainer._navAbortController) {
                try {
                    navContainer._navAbortController.abort();
                } catch (e) {
                    console.warn('Failed to abort navigation listeners:', e);
                }
                navContainer._navAbortController = null;
            }

            // Note: No popstate listener to clean up

            navContainer._navInitialized = false;
        }

        // Reset page-specific initialization flags and edit state
        page._pageInitialized = false;
        page._tabListenersInitialized = false;
        page._editMode = false;
        page._editingPlaylistId = null;
        page._loadingPlaylists = false;
        page._allPlaylists = null; // Clear stored playlist data
    };

    // ===== PAGE EVENT LISTENERS =====
    document.addEventListener('pageshow', function (e) {
        const page = e.target;
        if (page.classList.contains('SmartListsConfigurationPage')) {
            // Clean up any persisted inline styles from browser back/forward navigation
            // This fixes the issue where navigating back from user page leaves elements hidden
            const elementsWithInlineDisplay = page.querySelectorAll('[style*="display"]');
            elementsWithInlineDisplay.forEach(function(el) {
                // Only remove inline display styles, not other inline styles
                if (el.style.display) {
                    // Check if this element should have controlled visibility
                    const hasVisibilityClass = el.classList.contains('playlist-only-field') ||
                                              el.classList.contains('collection-only-field') ||
                                              el.classList.contains('playlist-only-description') ||
                                              el.classList.contains('collection-only-description') ||
                                              el.classList.contains('playlist-only-label') ||
                                              el.classList.contains('collection-only-label') ||
                                              el.id === 'publicCheckboxContainer';
                    
                    if (hasVisibilityClass) {
                        el.style.removeProperty('display');
                    }
                }
            });
            
            SmartLists.initPage(page);
            
            // Re-apply list type visibility after cleanup
            if (page._pageInitialized) {
                SmartLists.handleListTypeChange(page);
            }
        }
    });

    // Fallback initialization for pages loaded directly (not via Jellyfin navigation)
    // This handles the case where pageshow doesn't fire for custom plugin pages
    document.addEventListener('DOMContentLoaded', function () {
        const page = document.querySelector('.SmartListsConfigurationPage');
        if (page && !page._pageInitialized) {
            SmartLists.initPage(page);
        }
    });

    // Clean up all event listeners when page is hidden/unloaded
    document.addEventListener('pagehide', function (e) {
        const page = e.target;
        if (page.classList.contains('SmartListsConfigurationPage')) {
            SmartLists.cleanupAllEventListeners(page);
        }
    });

    // ===== LIST TYPE CHANGE HANDLER =====
    SmartLists.handleListTypeChange = function (page) {
        const listTypeSelect = page.querySelector('#listType');
        if (!listTypeSelect) return;

        const listType = listTypeSelect.value;
        const isCollection = listType === 'Collection';

        // Show/hide playlist-only fields
        const playlistOnlyFields = page.querySelectorAll('.playlist-only-field');
        playlistOnlyFields.forEach(function (field) {
            if (isCollection) {
                field.style.display = 'none';
            } else {
                // Remove inline style to prevent persistence across page navigations
                field.style.removeProperty('display');
            }
        });

        // Show/hide collection-only fields
        const collectionOnlyFields = page.querySelectorAll('.collection-only-field');
        collectionOnlyFields.forEach(function (field) {
            if (isCollection) {
                field.style.removeProperty('display');
            } else {
                field.style.display = 'none';
            }
        });

        // Show/hide playlist-only and collection-only descriptions
        const playlistOnlyDescriptions = page.querySelectorAll('.playlist-only-description');
        playlistOnlyDescriptions.forEach(function (desc) {
            if (isCollection) {
                desc.style.display = 'none';
            } else {
                desc.style.removeProperty('display');
            }
        });

        const collectionOnlyDescriptions = page.querySelectorAll('.collection-only-description');
        collectionOnlyDescriptions.forEach(function (desc) {
            if (isCollection) {
                desc.style.removeProperty('display');
            } else {
                desc.style.display = 'none';
            }
        });

        // Show/hide playlist-only and collection-only labels
        const playlistOnlyLabels = page.querySelectorAll('.playlist-only-label');
        playlistOnlyLabels.forEach(function (label) {
            if (isCollection) {
                label.style.display = 'none';
            } else {
                label.style.removeProperty('display');
            }
        });

        // Reload users with appropriate UI (single select for collections, multi-select for playlists)
        // Skip for user pages (admin-only endpoint)
        if (!SmartLists.IS_USER_PAGE) {
            SmartLists.loadUsers(page);
        }

        // Update public checkbox visibility
        if (SmartLists.updatePublicCheckboxVisibility) {
            SmartLists.updatePublicCheckboxVisibility(page);
        }

        const collectionOnlyLabels = page.querySelectorAll('.collection-only-label');
        collectionOnlyLabels.forEach(function (label) {
            if (isCollection) {
                label.style.removeProperty('display');
            } else {
                label.style.display = 'none';
            }
        });

        // Update list type label text
        const listTypeLabel = page.querySelector('.list-type-label');
        if (listTypeLabel) {
            listTypeLabel.textContent = isCollection ? 'Collection' : 'Playlist';
        }

        // Update required attributes
        const userSelect = page.querySelector('#playlistUser');
        if (userSelect) {
            if (isCollection) {
                userSelect.setAttribute('required', 'required');
            } else {
                userSelect.removeAttribute('required');
            }
        }

        // Update submit button text
        const submitBtn = page.querySelector('#submitBtn');
        const editState = SmartLists.getPageEditState(page);
        if (submitBtn) {
            if (editState.editMode) {
                submitBtn.textContent = 'Update ' + listType;
            } else {
                submitBtn.textContent = 'Create ' + listType;
            }
        }

        // Save currently selected media types before regenerating checkboxes
        const currentlySelectedMediaTypes = SmartLists.getSelectedMediaTypes(page);

        // Update Collections options visibility for all rules when list type changes
        if (SmartLists.updateAllCollectionsOptionsVisibility) {
            SmartLists.updateAllCollectionsOptionsVisibility(page);
        }

        // Regenerate media type checkboxes to show/hide collection-only types
        if (SmartLists.generateMediaTypeCheckboxes) {
            // Set flag to prevent change handlers from firing during regeneration
            page._skipMediaTypeChangeHandlers = true;
            SmartLists.generateMediaTypeCheckboxes(page);

            // Restore previously selected media types, excluding collection-only types when switching to Playlist
            const filteredMediaTypes = currentlySelectedMediaTypes.filter(function (value) {
                // Skip Series if switching to Playlist mode (Series is collection-only)
                return !(value === 'Series' && !isCollection);
            });

            if (filteredMediaTypes.length > 0) {
                SmartLists.setSelectedItems(page, 'mediaTypesMultiSelect', filteredMediaTypes, 'media-type-multi-select-checkbox', 'Select media types...');
            }

            // Clear flag to re-enable change handlers
            page._skipMediaTypeChangeHandlers = false;
        }

        // Update field selects when list type changes
        if (SmartLists.updateAllFieldSelects) {
            SmartLists.updateAllFieldSelects(page);
        }

        // Update visibility of parent series options based on media types
        if (SmartLists.updateAllTagsOptionsVisibility) {
            SmartLists.updateAllTagsOptionsVisibility(page);
        }
        if (SmartLists.updateAllStudiosOptionsVisibility) {
            SmartLists.updateAllStudiosOptionsVisibility(page);
        }
        if (SmartLists.updateAllGenresOptionsVisibility) {
            SmartLists.updateAllGenresOptionsVisibility(page);
        }
        if (SmartLists.updateAllAudioLanguagesOptionsVisibility) {
            SmartLists.updateAllAudioLanguagesOptionsVisibility(page);
        }

        // Collections are server-wide, no library loading needed
    };

})(window.SmartLists = window.SmartLists || {});

// Immediate initialization check - runs as soon as this script loads
(function() {
    // Store observer reference for cleanup
    var pageObserver = null;
    
    // Required functions that must be loaded before initialization
    var requiredFunctions = [
        'createAbortController',           // config-core.js
        'generateAutoRefreshOptions',      // config-formatters.js
        'initializeScheduleSystem',        // config-schedules.js
        'initializeVisibilityScheduleSystem', // config-schedules.js
        'addSortBox',                      // config-sorts.js
        'initializeSortSystem',            // config-sorts.js
        'initializeMultiSelect',           // config-multi-select.js
        'updateAllFieldSelects'            // config-rules.js
    ];
    
    // Check if all required dependencies are loaded
    var checkDependencies = function() {
        for (var i = 0; i < requiredFunctions.length; i++) {
            if (typeof window.SmartLists[requiredFunctions[i]] !== 'function') {
                return false;
            }
        }
        return true;
    };
    
    // Get list of missing dependencies for error reporting
    var getMissingDependencies = function() {
        var missing = [];
        for (var i = 0; i < requiredFunctions.length; i++) {
            if (typeof window.SmartLists[requiredFunctions[i]] !== 'function') {
                missing.push(requiredFunctions[i]);
            }
        }
        return missing;
    };
    
    // Disconnect the MutationObserver to prevent memory leaks
    var disconnectObserver = function() {
        if (pageObserver) {
            pageObserver.disconnect();
            pageObserver = null;
        }
    };
    
    var checkAndInit = function(retryCount, targetPage) {
        retryCount = retryCount || 0;
        var maxRetries = 30; // 30 retries × 100ms = 3 seconds total
        
        // Use the specific page element if provided, otherwise find it
        var page = targetPage || document.querySelector('.SmartListsConfigurationPage');
        if (!page) {
            return;
        }
        
        // Check if this specific page element is already initialized or initialization is in progress
        if (page._pageInitialized || page._initializationInProgress) {
            return;
        }
        
        // For user pages loaded via Plugin Pages, ensure the page is actually visible/attached
        // This prevents initializing stale elements from previous navigations
        if (!document.body.contains(page)) {
            return;
        }
        
        // Check if all dependencies are loaded
        if (!checkDependencies()) {
            if (retryCount < maxRetries) {
                // Retry after a short delay to allow all scripts to load
                setTimeout(function() {
                    checkAndInit(retryCount + 1, page);
                }, 100);
            } else {
                var missingFunctions = getMissingDependencies();
                console.error('SmartLists: Required dependencies not loaded after 3 seconds. Missing functions:', missingFunctions);
                // Initialize anyway to at least show the page
                page._initializationInProgress = true;
                window.SmartLists.initPage(page);
            }
            return;
        }
        
        // Mark initialization as in progress to prevent duplicate calls
        page._initializationInProgress = true;
        
        // Disconnect observer once we've found and are initializing our page
        disconnectObserver();
        
        // All dependencies loaded, proceed with initialization
        window.SmartLists.initPage(page);
    };
    
    // For Plugin Pages plugin - watch for when content is dynamically inserted into the DOM
    var setupMutationObserver = function() {
        // Don't set up if already exists
        if (pageObserver) {
            return;
        }
        
        var MutationObserver = window.MutationObserver || window.WebKitMutationObserver;
        if (!MutationObserver) {
            return;
        }
        
        pageObserver = new MutationObserver(function(mutations) {
            mutations.forEach(function(mutation) {
                if (mutation.addedNodes && mutation.addedNodes.length > 0) {
                    for (var i = 0; i < mutation.addedNodes.length; i++) {
                        var node = mutation.addedNodes[i];
                        var pageElement = null;
                        
                        // Check if our page was added
                        if (node.classList && node.classList.contains('SmartListsConfigurationPage')) {
                            pageElement = node;
                        }
                        // Also check descendants in case page is nested
                        else if (node.querySelector && node.querySelector('.SmartListsConfigurationPage')) {
                            pageElement = node.querySelector('.SmartListsConfigurationPage');
                        }
                        
                        if (pageElement) {
                            // Pass the specific page element to checkAndInit
                            checkAndInit(0, pageElement);
                            return;
                        }
                    }
                }
            });
        });
        
        // Observe the document body for added nodes
        pageObserver.observe(document.body, {
            childList: true,
            subtree: true
        });
    };
    
    // Standard page events for normal Jellyfin page navigation
    // Note: pageshow event target is the document, so we need to query for our page element
    document.addEventListener('pageshow', function (e) {
        var page = document.querySelector('.SmartListsConfigurationPage');
        if (page && !page._pageInitialized && !page._initializationInProgress) {
            checkAndInit(0, page);
        }
    });
    
    document.addEventListener('viewshow', function (e) {
        var page = e.detail && e.detail.element;
        if (page && page.classList && page.classList.contains('SmartListsConfigurationPage')) {
            checkAndInit(0, page);
        }
    });
    
    // Try immediate initialization for standard page loads
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function() {
            checkAndInit();
            setupMutationObserver();
        });
    } else {
        checkAndInit();
        setupMutationObserver();
    }
})();
