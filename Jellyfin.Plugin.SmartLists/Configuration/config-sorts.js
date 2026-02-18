(function(SmartLists) {
    'use strict';
    
    // Initialize namespace if it doesn't exist
    if (!SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }
    
    SmartLists.initializeSortSystem = function(page) {
        const sortsContainer = page.querySelector('#sorts-container');
        if (!sortsContainer) return;
        
        // Clear any existing content
        sortsContainer.innerHTML = '';
        
        // Don't add any sort boxes by default - start empty (will be added with defaults)
        // Just add the "Add Sort" button
        const addBtn = document.createElement('button');
        addBtn.type = 'button';
        addBtn.className = 'emby-button raised add-sort-btn';
        addBtn.textContent = '+ Add Sort';
        addBtn.addEventListener('click', function() {
            SmartLists.addSortBox(page, null);
        });
        sortsContainer.appendChild(addBtn);
    };
    
    SmartLists.createSortField = function(labelText, fieldId, fieldType, options) {
        const container = SmartLists.createStyledElement('div', 'sort-field-container', SmartLists.STYLES.sortField);
        
        const label = SmartLists.createStyledElement('label', '', SmartLists.STYLES.sortFieldLabel);
        label.textContent = labelText;
        label.setAttribute('for', fieldId);
        container.appendChild(label);
        
        let input;
        if (fieldType === 'select') {
            input = document.createElement('select');
            input.setAttribute('is', 'emby-select');
            input.className = 'emby-select-withcolor emby-select';
            // Let Jellyfin theme handle background color
            if (options) {
                SmartLists.populateSelectElement(input, options);
            }
        }
        
        input.id = fieldId;
        container.appendChild(input);
        
        return { container: container, input: input };
    };
    
    SmartLists.createSortSeparator = function() {
        const separator = document.createElement('div');
        separator.className = 'sort-separator';
        separator.style.textAlign = 'center';
        separator.style.margin = '0.75em 0';
        separator.style.opacity = '0.6';
        separator.style.fontSize = '0.9em';
        separator.style.fontWeight = 'bold';
        separator.textContent = 'AND THEN';
        return separator;
    };
    
    // Helper function to create "Ignore Article" checkbox
    SmartLists.createIgnoreArticleCheckbox = function(sortId, checked) {
        const container = document.createElement('div');
        container.className = 'sort-field-container ignore-article-container';
        container.style.minWidth = '200px';
        container.style.alignItems = 'center';
        container.style.flexDirection = 'column';
        
        // Create checkbox with label
        const checkboxLabel = document.createElement('label');
        checkboxLabel.className = 'emby-checkbox-label';
        checkboxLabel.style.display = 'flex';
        checkboxLabel.style.alignItems = 'center';
        checkboxLabel.style.cursor = 'pointer';
        checkboxLabel.style.marginTop = '0.5em';
        
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.id = 'sort-ignore-articles-' + sortId;
        checkbox.className = 'emby-checkbox';
        checkbox.checked = checked || false;
        
        const checkboxText = document.createElement('span');
        checkboxText.className = 'checkboxLabel';
        checkboxText.textContent = 'Ignore Article \'The\'';
        checkboxText.style.fontSize = '0.9em';
        checkboxText.style.paddingLeft = '0.1em';
        
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
        
        checkboxLabel.appendChild(checkbox);
        checkboxLabel.appendChild(checkboxText);
        checkboxLabel.appendChild(checkboxOutline);
        
        container.appendChild(checkboxLabel);
        
        return { container: container, checkbox: checkbox };
    };

    // Helper function to sync Sort Order UI based on Sort By value
    SmartLists.syncSortOrderUI = function(sortByValue, sortOrderContainer, sortOrderSelect) {
        if (!sortOrderContainer || !sortOrderSelect) return;
        
        // Hide Sort Order for Random and NoOrder (they don't use ordering)
        if (sortByValue === 'Random' || sortByValue === 'NoOrder') {
            sortOrderContainer.style.display = 'none';
        } else {
            sortOrderContainer.style.display = '';
            
            // Auto-set to Descending when Similarity is selected (most similar first)
            if (sortByValue === 'Similarity') {
                sortOrderSelect.value = 'Descending';
            }
        }
    };
    
    SmartLists.shouldShowSortOption = function(sortValue, selectedMediaTypes, hasSimilarToRule, hasExternalListRule) {
        // If no media types selected, show all options
        if (!selectedMediaTypes || selectedMediaTypes.length === 0) {
            return true;
        }

        const hasEpisode = selectedMediaTypes.indexOf('Episode') !== -1;
        const hasMovie = selectedMediaTypes.indexOf('Movie') !== -1;
        const hasSeries = selectedMediaTypes.indexOf('Series') !== -1;
        const hasAudio = selectedMediaTypes.indexOf('Audio') !== -1;
        const hasAudioBook = selectedMediaTypes.indexOf('AudioBook') !== -1;
        const hasMusicVideo = selectedMediaTypes.indexOf('MusicVideo') !== -1;
        const hasVideo = selectedMediaTypes.indexOf('Video') !== -1;

        // Episode-only sort options
        if (sortValue === 'SeasonNumber' || sortValue === 'EpisodeNumber' || sortValue === 'SeriesName') {
            return hasEpisode;
        }

        // Series-only sort options
        if (sortValue === 'LastEpisodeAirDate') {
            return hasSeries;
        }

        // Audio/MusicVideo/AudioBook sort options
        if (sortValue === 'TrackNumber') {
            return hasAudio || hasMusicVideo || hasAudioBook;
        }

        // Audio/MusicVideo sort options
        if (sortValue === 'AlbumName' || sortValue === 'Artist') {
            return hasAudio || hasMusicVideo;
        }

        // Video-capable sort options (Resolution)
        if (sortValue === 'Resolution') {
            return hasMovie || hasEpisode || hasMusicVideo || hasVideo;
        }

        // Runtime - shown for Movie, Episode, Audio, Music Video, Home Video (Video), Audiobook
        if (sortValue === 'Runtime') {
            return hasMovie || hasEpisode || hasAudio || hasMusicVideo || hasVideo || hasAudioBook;
        }

        // Similarity - only show if there's a "Similar To" rule
        if (sortValue === 'Similarity') {
            return hasSimilarToRule === true;
        }

        // External List Order - only show if there's an "ExternalList" rule
        if (sortValue === 'External List Order') {
            return hasExternalListRule === true;
        }

        // Always show: Name, ProductionYear, CommunityRating,
        // DateCreated, ReleaseDate, PlayCount (owner), LastPlayed (owner), Random, NoOrder
        return true;
    };

    // Filter sort options based on current context
    SmartLists.getFilteredSortOptions = function(page) {
        const selectedMediaTypes = SmartLists.getSelectedMediaTypes(page);
        const hasSimilarTo = SmartLists.hasSimilarToRuleInForm(page);
        const hasExternalList = SmartLists.hasExternalListRuleInForm(page);

        return SmartLists.SORT_OPTIONS.filter(function(opt) {
            return SmartLists.shouldShowSortOption(opt.value, selectedMediaTypes, hasSimilarTo, hasExternalList);
        });
    };
    
    SmartLists.createSortBox = function(page, sortData) {
        const sortId = 'sort-' + Date.now() + '-' + Math.random();

        // Parse sortData to handle "Name (Ignore Articles)" and "SeriesName (Ignore Articles)" backwards compatibility
        let actualSortBy = sortData ? sortData.SortBy : 'Name';
        let ignoreArticles = false;

        if (actualSortBy === 'Name (Ignore Articles)') {
            actualSortBy = 'Name';
            ignoreArticles = true;
        } else if (actualSortBy === 'SeriesName (Ignore Articles)') {
            actualSortBy = 'SeriesName';
            ignoreArticles = true;
        }

        // Create box container with paperList class for theme-aware background
        const box = SmartLists.createStyledElement('div', 'sort-box paperList', SmartLists.STYLES.sortBox);
        box.setAttribute('data-sort-id', sortId);

        // Create fields container
        const fieldsContainer = SmartLists.createStyledElement('div', 'sort-fields', SmartLists.STYLES.sortFields);

        // Sort By field
        const sortByField = SmartLists.createSortField('Sort By', 'sort-by-' + sortId, 'select');
        // Set a fixed width to prevent resizing when options change
        sortByField.container.style.minWidth = '280px';
        sortByField.container.style.maxWidth = '280px';
        // Get filtered options based on current context
        const filteredOptions = SmartLists.getFilteredSortOptions(page);
        // Mark the selected option (default to 'Name' if no sortData provided)
        const sortByOptions = filteredOptions.map(function(opt) {
            return {
                value: opt.value,
                label: opt.label,
                selected: opt.value === actualSortBy
            };
        });
        SmartLists.populateSelectElement(sortByField.input, sortByOptions);
        fieldsContainer.appendChild(sortByField.container);

        // Sort Order field
        const sortOrderField = SmartLists.createSortField('Sort Order', 'sort-order-' + sortId, 'select');
        const sortOrderOptions = SmartLists.SORT_ORDER_OPTIONS.map(function(opt) {
            return {
                value: opt.value,
                label: opt.label,
                selected: (sortData ? opt.value === sortData.SortOrder : opt.value === 'Ascending')
            };
        });
        SmartLists.populateSelectElement(sortOrderField.input, sortOrderOptions);
        fieldsContainer.appendChild(sortOrderField.container);

        // Ignore Articles checkbox (visible for Name and SeriesName)
        const ignoreArticlesField = SmartLists.createIgnoreArticleCheckbox(sortId, ignoreArticles);
        const shouldShowIgnoreArticles = (actualSortBy === 'Name' || actualSortBy === 'SeriesName');
        ignoreArticlesField.container.style.display = shouldShowIgnoreArticles ? '' : 'none';
        fieldsContainer.appendChild(ignoreArticlesField.container);

        box.appendChild(fieldsContainer);

        // Remove button (positioned absolutely within box)
        const removeBtn = SmartLists.createStyledElement('button', 'sort-remove-btn', SmartLists.STYLES.sortRemoveBtn);
        removeBtn.type = 'button';
        removeBtn.textContent = '\u00D7'; // × symbol
        removeBtn.title = 'Remove this sort';
        removeBtn.addEventListener('click', function() {
            SmartLists.removeSortBox(page, box);
        });
        box.appendChild(removeBtn);

        // Add event listener to sync Sort Order UI and checkbox visibility when Sort By changes
        sortByField.input.addEventListener('change', function() {
            SmartLists.syncSortOrderUI(this.value, sortOrderField.container, sortOrderField.input);
            // Show/hide ignore articles checkbox based on Sort By value
            const showIgnoreArticles = (this.value === 'Name' || this.value === 'SeriesName');
            ignoreArticlesField.container.style.display = showIgnoreArticles ? '' : 'none';
            // Reset checkbox when switching away from Name/SeriesName
            if (!showIgnoreArticles) {
                ignoreArticlesField.checkbox.checked = false;
            }
        });

        // Initialize Sort Order UI based on current Sort By value
        SmartLists.syncSortOrderUI(actualSortBy, sortOrderField.container, sortOrderField.input);

        return box;
    };
    
    SmartLists.addSortBox = function(page, sortData) {
        const sortsContainer = page.querySelector('#sorts-container');
        if (!sortsContainer) return;
        
        // Check if we already have 3 sort boxes (max limit)
        const existingBoxes = sortsContainer.querySelectorAll('.sort-box');
        if (existingBoxes.length >= 3) {
            SmartLists.showNotification('You can add a maximum of 3 sorting options.', 'warning');
            return;
        }
        
        // Add "AND THEN" separator before new box (if not first box)
        if (existingBoxes.length > 0) {
            const separator = SmartLists.createSortSeparator();
            
            // Find the add button and insert separator before it
            const addBtn = sortsContainer.querySelector('.add-sort-btn');
            if (addBtn) {
                sortsContainer.insertBefore(separator, addBtn);
            }
        }
        
        // Find the add button (it's always the last child)
        const addBtn = sortsContainer.querySelector('.add-sort-btn');
        
        // Create and insert the new box before the add button
        const newBox = SmartLists.createSortBox(page, sortData);
        
        // Hide remove button for the first sort box
        if (existingBoxes.length === 0) {
            const removeBtn = newBox.querySelector('.sort-remove-btn');
            if (removeBtn) {
                removeBtn.style.display = 'none';
            }
        }
        
        if (addBtn) {
            sortsContainer.insertBefore(newBox, addBtn);
            // Update button text
            if (existingBoxes.length === 0) {
                addBtn.textContent = '+ Add Another Sort';
            }
            // Hide button if we've reached max (3 boxes)
            if (existingBoxes.length >= 2) {
                addBtn.style.display = 'none';
            }
        } else {
            sortsContainer.appendChild(newBox);
        }
    };
    
    SmartLists.removeSortBox = function(page, box) {
        const sortsContainer = page.querySelector('#sorts-container');
        if (!sortsContainer) return;
        
        const boxes = sortsContainer.querySelectorAll('.sort-box');
        
        // Don't allow removing the last/only sort box
        if (boxes.length <= 1) {
            SmartLists.showNotification('You must have at least one sort option.', 'warning');
            return;
        }
        
        // Find and remove the separator before this box (if it exists)
        let prevSibling = box.previousElementSibling;
        if (prevSibling && prevSibling.classList.contains('sort-separator')) {
            prevSibling.remove();
        }
        
        box.remove();
        
        // Update button state
        const remainingBoxes = sortsContainer.querySelectorAll('.sort-box');
        const addBtn = sortsContainer.querySelector('.add-sort-btn');
        if (addBtn) {
            if (remainingBoxes.length === 0) {
                addBtn.textContent = '+ Add Sort';
            } else {
                addBtn.textContent = '+ Add Another Sort';
            }
            // Show button if we're below max
            if (remainingBoxes.length < 3) {
                addBtn.style.display = '';
            }
        }
    };
    
    SmartLists.collectSortsFromForm = function(page) {
        const sortsContainer = page.querySelector('#sorts-container');
        if (!sortsContainer) return [];

        const boxes = sortsContainer.querySelectorAll('.sort-box');
        const sorts = [];

        boxes.forEach(function(box) {
            const sortBySelect = box.querySelector('[id^="sort-by-"]');
            const sortOrderSelect = box.querySelector('[id^="sort-order-"]');
            const ignoreArticlesCheckbox = box.querySelector('[id^="sort-ignore-articles-"]');

            if (!sortBySelect || !sortBySelect.value) return; // Skip if no sort by selected

            let sortBy = sortBySelect.value;
            const sortOrder = (sortBy === 'Random' || sortBy === 'NoOrder') ? 'Ascending' : (sortOrderSelect ? sortOrderSelect.value : 'Ascending');

            // Handle "Ignore Articles" checkbox - convert to "(Ignore Articles)" for backwards compatibility
            if ((sortBy === 'Name' || sortBy === 'SeriesName') && ignoreArticlesCheckbox && ignoreArticlesCheckbox.checked) {
                sortBy = sortBy + ' (Ignore Articles)';
            }

            sorts.push({
                SortBy: sortBy,
                SortOrder: sortOrder
            });
        });
        
        return sorts;
    };
    
    // Update all sort dropdowns based on current context (media types and rules)
    SmartLists.updateAllSortOptionsVisibility = function(page) {
        const sortsContainer = page.querySelector('#sorts-container');
        if (!sortsContainer) return;

        const sortBoxes = sortsContainer.querySelectorAll('.sort-box');
        const filteredOptions = SmartLists.getFilteredSortOptions(page);

        sortBoxes.forEach(function(box) {
            const sortBySelect = box.querySelector('select[id^="sort-by-"]');
            const sortOrderSelect = box.querySelector('select[id^="sort-order-"]');
            const sortOrderContainer = sortOrderSelect ? sortOrderSelect.closest('.sort-field-container') : null;

            if (!sortBySelect) return;

            const currentValue = sortBySelect.value;

            // Rebuild the options
            const sortByOptions = filteredOptions.map(function(opt) {
                return {
                    value: opt.value,
                    label: opt.label,
                    selected: opt.value === currentValue
                };
            });

            // Check if current value is still valid
            const isCurrentValueValid = filteredOptions.some(function(opt) {
                return opt.value === currentValue;
            });

            // Repopulate the dropdown
            SmartLists.populateSelectElement(sortBySelect, sortByOptions);

            // Determine the effective sort value (new or current)
            var effectiveSortValue = currentValue;
            if (!isCurrentValueValid && currentValue && sortByOptions.length > 0) {
                sortBySelect.value = sortByOptions[0].value;
                effectiveSortValue = sortByOptions[0].value;
            }

            // Sync Sort Order UI for the effective value
            SmartLists.syncSortOrderUI(effectiveSortValue, sortOrderContainer, sortOrderSelect);

            // Update ignore article checkbox visibility
            const ignoreArticlesContainer = box.querySelector('.ignore-article-container');
            if (ignoreArticlesContainer) {
                const showIgnoreArticles = (effectiveSortValue === 'Name' || effectiveSortValue === 'SeriesName');
                ignoreArticlesContainer.style.display = showIgnoreArticles ? '' : 'none';
                if (!showIgnoreArticles) {
                    const ignoreArticlesCheckbox = ignoreArticlesContainer.querySelector('[id^="sort-ignore-articles-"]');
                    if (ignoreArticlesCheckbox) {
                        ignoreArticlesCheckbox.checked = false;
                    }
                }
            }
        });
    };
    
    SmartLists.parseSortOptions = function(playlist) {
        if (!playlist.Order) {
            return [{ SortBy: 'Name', SortOrder: 'Ascending' }];
        }
        
        // New format: SortOptions array
        if (playlist.Order.SortOptions && playlist.Order.SortOptions.length > 0) {
            return playlist.Order.SortOptions;
        }
        
        // Legacy format: Order.Name string
        if (playlist.Order.Name) {
            const orderName = playlist.Order.Name;
            let sortBy, sortOrder;
            
            if (orderName === 'Random' || orderName === 'NoOrder' || orderName === 'No Order') {
                // Special handling for Random/NoOrder - no Asc/Desc
                sortBy = (orderName === 'No Order') ? 'NoOrder' : orderName;
                sortOrder = 'Ascending'; // Default sort order (though it won't be used)
            } else {
                // Normal parsing for other orders like "Name Ascending" or "Similarity Descending"
                const parts = orderName.split(' ');
                sortBy = parts.slice(0, -1).join(' ') || 'Name';
                sortOrder = parts[parts.length - 1] || 'Ascending';
            }
            
            return [{ SortBy: sortBy, SortOrder: sortOrder }];
        }
        
        // Fallback
        return [{ SortBy: 'Name', SortOrder: 'Ascending' }];
    };
    
    // Helper function to load sort options into the UI
    SmartLists.loadSortOptionsIntoUI = function(page, playlist) {
        const sortsContainer = page.querySelector('#sorts-container');
        if (!sortsContainer) return;
        
        // Clear existing sort boxes and separators
        const existingBoxes = sortsContainer.querySelectorAll('.sort-box');
        existingBoxes.forEach(function(box) {
            box.remove();
        });
        const existingSeparators = sortsContainer.querySelectorAll('.sort-separator');
        existingSeparators.forEach(function(sep) {
            sep.remove();
        });
        
        // Parse sort options from playlist
        const sortOptions = SmartLists.parseSortOptions(playlist);
        
        // Add sort boxes for each sort option WITHOUT adding separators
        sortOptions.forEach(function(sortOption, index) {
            const sortBox = SmartLists.createSortBox(page, sortOption);
            
            // Hide remove button for the first sort box
            if (index === 0) {
                const removeBtn = sortBox.querySelector('.sort-remove-btn');
                if (removeBtn) {
                    removeBtn.style.display = 'none';
                }
            }
            
            // Insert before add button
            const addBtn = sortsContainer.querySelector('.add-sort-btn');
            if (addBtn) {
                sortsContainer.insertBefore(sortBox, addBtn);
            } else {
                sortsContainer.appendChild(sortBox);
            }
        });
        
        // Now add separators between boxes
        let boxes = sortsContainer.querySelectorAll('.sort-box');
        boxes.forEach(function(box, index) {
            if (index > 0) { // Skip first box
                const separator = SmartLists.createSortSeparator();
                
                // Insert separator before this box
                box.parentNode.insertBefore(separator, box);
            }
        });
        
        // Re-add the "Add Sort" button if it doesn't exist
        let addBtn = sortsContainer.querySelector('.add-sort-btn');
        if (!addBtn) {
            addBtn = document.createElement('button');
            addBtn.type = 'button';
            addBtn.className = 'emby-button raised add-sort-btn';
            addBtn.addEventListener('click', function() {
                SmartLists.addSortBox(page, null);
            });
            sortsContainer.appendChild(addBtn);
        }
        
        // Update button text and visibility
        boxes = sortsContainer.querySelectorAll('.sort-box');
        if (boxes.length === 0) {
            addBtn.textContent = '+ Add Sort';
            addBtn.style.display = '';
        } else if (boxes.length < 3) {
            addBtn.textContent = '+ Add Another Sort';
            addBtn.style.display = '';
        } else {
            addBtn.textContent = '+ Add Another Sort';
            addBtn.style.display = 'none';
        }
    };
    
    SmartLists.sortPlaylists = function(playlists, sortBy) {
        if (!sortBy || !playlists) return playlists || [];
        
        // Ensure playlists is an array
        if (!Array.isArray(playlists)) {
            console.error('sortPlaylists: playlists is not an array:', typeof playlists, playlists);
            return [];
        }
        
        if (playlists.length === 0) return playlists;
        
        // Create a copy to avoid mutating original (ES5 compatible)
        const sortedPlaylists = playlists.slice();
        
        if (sortBy === 'name-asc') {
            return sortedPlaylists.sort(function(a, b) {
                const nameA = (a.Name || '').toLowerCase();
                const nameB = (b.Name || '').toLowerCase();
                return nameA.localeCompare(nameB);
            });
        } else if (sortBy === 'name-desc') {
            return sortedPlaylists.sort(function(a, b) {
                const nameA = (a.Name || '').toLowerCase();
                const nameB = (b.Name || '').toLowerCase();
                return nameB.localeCompare(nameA);
            });
        } else if (sortBy === 'created-desc') {
            return sortedPlaylists.sort(function(a, b) {
                const dateA = a.DateCreated ? new Date(a.DateCreated) : new Date(0);
                const dateB = b.DateCreated ? new Date(b.DateCreated) : new Date(0);
                return dateB - dateA;
            });
        } else if (sortBy === 'created-asc') {
            return sortedPlaylists.sort(function(a, b) {
                const dateA = a.DateCreated ? new Date(a.DateCreated) : new Date(0);
                const dateB = b.DateCreated ? new Date(b.DateCreated) : new Date(0);
                return dateA - dateB;
            });
        } else if (sortBy === 'refreshed-desc') {
            return sortedPlaylists.sort(function(a, b) {
                const dateA = a.LastRefreshed ? new Date(a.LastRefreshed) : new Date(0);
                const dateB = b.LastRefreshed ? new Date(b.LastRefreshed) : new Date(0);
                return dateB - dateA;
            });
        } else if (sortBy === 'refreshed-asc') {
            return sortedPlaylists.sort(function(a, b) {
                const dateA = a.LastRefreshed ? new Date(a.LastRefreshed) : new Date(0);
                const dateB = b.LastRefreshed ? new Date(b.LastRefreshed) : new Date(0);
                return dateA - dateB;
            });
        } else if (sortBy === 'enabled-first') {
            return sortedPlaylists.sort(function(a, b) {
                const enabledA = a.Enabled !== false ? 1 : 0;
                const enabledB = b.Enabled !== false ? 1 : 0;
                if (enabledA !== enabledB) return enabledB - enabledA;
                // Secondary sort by name
                return (a.Name || '').toLowerCase().localeCompare((b.Name || '').toLowerCase());
            });
        } else if (sortBy === 'disabled-first') {
            return sortedPlaylists.sort(function(a, b) {
                const enabledA = a.Enabled !== false ? 1 : 0;
                const enabledB = b.Enabled !== false ? 1 : 0;
                if (enabledA !== enabledB) return enabledA - enabledB;
                // Secondary sort by name
                return (a.Name || '').toLowerCase().localeCompare((b.Name || '').toLowerCase());
            });
        }
        
        return sortedPlaylists;
    };
    
    SmartLists.applyAllFiltersAndSort = function(page, playlists) {
        if (!playlists) return [];
        
        // Ensure playlists is an array
        if (!Array.isArray(playlists)) {
            console.error('applyAllFiltersAndSort: playlists is not an array:', typeof playlists, playlists);
            return [];
        }
        
        // Create a copy (ES5 compatible)
        let filteredPlaylists = playlists.slice();
        
        // Apply all filters using the generic system
        const filterOrder = ['search', 'type', 'mediaType', 'user'];
        
        for (var i = 0; i < filterOrder.length; i++) {
            const filterKey = filterOrder[i];
            const filterValue = SmartLists.getFilterValue(page, filterKey);
            filteredPlaylists = SmartLists.applyFilter(filteredPlaylists, filterKey, filterValue, page);
        }
        
        // Apply sorting
        const sortValue = SmartLists.getFilterValue(page, 'sort') || 'name-asc';
        filteredPlaylists = SmartLists.sortPlaylists(filteredPlaylists, sortValue);
        
        return filteredPlaylists;
    };
    
})(window.SmartLists = window.SmartLists || {});

