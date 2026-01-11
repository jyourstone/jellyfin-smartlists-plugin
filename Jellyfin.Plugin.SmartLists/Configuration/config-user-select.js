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
        return SmartLists.getSelectedItems(page, 'playlistUserMultiSelect', 'user-multi-select-checkbox');
    };

    /**
     * Set selected users by user ID array
     */
    SmartLists.setSelectedUserIds = function (page, userIds) {
        SmartLists.setSelectedItems(page, 'playlistUserMultiSelect', userIds, 'user-multi-select-checkbox', 'Select users...');
        SmartLists.updatePublicCheckboxVisibility(page);
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
        const publicCheckboxContainer = page.querySelector('#publicCheckboxContainer');

        if (publicCheckboxContainer) {
            if (userIds.length > 1) {
                // Hide public checkbox for multi-user playlists
                // Use empty string removal to avoid inline style persistence issues
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
        SmartLists.cleanupMultiSelect(page, 'playlistUserMultiSelect');
    };

})(window.SmartLists);

