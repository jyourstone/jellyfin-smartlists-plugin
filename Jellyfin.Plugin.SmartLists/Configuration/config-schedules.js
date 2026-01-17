(function(SmartLists) {
    'use strict';
    
    // Initialize namespace if it doesn't exist
    if (!SmartLists) {
        window.SmartLists = {};
        SmartLists = window.SmartLists;
    }
    
    // Toggle schedule containers based on trigger value (DRY helper)
    SmartLists.toggleScheduleContainers = function(page, prefix, triggerValue) {
        // ES5 compatible template string replacement
        var timeContainerId = prefix + 'scheduleTimeContainer';
        var dayContainerId = prefix + 'scheduleDayContainer';
        var dayOfMonthContainerId = prefix + 'scheduleDayOfMonthContainer';
        var monthContainerId = prefix + 'scheduleMonthContainer';
        var intervalContainerId = prefix + 'scheduleIntervalContainer';
        
        const timeContainer = page.querySelector('#' + timeContainerId);
        const dayContainer = page.querySelector('#' + dayContainerId);
        const dayOfMonthContainer = page.querySelector('#' + dayOfMonthContainerId);
        const monthContainer = page.querySelector('#' + monthContainerId);
        const intervalContainer = page.querySelector('#' + intervalContainerId);

        var containers = [timeContainer, dayContainer, dayOfMonthContainer, monthContainer, intervalContainer];
        containers.forEach(function(el) {
            if (el) el.classList.add('hide');
        });

        if (triggerValue === 'Daily') {
            if (timeContainer) timeContainer.classList.remove('hide');
        } else if (triggerValue === 'Weekly') {
            if (timeContainer) timeContainer.classList.remove('hide');
            if (dayContainer) dayContainer.classList.remove('hide');
        } else if (triggerValue === 'Monthly') {
            if (timeContainer) timeContainer.classList.remove('hide');
            if (dayOfMonthContainer) dayOfMonthContainer.classList.remove('hide');
        } else if (triggerValue === 'Yearly') {
            if (timeContainer) timeContainer.classList.remove('hide');
            if (monthContainer) monthContainer.classList.remove('hide');
            if (dayOfMonthContainer) dayOfMonthContainer.classList.remove('hide');
        } else if (triggerValue === 'Interval') {
            if (intervalContainer) intervalContainer.classList.remove('hide');
        }
    };
    
    SmartLists.initializeScheduleSystem = function(page) {
        const schedulesContainer = page.querySelector('#schedules-container');
        if (!schedulesContainer) return;
        
        // Clear any existing content
        schedulesContainer.innerHTML = '';
        
        // Don't add any schedule boxes by default - start empty
        // Just add the "Add Schedule" button
        const addBtn = document.createElement('button');
        addBtn.type = 'button';
        addBtn.className = 'emby-button raised add-schedule-btn';
        addBtn.textContent = '+ Add Refresh Schedule';
        addBtn.addEventListener('click', function() {
            SmartLists.addScheduleBox(page, null);
        });
        schedulesContainer.appendChild(addBtn);
    };
    
    SmartLists.createScheduleBox = function(page, scheduleData) {
        const scheduleId = 'schedule-' + Date.now() + '-' + Math.random();

        // Create box container with inline styles and paperList class for theme-aware background
        const box = SmartLists.createStyledElement('div', 'schedule-box paperList', SmartLists.STYLES.scheduleBox);
        box.setAttribute('data-schedule-id', scheduleId);
        
        // Create fields container with inline styles
        const fieldsContainer = SmartLists.createStyledElement('div', 'schedule-fields', SmartLists.STYLES.scheduleFields);
        
        // Trigger field
        const triggerField = SmartLists.createScheduleField('Trigger', 'schedule-trigger-' + scheduleId, 'select');
        SmartLists.populateSelectElement(triggerField.input, SmartLists.generateScheduleTriggerOptions(scheduleData ? scheduleData.Trigger : '', false));
        fieldsContainer.appendChild(triggerField.container);
        
        // Month field (for Yearly)
        const monthField = SmartLists.createScheduleField('Month', 'schedule-month-' + scheduleId, 'select');
        SmartLists.populateSelectElement(monthField.input, SmartLists.generateMonthOptions(scheduleData && scheduleData.Month ? scheduleData.Month.toString() : '1'));
        monthField.container.style.display = 'none';
        fieldsContainer.appendChild(monthField.container);
        
        // Day of Month field (for Monthly/Yearly)
        const dayOfMonthField = SmartLists.createScheduleField('Day of Month', 'schedule-dayofmonth-' + scheduleId, 'select');
        SmartLists.populateSelectElement(dayOfMonthField.input, SmartLists.generateDayOfMonthOptions(scheduleData && scheduleData.DayOfMonth ? scheduleData.DayOfMonth.toString() : '1'));
        dayOfMonthField.container.style.display = 'none';
        fieldsContainer.appendChild(dayOfMonthField.container);
        
        // Day of Week field (for Weekly)
        const dayOfWeekField = SmartLists.createScheduleField('Weekday', 'schedule-dayofweek-' + scheduleId, 'select');
        SmartLists.populateSelectElement(dayOfWeekField.input, SmartLists.generateDayOfWeekOptions(scheduleData && scheduleData.DayOfWeek !== undefined ? scheduleData.DayOfWeek.toString() : '0'));
        dayOfWeekField.container.style.display = 'none';
        fieldsContainer.appendChild(dayOfWeekField.container);
        
        // Time field (for Daily/Weekly/Monthly/Yearly)
        const timeField = SmartLists.createScheduleField('Time', 'schedule-time-' + scheduleId, 'select');
        const defaultTime = scheduleData && scheduleData.Time ? scheduleData.Time.substring(0, 5) : '00:00';
        SmartLists.populateSelectElement(timeField.input, SmartLists.generateTimeOptions(defaultTime));
        timeField.container.style.display = 'none';
        fieldsContainer.appendChild(timeField.container);
        
        // Interval field (for Interval)
        const intervalField = SmartLists.createScheduleField('Every', 'schedule-interval-' + scheduleId, 'select');
        SmartLists.populateSelectElement(intervalField.input, SmartLists.generateIntervalOptions(scheduleData && scheduleData.Interval ? scheduleData.Interval : '1.00:00:00'));
        intervalField.container.style.display = 'none';
        fieldsContainer.appendChild(intervalField.container);
        
        // Create remove button (X icon at end of fields row)
        const removeBtn = SmartLists.createStyledElement('button', 'schedule-remove-btn', SmartLists.STYLES.scheduleRemoveBtn);
        removeBtn.type = 'button';
        removeBtn.textContent = '×';
        removeBtn.title = 'Remove schedule';
        removeBtn.addEventListener('click', function() {
            SmartLists.removeScheduleBox(page, box);
        });
        fieldsContainer.appendChild(removeBtn);
        
        box.appendChild(fieldsContainer);
        
        // Add change listener to trigger to update field visibility
        triggerField.input.addEventListener('change', function() {
            SmartLists.updateScheduleFieldsVisibility(box, this.value);
        });
        
        // Set initial visibility based on actual selected value in dropdown
        var initialTrigger = triggerField.input.value;
        if (initialTrigger) {
            SmartLists.updateScheduleFieldsVisibility(box, initialTrigger);
        }
        
        return box;
    };
    
    SmartLists.createScheduleField = function(label, id, type) {
        const container = SmartLists.createStyledElement('div', 'schedule-field', SmartLists.STYLES.scheduleField);
        
        const labelElement = SmartLists.createStyledElement('label', '', SmartLists.STYLES.scheduleFieldLabel);
        labelElement.textContent = label;
        labelElement.htmlFor = id;
        container.appendChild(labelElement);
        
        const input = document.createElement(type === 'select' ? 'select' : 'input');
        input.id = id;
        if (type === 'select') {
            input.setAttribute('is', 'emby-select');
            input.className = 'emby-select-withcolor emby-select';
        } else {
            input.type = type;
            input.className = 'emby-input';
        }
        container.appendChild(input);
        
        return { container: container, input: input };
    };
    
    SmartLists.updateScheduleFieldsVisibility = function(box, triggerValue) {
        // Get all field containers
        const fields = box.querySelectorAll('.schedule-field');
        const monthField = Array.prototype.find.call(fields, function(f) {
            return f.querySelector('[id^="schedule-month-"]');
        });
        const dayOfMonthField = Array.prototype.find.call(fields, function(f) {
            return f.querySelector('[id^="schedule-dayofmonth-"]');
        });
        const dayOfWeekField = Array.prototype.find.call(fields, function(f) {
            return f.querySelector('[id^="schedule-dayofweek-"]');
        });
        const timeField = Array.prototype.find.call(fields, function(f) {
            return f.querySelector('[id^="schedule-time-"]');
        });
        const intervalField = Array.prototype.find.call(fields, function(f) {
            return f.querySelector('[id^="schedule-interval-"]');
        });
        
        // Hide all optional fields
        if (monthField) monthField.style.display = 'none';
        if (dayOfMonthField) dayOfMonthField.style.display = 'none';
        if (dayOfWeekField) dayOfWeekField.style.display = 'none';
        if (timeField) timeField.style.display = 'none';
        if (intervalField) intervalField.style.display = 'none';
        
        // Show relevant fields based on trigger
        if (triggerValue === 'Daily') {
            if (timeField) timeField.style.display = '';
        } else if (triggerValue === 'Weekly') {
            if (dayOfWeekField) dayOfWeekField.style.display = '';
            if (timeField) timeField.style.display = '';
        } else if (triggerValue === 'Monthly') {
            if (dayOfMonthField) dayOfMonthField.style.display = '';
            if (timeField) timeField.style.display = '';
        } else if (triggerValue === 'Yearly') {
            if (monthField) monthField.style.display = '';
            if (dayOfMonthField) dayOfMonthField.style.display = '';
            if (timeField) timeField.style.display = '';
        } else if (triggerValue === 'Interval') {
            if (intervalField) intervalField.style.display = '';
        }
    };
    
    SmartLists.addScheduleBox = function(page, scheduleData) {
        const schedulesContainer = page.querySelector('#schedules-container');
        if (!schedulesContainer) return;
        
        // Find the add button (it's always the last child)
        const addBtn = schedulesContainer.querySelector('.add-schedule-btn');
        
        // Create and insert the new box before the add button
        const newBox = SmartLists.createScheduleBox(page, scheduleData);
        if (addBtn) {
            schedulesContainer.insertBefore(newBox, addBtn);
            // Change button text after first schedule is added
            addBtn.textContent = '+ Add Another Schedule';
        } else {
            schedulesContainer.appendChild(newBox);
        }
    };
    
    SmartLists.removeScheduleBox = function(page, box) {
        const schedulesContainer = page.querySelector('#schedules-container');
        if (!schedulesContainer) return;
        
        box.remove();
        
        // Update button text if no schedules left
        const boxes = schedulesContainer.querySelectorAll('.schedule-box');
        const addBtn = schedulesContainer.querySelector('.add-schedule-btn');
        if (boxes.length === 0 && addBtn) {
            addBtn.textContent = '+ Add Schedule';
        }
    };
    
    SmartLists.collectSchedulesFromForm = function(page) {
        const schedulesContainer = page.querySelector('#schedules-container');
        if (!schedulesContainer) return [];
        
        const boxes = schedulesContainer.querySelectorAll('.schedule-box');
        const schedules = [];
        
        boxes.forEach(function(box) {
            const triggerSelect = box.querySelector('[id^="schedule-trigger-"]');
            if (!triggerSelect) return;
            
            const trigger = triggerSelect.value;
            if (!trigger) return; // Skip empty triggers
            
            const schedule = { Trigger: trigger };
            
            // Collect fields based on trigger type
            if (trigger === 'Daily') {
                const timeSelect = box.querySelector('[id^="schedule-time-"]');
                if (timeSelect && timeSelect.value) {
                    schedule.Time = timeSelect.value + ':00';
                }
            } else if (trigger === 'Weekly') {
                const dayOfWeekSelect = box.querySelector('[id^="schedule-dayofweek-"]');
                const timeSelect = box.querySelector('[id^="schedule-time-"]');
                if (dayOfWeekSelect && dayOfWeekSelect.value !== '') {
                    schedule.DayOfWeek = parseInt(dayOfWeekSelect.value, 10);
                }
                if (timeSelect && timeSelect.value) {
                    schedule.Time = timeSelect.value + ':00';
                }
            } else if (trigger === 'Monthly') {
                const dayOfMonthSelect = box.querySelector('[id^="schedule-dayofmonth-"]');
                const timeSelect = box.querySelector('[id^="schedule-time-"]');
                if (dayOfMonthSelect && dayOfMonthSelect.value) {
                    schedule.DayOfMonth = parseInt(dayOfMonthSelect.value, 10);
                }
                if (timeSelect && timeSelect.value) {
                    schedule.Time = timeSelect.value + ':00';
                }
            } else if (trigger === 'Yearly') {
                const monthSelect = box.querySelector('[id^="schedule-month-"]');
                const dayOfMonthSelect = box.querySelector('[id^="schedule-dayofmonth-"]');
                const timeSelect = box.querySelector('[id^="schedule-time-"]');
                if (monthSelect && monthSelect.value) {
                    schedule.Month = parseInt(monthSelect.value, 10);
                }
                if (dayOfMonthSelect && dayOfMonthSelect.value) {
                    schedule.DayOfMonth = parseInt(dayOfMonthSelect.value, 10);
                }
                if (timeSelect && timeSelect.value) {
                    schedule.Time = timeSelect.value + ':00';
                }
            } else if (trigger === 'Interval') {
                const intervalSelect = box.querySelector('[id^="schedule-interval-"]');
                if (intervalSelect && intervalSelect.value) {
                    schedule.Interval = intervalSelect.value;
                }
            }
            
            schedules.push(schedule);
        });
        
        return schedules;
    };
    
    // Helper function to show/hide schedule containers based on selected trigger (LEGACY - kept for backward compat)
    SmartLists.updateScheduleContainers = function(page, triggerValue) {
        SmartLists.toggleScheduleContainers(page, '', triggerValue);
    };
    
    // Helper function for default schedule containers
    SmartLists.updateDefaultScheduleContainers = function(page, triggerValue) {
        SmartLists.toggleScheduleContainers(page, 'default', triggerValue);
    };
    
    SmartLists.loadSchedulesIntoUI = function(page, playlist) {
        const schedulesContainer = page.querySelector('#schedules-container');
        if (!schedulesContainer) return;
        
        schedulesContainer.innerHTML = '';
        
        var schedulesToLoad = [];
        
        // Check for new Schedules array first
        if (playlist.Schedules && playlist.Schedules.length > 0) {
            schedulesToLoad = playlist.Schedules;
        }
        // Legacy: convert old single schedule fields to schedule objects
        else if (playlist.ScheduleTrigger && playlist.ScheduleTrigger !== 'None') {
            var legacySchedule = { Trigger: playlist.ScheduleTrigger };
            if (playlist.ScheduleTime) {
                legacySchedule.Time = playlist.ScheduleTime;
            }
            if (playlist.ScheduleDayOfWeek !== undefined) {
                legacySchedule.DayOfWeek = playlist.ScheduleDayOfWeek;
            }
            if (playlist.ScheduleDayOfMonth !== undefined) {
                legacySchedule.DayOfMonth = playlist.ScheduleDayOfMonth;
            }
            if (playlist.ScheduleMonth !== undefined) {
                legacySchedule.Month = playlist.ScheduleMonth;
            }
            if (playlist.ScheduleInterval) {
                legacySchedule.Interval = playlist.ScheduleInterval;
            }
            schedulesToLoad = [legacySchedule];
        }
        
        // Add schedule boxes for each schedule (if any exist)
        if (schedulesToLoad.length > 0) {
            schedulesToLoad.forEach(function(schedule) {
                SmartLists.addScheduleBox(page, schedule);
            });
        }
        
        // Re-add the "Add Schedule" button
        var addBtn = document.createElement('button');
        addBtn.type = 'button';
        addBtn.className = 'emby-button raised add-schedule-btn';
        addBtn.textContent = schedulesToLoad.length > 0 ? '+ Add Another Schedule' : '+ Add Schedule';
        addBtn.addEventListener('click', function() {
            SmartLists.addScheduleBox(page, null);
        });
        schedulesContainer.appendChild(addBtn);
    };
    
    // Helper function to apply default schedule from config (DRY)
    SmartLists.applyDefaultScheduleFromConfig = function(page, config) {
        // Apply default schedule if configured
        if (config.DefaultScheduleTrigger && config.DefaultScheduleTrigger !== '') {
            const defaultSchedule = {
                Trigger: config.DefaultScheduleTrigger
            };
            
            // Add schedule-specific fields based on trigger type
            if (config.DefaultScheduleTrigger === 'Daily') {
                if (config.DefaultScheduleTime) {
                    const timeParts = config.DefaultScheduleTime.split(':');
                    if (timeParts.length >= 2) {
                        defaultSchedule.Time = timeParts[0] + ':' + timeParts[1] + ':00';
                    }
                }
            } else if (config.DefaultScheduleTrigger === 'Weekly') {
                if (config.DefaultScheduleDayOfWeek !== undefined) {
                    defaultSchedule.DayOfWeek = config.DefaultScheduleDayOfWeek;
                }
                if (config.DefaultScheduleTime) {
                    const timeParts = config.DefaultScheduleTime.split(':');
                    if (timeParts.length >= 2) {
                        defaultSchedule.Time = timeParts[0] + ':' + timeParts[1] + ':00';
                    }
                }
            } else if (config.DefaultScheduleTrigger === 'Monthly') {
                if (config.DefaultScheduleDayOfMonth !== undefined) {
                    defaultSchedule.DayOfMonth = config.DefaultScheduleDayOfMonth;
                }
                if (config.DefaultScheduleTime) {
                    const timeParts = config.DefaultScheduleTime.split(':');
                    if (timeParts.length >= 2) {
                        defaultSchedule.Time = timeParts[0] + ':' + timeParts[1] + ':00';
                    }
                }
            } else if (config.DefaultScheduleTrigger === 'Yearly') {
                if (config.DefaultScheduleMonth !== undefined) {
                    defaultSchedule.Month = config.DefaultScheduleMonth;
                }
                if (config.DefaultScheduleDayOfMonth !== undefined) {
                    defaultSchedule.DayOfMonth = config.DefaultScheduleDayOfMonth;
                }
                if (config.DefaultScheduleTime) {
                    const timeParts = config.DefaultScheduleTime.split(':');
                    if (timeParts.length >= 2) {
                        defaultSchedule.Time = timeParts[0] + ':' + timeParts[1] + ':00';
                    }
                }
            } else if (config.DefaultScheduleTrigger === 'Interval') {
                if (config.DefaultScheduleInterval) {
                    defaultSchedule.Interval = config.DefaultScheduleInterval;
                }
            }
            
            // Add the default schedule box
            if (SmartLists.addScheduleBox) {
                SmartLists.addScheduleBox(page, defaultSchedule);
            }
        }
    };
    
    // ============================================
    // Visibility Schedule Functions
    // ============================================
    
    SmartLists.initializeVisibilityScheduleSystem = function(page) {
        const schedulesContainer = page.querySelector('#visibility-schedules-container');
        if (!schedulesContainer) return;
        
        // Clear any existing content
        schedulesContainer.innerHTML = '';
        
        // Add the "Add Visibility Schedule" button
        const addBtn = document.createElement('button');
        addBtn.type = 'button';
        addBtn.className = 'emby-button raised add-visibility-schedule-btn';
        addBtn.textContent = '+ Add Visibility Schedule';
        addBtn.addEventListener('click', function() {
            SmartLists.addVisibilityScheduleBox(page, null);
        });
        schedulesContainer.appendChild(addBtn);
    };
    
    SmartLists.createVisibilityScheduleBox = function(page, scheduleData) {
        const scheduleId = 'visibility-schedule-' + Date.now() + '-' + Math.random();

        // Create box container with inline styles and paperList class for theme-aware background
        const box = SmartLists.createStyledElement('div', 'schedule-box paperList', SmartLists.STYLES.scheduleBox);
        box.setAttribute('data-schedule-id', scheduleId);
        
        // Create fields container with inline styles
        const fieldsContainer = SmartLists.createStyledElement('div', 'schedule-fields', SmartLists.STYLES.scheduleFields);
        
        // Action field (Enable/Disable)
        const actionField = SmartLists.createScheduleField('Action', 'visibility-action-' + scheduleId, 'select');
        SmartLists.populateSelectElement(actionField.input, [
            { value: 'Enable', label: 'Enable list', selected: (!scheduleData || scheduleData.Action === 'Enable') },
            { value: 'Disable', label: 'Disable list', selected: (scheduleData && scheduleData.Action === 'Disable') }
        ]);
        fieldsContainer.appendChild(actionField.container);
        
        // Trigger field
        const triggerField = SmartLists.createScheduleField('Trigger', 'visibility-trigger-' + scheduleId, 'select');
        SmartLists.populateSelectElement(triggerField.input, SmartLists.generateScheduleTriggerOptions(scheduleData ? scheduleData.Trigger : '', false));
        fieldsContainer.appendChild(triggerField.container);
        
        // Month field (for Yearly)
        const monthField = SmartLists.createScheduleField('Month', 'visibility-month-' + scheduleId, 'select');
        SmartLists.populateSelectElement(monthField.input, SmartLists.generateMonthOptions(scheduleData && scheduleData.Month ? scheduleData.Month.toString() : '1'));
        monthField.container.style.display = 'none';
        fieldsContainer.appendChild(monthField.container);
        
        // Day of Month field (for Monthly/Yearly)
        const dayOfMonthField = SmartLists.createScheduleField('Day of Month', 'visibility-dayofmonth-' + scheduleId, 'select');
        SmartLists.populateSelectElement(dayOfMonthField.input, SmartLists.generateDayOfMonthOptions(scheduleData && scheduleData.DayOfMonth ? scheduleData.DayOfMonth.toString() : '1'));
        dayOfMonthField.container.style.display = 'none';
        fieldsContainer.appendChild(dayOfMonthField.container);
        
        // Day of Week field (for Weekly)
        const dayOfWeekField = SmartLists.createScheduleField('Weekday', 'visibility-dayofweek-' + scheduleId, 'select');
        SmartLists.populateSelectElement(dayOfWeekField.input, SmartLists.generateDayOfWeekOptions(scheduleData && scheduleData.DayOfWeek !== undefined ? scheduleData.DayOfWeek.toString() : '0'));
        dayOfWeekField.container.style.display = 'none';
        fieldsContainer.appendChild(dayOfWeekField.container);
        
        // Time field (for Daily/Weekly/Monthly/Yearly)
        const timeField = SmartLists.createScheduleField('Time', 'visibility-time-' + scheduleId, 'select');
        SmartLists.populateSelectElement(timeField.input, SmartLists.generateTimeOptions(scheduleData && scheduleData.Time ? scheduleData.Time : '00:00:00'));
        timeField.container.style.display = 'none';
        fieldsContainer.appendChild(timeField.container);
        
        // Interval field (for Interval)
        const intervalField = SmartLists.createScheduleField('Interval', 'visibility-interval-' + scheduleId, 'select');
        SmartLists.populateSelectElement(intervalField.input, SmartLists.generateIntervalOptions(scheduleData && scheduleData.Interval ? scheduleData.Interval : '1.00:00:00'));
        intervalField.container.style.display = 'none';
        fieldsContainer.appendChild(intervalField.container);
        
        // Helper function to show/hide visibility schedule fields based on trigger
        var toggleVisibilityFields = function(trigger) {
            // Hide all conditional fields first
            monthField.container.style.display = 'none';
            dayOfMonthField.container.style.display = 'none';
            dayOfWeekField.container.style.display = 'none';
            timeField.container.style.display = 'none';
            intervalField.container.style.display = 'none';
            
            // Show fields based on trigger type
            if (trigger === 'Daily') {
                timeField.container.style.display = '';
            } else if (trigger === 'Weekly') {
                dayOfWeekField.container.style.display = '';
                timeField.container.style.display = '';
            } else if (trigger === 'Monthly') {
                dayOfMonthField.container.style.display = '';
                timeField.container.style.display = '';
            } else if (trigger === 'Yearly') {
                monthField.container.style.display = '';
                dayOfMonthField.container.style.display = '';
                timeField.container.style.display = '';
            } else if (trigger === 'Interval') {
                intervalField.container.style.display = '';
            }
        };
        
        // Create remove button (X icon at end of fields row) - matches refresh schedule styling
        const removeBtn = SmartLists.createStyledElement('button', 'visibility-schedule-remove-btn', SmartLists.STYLES.scheduleRemoveBtn);
        removeBtn.type = 'button';
        removeBtn.textContent = '×';
        removeBtn.title = 'Remove visibility schedule';
        removeBtn.addEventListener('click', function() {
            SmartLists.removeVisibilityScheduleBox(page, box);
        });
        fieldsContainer.appendChild(removeBtn);
        
        box.appendChild(fieldsContainer);
        
        // Add event listener to trigger select to show/hide fields
        triggerField.input.addEventListener('change', function() {
            toggleVisibilityFields(this.value);
        });
        
        // Set initial visibility based on actual selected value in dropdown
        var initialTrigger = triggerField.input.value;
        if (initialTrigger) {
            toggleVisibilityFields(initialTrigger);
        }
        
        return box;
    };
    
    SmartLists.addVisibilityScheduleBox = function(page, scheduleData) {
        const schedulesContainer = page.querySelector('#visibility-schedules-container');
        if (!schedulesContainer) return;
        
        // Find the add button (it's always the last child)
        const addBtn = schedulesContainer.querySelector('.add-visibility-schedule-btn');
        
        // Create and insert the new box before the add button
        const newBox = SmartLists.createVisibilityScheduleBox(page, scheduleData);
        if (addBtn) {
            schedulesContainer.insertBefore(newBox, addBtn);
            // Change button text after first schedule is added
            addBtn.textContent = '+ Add Another Visibility Schedule';
        } else {
            schedulesContainer.appendChild(newBox);
        }
    };
    
    SmartLists.removeVisibilityScheduleBox = function(page, box) {
        const schedulesContainer = page.querySelector('#visibility-schedules-container');
        if (!schedulesContainer) return;
        
        box.remove();
        
        // Update button text if no schedules left
        const boxes = schedulesContainer.querySelectorAll('.schedule-box');
        const addBtn = schedulesContainer.querySelector('.add-visibility-schedule-btn');
        if (boxes.length === 0 && addBtn) {
            addBtn.textContent = '+ Add Visibility Schedule';
        }
    };
    
    SmartLists.collectVisibilitySchedulesFromForm = function(page) {
        const schedulesContainer = page.querySelector('#visibility-schedules-container');
        if (!schedulesContainer) return [];
        
        const boxes = schedulesContainer.querySelectorAll('.schedule-box');
        const schedules = [];
        
        boxes.forEach(function(box) {
            const actionSelect = box.querySelector('[id^="visibility-action-"]');
            const triggerSelect = box.querySelector('[id^="visibility-trigger-"]');
            if (!actionSelect || !triggerSelect) return;
            
            const action = actionSelect.value;
            const trigger = triggerSelect.value;
            if (!action || !trigger) return; // Skip incomplete schedules
            
            const schedule = {
                Action: action,
                Trigger: trigger
            };
            
            // Collect fields based on trigger type
            if (trigger === 'Daily') {
                const timeSelect = box.querySelector('[id^="visibility-time-"]');
                if (timeSelect && timeSelect.value) {
                    schedule.Time = timeSelect.value + ':00';
                }
            } else if (trigger === 'Weekly') {
                const dayOfWeekSelect = box.querySelector('[id^="visibility-dayofweek-"]');
                const timeSelect = box.querySelector('[id^="visibility-time-"]');
                if (dayOfWeekSelect && dayOfWeekSelect.value !== '') {
                    schedule.DayOfWeek = parseInt(dayOfWeekSelect.value, 10);
                }
                if (timeSelect && timeSelect.value) {
                    schedule.Time = timeSelect.value + ':00';
                }
            } else if (trigger === 'Monthly') {
                const dayOfMonthSelect = box.querySelector('[id^="visibility-dayofmonth-"]');
                const timeSelect = box.querySelector('[id^="visibility-time-"]');
                if (dayOfMonthSelect && dayOfMonthSelect.value) {
                    schedule.DayOfMonth = parseInt(dayOfMonthSelect.value, 10);
                }
                if (timeSelect && timeSelect.value) {
                    schedule.Time = timeSelect.value + ':00';
                }
            } else if (trigger === 'Yearly') {
                const monthSelect = box.querySelector('[id^="visibility-month-"]');
                const dayOfMonthSelect = box.querySelector('[id^="visibility-dayofmonth-"]');
                const timeSelect = box.querySelector('[id^="visibility-time-"]');
                if (monthSelect && monthSelect.value) {
                    schedule.Month = parseInt(monthSelect.value, 10);
                }
                if (dayOfMonthSelect && dayOfMonthSelect.value) {
                    schedule.DayOfMonth = parseInt(dayOfMonthSelect.value, 10);
                }
                if (timeSelect && timeSelect.value) {
                    schedule.Time = timeSelect.value + ':00';
                }
            } else if (trigger === 'Interval') {
                const intervalSelect = box.querySelector('[id^="visibility-interval-"]');
                if (intervalSelect && intervalSelect.value) {
                    schedule.Interval = intervalSelect.value;
                }
            }
            
            schedules.push(schedule);
        });
        
        return schedules;
    };
    
    SmartLists.loadVisibilitySchedulesIntoUI = function(page, list) {
        const schedulesContainer = page.querySelector('#visibility-schedules-container');
        if (!schedulesContainer) return;
        
        schedulesContainer.innerHTML = '';
        
        var schedulesToLoad = list.VisibilitySchedules || [];
        
        // Add schedule boxes for each schedule (if any exist)
        if (schedulesToLoad.length > 0) {
            schedulesToLoad.forEach(function(schedule) {
                SmartLists.addVisibilityScheduleBox(page, schedule);
            });
        }
        
        // Re-add the "Add Visibility Schedule" button
        var addBtn = document.createElement('button');
        addBtn.type = 'button';
        addBtn.className = 'emby-button raised add-visibility-schedule-btn';
        addBtn.textContent = schedulesToLoad.length > 0 ? '+ Add Another Visibility Schedule' : '+ Add Visibility Schedule';
        addBtn.addEventListener('click', function() {
            SmartLists.addVisibilityScheduleBox(page, null);
        });
        schedulesContainer.appendChild(addBtn);
    };
    
})(window.SmartLists = window.SmartLists || {});


