(function (SmartLists) {
    'use strict';

    // Initialize namespace if it doesn't exist
    if (!window.SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }

    // ===== MULTI-SELECT USER COMPONENT =====

    /**
     * Initialize the multi-select user component for playlists
     */
    SmartLists.initializeUserMultiSelect = function (page) {
        // Use generic multi-select component
        SmartLists.initializeMultiSelect(page, {
            containerId: 'playlistUserMultiSelect',
            displayId: 'userMultiSelectDisplay',
            dropdownId: 'userMultiSelectDropdown',
            optionsId: 'userMultiSelectOptions',
            placeholderText: 'Select users...',
            checkboxClass: 'user-multi-select-checkbox',
            onChange: function (selectedValues) {
                SmartLists.updatePublicCheckboxVisibility(page);
            }
        });

        const allUsersCheckbox = page.querySelector('#playlistAllUsers');
        if (allUsersCheckbox && !allUsersCheckbox._smartListsAllUsersInitialized) {
            allUsersCheckbox._smartListsAllUsersInitialized = true;
            allUsersCheckbox._smartListsAllUsersChangeHandler = function () {
                SmartLists.updateAllUsersSelectionState(page);
                SmartLists.updatePublicCheckboxVisibility(page);
            };
            allUsersCheckbox.addEventListener('change', allUsersCheckbox._smartListsAllUsersChangeHandler);
        }

        SmartLists.updateAllUsersSelectionState(page);
    };

    /**
     * Load users into the multi-select component
     */
    SmartLists.loadUsersIntoMultiSelect = function (page, users) {
        SmartLists.loadItemsIntoMultiSelect(
            page,
            'playlistUserMultiSelect',
            users,
            'user-multi-select-checkbox',
            function (user) { return user.Name || user.Username || user.Id; },
            function (user) { return user.Id; }
        );
    };

    /**
     * Get array of selected user IDs
     */
    SmartLists.getSelectedUserIds = function (page) {
        if (SmartLists.isAllUsersSelected(page)) {
            return [];
        }
        return SmartLists.getSelectedItems(page, 'playlistUserMultiSelect', 'user-multi-select-checkbox');
    };

    /**
     * Set selected users by user ID array
     */
    SmartLists.setSelectedUserIds = function (page, userIds) {
        SmartLists.setSelectedItems(page, 'playlistUserMultiSelect', userIds, 'user-multi-select-checkbox', 'Select users...');
        SmartLists.updatePublicCheckboxVisibility(page);
    };

    SmartLists.isAllUsersSelected = function (page) {
        const allUsersCheckbox = page.querySelector('#playlistAllUsers');
        return !!(allUsersCheckbox && allUsersCheckbox.checked);
    };

    SmartLists.setAllUsersSelected = function (page, selected) {
        const allUsersCheckbox = page.querySelector('#playlistAllUsers');
        if (allUsersCheckbox) {
            allUsersCheckbox.checked = !!selected;
        }
        SmartLists.updateAllUsersSelectionState(page);
        SmartLists.updatePublicCheckboxVisibility(page);
    };

    SmartLists.updateAllUsersSelectionState = function (page) {
        const allUsers = SmartLists.isAllUsersSelected(page);
        const options = page.querySelector('#userMultiSelectOptions');
        const display = page.querySelector('#userMultiSelectDisplay');

        if (options) {
            const checkboxes = options.querySelectorAll('.user-multi-select-checkbox');
            checkboxes.forEach(function (checkbox) {
                checkbox.disabled = allUsers;
                if (allUsers) {
                    checkbox.checked = false;
                }
            });
        }

        if (display) {
            display.style.opacity = allUsers ? '0.6' : '';
            display.style.pointerEvents = allUsers ? 'none' : '';
        }

        if (allUsers) {
            SmartLists.setUserMultiSelectDisplayText(page, 'All users');
        } else {
            SmartLists.updateUserMultiSelectDisplay(page);
        }
    };

    SmartLists.setUserMultiSelectDisplayText = function (page, text) {
        const display = page.querySelector('#userMultiSelectDisplay');
        if (!display) return;

        const placeholder = display.querySelector('.multi-select-placeholder');
        if (placeholder) {
            placeholder.style.display = 'none';
        }

        const existingSelected = display.querySelector('.multi-select-selected-items');
        if (existingSelected) {
            existingSelected.remove();
        }

        const selectedItems = document.createElement('span');
        selectedItems.className = 'multi-select-selected-items';
        selectedItems.textContent = text;

        const arrow = display.querySelector('.multi-select-arrow');
        if (arrow) {
            display.insertBefore(selectedItems, arrow);
        } else {
            display.appendChild(selectedItems);
        }
    };

    /**
     * Update the display text showing selected users
     */
    SmartLists.updateUserMultiSelectDisplay = function (page) {
        SmartLists.updateMultiSelectDisplay(page, 'playlistUserMultiSelect', 'Select users...', 'user-multi-select-checkbox');
    };

    /**
     * Update public checkbox visibility based on selected user count
     */
    SmartLists.updatePublicCheckboxVisibility = function (page) {
        // Skip on user pages - users don't have the multi-select component
        if (SmartLists.IS_USER_PAGE) {
            return;
        }
        
        const listType = SmartLists.getElementValue(page, '#listType', 'Playlist');
        const isCollection = listType === 'Collection';
        if (isCollection) {
            // Collections don't have public checkbox
            return;
        }

        const userIds = SmartLists.getSelectedUserIds(page);
        const allUsers = SmartLists.isAllUsersSelected(page);
        const publicCheckboxContainer = page.querySelector('#publicCheckboxContainer');

        if (publicCheckboxContainer) {
            if (allUsers || userIds.length > 1) {
                // Hide public checkbox for all-user and multi-user playlists (using inline style)
                // Note: We use removeProperty() when showing to avoid style persistence across navigations
                publicCheckboxContainer.style.display = 'none';
            } else {
                // Show public checkbox for single-user playlists
                // Remove inline style to prevent persistence across page navigations
                publicCheckboxContainer.style.removeProperty('display');
            }
        }
    };

    /**
     * Cleanup function to be called on page navigation to prevent memory leaks
     */
    SmartLists.cleanupUserMultiSelect = function (page) {
        const allUsersCheckbox = page.querySelector('#playlistAllUsers');
        if (allUsersCheckbox && allUsersCheckbox._smartListsAllUsersChangeHandler) {
            allUsersCheckbox.removeEventListener('change', allUsersCheckbox._smartListsAllUsersChangeHandler);
            delete allUsersCheckbox._smartListsAllUsersChangeHandler;
            delete allUsersCheckbox._smartListsAllUsersInitialized;
        }

        SmartLists.cleanupMultiSelect(page, 'playlistUserMultiSelect');
    };

})(window.SmartLists);
