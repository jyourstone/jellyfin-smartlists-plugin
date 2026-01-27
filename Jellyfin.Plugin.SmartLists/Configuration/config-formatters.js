(function (SmartLists) {
    'use strict';

    // Determine if locale uses 12-hour format
    SmartLists.isLocale12Hour = function (locale) {
        try {
            return new Intl.DateTimeFormat(locale, { hour: 'numeric' })
                .resolvedOptions().hour12 === true;
        } catch (_) {
            // Fallback heuristic: default to 24h if unsure
            return false;
        }
    };

    // Fallback time formatting for older browsers
    SmartLists.formatTimeFallback = function (hour, minute, use12Hour) {
        var displayMinute = minute < 10 ? '0' + minute : minute;
        var displayHour;

        if (use12Hour) {
            displayHour = hour === 0 ? 12 : (hour > 12 ? hour - 12 : hour);
            var ampm = hour < 12 ? 'AM' : 'PM';
            return displayHour + ':' + displayMinute + ' ' + ampm;
        } else {
            displayHour = hour < 10 ? '0' + hour : hour;
            return displayHour + ':' + displayMinute;
        }
    };

    // Format time according to user's locale preferences
    SmartLists.formatTimeForUser = function (hour, minute) {
        // Use browser locale for time formatting
        var locale = navigator.language || navigator.userLanguage || 'en-US';

        // Create a Date object for formatting
        var date = new Date();
        date.setHours(hour);
        date.setMinutes(minute);
        date.setSeconds(0);

        // Use Intl.DateTimeFormat for locale-aware time formatting
        try {
            return new Intl.DateTimeFormat(locale, {
                hour: 'numeric',
                minute: '2-digit',
                hour12: SmartLists.isLocale12Hour(locale)
            }).format(date);
        } catch (e) {
            // Fallback to manual formatting if Intl is not available
            return SmartLists.formatTimeFallback(hour, minute, SmartLists.isLocale12Hour(locale));
        }
    };

    // Helper functions to generate common option sets (DRY principle)
    SmartLists.generateTimeOptions = function (defaultValue) {
        // Strip seconds from defaultValue if present (e.g., "16:45:00" -> "16:45")
        if (defaultValue && defaultValue.length > 5) {
            defaultValue = defaultValue.substring(0, 5);
        }
        
        var options = [];
        for (var hour = 0; hour < 24; hour++) {
            for (var minute = 0; minute < 60; minute += 15) {
                var timeValue = (hour < 10 ? '0' : '') + hour + ':' + (minute < 10 ? '0' : '') + minute;
                var displayTime = SmartLists.formatTimeForUser(hour, minute);
                var selected = timeValue === defaultValue;
                options.push({ value: timeValue, label: displayTime, selected: selected });
            }
        }
        return options;
    };

    // Format relative time from ISO string (e.g., "2 minutes ago", "3 hours ago")
    SmartLists.formatRelativeTimeFromIso = function (isoString, emptyText) {
        emptyText = emptyText || 'Unknown';
        if (!isoString) return emptyText;
        const ts = Date.parse(isoString);
        if (Number.isNaN(ts)) return emptyText;
        const diffMins = Math.floor((Date.now() - ts) / 60000);
        if (diffMins < 1) return 'Just now';
        if (diffMins < 60) return diffMins + ' minute' + (diffMins === 1 ? '' : 's') + ' ago';
        const diffHours = Math.floor(diffMins / 60);
        if (diffHours < 24) return diffHours + ' hour' + (diffHours === 1 ? '' : 's') + ' ago';
        const diffDays = Math.floor(diffHours / 24);
        return diffDays + ' day' + (diffDays === 1 ? '' : 's') + ' ago';
    };

    // Format runtime in minutes to human-readable format (e.g., "2d 6h", "3h 42m", "45m")
    SmartLists.formatRuntime = function (totalMinutes) {
        if (!totalMinutes || totalMinutes <= 0) return null;

        const days = Math.floor(totalMinutes / 1440); // 1440 minutes in a day

        if (days > 0) {
            // For playlists with 1+ days, round to nearest hour for space efficiency
            const totalHours = Math.round(totalMinutes / 60);
            const roundedDays = Math.floor(totalHours / 24);
            const roundedHours = totalHours % 24;

            const parts = [];
            if (roundedDays > 0) parts.push(roundedDays + 'd');
            if (roundedHours > 0) parts.push(roundedHours + 'h');

            return parts.length > 0 ? parts.join(' ') : '1d';
        } else {
            // For playlists < 1 day, show hours and minutes
            const hours = Math.floor(totalMinutes / 60);
            const minutesRemainder = totalMinutes % 60;
            const roundedMinutes = Math.round(minutesRemainder);

            // Handle edge case: rounding can produce 60 minutes
            let finalHours = hours;
            let finalMinutes = roundedMinutes;
            if (finalMinutes >= 60) {
                finalHours += 1;
                finalMinutes = 0;
            }

            const parts = [];
            if (finalHours > 0) parts.push(finalHours + 'h');
            if (finalMinutes > 0) parts.push(finalMinutes + 'm');

            return parts.length > 0 ? parts.join(' ') : '0m';
        }
    };

    // Format runtime in minutes to long text format
    SmartLists.formatRuntimeLong = function (totalMinutes) {
        if (!totalMinutes || totalMinutes <= 0) return null;

        let days = Math.floor(totalMinutes / 1440);
        const remainingMinutesAfterDays = totalMinutes % 1440;
        const hours = Math.floor(remainingMinutesAfterDays / 60);
        const minutesRemainder = remainingMinutesAfterDays % 60;
        const roundedMinutes = Math.round(minutesRemainder);

        // Handle edge case: rounding can produce 60 minutes
        let finalHours = hours;
        let finalMinutes = roundedMinutes;
        if (finalMinutes >= 60) {
            finalHours += 1;
            finalMinutes = 0;
        }

        // Handle edge case: incrementing hours can produce 24+ hours
        if (finalHours >= 24) {
            const extraDays = Math.floor(finalHours / 24);
            days += extraDays;
            finalHours = finalHours % 24;
        }

        const parts = [];
        if (days > 0) parts.push(days + ' day' + (days === 1 ? '' : 's'));
        if (finalHours > 0) parts.push(finalHours + ' hour' + (finalHours === 1 ? '' : 's'));
        if (finalMinutes > 0) parts.push(finalMinutes + ' minute' + (finalMinutes === 1 ? '' : 's'));

        return parts.length > 0 ? parts.join(' ') : '0 minutes';
    };

    SmartLists.generateAutoRefreshOptions = function (defaultValue) {
        var options = [
            { value: 'Never', label: 'Never - Manual/scheduled refresh only' },
            { value: 'OnLibraryChanges', label: 'On library changes - When new items are added' },
            { value: 'OnAllChanges', label: 'On all changes - Including playback status changes' }
        ];
        // Mark the default option as selected
        for (var i = 0; i < options.length; i++) {
            options[i].selected = options[i].value === defaultValue;
        }
        return options;
    };

    SmartLists.generateScheduleTriggerOptions = function (defaultValue, includeNoSchedule) {
        var options = [];
        if (includeNoSchedule) {
            options.push({ value: '', label: 'No schedule' });
        }
        options.push(
            { value: 'Daily', label: 'Daily' },
            { value: 'Weekly', label: 'Weekly' },
            { value: 'Monthly', label: 'Monthly' },
            { value: 'Yearly', label: 'Yearly' },
            { value: 'Interval', label: 'Interval' }
        );
        // Mark the default option as selected
        for (var i = 0; i < options.length; i++) {
            options[i].selected = options[i].value === defaultValue;
        }
        return options;
    };

    SmartLists.generateMonthOptions = function (defaultValue) {
        var months = ['January', 'February', 'March', 'April', 'May', 'June',
            'July', 'August', 'September', 'October', 'November', 'December'];
        var options = [];
        for (var i = 0; i < months.length; i++) {
            options.push({
                value: (i + 1).toString(),
                label: months[i],
                selected: defaultValue === (i + 1).toString()
            });
        }
        return options;
    };

    SmartLists.generateDayOfWeekOptions = function (defaultValue) {
        var days = [
            { value: '0', label: 'Sunday' },
            { value: '1', label: 'Monday' },
            { value: '2', label: 'Tuesday' },
            { value: '3', label: 'Wednesday' },
            { value: '4', label: 'Thursday' },
            { value: '5', label: 'Friday' },
            { value: '6', label: 'Saturday' }
        ];
        // Mark the default option as selected
        for (var i = 0; i < days.length; i++) {
            days[i].selected = days[i].value === defaultValue;
        }
        return days;
    };

    /**
     * Convert numeric weekday value (0-6) to day name
     * @param {string|number} dayValue - The day value (0=Sunday, 6=Saturday)
     * @returns {string} The day name (e.g., "Sunday", "Monday")
     */
    SmartLists.getDayNameFromValue = function (dayValue) {
        var days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
        var index = typeof dayValue === 'string' ? parseInt(dayValue, 10) : dayValue;
        return days[index] || 'Sunday';
    };

    /**
     * Get ordinal suffix for day of month (1st, 2nd, 3rd, 4th, etc.)
     */
    function getDayOfMonthSuffix(dayOfMonth) {
        if (dayOfMonth === 1 || dayOfMonth === 21 || dayOfMonth === 31) return 'st';
        if (dayOfMonth === 2 || dayOfMonth === 22) return 'nd';
        if (dayOfMonth === 3 || dayOfMonth === 23) return 'rd';
        return 'th';
    }

    SmartLists.generateDayOfMonthOptions = function (defaultValue) {
        var days = [];
        for (var i = 1; i <= 31; i++) {
            var suffix = getDayOfMonthSuffix(i);
            days.push({ value: i.toString(), label: i + suffix });
        }
        // Mark the default option as selected
        for (var j = 0; j < days.length; j++) {
            days[j].selected = days[j].value === defaultValue;
        }
        return days;
    };

    SmartLists.convertDayOfWeekToValue = function (dayOfWeek) {
        if (dayOfWeek === undefined || dayOfWeek === null) {
            return '0'; // Default to Sunday
        }

        // Handle numeric values (0-6)
        if (typeof dayOfWeek === 'number') {
            return dayOfWeek.toString();
        }

        // Handle string values ("Sunday", etc.)
        if (typeof dayOfWeek === 'string') {
            const dayMap = {
                'Sunday': '0', 'Monday': '1', 'Tuesday': '2', 'Wednesday': '3',
                'Thursday': '4', 'Friday': '5', 'Saturday': '6'
            };
            return dayMap[dayOfWeek] || '0';
        }

        return '0'; // Fallback to Sunday
    };

    SmartLists.generateIntervalOptions = function (defaultValue) {
        var intervals = [
            { value: '00:15:00', label: '15 minutes' },
            { value: '00:30:00', label: '30 minutes' },
            { value: '01:00:00', label: '1 hour' },
            { value: '02:00:00', label: '2 hours' },
            { value: '03:00:00', label: '3 hours' },
            { value: '04:00:00', label: '4 hours' },
            { value: '06:00:00', label: '6 hours' },
            { value: '08:00:00', label: '8 hours' },
            { value: '12:00:00', label: '12 hours' },
            { value: '1.00:00:00', label: '24 hours' }
        ];
        // Mark the default option as selected
        for (var i = 0; i < intervals.length; i++) {
            intervals[i].selected = intervals[i].value === defaultValue;
        }
        return intervals;
    };

    // Helper function to generate summary text with consistent styling
    SmartLists.generateSummaryText = function (totalPlaylists, playlistCount, collectionCount, filteredCount, searchTerm) {
        filteredCount = filteredCount !== undefined ? filteredCount : null;
        searchTerm = searchTerm || null;
        const bulletStyle = 'margin: 0 0.25em;';
        const bullet = '<span style="' + bulletStyle + '">•</span>';

        let summaryText = '<strong>Summary:&nbsp;</strong> ';

        if (filteredCount !== null && filteredCount !== totalPlaylists) {
            // Filtered results
            summaryText += filteredCount + ' of ' + totalPlaylists + ' list' + (totalPlaylists !== 1 ? 's' : '');
            if (searchTerm) {
                summaryText += ' matching "' + SmartLists.escapeHtml(searchTerm) + '"';
            }
        } else {
            // All lists
            summaryText += totalPlaylists + ' list' + (totalPlaylists !== 1 ? 's' : '');
        }

        summaryText += ' ' + bullet + ' ' + playlistCount + ' playlist' + (playlistCount !== 1 ? 's' : '') + ' ' + bullet + ' ' + collectionCount + ' collection' + (collectionCount !== 1 ? 's' : '');

        return summaryText;
    };

    // Helper function to generate bulk actions HTML
    SmartLists.generateBulkActionsHTML = function (summaryText) {
        let html = '';
        html += '<div class="inputContainer" id="bulkActionsContainer" style="margin-bottom: 1em; display: none;">';
        html += '<div class="paperList" style="padding: 1em; border: 1px solid var(--jf-palette-divider);">';

        // Summary row at top
        html += '<div id="playlist-summary" class="field-description" style="margin: 0 0 1em 0; padding: 0.5em; background: var(--jf-palette-action-hover); border-radius: 4px;">';
        html += summaryText;
        html += '</div>';

        // Layout: Left side (Select All, bulk actions) | Right side (Expand All, Reload List)
        html += '<div style="display: flex; align-items: center; justify-content: flex-start; flex-wrap: wrap; gap: 0.5em;">';

        // Left side: Select All checkbox and bulk action buttons
        html += '<div style="display: flex; align-items: center; gap: 0.25em; flex-wrap: wrap;">';

        // 1. Select All checkbox
        html += '<label class="emby-checkbox-label" style="width: auto; min-width: auto;">';
        html += '<input type="checkbox" id="selectAllCheckbox" data-embycheckbox="true" class="emby-checkbox">';
        html += '<span class="checkboxLabel">Select All</span>';
        html += '<span class="checkboxOutline">';
        html += '<span class="material-icons checkboxIcon checkboxIcon-checked check" aria-hidden="true"></span>';
        html += '<span class="material-icons checkboxIcon checkboxIcon-unchecked" aria-hidden="true"></span>';
        html += '</span>';
        html += '</label>';

        // Selected count display (min-width prevents layout shift when count changes)
        html += '<span id="selectedCountDisplay" class="fieldDescription" style="opacity: 0.6; font-style: italic; display: inline-block; min-width: 2.5em;">(0)</span>';

        // 2. Action dropdown (always enabled for better UX, max-width keeps it compact when closed)
        html += '<select id="bulkActionSelect" is="emby-select" class="emby-select-withcolor emby-select" style="width: auto; min-width: 205px;">';
        html += '<option value="">Bulk action...</option>';
        html += '<option value="enable">Enable</option>';
        html += '<option value="disable">Disable</option>';
        html += '<option value="refresh">Refresh</option>';
        html += '<option value="delete">Delete</option>';
        html += '<option value="convertToPlaylist">Convert to Playlist</option>';
        html += '<option value="convertToCollection">Convert to Collection</option>';
        html += '</select>';

        // 3. Apply button (styled to match dropdown height)
        html += '<button type="button" id="bulkApplyBtn" class="emby-button raised" style="height: 2.7em; padding: 0 1.5em;" disabled>Apply</button>';

        html += '</div>'; // End left side

        // Right side: View control buttons
        html += '<div style="display: flex; align-items: center; gap: 0.25em; flex-wrap: wrap; margin-left: auto;">';

        // 5. Expand All button
        html += '<button type="button" id="expandAllBtn" class="emby-button raised">Expand All</button>';

        // 6. Reload List button
        html += '<button type="button" id="refreshPlaylistListBtn" class="emby-button raised">Reload List</button>';

        html += '</div>'; // End right side
        html += '</div>'; // End flex container
        html += '</div>'; // End paperList
        html += '</div>'; // End inputContainer

        return html;
    };

    // Format schedule display text
    SmartLists.formatScheduleDisplay = function (playlist) {
        // Check if Schedules array exists and has items
        if (playlist.Schedules && playlist.Schedules.length > 0) {
            var scheduleTexts = playlist.Schedules.map(function (schedule) {
                return SmartLists.formatSingleSchedule(schedule);
            });
            return scheduleTexts.join(' • ');
        }

        // No schedules configured
        return 'None';
    };

    // Format visibility schedule display text
    SmartLists.formatVisibilityScheduleDisplay = function (list) {
        // Check if VisibilitySchedules array exists and has items
        if (list.VisibilitySchedules && list.VisibilitySchedules.length > 0) {
            var scheduleTexts = list.VisibilitySchedules.map(function (schedule) {
                var action = schedule.Action === 'Enable' ? 'Enable' : 'Disable';
                var timing = SmartLists.formatSingleSchedule(schedule);
                return action + ': ' + timing;
            });
            return scheduleTexts.join(' • ');
        }

        // No visibility schedules configured
        return 'None';
    };

    // Helper function to format sort display text
    SmartLists.formatSortDisplay = function (playlist) {
        if (!playlist.Order) {
            return 'Default';
        }

        // New format: SortOptions array
        if (playlist.Order.SortOptions && playlist.Order.SortOptions.length > 0) {
            return playlist.Order.SortOptions.map(function (opt) {
                var displaySortBy = opt.SortBy;
                // Format "Name (Ignore Articles)" and "SeriesName (Ignore Articles)" for display
                if (displaySortBy === 'Name (Ignore Articles)') {
                    displaySortBy = 'Name (Ignore Article \'The\')';
                } else if (displaySortBy === 'SeriesName (Ignore Articles)') {
                    displaySortBy = 'Series Name (Ignore Article \'The\')';
                }
                // Random and NoOrder don't have meaningful order, so don't show "Ascending"
                // Normalize "NoOrder" to "No Order" for display consistency
                if (displaySortBy === 'Random' || displaySortBy === 'NoOrder' || displaySortBy === 'No Order') {
                    return displaySortBy === 'NoOrder' ? 'No Order' : displaySortBy;
                }
                var result = displaySortBy + ' ' + opt.SortOrder;
                // Add indicator when sorting by child item values
                if (opt.UseChildValues) {
                    result += ' (child items)';
                }
                return result;
            }).join(' → ');
        }

        // Legacy format: Order.Name string
        if (playlist.Order.Name) {
            return playlist.Order.Name;
        }

        return 'Default';
    };

    SmartLists.formatSingleSchedule = function (schedule) {
        if (schedule.Trigger === 'Daily') {
            const raw = schedule.Time ? schedule.Time.substring(0, 5) : '00:00';
            const parts = raw.split(':');
            const h = parts[0] !== undefined ? parseInt(parts[0], 10) : 0;
            const m = parts[1] !== undefined ? parseInt(parts[1], 10) : 0;
            return 'Daily at ' + SmartLists.formatTimeForUser(h, m);
        } else if (schedule.Trigger === 'Weekly') {
            const raw = schedule.Time ? schedule.Time.substring(0, 5) : '00:00';
            const parts = raw.split(':');
            const h = parts[0] !== undefined ? parseInt(parts[0], 10) : 0;
            const m = parts[1] !== undefined ? parseInt(parts[1], 10) : 0;
            const dayIndex = (schedule.DayOfWeek !== undefined && schedule.DayOfWeek !== null) ? schedule.DayOfWeek : 0;
            const dayName = SmartLists.getDayNameFromValue(dayIndex);
            return 'Weekly on ' + dayName + ' at ' + SmartLists.formatTimeForUser(h, m);
        } else if (schedule.Trigger === 'Monthly') {
            const raw = schedule.Time ? schedule.Time.substring(0, 5) : '00:00';
            const parts = raw.split(':');
            const h = parts[0] !== undefined ? parseInt(parts[0], 10) : 0;
            const m = parts[1] !== undefined ? parseInt(parts[1], 10) : 0;
            const dayOfMonth = schedule.DayOfMonth || 1;
            const suffix = getDayOfMonthSuffix(dayOfMonth);
            return 'Monthly on the ' + dayOfMonth + suffix + ' at ' + SmartLists.formatTimeForUser(h, m);
        } else if (schedule.Trigger === 'Yearly') {
            const raw = schedule.Time ? schedule.Time.substring(0, 5) : '00:00';
            const parts = raw.split(':');
            const h = parts[0] !== undefined ? parseInt(parts[0], 10) : 0;
            const m = parts[1] !== undefined ? parseInt(parts[1], 10) : 0;
            const monthNames = ['January', 'February', 'March', 'April', 'May', 'June',
                'July', 'August', 'September', 'October', 'November', 'December'];
            const month = schedule.Month || 1;
            const dayOfMonth = schedule.DayOfMonth || 1;
            const suffix = getDayOfMonthSuffix(dayOfMonth);
            return 'Yearly on ' + monthNames[month - 1] + ' ' + dayOfMonth + suffix + ' at ' + SmartLists.formatTimeForUser(h, m);
        } else if (schedule.Trigger === 'Interval') {
            const interval = schedule.Interval || '24:00:00';
            if (interval === '00:15:00') return 'Every 15 minutes';
            if (interval === '00:30:00') return 'Every 30 minutes';
            if (interval === '01:00:00') return 'Every hour';
            if (interval === '02:00:00') return 'Every 2 hours';
            if (interval === '03:00:00') return 'Every 3 hours';
            if (interval === '04:00:00') return 'Every 4 hours';
            if (interval === '06:00:00') return 'Every 6 hours';
            if (interval === '08:00:00') return 'Every 8 hours';
            if (interval === '12:00:00') return 'Every 12 hours';
            if (interval === '24:00:00' || interval === '1.00:00:00') return 'Every 24 hours';
            return 'Every ' + interval;
        }
        return schedule.Trigger || 'Unknown';
    };

    // Format playlist display values (used in playlist cards)
    // Note: This function returns maxItemsDisplay and maxPlayTimeDisplay
    // The runtime/schedule/sort formatting is done separately in generatePlaylistCardHtml
    SmartLists.formatPlaylistDisplayValues = function (playlist) {
        const maxItemsDisplay = (playlist.MaxItems === undefined || playlist.MaxItems === null || playlist.MaxItems === 0) ? 'Unlimited' : playlist.MaxItems.toString();
        const maxPlayTimeDisplay = (playlist.MaxPlayTimeMinutes === undefined || playlist.MaxPlayTimeMinutes === null || playlist.MaxPlayTimeMinutes === 0) ? 'Unlimited' : playlist.MaxPlayTimeMinutes.toString() + ' minutes';
        return { maxItemsDisplay: maxItemsDisplay, maxPlayTimeDisplay: maxPlayTimeDisplay };
    };

    // Get friendly display name for people fields
    SmartLists.getPeopleFieldDisplayName = function (fieldName) {
        const displayNames = {
            'People': 'People (All)',
            'Actors': 'People (Actors)',
            'Directors': 'People (Directors)',
            'Composers': 'People (Composers)',
            'Writers': 'People (Writers)',
            'GuestStars': 'People (Guest Stars)',
            'Producers': 'People (Producers)',
            'Conductors': 'People (Conductors)',
            'Lyricists': 'People (Lyricists)',
            'Arrangers': 'People (Arrangers)',
            'SoundEngineers': 'People (Sound Engineers)',
            'Mixers': 'People (Mixers)',
            'Remixers': 'People (Remixers)',
            'Creators': 'People (Creators)',
            'PersonArtists': 'People (Artists)',
            'PersonAlbumArtists': 'People (Album Artists)',
            'Authors': 'People (Authors)',
            'Illustrators': 'People (Illustrators)',
            'Pencilers': 'People (Pencilers)',
            'Inkers': 'People (Inkers)',
            'Colorists': 'People (Colorists)',
            'Letterers': 'People (Letterers)',
            'CoverArtists': 'People (Cover Artists)',
            'Editors': 'People (Editors)',
            'Translators': 'People (Translators)'
        };
        return displayNames[fieldName] || fieldName;
    };

})(window.SmartLists = window.SmartLists || {});

