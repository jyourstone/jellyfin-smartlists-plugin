(function (SmartLists) {
    'use strict';

    // Constants
    SmartLists.PLUGIN_ID = "A0A2A7B2-747A-4113-8B39-757A9D267C79";
    
    // Determine if we're in user mode (set by user-playlists.html before loading this script).
    // This value is refreshed for each page in setPageContext() to avoid SPA navigation leaks.
    SmartLists.IS_USER_PAGE = SmartLists.IS_USER_PAGE || false;

    SmartLists.isUserPage = function (page) {
        if (page && page.classList) {
            return page.classList.contains('SmartListsUserPage');
        }

        return !!SmartLists.IS_USER_PAGE;
    };

    SmartLists.buildEndpoints = function (isUserPage) {
        var endpointPrefix = isUserPage ? 'Plugins/SmartLists/User' : 'Plugins/SmartLists';
        return {
            fields: endpointPrefix + '/fields',
            base: endpointPrefix,
            users: 'Plugins/SmartLists/users',
            libraries: 'Plugins/SmartLists/libraries',
            refresh: endpointPrefix + '/refresh',
            refreshDirect: endpointPrefix + '/refresh-direct',
            backups: 'Plugins/SmartLists/backups',
            backupUpload: 'Plugins/SmartLists/backups/upload',
            backupPreview: 'Plugins/SmartLists/backups/preview'
        };
    };

    SmartLists.setPageContext = function (page) {
        SmartLists.IS_USER_PAGE = SmartLists.isUserPage(page);
        SmartLists.ENDPOINTS = SmartLists.buildEndpoints(SmartLists.IS_USER_PAGE);
    };

    SmartLists.setPageContext(document.querySelector('.SmartListsConfigurationPage'));

    // Field type constants to avoid duplication
    SmartLists.FIELD_TYPES = {
        LIST_FIELDS: ['Collections', 'People', 'Actors', 'Directors', 'Writers', 'Producers', 'GuestStars', 'Genres', 'Studios', 'Tags', 'Artists', 'AlbumArtists', 'AudioLanguages', 'SubtitleLanguages', 'ProductionLocations'],
        NUMERIC_FIELDS: ['ProductionYear', 'CommunityRating', 'CriticRating', 'RuntimeMinutes', 'PlayCount', 'Framerate', 'AudioBitrate', 'AudioSampleRate', 'AudioBitDepth', 'AudioChannels'],
        DATE_FIELDS: ['DateCreated', 'DateLastRefreshed', 'DateLastSaved', 'DateModified', 'ReleaseDate', 'LastPlayedDate', 'LastEpisodeAirDate'],
        BOOLEAN_FIELDS: ['IsFavorite', 'NextUnwatched'],
        SIMPLE_FIELDS: ['ItemType', 'SeriesStatus'],
        RESOLUTION_FIELDS: ['Resolution'],
        STRING_FIELDS: ['SimilarTo', 'Name', 'Album', 'SeriesName', 'OfficialRating', 'Overview', 'FileName', 'FolderPath', 'AudioCodec', 'AudioProfile', 'VideoCodec', 'VideoProfile', 'VideoRange', 'VideoRangeType', 'PlaybackStatus', 'CustomRating', 'ImdbId', 'TmdbId', 'TvdbId'],
        USER_DATA_FIELDS: ['PlaybackStatus', 'IsFavorite', 'PlayCount', 'NextUnwatched', 'LastPlayedDate']
    };

    // Media type capabilities - which types support audio/video streams
    SmartLists.AUDIO_CAPABLE_TYPES = ['Movie', 'Episode', 'Audio', 'AudioBook', 'MusicVideo', 'Video'];
    SmartLists.VIDEO_CAPABLE_TYPES = ['Movie', 'Episode', 'MusicVideo', 'Video'];

    // Audio and video field lists for visibility gating
    SmartLists.AUDIO_FIELD_NAMES = ['AudioBitrate', 'AudioSampleRate', 'AudioBitDepth', 'AudioCodec', 'AudioProfile', 'AudioChannels', 'AudioLanguages', 'SubtitleLanguages'];
    SmartLists.VIDEO_FIELD_NAMES = ['Resolution', 'Framerate', 'VideoCodec', 'VideoProfile', 'VideoRange', 'VideoRangeType'];

    // Debounce delay for media type change updates (milliseconds)
    SmartLists.MEDIA_TYPE_UPDATE_DEBOUNCE_MS = 200;

    // Constants for sort options (used throughout the application)
    SmartLists.SORT_OPTIONS = [
        { value: 'Name', label: 'Name', group: 'General' },
        { value: 'Random', label: 'Random', group: 'General' },
        { value: 'NoOrder', label: 'Default', group: 'General' },
        { value: 'DateCreated', label: 'Date Created', group: 'Dates' },
        { value: 'ReleaseDate', label: 'Release Date', group: 'Dates' },
        { value: 'ProductionYear', label: 'Production Year', group: 'Dates' },
        { value: 'CommunityRating', label: 'Community Rating', group: 'Ratings & Playback' },
        { value: 'PlayCount (owner)', label: 'Play Count (owner)', group: 'Ratings & Playback' },
        { value: 'LastPlayed (owner)', label: 'Last Played (owner)', group: 'Ratings & Playback' },
        { value: 'SeriesName', label: 'Series Name', group: 'TV' },
        { value: 'SeasonNumber', label: 'Season Number', group: 'TV' },
        { value: 'EpisodeNumber', label: 'Episode Number', group: 'TV' },
        { value: 'LastEpisodeAirDate', label: 'Last Episode Air Date', group: 'TV' },
        { value: 'AlbumName', label: 'Album Name', group: 'Music' },
        { value: 'Artist', label: 'Artist', group: 'Music' },
        { value: 'TrackNumber', label: 'Track Number', group: 'Music' },
        { value: 'Runtime', label: 'Runtime', group: 'Media Info' },
        { value: 'Resolution', label: 'Resolution', group: 'Media Info' },
        { value: 'Similarity', label: 'Similarity (requires Similar To rule)', group: 'Rule-Based' },
        { value: 'Rule Block Order', label: 'Rule Block Order', group: 'Rule-Based' },
        { value: 'External List Order', label: 'External List Order', group: 'Rule-Based' },
        { value: 'Round Robin', label: 'Round Robin (Interleave)', group: 'Round Robin' },
        { value: 'Random Round Robin', label: 'Random Round Robin (Interleave)', group: 'Round Robin' },
        { value: 'Shuffled Round Robin', label: 'Shuffled Round Robin (Interleave)', group: 'Round Robin' },
        { value: 'Least Recently Watched Round Robin', label: 'Least Recently Watched Round Robin (Interleave)', group: 'Round Robin' }
    ];

    // Sorts that have no Ascending/Descending direction
    SmartLists.ORDERLESS_SORTS = ['Random', 'Random Round Robin', 'Shuffled Round Robin', 'Least Recently Watched Round Robin', 'NoOrder'];

    // Round Robin sort variants (all use a GroupBy field)
    SmartLists.ROUND_ROBIN_SORTS = ['Round Robin', 'Random Round Robin', 'Shuffled Round Robin', 'Least Recently Watched Round Robin'];

    SmartLists.isOrderlessSort = function (name) {
        return SmartLists.ORDERLESS_SORTS.indexOf(name) !== -1;
    };

    SmartLists.isRoundRobinSort = function (name) {
        return SmartLists.ROUND_ROBIN_SORTS.indexOf(name) !== -1;
    };

    SmartLists.SORT_ORDER_OPTIONS = [
        { value: 'Ascending', label: 'Ascending' },
        { value: 'Descending', label: 'Descending' }
    ];

    // Fields available for Round Robin grouping, filtered by media type in the UI
    SmartLists.ROUND_ROBIN_GROUP_FIELDS = [
        { value: 'SeriesName', label: 'Series Name', mediaTypes: ['Episode'] },
        { value: 'AlbumName', label: 'Album Name', mediaTypes: ['Audio', 'MusicVideo'] },
        { value: 'Artist', label: 'Artist', mediaTypes: ['Audio', 'MusicVideo'] },
        { value: 'Genres', label: 'Genre (first)', mediaTypes: null },
        { value: 'Studios', label: 'Studio (first)', mediaTypes: null }
    ];

    SmartLists.RANDOM_GROUP_FIELDS = [
        { value: 'Artists', label: 'Artist', mediaTypes: ['Audio', 'MusicVideo'] },
        { value: 'AlbumArtists', label: 'Album Artist', mediaTypes: ['Audio', 'MusicVideo'] },
        { value: 'Album', label: 'Album', mediaTypes: ['Audio', 'MusicVideo'] },
        { value: 'SeriesName', label: 'Series Name', mediaTypes: ['Episode'] },
        { value: 'Genres', label: 'Genre', mediaTypes: null },
        { value: 'Studios', label: 'Studio', mediaTypes: null },
        { value: 'Tags', label: 'Tag', mediaTypes: null }
    ];

    // Sort fields that support child value aggregation (for collections containing collections/playlists)
    // When enabled, sort values are calculated from the Max of child items (e.g., newest DateCreated among children)
    SmartLists.CHILD_VALUE_SORT_FIELDS = ['ProductionYear', 'CommunityRating', 'DateCreated', 'ReleaseDate'];

    // Constants for operators
    SmartLists.RELATIVE_DATE_OPERATORS = ['NewerThan', 'OlderThan'];
    SmartLists.MULTI_VALUE_OPERATORS = ['IsIn', 'IsNotIn'];

    // Global state - availableFields is populated by loadAndPopulateFields
    SmartLists.availableFields = {};

    // Media types constant
    SmartLists.mediaTypes = [
        { Value: "Movie", Label: "Movie" },
        { Value: "Series", Label: "Series (TV Show)", CollectionOnly: true }, // Series can only be added to Collections, not Playlists
        { Value: "Season", Label: "Season (TV Show)", CollectionOnly: true }, // Season can only be added to Collections, not Playlists
        { Value: "Episode", Label: "Episode (TV Show)" },
        { Value: "Audio", Label: "Audio (Music)" },
        { Value: "MusicAlbum", Label: "Album (Music)", CollectionOnly: true }, // MusicAlbum can only be added to Collections, not Playlists
        { Value: "MusicVideo", Label: "Music Video" },
        { Value: "Video", Label: "Video" },
        { Value: "Photo", Label: "Photo (Home Photo)" },
        { Value: "Book", Label: "Book" },
        { Value: "AudioBook", Label: "Audiobook" }
    ];

    // Resolve the rules container for a scope: 'main' (default) or 'bumper'
    SmartLists.getRulesContainer = function (page, scope) {
        return page.querySelector(scope === 'bumper' ? '#bumper-rules-container' : '#rules-container');
    };

    // Utility function to get selected media types from page for a scope: 'main' (default) or 'bumper'
    SmartLists.getSelectedMediaTypes = function (page, scope) {
        if (scope === 'bumper') {
            var bumperSelect = page.querySelector('#bumperMediaType');
            return (bumperSelect && bumperSelect.value) ? [bumperSelect.value] : [];
        }
        return SmartLists.getSelectedItems(page, 'mediaTypesMultiSelect', 'media-type-multi-select-checkbox');
    };

    // Resolve a rule row's editor scope ('main' or 'bumper') from its logic group's data-rule-scope attribute
    SmartLists.getRowScope = function (ruleRow) {
        var logicGroup = ruleRow && ruleRow.closest ? ruleRow.closest('.logic-group') : null;
        return (logicGroup && logicGroup.getAttribute('data-rule-scope')) || 'main';
    };

    // Check if any rule has "Similar To" field selected
    SmartLists.hasSimilarToRuleInForm = function (page) {
        const allRules = page.querySelectorAll('#rules-container .rule-row');
        for (var i = 0; i < allRules.length; i++) {
            const ruleRow = allRules[i];
            const fieldSelect = ruleRow.querySelector('.rule-field-select');
            if (fieldSelect && fieldSelect.value === 'SimilarTo') {
                return true;
            }
        }
        return false;
    };

    SmartLists.hasExternalListRuleInForm = function (page) {
        const allRules = page.querySelectorAll('#rules-container .rule-row');
        for (var i = 0; i < allRules.length; i++) {
            const ruleRow = allRules[i];
            const fieldSelect = ruleRow.querySelector('.rule-field-select');
            if (fieldSelect && fieldSelect.value === 'ExternalList') {
                return true;
            }
        }
        return false;
    };

    /**
     * Escape HTML entities for safe insertion into HTML content.
     * Returns empty string for null/undefined, converts to string, then escapes:
     * & < > " ' / and ` (backtick) with their corresponding HTML entities.
     * 
     * Usage: Use this when inserting user-controlled content into HTML element content
     * (e.g., textContent, innerHTML, or between HTML tags).
     * 
     * @param {*} text - The text to escape
     * @returns {string} - The escaped HTML string
     */
    SmartLists.escapeHtml = function (text) {
        if (text == null) return '';
        var str = String(text);
        return str
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;')
            .replace(/\//g, '&#x2F;')
            .replace(/`/g, '&#96;');
    };

    /**
     * Escape HTML entities for safe insertion into HTML attribute values.
     * Returns empty string for null/undefined, converts to string, then escapes:
     * & < > " ' / and ` (backtick) with their corresponding HTML entities.
     * Quotes are escaped as &quot; (double quote) and &#39; (single quote) to prevent
     * attribute injection attacks.
     * 
     * Usage: Use this when inserting user-controlled content into HTML attribute values
     * (e.g., href, data-*, title, alt, value attributes, or data-* attributes).
     * 
     * @param {*} text - The text to escape
     * @returns {string} - The escaped HTML attribute string
     */
    SmartLists.escapeHtmlAttribute = function (text) {
        if (text == null) return '';
        var str = String(text);
        return str
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;')
            .replace(/\//g, '&#x2F;')
            .replace(/`/g, '&#96;');
    };

    // Extract error message from API error responses
    // Handles both modern Response objects and legacy error formats
    SmartLists.extractErrorMessage = async function (err, defaultMessage) {
        if (!err) return defaultMessage;

        try {
            // Check if this is a Response object (from fetch API)
            if (err && typeof err.text === 'function') {
                // Response body is a single-use stream - must call .text() first, then parse JSON
                try {
                    const textContent = await err.text();
                    if (textContent) {
                        // Try to parse as JSON first
                        try {
                            const errorData = JSON.parse(textContent);
                            if (errorData.message) {
                                return errorData.message;
                            } else if (typeof errorData === 'string') {
                                return errorData;
                            }
                        } catch (parseError) {
                            // If JSON parsing fails, use the raw text
                            return textContent;
                        }
                    }
                } catch (textError) {
                    console.log('Could not extract error text:', textError);
                }
            }
            // Legacy check for Response with json method (shouldn't happen, but defensive)
            else if (err && typeof err.json === 'function') {
                try {
                    const errorData = await err.json();
                    if (errorData.message) {
                        return errorData.message;
                    } else if (typeof errorData === 'string') {
                        return errorData;
                    }
                } catch (parseError) {
                    console.log('Could not parse error JSON:', parseError);
                }
            }
            // Check if the error has response text (legacy error format)
            else if (err.responseText) {
                try {
                    const errorData = JSON.parse(err.responseText);
                    if (errorData.message) {
                        return errorData.message;
                    } else if (typeof errorData === 'string') {
                        return errorData;
                    }
                } catch (parseError) {
                    // If JSON parsing fails, use the raw response text
                    return err.responseText;
                }
            }
            // Check if the error has a message property
            else if (err.message) {
                return err.message;
            }
        } catch (extractError) {
            console.error('Error extracting error message:', extractError);
        }

        return defaultMessage;
    };

    // Custom error class for API errors
    SmartLists.ApiError = function (message, status) {
        this.name = 'ApiError';
        this.message = message;
        this.status = status;
        this.stack = (new Error()).stack;
    };
    SmartLists.ApiError.prototype = Object.create(Error.prototype);
    SmartLists.ApiError.prototype.constructor = SmartLists.ApiError;

    // Standardized error display function
    SmartLists.displayApiError = function (error, context) {
        context = context || '';
        let message = 'An unexpected error occurred, check the logs for more details.';

        if (error instanceof SmartLists.ApiError) {
            message = error.message;
        } else if (error && error.message) {
            message = error.message;
        } else if (typeof error === 'string') {
            message = error;
        }

        const contextPrefix = context ? context + ': ' : '';
        const fullMessage = contextPrefix + message;

        console.error('API Error:', fullMessage, error);
        if (SmartLists.showNotification) {
            SmartLists.showNotification(fullMessage, 'error');
        }

        return fullMessage;
    };

    // Safe DOM manipulation helper to prevent XSS vulnerabilities
    // Accepts an array of {value, label, selected} objects
    SmartLists.populateSelectElement = function (selectElement, optionsData) {
        // Clear existing options
        selectElement.innerHTML = '';

        if (Array.isArray(optionsData)) {
            // Create option elements programmatically
            optionsData.forEach(function (optionData) {
                var option = document.createElement('option');
                option.value = optionData.value || '';
                option.textContent = optionData.label || optionData.value || '';
                if (optionData.selected) {
                    option.selected = true;
                }
                selectElement.appendChild(option);
            });
        }
    };

    // DOM Helper Functions to reduce repetition and improve maintainability

    /**
     * Get element value safely with optional default
     */
    SmartLists.getElementValue = function (page, selector, defaultValue) {
        defaultValue = defaultValue || '';
        const element = page.querySelector(selector);
        return element ? element.value : defaultValue;
    };

    /**
     * Get element checked state safely with optional default
     */
    SmartLists.getElementChecked = function (page, selector, defaultValue) {
        defaultValue = defaultValue !== undefined ? defaultValue : false;
        const element = page.querySelector(selector);
        return element ? element.checked : defaultValue;
    };

    /**
     * Set element value safely (only if element exists)
     */
    SmartLists.setElementValue = function (page, selector, value) {
        const element = page.querySelector(selector);
        if (element) {
            element.value = value;
            return true;
        }
        return false;
    };

    /**
     * Set element checked state safely (only if element exists)
     */
    SmartLists.setElementChecked = function (page, selector, checked) {
        const element = page.querySelector(selector);
        if (element) {
            element.checked = checked;
            return true;
        }
        return false;
    };

    SmartLists.getPluginId = function () {
        return SmartLists.PLUGIN_ID;
    };

    SmartLists.getApiClient = function () {
        return window.ApiClient;
    };

    SmartLists.loadAndPopulateFields = function () {
        const apiClient = SmartLists.getApiClient();
        const url = apiClient.getUrl(SmartLists.ENDPOINTS.fields);

        return apiClient.ajax({
            type: 'GET',
            url: url,
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('Network response was not ok: ' + response.statusText);
            }
            return response.json();
        }).then(function (fields) {
            SmartLists.availableFields = fields;
            return fields;
        }).catch(function (err) {
            console.error('Error loading or parsing fields:', err);
            throw err;
        });
    };

    SmartLists.populateSelect = function (selectElement, options, defaultValue, forceSelection) {
        defaultValue = defaultValue !== undefined ? defaultValue : null;
        forceSelection = forceSelection !== undefined ? forceSelection : true;
        if (!selectElement) return;
        options.forEach(function (opt, index) {
            const option = document.createElement('option');
            option.value = opt.Value;
            option.textContent = opt.Label;
            selectElement.appendChild(option);

            if ((defaultValue && opt.Value === defaultValue) || (!defaultValue && forceSelection && index === 0)) {
                option.selected = true;
            }
        });
    };

    // Page state management
    SmartLists.getPageEditState = function (page) {
        return {
            editMode: page._editMode || false,
            editingPlaylistId: page._editingPlaylistId || null
        };
    };

    SmartLists.setPageEditState = function (page, editMode, editingPlaylistId) {
        editingPlaylistId = editingPlaylistId || null;
        page._editMode = editMode;
        page._editingPlaylistId = editingPlaylistId;
    };

    SmartLists.createAbortController = function () {
        return typeof AbortController !== 'undefined' ? new AbortController() : null;
    };

    SmartLists.getEventListenerOptions = function (signal) {
        return signal ? { signal: signal } : {};
    };

    // Centralized styling configuration
    SmartLists.STYLES = {
        scheduleBox: {
            border: '1px solid var(--jf-palette-divider)',
            padding: '1em 3em 1em 1.5em',
            marginBottom: '1em',
            position: 'relative'
        },
        scheduleFields: {
            display: 'flex',
            gap: '0.75em',
            alignItems: 'flex-end',
            flexWrap: 'wrap',
            marginBottom: '0.5em',
            position: 'relative'
        },
        scheduleField: {
            display: 'flex',
            flexDirection: 'column',
            minWidth: '120px',
            flex: '0 1 auto'
        },
        scheduleFieldLabel: {
            marginBottom: '0.3em',
            fontSize: '0.85em',
            opacity: '0.8',
            fontWeight: '500'
        },
        scheduleRemoveBtn: {
            padding: '0.3em 0.6em',
            fontSize: '1.3em',
            border: 'none',
            background: 'var(--jf-palette-error-main)',
            color: 'var(--jf-palette-error-contrastText)',
            borderRadius: '4px',
            cursor: 'pointer',
            fontWeight: '500',
            lineHeight: '1',
            width: 'auto',
            minWidth: 'auto',
            position: 'absolute',
            right: '0.75em',
            top: '50%',
            transform: 'translateY(-50%)'
        },
        sortBox: {
            border: '1px solid var(--jf-palette-divider)',
            padding: '1em 3em 1em 1.5em',
            marginBottom: '1em',
            position: 'relative'
        },
        sortFields: {
            display: 'flex',
            gap: '0.75em',
            alignItems: 'center',
            flexWrap: 'wrap'
        },
        sortField: {
            display: 'flex',
            flexDirection: 'column',
            minWidth: '180px',
            flex: '0 1 auto'
        },
        sortFieldLabel: {
            marginBottom: '0.3em',
            fontSize: '0.85em',
            opacity: '0.8',
            fontWeight: '500'
        },
        sortRemoveBtn: {
            padding: '0.3em 0.6em',
            fontSize: '1.3em',
            border: 'none',
            background: 'var(--jf-palette-error-main)',
            color: 'var(--jf-palette-error-contrastText)',
            borderRadius: '4px',
            cursor: 'pointer',
            fontWeight: '500',
            lineHeight: '1',
            width: 'auto',
            minWidth: 'auto',
            position: 'absolute',
            right: '0.75em',
            top: '50%',
            transform: 'translateY(-50%)'
        },
        modal: {
            container: {
                position: 'fixed',
                top: '50%',
                left: '50%',
                transform: 'translate(-50%, -50%)',
                zIndex: '10001',
                backgroundColor: 'var(--jf-palette-background-paper)',
                color: 'var(--jf-palette-text-primary)',
                border: '1px solid var(--jf-palette-divider)',
                padding: '1.5em',
                width: '90%',
                maxWidth: '400px'
            },
            backdrop: {
                position: 'fixed',
                top: '0',
                left: '0',
                width: '100%',
                height: '100%',
                backgroundColor: 'rgba(0,0,0,0.5)',
                zIndex: '10000'
            }
        },
        logicGroup: {
            border: '1px solid var(--jf-palette-divider)',
            padding: '1.5em 1.5em 0.5em 1.5em',
            marginBottom: '1em',
            position: 'relative'
        },
        logicGroupHeader: {
            position: 'absolute',
            top: '-32px',
            right: '0',
            padding: '0',
            background: 'transparent',
            zIndex: '5',
            display: 'flex',
            gap: '4px'
        },
        buttons: {
            action: {
                base: {
                    padding: '0.3em 0.8em',
                    fontSize: '0.8em',
                    border: '1px solid var(--jf-palette-divider)',
                    background: 'var(--jf-palette-action-hover)',
                    color: 'var(--jf-palette-text-secondary)',
                    borderRadius: '4px',
                    cursor: 'pointer',
                    fontWeight: '400',
                    height: '29px',
                    display: 'flex',
                    justifyContent: 'center',
                    alignItems: 'center'
                }
            },
            clone: {
                base: {
                    padding: '0.3em 0.8em',
                    fontSize: '0.8em',
                    border: '1px solid var(--jf-palette-divider)',
                    background: 'var(--jf-palette-action-hover)',
                    color: 'var(--jf-palette-text-secondary)',
                    borderRadius: '4px',
                    cursor: 'pointer',
                    fontWeight: '400',
                    height: '29px',
                    width: '34px',
                    display: 'flex',
                    justifyContent: 'center',
                    alignItems: 'center'
                }
            },
            delete: {
                base: {
                    padding: '0.3em 0.8em',
                    fontSize: '1em',
                    border: 'none',
                    background: 'var(--jf-palette-error-main)',
                    color: 'var(--jf-palette-error-contrastText)',
                    borderRadius: '4px',
                    cursor: 'pointer',
                    fontWeight: '400',
                    height: '29px',
                    display: 'flex',
                    justifyContent: 'center',
                    alignItems: 'center'
                }
            }
        },
        separators: {
            and: {
                textAlign: 'center',
                margin: '0.8em 0',
                opacity: '0.6',
                fontSize: '0.8em',
                fontWeight: 'bold',
                position: 'relative',
                padding: '0.3em 0'
            },
            or: {
                textAlign: 'center',
                margin: '1em 0',
                position: 'relative'
            },
            orText: {
                background: 'var(--jf-palette-background-paper)',
                padding: '0.4em',
                borderRadius: '4px',
                fontWeight: 'bold',
                fontSize: '0.9em',
                position: 'relative',
                zIndex: '2',
                display: 'inline-block',
                border: '1px solid var(--jf-palette-divider)',
                boxShadow: '0 2px 6px rgba(0, 0, 0, 0.4)'
            },
            orLine: {
                position: 'absolute',
                top: '50%',
                left: '0',
                right: '0',
                height: '2px',
                background: 'linear-gradient(to right, transparent, var(--jf-palette-divider), transparent)',
                zIndex: '1'
            },
            andLine: {
                position: 'absolute',
                top: '50%',
                left: '20%',
                right: '20%',
                height: '1px',
                background: 'var(--jf-palette-divider)',
                zIndex: '1'
            }
        }
    };

    SmartLists.styleRuleActionButton = function (button, buttonType) {
        // Map and/or buttons to shared 'action' styling
        const styleKey = (buttonType === 'and' || buttonType === 'or') ? 'action' : buttonType;
        const buttonStyles = SmartLists.STYLES.buttons[styleKey];
        if (!buttonStyles) return;

        const styles = buttonStyles.base;
        SmartLists.applyStyles(button, styles);
    };

    SmartLists.createAndSeparator = function () {
        const separator = SmartLists.createStyledElement('div', 'rule-within-group-separator', SmartLists.STYLES.separators.and);
        separator.textContent = 'AND';

        const line = SmartLists.createStyledElement('div', '', SmartLists.STYLES.separators.andLine);
        separator.appendChild(line);

        return separator;
    };

    SmartLists.createOrSeparator = function () {
        const separator = SmartLists.createStyledElement('div', 'logic-group-separator', SmartLists.STYLES.separators.or);
        const orText = SmartLists.createStyledElement('div', '', SmartLists.STYLES.separators.orText);
        orText.textContent = 'OR';
        separator.appendChild(orText);

        const line = SmartLists.createStyledElement('div', '', SmartLists.STYLES.separators.orLine);
        separator.appendChild(line);

        return separator;
    };

    // Utility functions for applying styles
    SmartLists.applyStyles = function (element, styles) {
        if (!element || !styles) return;

        Object.keys(styles).forEach(function (property) {
            const value = styles[property];
            // Convert camelCase to kebab-case
            const cssProperty = property.replace(/([A-Z])/g, '-$1').toLowerCase();
            element.style.setProperty(cssProperty, value, 'important');
        });
    };

    SmartLists.createStyledElement = function (tagName, className, styles) {
        const element = document.createElement(tagName);
        if (className) element.className = className;
        if (styles) SmartLists.applyStyles(element, styles);
        return element;
    };

    /**
     * Get theme colors from CSS variables for consistent styling.
     * Returns an object with common color values and their channel variants for rgba backgrounds.
     */
    SmartLists.getThemeColors = function () {
        var styles = getComputedStyle(document.documentElement);
        var primaryChannel = styles.getPropertyValue('--jf-palette-primary-mainChannel').trim() || '0 164 220';
        var successChannel = styles.getPropertyValue('--jf-palette-success-mainChannel').trim() || '102 187 106';
        return {
            primaryColor: styles.getPropertyValue('--jf-palette-primary-main').trim() || '#00a4dc',
            primaryChannel: primaryChannel,
            primaryBg: 'rgb(' + primaryChannel + ' / 0.1)',
            successColor: styles.getPropertyValue('--jf-palette-success-main').trim() || '#66bb6a',
            successChannel: successChannel,
            successBg: 'rgb(' + successChannel + ' / 0.1)',
            dividerColor: styles.getPropertyValue('--jf-palette-divider').trim() || 'rgba(255, 255, 255, 0.12)'
        };
    };

    // Notification system with stacking support
    var notificationContainer = null;
    var activeNotifications = [];

    function ensureNotificationContainer() {
        if (!notificationContainer) {
            notificationContainer = document.querySelector('#floating-notification-container');
            if (!notificationContainer) {
                notificationContainer = document.createElement('div');
                notificationContainer.id = 'floating-notification-container';
                const containerStyles = {
                    position: 'fixed',
                    bottom: '20px',
                    left: '20px',
                    zIndex: '10000',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: '10px',
                    pointerEvents: 'none',
                    maxWidth: '400px',
                    minWidth: '300px'
                };
                Object.entries(containerStyles).forEach(function (entry) {
                    const property = entry[0].replace(/([A-Z])/g, '-$1').toLowerCase();
                    notificationContainer.style.setProperty(property, entry[1], 'important');
                });
                document.body.appendChild(notificationContainer);
            }
        }
        return notificationContainer;
    }

    function removeNotification(notificationElement, timeoutId) {
        // Find and remove from active notifications
        var index = -1;
        for (var i = 0; i < activeNotifications.length; i++) {
            if (activeNotifications[i].element === notificationElement) {
                index = i;
                break;
            }
        }

        if (index !== -1) {
            activeNotifications.splice(index, 1);
        }

        // Clear timeout if provided
        if (timeoutId) {
            clearTimeout(timeoutId);
        }

        // Animate out
        notificationElement.style.setProperty('transform', 'translateY(20px)', 'important');
        notificationElement.style.setProperty('opacity', '0', 'important');

        // Remove from DOM after animation
        setTimeout(function () {
            if (notificationElement && notificationElement.parentNode) {
                notificationElement.parentNode.removeChild(notificationElement);
            }

            // Update positions of remaining notifications
            updateNotificationPositions();
        }, 300);
    }

    function updateNotificationPositions() {
        // Positions are automatically handled by flexbox column layout
        // New notifications appear at top, older ones move down
        // No manual positioning needed
    }

    // Helper function to create a link to the status page (or just text for user pages)
    SmartLists.createStatusPageLink = function (linkText) {
        linkText = linkText || 'status page';
        
        // On user pages, users can't access the status tab, so just return plain text
        if (SmartLists.IS_USER_PAGE) {
            return '';  // Return empty string to remove the reference entirely
        }
        
        // Create a unique ID for the link to attach event listener
        var linkId = 'status-link-' + Date.now() + '-' + Math.random().toString(36).slice(2, 11);
        var linkHtml = '<a href="#" id="' + linkId + '">' + linkText + '</a>';

        // Attach click handler after a short delay to ensure DOM is ready
        setTimeout(function () {
            var linkElement = document.getElementById(linkId);
            if (linkElement) {
                linkElement.addEventListener('click', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    var page = document.querySelector('.SmartListsConfigurationPage');
                    if (page && window.SmartLists && window.SmartLists.switchToTab) {
                        window.SmartLists.switchToTab(page, 'status');
                    }
                    return false;
                });
            }
        }, 50);

        return linkHtml;
    };

    SmartLists.showNotification = function (message, type, options) {
        type = type || 'error';
        options = options || {};

        // Ensure container exists
        var container = ensureNotificationContainer();

        // Add type prefix for better clarity (only if not using HTML)
        let prefixedMessage = message;
        if (!options.html) {
            if (type === 'warning') {
                prefixedMessage = '⚠ ' + message;
            } else if (type === 'error') {
                prefixedMessage = '✗ ' + message;
            }
            // Info type notifications don't have a prefix icon
        }

        // Create individual notification element
        var notificationElement = document.createElement('div');
        notificationElement.className = 'floating-notification';

        // Create close button
        var closeButton = document.createElement('button');
        closeButton.className = 'notification-close-button';
        closeButton.innerHTML = '&#xE5CD;'; // Material Icons 'close' character
        closeButton.setAttribute('aria-label', 'Close notification');
        closeButton.type = 'button';

        // Style close button
        const closeButtonStyles = {
            position: 'absolute',
            top: '50%',
            right: '8px',
            transform: 'translateY(-50%)',
            background: 'transparent',
            border: 'none',
            color: 'var(--jf-palette-text-secondary)',
            fontSize: '20px',
            cursor: 'pointer',
            padding: '4px',
            lineHeight: '1',
            width: '24px',
            height: '24px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            borderRadius: '2px',
            fontFamily: '"Material Icons", "MaterialIcons-Regular", sans-serif',
            fontWeight: 'normal',
            fontStyle: 'normal',
            textRendering: 'optimizeLegibility'
        };

        SmartLists.applyStyles(closeButton, closeButtonStyles);

        // Click handler to close notification
        closeButton.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            // Find the notification data and remove it
            for (var i = 0; i < activeNotifications.length; i++) {
                if (activeNotifications[i].element === notificationElement) {
                    removeNotification(notificationElement, activeNotifications[i].timeoutId);
                    break;
                }
            }
        });

        // Set content - support HTML or plain text
        if (options.html) {
            notificationElement.innerHTML = prefixedMessage;
        } else {
            notificationElement.textContent = prefixedMessage;
        }

        // Append close button to notification
        notificationElement.appendChild(closeButton);

        // Apply notification styles
        const notificationStyles = {
            padding: '16px 40px 16px 20px', // Extra right padding for close button
            color: 'var(--jf-palette-text-primary)',
            backgroundColor: type === 'success' ? 'var(--jf-palette-background-paper)' :
                type === 'warning' ? 'var(--jf-palette-warning-main)' :
                    type === 'info' ? 'var(--jf-palette-background-paper)' : 'var(--jf-palette-error-main)',
            boxShadow: '0 4px 12px rgba(0, 0, 0, 0.4)',
            fontSize: '16px',
            fontWeight: 'normal',
            textAlign: 'left',
            boxSizing: 'border-box',
            borderRadius: '4px',
            transform: 'translateY(20px)',
            opacity: '0',
            transition: 'all 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
            pointerEvents: 'auto',
            cursor: 'default',
            wordWrap: 'break-word',
            overflowWrap: 'break-word',
            userSelect: 'text',
            WebkitUserSelect: 'text',
            MozUserSelect: 'text',
            msUserSelect: 'text',
            position: 'relative' // Required for absolute positioning of close button
        };

        // Style links within notifications
        if (options.html) {
            setTimeout(function () {
                var links = notificationElement.querySelectorAll('a');
                for (var i = 0; i < links.length; i++) {
                    links[i].style.color = 'var(--jf-palette-text-primary)';
                    links[i].style.textDecoration = 'underline';
                    links[i].style.cursor = 'pointer';
                }
            }, 0);
        }

        // Apply styles
        SmartLists.applyStyles(notificationElement, notificationStyles);

        // Add to container (at the beginning, so newest appears at top)
        if (container.firstChild) {
            container.insertBefore(notificationElement, container.firstChild);
        } else {
            container.appendChild(notificationElement);
        }

        // Track this notification
        var notificationData = {
            element: notificationElement,
            timeoutId: null
        };
        activeNotifications.push(notificationData);

        // Animate in
        setTimeout(function () {
            notificationElement.style.setProperty('transform', 'translateY(0)', 'important');
            notificationElement.style.setProperty('opacity', '1', 'important');
        }, 10);

        // Set timeout to auto-dismiss
        notificationData.timeoutId = setTimeout(function () {
            removeNotification(notificationElement, notificationData.timeoutId);
        }, 8000);
    };

    SmartLists.clearNotification = function () {
        // Clear all notifications
        if (notificationContainer) {
            var notifications = notificationContainer.querySelectorAll('.floating-notification');
            for (var i = 0; i < notifications.length; i++) {
                var notification = notifications[i];
                // Find its timeout and clear it
                for (var j = 0; j < activeNotifications.length; j++) {
                    if (activeNotifications[j].element === notification) {
                        if (activeNotifications[j].timeoutId) {
                            clearTimeout(activeNotifications[j].timeoutId);
                        }
                        break;
                    }
                }
                removeNotification(notification, null);
            }
        }
        activeNotifications = [];
    };

    SmartLists.cleanupModalListeners = function (modal) {
        // Remove any existing backdrop listener to prevent accumulation
        if (modal._modalBackdropHandler) {
            modal.removeEventListener('click', modal._modalBackdropHandler);
            modal._modalBackdropHandler = null;
        }

        // Abort any AbortController-managed listeners
        if (modal._modalAbortController) {
            try {
                modal._modalAbortController.abort();
            } catch (err) {
                console.warn('Error aborting modal listeners:', err);
            } finally {
                modal._modalAbortController = null;
            }
        }
    };

})(window.SmartLists = window.SmartLists || {});
