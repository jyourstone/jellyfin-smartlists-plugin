(function (SmartLists) {
    'use strict';

    // Initialize namespace if it doesn't exist
    if (!window.SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }

    // ===== GENERIC MULTI-SELECT COMPONENT =====

    /**
     * Initialize a multi-select component with configuration
     * @param {HTMLElement} page - The page element
     * @param {Object} config - Configuration object
     * @param {string} config.containerId - ID of the container element
     * @param {string} config.displayId - ID of the display element
     * @param {string} config.dropdownId - ID of the dropdown element
     * @param {string} config.optionsId - ID of the options container element
     * @param {string} config.placeholderText - Placeholder text when nothing is selected
     * @param {string} config.checkboxClass - CSS class for checkboxes
     * @param {Function} config.onChange - Callback function when selection changes (selectedValues)
     */
    SmartLists.initializeMultiSelect = function (page, config) {
        if (!config || !config.containerId) {
            console.error('SmartLists.initializeMultiSelect: config.containerId is required');
            return;
        }

        const container = page.querySelector('#' + config.containerId);
        if (!container) return;

        const display = page.querySelector('#' + config.displayId);
        const dropdown = page.querySelector('#' + config.dropdownId);
        const options = page.querySelector('#' + config.optionsId);

        if (!display || !dropdown || !options) {
            console.error('SmartLists.initializeMultiSelect: Required elements not found', {
                display: !!display,
                dropdown: !!dropdown,
                options: !!options
            });
            return;
        }

        // Prevent double-initialization
        const initKey = '_multiSelectInitialized_' + config.containerId;
        if (container[initKey]) {
            return;
        }
        container[initKey] = true;

        // Create AbortController for this component
        const abortControllerKey = '_multiSelectAbortController_' + config.containerId;
        if (!container[abortControllerKey]) {
            container[abortControllerKey] = SmartLists.createAbortController();
        }

        // Track if dropdown is open
        let isOpen = false;

        // Toggle dropdown on display click
        display.addEventListener('click', function (e) {
            e.stopPropagation();
            isOpen = !isOpen;
            dropdown.style.display = isOpen ? 'block' : 'none';
            if (isOpen) {
                // Focus first checkbox when opening
                const firstCheckbox = options.querySelector('input[type="checkbox"]');
                if (firstCheckbox) {
                    firstCheckbox.focus();
                }
            }
        }, SmartLists.getEventListenerOptions(container[abortControllerKey].signal));

        // Close dropdown when clicking outside
        document.addEventListener('click', function (e) {
            if (isOpen && !container.contains(e.target)) {
                isOpen = false;
                dropdown.style.display = 'none';
            }
        }, SmartLists.getEventListenerOptions(container[abortControllerKey].signal));

        // Prevent dropdown from closing when clicking inside
        dropdown.addEventListener('click', function (e) {
            e.stopPropagation();
        }, SmartLists.getEventListenerOptions(container[abortControllerKey].signal));

        // Update display when checkboxes change
        options.addEventListener('change', function (e) {
            if (e.target.type === 'checkbox') {
                SmartLists.updateMultiSelectDisplay(page, config.containerId, config.placeholderText, config.checkboxClass);
                if (config.onChange && typeof config.onChange === 'function') {
                    const selectedValues = SmartLists.getSelectedItems(page, config.containerId, config.checkboxClass);
                    config.onChange(selectedValues);
                }
            }
        }, SmartLists.getEventListenerOptions(container[abortControllerKey].signal));

        // Add dropdown arrow to display (only if not already added)
        if (!display.querySelector('.multi-select-arrow')) {
            const arrow = document.createElement('span');
            arrow.className = 'multi-select-arrow';
            arrow.innerHTML = '▼';
            display.appendChild(arrow);
        }
    };

    // ===== HELPER FUNCTIONS FOR ELEMENT ID DERIVATION =====

    /**
     * Derive the canonical options ID from a container ID
     * @param {string} containerId - ID of the container element
     * @returns {string} Canonical options ID
     */
    SmartLists.deriveOptionsId = function (containerId) {
        return containerId.replace('MultiSelect', 'MultiSelectOptions');
    };

    /**
     * Derive the canonical display ID from a container ID
     * @param {string} containerId - ID of the container element
     * @returns {string} Canonical display ID
     */
    SmartLists.deriveDisplayId = function (containerId) {
        return containerId.replace('MultiSelect', 'MultiSelectDisplay');
    };

    /**
     * Get the options element for a multi-select component, handling fallback patterns
     * @param {HTMLElement} page - The page element
     * @param {string} containerId - ID of the container element
     * @returns {HTMLElement|null} The options element or null if not found
     */
    SmartLists.getOptionsElement = function (page, containerId) {
        // Try the canonical derived ID first
        let optionsId = SmartLists.deriveOptionsId(containerId);
        let options = page.querySelector('#' + optionsId);

        // If that doesn't work, try alternative pattern (for user multi-select)
        if (!options && containerId === 'playlistUserMultiSelect') {
            optionsId = 'userMultiSelectOptions';
            options = page.querySelector('#' + optionsId);
        }

        // If still not found, try removing "playlist" prefix
        if (!options && containerId.startsWith('playlist')) {
            optionsId = containerId.replace('playlist', '').replace('MultiSelect', 'MultiSelectOptions');
            options = page.querySelector('#' + optionsId);
        }

        return options;
    };

    /**
     * Get the display element for a multi-select component, handling fallback patterns
     * @param {HTMLElement} page - The page element
     * @param {string} containerId - ID of the container element
     * @returns {HTMLElement|null} The display element or null if not found
     */
    SmartLists.getDisplayElement = function (page, containerId) {
        // Try the canonical derived ID first
        let displayId = SmartLists.deriveDisplayId(containerId);
        let display = page.querySelector('#' + displayId);

        // If that doesn't work, try alternative pattern (for user multi-select)
        if (!display && containerId === 'playlistUserMultiSelect') {
            displayId = 'userMultiSelectDisplay';
            display = page.querySelector('#' + displayId);
        }

        // If still not found, try removing "playlist" prefix
        if (!display && containerId.startsWith('playlist')) {
            displayId = containerId.replace('playlist', '').replace('MultiSelect', 'MultiSelectDisplay');
            display = page.querySelector('#' + displayId);
        }

        return display;
    };

    /**
     * Load items into a multi-select component
     * @param {HTMLElement} page - The page element
     * @param {string} containerId - ID of the container element
     * @param {Array} items - Array of items to populate (each item should have value and label properties)
     * @param {string} checkboxClass - CSS class for checkboxes
     * @param {Function} getItemLabel - Optional function to extract label from item (default: item.Label or item.label or item.Name or item.name)
     * @param {Function} getItemValue - Optional function to extract value from item (default: item.Value or item.value or item.Id or item.id)
     */
    SmartLists.loadItemsIntoMultiSelect = function (page, containerId, items, checkboxClass, getItemLabel, getItemValue) {
        const options = SmartLists.getOptionsElement(page, containerId);

        if (!options) {
            console.error('SmartLists.loadItemsIntoMultiSelect: Options container not found for', containerId, 'tried:', SmartLists.deriveOptionsId(containerId));
            return;
        }

        // Default label/value extractors
        const defaultGetLabel = getItemLabel || function (item) {
            return item.Label || item.label || item.Name || item.name || item.Id || item.id || String(item);
        };
        const defaultGetValue = getItemValue || function (item) {
            return item.Value || item.value || item.Id || item.id || String(item);
        };

        // Preserve currently selected values before clearing
        const currentlySelected = SmartLists.getSelectedItems(page, containerId, checkboxClass);

        // Clear existing options
        options.innerHTML = '';

        if (!items || items.length === 0) {
            const noItems = document.createElement('div');
            noItems.className = 'multi-select-option';
            noItems.style.padding = '0.5em';
            noItems.style.color = 'var(--jf-palette-text-secondary)';
            noItems.textContent = 'No items available';
            options.appendChild(noItems);
            return;
        }

        // Create checkbox for each item
        items.forEach(function (item) {
            const option = document.createElement('div');
            option.className = 'multi-select-option';

            const label = document.createElement('label');
            label.className = 'emby-checkbox-label';

            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.setAttribute('is', 'emby-checkbox');
            checkbox.setAttribute('data-embycheckbox', 'true');
            checkbox.className = 'emby-checkbox ' + (checkboxClass || '');
            checkbox.value = defaultGetValue(item);
            checkbox.id = containerId + '_' + defaultGetValue(item);

            const checkboxLabel = document.createElement('span');
            checkboxLabel.className = 'checkboxLabel';
            checkboxLabel.textContent = defaultGetLabel(item);

            const checkboxOutline = document.createElement('span');
            checkboxOutline.className = 'checkboxOutline';

            const checkedIcon = document.createElement('span');
            checkedIcon.className = 'material-icons checkboxIcon checkboxIcon-checked check';
            checkedIcon.setAttribute('aria-hidden', 'true');

            const uncheckedIcon = document.createElement('span');
            uncheckedIcon.className = 'material-icons checkboxIcon checkboxIcon-unchecked';
            uncheckedIcon.setAttribute('aria-hidden', 'true');

            checkboxOutline.appendChild(checkedIcon);
            checkboxOutline.appendChild(uncheckedIcon);

            // Order: checkbox, label, outline (matching Jellyfin HTML)
            label.appendChild(checkbox);
            label.appendChild(checkboxLabel);
            label.appendChild(checkboxOutline);
            option.appendChild(label);
            options.appendChild(option);
        });

        // Restore previously selected values after recreating checkboxes
        if (currentlySelected && currentlySelected.length > 0) {
            // Use setTimeout to ensure checkboxes are fully rendered
            setTimeout(function () {
                SmartLists.setSelectedItems(page, containerId, currentlySelected, checkboxClass, undefined);
            }, 0);
        }
    };

    /**
     * Get array of selected item values from a multi-select component
     * @param {HTMLElement} page - The page element
     * @param {string} containerId - ID of the container element
     * @param {string} checkboxClass - CSS class for checkboxes
     * @returns {Array} Array of selected values
     */
    SmartLists.getSelectedItems = function (page, containerId, checkboxClass) {
        const options = SmartLists.getOptionsElement(page, containerId);

        if (!options) {
            console.warn('SmartLists.getSelectedItems: Options container not found for', containerId);
            return [];
        }

        const selector = checkboxClass ? '.' + checkboxClass + ':checked' : 'input[type="checkbox"]:checked';
        const checkboxes = options.querySelectorAll(selector);
        const values = [];
        checkboxes.forEach(function (checkbox) {
            if (checkbox.value) {
                values.push(checkbox.value);
            }
        });
        return values;
    };

    /**
     * Set selected items by value array
     * @param {HTMLElement} page - The page element
     * @param {string} containerId - ID of the container element
     * @param {Array} values - Array of values to select
     * @param {string} checkboxClass - CSS class for checkboxes
     * @param {string} placeholderText - Optional placeholder text when nothing is selected
     */
    SmartLists.setSelectedItems = function (page, containerId, values, checkboxClass, placeholderText) {
        if (!values || !Array.isArray(values)) {
            values = [];
        }

        const options = SmartLists.getOptionsElement(page, containerId);

        if (!options) {
            console.warn('SmartLists.setSelectedItems: Options container not found for', containerId);
            return;
        }

        const selector = checkboxClass ? '.' + checkboxClass : 'input[type="checkbox"]';
        const checkboxes = options.querySelectorAll(selector);
        if (checkboxes.length === 0) {
            console.warn('SmartLists.setSelectedItems: No checkboxes found for', containerId);
            return;
        }

        // Normalize values for comparison (remove dashes, lowercase)
        const normalizedValues = values.map(function (val) {
            return val ? String(val).replace(/-/g, '').toLowerCase() : '';
        });

        checkboxes.forEach(function (checkbox) {
            const checkboxValue = checkbox.value ? String(checkbox.value).replace(/-/g, '').toLowerCase() : '';
            checkbox.checked = normalizedValues.indexOf(checkboxValue) !== -1;
        });

        SmartLists.updateMultiSelectDisplay(page, containerId, placeholderText, checkboxClass);
    };

    /**
     * Update the display text showing selected items
     * @param {HTMLElement} page - The page element
     * @param {string} containerId - ID of the container element
     * @param {string} placeholderText - Placeholder text when nothing is selected
     * @param {string} checkboxClass - CSS class for checkboxes
     */
    SmartLists.updateMultiSelectDisplay = function (page, containerId, placeholderText, checkboxClass) {
        placeholderText = placeholderText || 'Select items...';

        const display = SmartLists.getDisplayElement(page, containerId);

        if (!display) {
            console.warn('SmartLists.updateMultiSelectDisplay: Display element not found for', containerId);
            return;
        }

        const options = SmartLists.getOptionsElement(page, containerId);

        if (!options) return;

        const selector = checkboxClass ? '.' + checkboxClass + ':checked' : 'input[type="checkbox"]:checked';
        const checkedBoxes = options.querySelectorAll(selector);
        const placeholder = display.querySelector('.multi-select-placeholder');

        if (checkedBoxes.length === 0) {
            if (placeholder) {
                placeholder.textContent = placeholderText;
                placeholder.style.display = 'inline';
            }
            // Hide any selected items display
            const selectedItems = display.querySelector('.multi-select-selected-items');
            if (selectedItems) {
                selectedItems.remove();
            }
        } else {
            // Get item labels
            const itemLabels = [];
            checkedBoxes.forEach(function (checkbox) {
                const label = checkbox.closest('label');
                if (label) {
                    const labelText = label.querySelector('.checkboxLabel');
                    if (labelText) {
                        itemLabels.push(labelText.textContent);
                    }
                }
            });

            if (placeholder) {
                placeholder.style.display = 'none';
            }

            // Remove existing selected items display
            const existingSelected = display.querySelector('.multi-select-selected-items');
            if (existingSelected) {
                existingSelected.remove();
            }

            // Create new selected items display
            const selectedItems = document.createElement('span');
            selectedItems.className = 'multi-select-selected-items';

            // Show comma-separated names
            const displayText = itemLabels.length > 0 ? itemLabels.join(', ') : '';
            selectedItems.textContent = displayText;

            // Insert before the arrow (if it exists)
            const arrow = display.querySelector('.multi-select-arrow');
            if (arrow) {
                display.insertBefore(selectedItems, arrow);
            } else {
                display.appendChild(selectedItems);
            }
        }
    };

    /**
     * Cleanup function for a multi-select component
     * @param {HTMLElement} page - The page element
     * @param {string} containerId - ID of the container element
     */
    SmartLists.cleanupMultiSelect = function (page, containerId) {
        const container = page.querySelector('#' + containerId);
        if (!container) return;

        const abortControllerKey = '_multiSelectAbortController_' + containerId;
        if (container[abortControllerKey]) {
            container[abortControllerKey].abort();
            delete container[abortControllerKey];
        }

        const initKey = '_multiSelectInitialized_' + containerId;
        container[initKey] = false;
    };

})(window.SmartLists = window.SmartLists || {});

