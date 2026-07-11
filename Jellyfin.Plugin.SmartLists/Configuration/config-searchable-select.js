(function (SmartLists) {
    'use strict';

    if (!window.SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }

    // ===== SEARCHABLE SELECT COMPONENT =====
    // Wraps a native <select> with a searchable dropdown overlay.
    // The native select stays in the DOM (hidden) so .value and change events work unchanged.

    /**
     * Enhance a native <select> element with a searchable dropdown.
     * Call this after the select has been populated with options/optgroups.
     * @param {HTMLSelectElement} selectElement - The native select to enhance
     * @param {Object} [options] - Configuration options
     * @param {AbortSignal} [options.signal] - AbortController signal for cleanup
     * @param {string} [options.searchPlaceholder] - Placeholder for the search input (default 'Search fields...')
     * @param {string} [options.placeholder] - Display text when nothing is selected (default '-- Field --')
     * @param {string} [options.noResultsText] - Message when no options match the search (default 'No matching fields')
     */
    SmartLists.initSearchableSelect = function (selectElement, options) {
        if (!selectElement) return;

        // Prevent double-initialization
        if (selectElement._searchableSelectWrapper) {
            SmartLists.refreshSearchableSelect(selectElement);
            return;
        }

        options = options || {};
        // Captured in the closures below (buildOptions/updateDisplayText/filterOptions),
        // so they survive refreshSearchableSelect and repeated init calls.
        const searchPlaceholder = options.searchPlaceholder || 'Search fields...';
        const placeholder = options.placeholder || '-- Field --';
        const noResultsText = options.noResultsText || 'No matching fields';
        const wrapper = document.createElement('div');
        wrapper.className = 'searchable-select-container';

        // Copy flex style from the original select to the wrapper
        const originalStyle = selectElement.style.cssText;
        if (originalStyle) {
            wrapper.style.cssText = originalStyle;
        }

        // Insert wrapper before select in DOM, then move select inside
        selectElement.parentNode.insertBefore(wrapper, selectElement);
        wrapper.appendChild(selectElement);

        // Hide the native select
        selectElement.style.cssText = 'position:absolute;opacity:0;pointer-events:none;width:0;height:0;overflow:hidden;';

        // Create display element
        const display = document.createElement('div');
        display.className = 'searchable-select-display emby-select-withcolor';
        display.tabIndex = 0;

        const displayText = document.createElement('span');
        displayText.className = 'searchable-select-display-text';
        display.appendChild(displayText);

        const arrow = document.createElement('span');
        arrow.className = 'searchable-select-arrow';
        arrow.innerHTML = '&#9660;';
        display.appendChild(arrow);

        wrapper.appendChild(display);

        // Create dropdown
        const dropdown = document.createElement('div');
        dropdown.className = 'searchable-select-dropdown';
        dropdown.style.display = 'none';

        // Search input
        const searchWrap = document.createElement('div');
        searchWrap.className = 'searchable-select-search-wrap';

        const searchInput = document.createElement('input');
        searchInput.type = 'text';
        searchInput.className = 'searchable-select-search emby-input';
        searchInput.placeholder = searchPlaceholder;
        searchInput.autocomplete = 'off';
        searchWrap.appendChild(searchInput);

        dropdown.appendChild(searchWrap);

        // Options container
        const optionsContainer = document.createElement('div');
        optionsContainer.className = 'searchable-select-options';
        dropdown.appendChild(optionsContainer);

        wrapper.appendChild(dropdown);

        // State
        let isOpen = false;
        let highlightedIndex = -1;
        let flatOptions = []; // Array of { element, value, label, groupLabel }

        // Store references
        selectElement._searchableSelectWrapper = wrapper;
        selectElement._searchableSelectDisplay = display;
        selectElement._searchableSelectDisplayText = displayText;
        selectElement._searchableSelectDropdown = dropdown;
        selectElement._searchableSelectSearch = searchInput;
        selectElement._searchableSelectOptions = optionsContainer;

        // Build options from the native select
        function buildOptions() {
            optionsContainer.innerHTML = '';
            flatOptions = [];
            highlightedIndex = -1;

            const children = selectElement.children;
            for (let i = 0; i < children.length; i++) {
                const child = children[i];

                if (child.tagName === 'OPTGROUP') {
                    const groupDiv = document.createElement('div');
                    groupDiv.className = 'searchable-select-group';
                    groupDiv.setAttribute('data-group-label', child.label || '');

                    const groupLabel = document.createElement('div');
                    groupLabel.className = 'searchable-select-group-label';
                    groupLabel.textContent = child.label || '';
                    groupDiv.appendChild(groupLabel);

                    const groupOptions = child.children;
                    for (let j = 0; j < groupOptions.length; j++) {
                        const opt = groupOptions[j];
                        const optDiv = createOptionElement(opt.value, opt.textContent, child.label);
                        groupDiv.appendChild(optDiv);
                    }

                    optionsContainer.appendChild(groupDiv);
                } else if (child.tagName === 'OPTION') {
                    if (child.value === '') {
                        continue;
                    }
                    const optDiv = createOptionElement(child.value, child.textContent, '');
                    optionsContainer.appendChild(optDiv);
                }
            }

            updateDisplayText();
        }

        function createOptionElement(value, label, groupLabel) {
            const optDiv = document.createElement('div');
            optDiv.className = 'searchable-select-option';
            optDiv.setAttribute('data-value', value);
            optDiv.textContent = label;

            const idx = flatOptions.length;
            flatOptions.push({ element: optDiv, value: value, label: label, groupLabel: groupLabel || '' });

            optDiv.addEventListener('mouseenter', function () {
                setHighlight(idx);
            });

            optDiv.addEventListener('click', function (e) {
                e.stopPropagation();
                selectOption(value);
            });

            return optDiv;
        }

        function updateDisplayText() {
            const selected = selectElement.value;
            if (!selected) {
                displayText.textContent = placeholder;
                displayText.classList.add('searchable-select-placeholder');
            } else {
                const selectedOpt = selectElement.querySelector('option[value="' + CSS.escape(selected) + '"]');
                displayText.textContent = selectedOpt ? selectedOpt.textContent : selected;
                displayText.classList.remove('searchable-select-placeholder');
            }

            // Mark selected option in dropdown
            flatOptions.forEach(function (opt) {
                if (opt.value === selected) {
                    opt.element.classList.add('selected');
                } else {
                    opt.element.classList.remove('selected');
                }
            });
        }

        function openDropdown() {
            if (isOpen) return;
            isOpen = true;
            dropdown.style.display = 'block';
            searchInput.value = '';
            filterOptions('');
            searchInput.focus();

            // Pre-highlight the currently selected option
            const currentValue = selectElement.value;
            if (currentValue) {
                for (let i = 0; i < flatOptions.length; i++) {
                    if (flatOptions[i].value === currentValue && flatOptions[i].element.style.display !== 'none') {
                        setHighlight(i);
                        flatOptions[i].element.scrollIntoView({ block: 'nearest' });
                        break;
                    }
                }
            }
        }

        function closeDropdown() {
            if (!isOpen) return;
            isOpen = false;
            dropdown.style.display = 'none';
            highlightedIndex = -1;
            clearHighlight();
        }

        function selectOption(value) {
            selectElement.value = value;
            updateDisplayText();
            closeDropdown();
            display.focus();

            // Dispatch change event on the native select so all existing handlers fire
            const event = new Event('change', { bubbles: true });
            selectElement.dispatchEvent(event);
        }

        function setHighlight(index) {
            clearHighlight();
            if (index >= 0 && index < flatOptions.length) {
                highlightedIndex = index;
                flatOptions[index].element.classList.add('highlighted');
            }
        }

        function clearHighlight() {
            flatOptions.forEach(function (opt) {
                opt.element.classList.remove('highlighted');
            });
        }

        function filterOptions(query) {
            const lowerQuery = query.toLowerCase().trim();
            highlightedIndex = -1;
            clearHighlight();
            let firstVisibleIndex = -1;

            // Track which groups have visible options
            const groupVisibility = {};

            flatOptions.forEach(function (opt, index) {
                const matchesLabel = opt.label.toLowerCase().indexOf(lowerQuery) !== -1;
                const matchesGroup = opt.groupLabel.toLowerCase().indexOf(lowerQuery) !== -1;
                const visible = !lowerQuery || matchesLabel || matchesGroup;

                opt.element.style.display = visible ? '' : 'none';

                if (visible && firstVisibleIndex === -1) {
                    firstVisibleIndex = index;
                }

                if (opt.groupLabel) {
                    if (!groupVisibility[opt.groupLabel]) {
                        groupVisibility[opt.groupLabel] = false;
                    }
                    if (visible) {
                        groupVisibility[opt.groupLabel] = true;
                    }
                }
            });

            // Show/hide group labels based on whether they have visible options
            const groupDivs = optionsContainer.querySelectorAll('.searchable-select-group');
            for (let i = 0; i < groupDivs.length; i++) {
                const groupName = groupDivs[i].getAttribute('data-group-label') || '';
                const hasVisible = groupVisibility[groupName] === true;
                groupDivs[i].style.display = hasVisible ? '' : 'none';
            }

            // Show "no results" message if nothing matches
            let noResults = optionsContainer.querySelector('.searchable-select-no-results');
            if (firstVisibleIndex === -1 && lowerQuery) {
                if (!noResults) {
                    noResults = document.createElement('div');
                    noResults.className = 'searchable-select-no-results';
                    noResults.textContent = noResultsText;
                    optionsContainer.appendChild(noResults);
                }
                noResults.style.display = '';
            } else if (noResults) {
                noResults.style.display = 'none';
            }

            if (firstVisibleIndex !== -1) {
                setHighlight(firstVisibleIndex);
            }
        }

        function getNextVisibleIndex(startIndex, direction) {
            let i = startIndex;
            while (true) {
                i += direction;
                if (i < 0 || i >= flatOptions.length) return -1;
                if (flatOptions[i].element.style.display !== 'none') return i;
            }
        }

        // ===== EVENT LISTENERS =====

        const listenerOptions = options.signal ? { signal: options.signal } : {};

        // Toggle on display click
        display.addEventListener('click', function (e) {
            e.stopPropagation();
            if (isOpen) {
                closeDropdown();
            } else {
                openDropdown();
            }
        }, listenerOptions);

        // Keyboard on display (Enter/Space to open, typing to search)
        display.addEventListener('keydown', function (e) {
            if (!isOpen && (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown')) {
                e.preventDefault();
                openDropdown();
            }
        }, listenerOptions);

        // Search input events
        searchInput.addEventListener('input', function () {
            filterOptions(searchInput.value);
        }, listenerOptions);

        searchInput.addEventListener('keydown', function (e) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                const next = getNextVisibleIndex(highlightedIndex, 1);
                if (next !== -1) {
                    setHighlight(next);
                    flatOptions[next].element.scrollIntoView({ block: 'nearest' });
                }
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                const prev = getNextVisibleIndex(highlightedIndex, -1);
                if (prev !== -1) {
                    setHighlight(prev);
                    flatOptions[prev].element.scrollIntoView({ block: 'nearest' });
                }
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (highlightedIndex >= 0 && highlightedIndex < flatOptions.length) {
                    selectOption(flatOptions[highlightedIndex].value);
                }
            } else if (e.key === 'Escape') {
                e.preventDefault();
                closeDropdown();
                display.focus();
            }
        }, listenerOptions);

        // Prevent dropdown clicks from closing
        dropdown.addEventListener('click', function (e) {
            e.stopPropagation();
        }, listenerOptions);

        // Close on outside click
        document.addEventListener('click', function (e) {
            if (isOpen && !wrapper.contains(e.target)) {
                closeDropdown();
            }
        }, listenerOptions);

        // Keep display text in sync when the native select changes outside the
        // overlay (e.g. a label click focuses the hidden select and arrow keys
        // change its value)
        selectElement.addEventListener('change', function () {
            updateDisplayText();
        }, listenerOptions);

        // Build initial options
        buildOptions();

        // Expose rebuild function
        selectElement._searchableSelectBuild = buildOptions;
    };

    /**
     * Refresh the searchable dropdown options after the native select has been repopulated.
     * @param {HTMLSelectElement} selectElement - The native select element
     */
    SmartLists.refreshSearchableSelect = function (selectElement) {
        if (!selectElement || !selectElement._searchableSelectBuild) return;
        selectElement._searchableSelectBuild();
    };

    /**
     * Destroy the searchable select wrapper and restore the native select.
     * @param {HTMLSelectElement} selectElement - The native select element
     */
    SmartLists.destroySearchableSelect = function (selectElement) {
        if (!selectElement || !selectElement._searchableSelectWrapper) return;

        const wrapper = selectElement._searchableSelectWrapper;
        const parent = wrapper.parentNode;

        // Restore original select styles
        selectElement.style.cssText = 'flex: 0 0 25%;';

        // Move select back out of wrapper
        parent.insertBefore(selectElement, wrapper);
        wrapper.remove();

        // Clean up references
        delete selectElement._searchableSelectWrapper;
        delete selectElement._searchableSelectDisplay;
        delete selectElement._searchableSelectDisplayText;
        delete selectElement._searchableSelectDropdown;
        delete selectElement._searchableSelectSearch;
        delete selectElement._searchableSelectOptions;
        delete selectElement._searchableSelectBuild;
    };

})(window.SmartLists || {});
