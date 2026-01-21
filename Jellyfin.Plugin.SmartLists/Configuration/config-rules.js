(function (SmartLists) {
    'use strict';

    // ===== CONSTANTS =====
    SmartLists.PLAYBACK_STATUS_OPTIONS = [
        { Value: 'Played', Label: 'Played (Fully Played)' },
        { Value: 'InProgress', Label: 'In Progress (Partially Played)' },
        { Value: 'Unplayed', Label: 'Unplayed (Not Started)' }
    ];

    // ===== OPERATOR OPTIONS MANAGEMENT =====
    SmartLists.updateOperatorOptions = function (fieldValue, operatorSelect) {
        // Capture the previous operator value before clearing
        const previousOperator = operatorSelect.value;

        operatorSelect.innerHTML = '<option value="">-- Select Operator --</option>';
        let allowedOperators = [];

        // Check if availableFields is loaded - if not, return early (fields should be loaded before this is called)
        if (!SmartLists.availableFields || !SmartLists.availableFields.Operators) {
            return;
        }

        // Use the new field-specific operator mappings from the API
        if (SmartLists.availableFields.FieldOperators && SmartLists.availableFields.FieldOperators[fieldValue]) {
            const allowedOperatorValues = SmartLists.availableFields.FieldOperators[fieldValue];
            allowedOperators = SmartLists.availableFields.Operators.filter(function (op) {
                return allowedOperatorValues.includes(op.Value);
            });
        } else {
            // Fallback to the old logic if FieldOperators is not available
            // Define common operator sets to avoid duplication
            const stringListOperators = ['Contains', 'NotContains', 'IsIn', 'IsNotIn', 'MatchRegex'];
            const stringOperators = ['Equal', 'NotEqual', 'Contains', 'NotContains', 'IsIn', 'IsNotIn', 'MatchRegex'];
            const numericOperators = ['Equal', 'NotEqual', 'GreaterThan', 'LessThan', 'GreaterThanOrEqual', 'LessThanOrEqual'];
            const booleanOperators = ['Equal', 'NotEqual'];

            if (SmartLists.FIELD_TYPES.LIST_FIELDS.indexOf(fieldValue) !== -1) {
                allowedOperators = SmartLists.availableFields.Operators.filter(function (op) {
                    return stringListOperators.indexOf(op.Value) !== -1;
                });
            } else if (SmartLists.FIELD_TYPES.NUMERIC_FIELDS.indexOf(fieldValue) !== -1) {
                // Numeric fields should NOT include date-specific operators
                allowedOperators = SmartLists.availableFields.Operators.filter(function (op) {
                    return numericOperators.indexOf(op.Value) !== -1;
                });
            } else if (SmartLists.FIELD_TYPES.DATE_FIELDS.indexOf(fieldValue) !== -1) {
                // Date fields: exclude string operators and numeric-specific operators, include date-specific operators
                allowedOperators = SmartLists.availableFields.Operators.filter(function (op) {
                    return stringListOperators.indexOf(op.Value) === -1 &&
                        numericOperators.indexOf(op.Value) === -1;
                });
            } else if (SmartLists.FIELD_TYPES.BOOLEAN_FIELDS.indexOf(fieldValue) !== -1 ||
                SmartLists.FIELD_TYPES.SIMPLE_FIELDS.indexOf(fieldValue) !== -1) {
                allowedOperators = SmartLists.availableFields.Operators.filter(function (op) {
                    return booleanOperators.indexOf(op.Value) !== -1;
                });
            } else { // Default to string fields
                allowedOperators = SmartLists.availableFields.Operators.filter(function (op) {
                    return stringOperators.indexOf(op.Value) !== -1;
                });
            }
        }

        allowedOperators.forEach(function (opt) {
            const option = document.createElement('option');
            option.value = opt.Value;
            option.textContent = opt.Label;
            operatorSelect.appendChild(option);
        });

        // Restore previous operator if it's still valid, otherwise set default
        if (previousOperator && allowedOperators.some(function (op) { return op.Value === previousOperator; })) {
            operatorSelect.value = previousOperator;
        } else if (fieldValue === 'ItemType' || SmartLists.FIELD_TYPES.BOOLEAN_FIELDS.indexOf(fieldValue) !== -1) {
            operatorSelect.value = 'Equal';
        } else {
            operatorSelect.value = '';
        }
    };

    // ===== VALUE INPUT MANAGEMENT =====
    SmartLists.setValueInput = function (fieldValue, valueContainer, operatorValue, explicitCurrentValue) {
        // Store the current value before clearing the container
        // For relative date operators, we need to capture both number and unit
        let currentValue = explicitCurrentValue;

        if (!currentValue) {
            // Check if this is a multi-value operator
            if (SmartLists.MULTI_VALUE_OPERATORS.indexOf(operatorValue) !== -1) {
                // For multi-value fields, get the value from the hidden input directly
                const hiddenInput = valueContainer.querySelector('input[type="hidden"].rule-value-input');
                if (hiddenInput) {
                    currentValue = hiddenInput.value;
                }
            } else {
                const currentValueInput = valueContainer.querySelector('.rule-value-input');
                const currentUnitSelect = valueContainer.querySelector('.rule-value-unit');

                if (currentValueInput) {
                    if (currentUnitSelect && currentUnitSelect.value) {
                        // This is a relative date input, combine number:unit format
                        currentValue = currentValueInput.value + ':' + currentUnitSelect.value;
                    } else {
                        // Regular input, just use the value
                        currentValue = currentValueInput.value;
                    }
                }
            }
        }

        valueContainer.innerHTML = '';

        // Check if this is an IsIn/IsNotIn operator to use tag-based input
        const ruleRow = valueContainer.closest('.rule-row');
        const operatorSelect = ruleRow ? ruleRow.querySelector('.rule-operator-select') : null;
        const currentOperator = operatorValue || (operatorSelect ? operatorSelect.value : '');
        const isMultiValueOperator = SmartLists.MULTI_VALUE_OPERATORS.indexOf(currentOperator) !== -1;

        if (isMultiValueOperator) {
            // Create tag-based input for IsIn/IsNotIn operators
            SmartLists.createTagBasedInput(valueContainer, currentValue);
        } else if (SmartLists.FIELD_TYPES.SIMPLE_FIELDS.indexOf(fieldValue) !== -1) {
            SmartLists.handleSimpleFieldInput(valueContainer, currentValue);
        } else if (fieldValue === 'PlaybackStatus') {
            SmartLists.handlePlaybackStatusFieldInput(valueContainer, currentValue);
        } else if (SmartLists.FIELD_TYPES.BOOLEAN_FIELDS.indexOf(fieldValue) !== -1) {
            SmartLists.handleBooleanFieldInput(valueContainer, fieldValue, currentValue);
        } else if (SmartLists.FIELD_TYPES.NUMERIC_FIELDS.indexOf(fieldValue) !== -1) {
            SmartLists.handleNumericFieldInput(valueContainer, fieldValue, currentValue);
        } else if (SmartLists.FIELD_TYPES.DATE_FIELDS.indexOf(fieldValue) !== -1) {
            SmartLists.handleDateFieldInput(valueContainer, currentOperator, currentValue);
        } else if (SmartLists.FIELD_TYPES.RESOLUTION_FIELDS.indexOf(fieldValue) !== -1) {
            SmartLists.handleResolutionFieldInput(valueContainer, currentValue);
        } else {
            SmartLists.handleTextFieldInput(valueContainer, currentValue);
        }

        // Restore the current value if it exists and is valid for the new field type
        SmartLists.restoreFieldValue(valueContainer, fieldValue, currentOperator, currentValue, isMultiValueOperator);
    };

    SmartLists.handleSimpleFieldInput = function (valueContainer, currentValue) {
        const select = document.createElement('select');
        select.className = 'emby-select rule-value-input';
        select.setAttribute('is', 'emby-select');
        select.style.width = '100%';
        SmartLists.mediaTypes.forEach(function (opt) {
            const option = document.createElement('option');
            option.value = opt.Value;
            option.textContent = opt.Label;
            if (currentValue && opt.Value === currentValue) {
                option.selected = true;
            }
            select.appendChild(option);
        });
        valueContainer.appendChild(select);
    };

    SmartLists.handleBooleanFieldInput = function (valueContainer, fieldValue, currentValue) {
        const select = document.createElement('select');
        select.className = 'emby-select rule-value-input';
        select.setAttribute('is', 'emby-select');
        select.style.width = '100%';
        let boolOptions;
        if (fieldValue === 'IsFavorite') {
            boolOptions = [{ Value: 'true', Label: 'Yes (Favorite)' }, { Value: 'false', Label: 'No (Not Favorite)' }];
        } else if (fieldValue === 'NextUnwatched') {
            boolOptions = [{ Value: 'true', Label: 'Yes (Next to Watch)' }, { Value: 'false', Label: 'No (Not Next)' }];
        } else {
            boolOptions = [{ Value: 'true', Label: 'Yes' }, { Value: 'false', Label: 'No' }];
        }
        boolOptions.forEach(function (opt) {
            const option = document.createElement('option');
            option.value = opt.Value;
            option.textContent = opt.Label;
            if (currentValue && opt.Value === currentValue) {
                option.selected = true;
            }
            select.appendChild(option);
        });
        valueContainer.appendChild(select);
    };

    SmartLists.handlePlaybackStatusFieldInput = function (valueContainer, currentValue) {
        const select = document.createElement('select');
        select.className = 'emby-select rule-value-input';
        select.setAttribute('is', 'emby-select');
        select.style.width = '100%';

        const statusOptions = SmartLists.PLAYBACK_STATUS_OPTIONS;

        statusOptions.forEach(function (opt) {
            const option = document.createElement('option');
            option.value = opt.Value;
            option.textContent = opt.Label;
            if (currentValue && opt.Value === currentValue) {
                option.selected = true;
            }
            select.appendChild(option);
        });

        valueContainer.appendChild(select);
    };

    SmartLists.handleNumericFieldInput = function (valueContainer, fieldValue, currentValue) {
        const input = document.createElement('input');
        input.type = 'number';
        input.className = 'emby-input rule-value-input';
        input.placeholder = 'Value';
        input.style.width = '100%';

        // Set appropriate step for decimal fields like Framerate
        if (fieldValue === 'Framerate' || fieldValue === 'CommunityRating' || fieldValue === 'CriticRating') {
            input.step = 'any'; // Allow any decimal precision
        } else {
            input.step = '1'; // Integer fields like ProductionYear, RuntimeMinutes, PlayCount
        }

        if (currentValue) {
            input.value = currentValue;
        }
        valueContainer.appendChild(input);
    };

    SmartLists.handleDateFieldInput = function (valueContainer, currentOperator, currentValue) {
        const isRelativeDateOperator = SmartLists.RELATIVE_DATE_OPERATORS.indexOf(currentOperator) !== -1;
        const isWeekdayOperator = currentOperator === 'Weekday';

        if (isRelativeDateOperator) {
            SmartLists.handleRelativeDateInput(valueContainer, currentValue);
        } else if (isWeekdayOperator) {
            SmartLists.handleWeekdayInput(valueContainer, currentValue);
        } else {
            SmartLists.handleAbsoluteDateInput(valueContainer, currentValue);
        }
    };

    SmartLists.handleRelativeDateInput = function (valueContainer) {
        const inputContainer = document.createElement('div');
        inputContainer.style.display = 'flex';
        inputContainer.style.gap = '0.5em';
        inputContainer.style.alignItems = 'center';
        valueContainer.appendChild(inputContainer);

        const input = document.createElement('input');
        input.type = 'number';
        input.className = 'emby-input rule-value-input';
        input.placeholder = 'Number';
        input.min = '0';
        input.style.flex = '0 0 43%';
        inputContainer.appendChild(input);

        const unitSelect = document.createElement('select');
        unitSelect.className = 'emby-select rule-value-unit';
        unitSelect.setAttribute('is', 'emby-select');
        unitSelect.style.flex = '0 0 55%';

        // Add placeholder option
        const placeholderOption = document.createElement('option');
        placeholderOption.value = '';
        placeholderOption.textContent = '-- Select Unit --';
        placeholderOption.disabled = true;
        placeholderOption.selected = true;
        unitSelect.appendChild(placeholderOption);

        [
            { value: 'hours', label: 'Hour(s)' },
            { value: 'days', label: 'Day(s)' },
            { value: 'weeks', label: 'Week(s)' },
            { value: 'months', label: 'Month(s)' },
            { value: 'years', label: 'Year(s)' }
        ].forEach(function (opt) {
            const option = document.createElement('option');
            option.value = opt.value;
            option.textContent = opt.label;
            unitSelect.appendChild(option);
        });
        inputContainer.appendChild(unitSelect);
    };

    SmartLists.handleAbsoluteDateInput = function (valueContainer) {
        const input = document.createElement('input');
        input.type = 'date';
        input.className = 'emby-input rule-value-input';
        input.style.width = '100%';
        valueContainer.appendChild(input);
    };

    SmartLists.handleWeekdayInput = function (valueContainer, currentValue) {
        const select = document.createElement('select');
        select.className = 'emby-select rule-value-input';
        select.setAttribute('is', 'emby-select');
        select.style.width = '100%';

        // Use existing generateDayOfWeekOptions function from config-formatters.js
        const dayOptions = SmartLists.generateDayOfWeekOptions(currentValue);

        dayOptions.forEach(function (opt) {
            const option = document.createElement('option');
            option.value = opt.value;
            option.textContent = opt.label;
            if (opt.selected) {
                option.selected = true;
            }
            select.appendChild(option);
        });

        valueContainer.appendChild(select);
    };

    SmartLists.handleResolutionFieldInput = function (valueContainer, currentValue) {
        const select = document.createElement('select');
        select.className = 'emby-select rule-value-input';
        select.setAttribute('is', 'emby-select');
        select.style.width = '100%';

        // Add placeholder option
        const placeholderOption = document.createElement('option');
        placeholderOption.value = '';
        placeholderOption.textContent = '-- Select Resolution --';
        placeholderOption.disabled = true;
        // Only select placeholder if no currentValue
        if (!currentValue) {
            placeholderOption.selected = true;
        }
        select.appendChild(placeholderOption);

        // Resolution options with display names
        const resolutionOptions = [
            { Value: '480p', Label: '480p (854x480)' },
            { Value: '720p', Label: '720p (1280x720)' },
            { Value: '1080p', Label: '1080p (1920x1080)' },
            { Value: '1440p', Label: '1440p (2560x1440)' },
            { Value: '4K', Label: '4K (3840x2160)' },
            { Value: '8K', Label: '8K (7680x4320)' }
        ];

        // Add resolution options and select if matches currentValue
        resolutionOptions.forEach(function (opt) {
            const option = document.createElement('option');
            option.value = opt.Value;
            option.textContent = opt.Label;
            if (currentValue && opt.Value === currentValue) {
                option.selected = true;
            }
            select.appendChild(option);
        });

        valueContainer.appendChild(select);
    };

    SmartLists.handleTextFieldInput = function (valueContainer, currentValue) {
        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'emby-input rule-value-input';
        input.placeholder = 'Value';
        input.style.width = '100%';
        if (currentValue) {
            input.value = currentValue;
        }
        valueContainer.appendChild(input);
    };

    // ===== VALUE RESTORATION =====
    SmartLists.restoreFieldValue = function (valueContainer, fieldValue, currentOperator, currentValue, isMultiValueOperator) {
        const newValueInput = valueContainer.querySelector('.rule-value-input');
        if (newValueInput && currentValue) {
            // Store the original value as a data attribute for potential restoration
            newValueInput.setAttribute('data-original-value', currentValue);

            // Try to restore the value if it's appropriate for the new field type
            if (SmartLists.FIELD_TYPES.SIMPLE_FIELDS.indexOf(fieldValue) !== -1 ||
                SmartLists.FIELD_TYPES.BOOLEAN_FIELDS.indexOf(fieldValue) !== -1) {
                SmartLists.restoreSelectValue(newValueInput, currentValue);
            } else if (SmartLists.FIELD_TYPES.DATE_FIELDS.indexOf(fieldValue) !== -1) {
                SmartLists.restoreDateValue(valueContainer, currentOperator, currentValue, newValueInput);
            } else if (isMultiValueOperator) {
                SmartLists.restoreMultiValueInput(valueContainer, currentValue);
            } else {
                // For inputs, restore the value directly
                // If switching from multi-value operators, use first tag as fallback
                if (currentValue && currentValue.indexOf(';') !== -1) {
                    const tags = currentValue.split(';').map(function (tag) {
                        return tag.trim();
                    }).filter(function (tag) {
                        return tag.length > 0;
                    });
                    newValueInput.value = tags[0] || '';
                } else {
                    newValueInput.value = currentValue;
                }
            }
        }
    };

    SmartLists.restoreSelectValue = function (selectElement, currentValue) {
        if (selectElement.tagName === 'SELECT') {
            const option = Array.from(selectElement.options).find(function (opt) {
                return opt.value === currentValue;
            });
            if (option) {
                selectElement.value = currentValue;
            }
        }
    };

    SmartLists.restoreDateValue = function (valueContainer, currentOperator, currentValue, newValueInput) {
        const isRelativeDateOperator = SmartLists.RELATIVE_DATE_OPERATORS.indexOf(currentOperator) !== -1;
        const isWeekdayOperator = currentOperator === 'Weekday';

        if (isRelativeDateOperator) {
            SmartLists.restoreRelativeDateValue(valueContainer, currentValue, newValueInput);
        } else if (isWeekdayOperator) {
            // For weekday, restore the select value via the shared helper
            if (newValueInput.tagName === 'SELECT') {
                SmartLists.restoreSelectValue(newValueInput, currentValue);
            }
        } else {
            // For regular date operators, restore the date value directly
            if (newValueInput.tagName === 'INPUT') {
                newValueInput.value = currentValue;
            }
        }
    };

    SmartLists.restoreRelativeDateValue = function (valueContainer, currentValue, newValueInput) {
        // Parse number:unit format for relative date operators
        const parts = currentValue.split(':');
        const validUnits = ['hours', 'days', 'weeks', 'months', 'years'];
        const num = parts[0];
        const unit = parts[1];
        const isValidNum = /^\d+$/.test(num) && parseInt(num, 10) >= 0;
        const isValidUnit = validUnits.indexOf(unit) !== -1;

        if (parts.length === 2 && isValidNum && isValidUnit) {
            // Set the number input
            if (newValueInput.tagName === 'INPUT') {
                newValueInput.value = num;
            }
            // Set the unit dropdown
            const unitSelect = valueContainer.querySelector('.rule-value-unit');
            if (unitSelect) {
                unitSelect.value = unit;
            }
        } else {
            // Log a warning if the value is malformed
            console.warn('Malformed relative date value: \'' + currentValue + '\'. Expected format: <number>:<unit> (e.g., \'3:months\'). Parts:', parts, 'isValidNum: ' + isValidNum, 'isValidUnit: ' + isValidUnit);
        }
    };

    SmartLists.restoreMultiValueInput = function (valueContainer, currentValue) {
        // For tag-based inputs, restore the semicolon-separated values as individual tags
        if (currentValue) {
            // Clear existing tags first to prevent duplicates and ensure UI consistency
            const existingTags = valueContainer.querySelectorAll('.tag-item');
            existingTags.forEach(function (tag) {
                tag.remove();
            });

            const tags = currentValue.split(';').map(function (tag) {
                return tag.trim();
            }).filter(function (tag) {
                return tag.length > 0;
            });
            tags.forEach(function (tag) {
                SmartLists.addTagToContainer(valueContainer, tag);
            });
        }
    };

    // ===== TAG-BASED INPUT MANAGEMENT =====
    SmartLists.createTagBasedInput = function (valueContainer, currentValue) {
        // Create the main container with EXACT same styling as standard Jellyfin inputs
        const tagContainer = document.createElement('div');
        tagContainer.className = 'tag-input-container emby-input';
        tagContainer.style.cssText = 'width: 100%; border: none; border-radius: 0; padding: 0.55em 0.5em; display: flex; flex-wrap: wrap; gap: 0.5em; align-items: center; box-sizing: border-box; align-content: flex-start;';

        // Create the input field with standard Jellyfin styling
        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'tag-input-field';
        input.placeholder = 'Type a value and press Enter';
        input.style.cssText = 'border: none; background: transparent; color: inherit; flex: 1; min-width: 200px; outline: none; font-family: inherit; font-size: inherit; padding: 0; margin: 0;';
        input.setAttribute('data-input-type', 'tag-input');

        // Use page-level ::placeholder styling (see config.html)
        input.style.setProperty('color-scheme', 'dark');

        // Create the hidden input that will store the semicolon-separated values for the backend
        const hiddenInput = document.createElement('input');
        hiddenInput.type = 'hidden';
        hiddenInput.className = 'rule-value-input';
        hiddenInput.setAttribute('data-input-type', 'hidden-tag-input');

        // Add elements to container
        tagContainer.appendChild(input);
        valueContainer.appendChild(tagContainer);
        valueContainer.appendChild(hiddenInput);

        // Add event listeners
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                const value = input.value.trim();
                if (value) {
                    SmartLists.addTagToContainer(valueContainer, value);
                    input.value = '';
                    SmartLists.updateHiddenInput(valueContainer);
                }
            } else if (e.key === 'Backspace' && input.value === '') {
                // Remove last tag when backspace is pressed on empty input
                e.preventDefault();
                SmartLists.removeLastTag(valueContainer);
            }
        });

        input.addEventListener('input', function () {
            const value = input.value.trim();

            // Check if value contains semicolon (for pasting multiple values)
            if (value && value.indexOf(';') !== -1) {
                const parts = value.split(';');
                parts.forEach(function (part) {
                    const trimmedPart = part.trim();
                    if (trimmedPart) {
                        SmartLists.addTagToContainer(valueContainer, trimmedPart);
                    }
                });
                input.value = '';
                SmartLists.updateHiddenInput(valueContainer);
            }
        });

        // Restore existing tags if any
        if (currentValue) {
            const tags = currentValue.split(';').map(function (tag) {
                return tag.trim();
            }).filter(function (tag) {
                return tag.length > 0;
            });
            tags.forEach(function (tag) {
                SmartLists.addTagToContainer(valueContainer, tag);
            });
        }

        // Initial update of hidden input
        SmartLists.updateHiddenInput(valueContainer);
    };

    SmartLists.addTagToContainer = function (valueContainer, tagText) {
        const tagContainer = valueContainer.querySelector('.tag-input-container');
        if (!tagContainer) return;

        // Check if tag already exists to prevent duplicates (case-insensitive)
        const existingTags = Array.from(tagContainer.querySelectorAll('.tag-item span'))
            .map(function (span) {
                return span.textContent.toLowerCase();
            });
        if (existingTags.indexOf(tagText.toLowerCase()) !== -1) {
            return; // Tag already exists, don't add duplicate
        }

        // Create tag element with subtle Jellyfin styling
        const tag = document.createElement('div');
        tag.className = 'tag-item';
        tag.style.cssText = 'background: var(--jf-palette-background-paper); padding: 0.3em 0.6em; border-radius: 2px; font-size: 0.85em; display: inline-flex; align-items: center; gap: 0.5em; max-width: none; flex: 0 0 auto; border: 1px solid var(--jf-palette-divider); white-space: nowrap; overflow: hidden;';

        // Tag text
        const tagTextSpan = document.createElement('span');
        tagTextSpan.textContent = tagText;
        tagTextSpan.style.cssText = 'overflow: hidden; text-overflow: ellipsis; white-space: nowrap;';

        // Remove button
        const removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.innerHTML = '×';
        removeBtn.style.cssText = 'background: none; border: none; color: var(--jf-palette-text-secondary); cursor: pointer; font-size: 1.2em; font-weight: bold; padding: 0; line-height: 1; width: 1.2em; height: 1.2em; display: flex; align-items: center; justify-content: center; border-radius: 50%; transition: background-color 0.2s ease;';

        removeBtn.addEventListener('mouseenter', function () {
            this.style.backgroundColor = 'var(--jf-palette-action-hover)';
        });

        removeBtn.addEventListener('mouseleave', function () {
            this.style.backgroundColor = 'transparent';
        });

        removeBtn.addEventListener('click', function () {
            tag.remove();
            SmartLists.updateHiddenInput(valueContainer);
        });

        // Assemble tag
        tag.appendChild(tagTextSpan);
        tag.appendChild(removeBtn);

        // Insert before the input field
        const input = tagContainer.querySelector('.tag-input-field');
        tagContainer.insertBefore(tag, input);

        // Update hidden input
        SmartLists.updateHiddenInput(valueContainer);
    };

    SmartLists.removeTagFromContainer = function (tagElement) {
        tagElement.remove();
        const valueContainer = tagElement.closest('.rule-value-container');
        if (valueContainer) {
            SmartLists.updateHiddenInput(valueContainer);
        }
    };

    SmartLists.removeLastTag = function (valueContainer) {
        const tagContainer = valueContainer.querySelector('.tag-input-container');
        if (!tagContainer) return;

        const tags = tagContainer.querySelectorAll('.tag-item');
        if (tags.length > 0) {
            const lastTag = tags[tags.length - 1];
            lastTag.remove();
            SmartLists.updateHiddenInput(valueContainer);
        }
    };

    SmartLists.updateHiddenInput = function (valueContainer) {
        const tagContainer = valueContainer.querySelector('.tag-input-container');
        const hiddenInput = valueContainer.querySelector('input[type="hidden"].rule-value-input');

        if (!tagContainer || !hiddenInput) return;

        const tags = Array.from(tagContainer.querySelectorAll('.tag-item span'))
            .map(function (span) {
                return span.textContent.trim();
            })
            .filter(function (tag) {
                return tag.length > 0;
            });

        hiddenInput.value = tags.join(';');
    };

    // ===== REGEX HELP =====
    SmartLists.updateRegexHelp = function (ruleGroup) {
        const operatorSelect = ruleGroup.querySelector('.rule-operator-select');
        const existingHelp = ruleGroup.querySelector('.regex-help');
        if (existingHelp) existingHelp.remove();

        if (operatorSelect && operatorSelect.value === 'MatchRegex') {
            const helpDiv = document.createElement('div');
            helpDiv.className = 'regex-help field-description';
            helpDiv.style.cssText = 'margin-top: 0.5em; margin-bottom: 0.5em; font-size: 0.85em; opacity: 0.7; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); padding: 0.5em; border-radius: 1px;';
            // Use safe HTML creation instead of innerHTML for security
            helpDiv.innerHTML = '';

            // Create help content safely
            const strongRegexHelp = document.createElement('strong');
            strongRegexHelp.textContent = 'Regex Help:';
            helpDiv.appendChild(strongRegexHelp);

            helpDiv.appendChild(document.createTextNode(' Use .NET syntax. Examples: '));

            const code1 = document.createElement('code');
            code1.textContent = '(?i)swe';
            helpDiv.appendChild(code1);
            helpDiv.appendChild(document.createTextNode(' (case-insensitive), '));

            const code2 = document.createElement('code');
            code2.textContent = '(?i)(eng|en)';
            helpDiv.appendChild(code2);
            helpDiv.appendChild(document.createTextNode(' (multiple options), '));

            const code3 = document.createElement('code');
            code3.textContent = '^Action';
            helpDiv.appendChild(code3);
            helpDiv.appendChild(document.createTextNode(' (starts with). Do not use JavaScript-style /pattern/flags.'));

            helpDiv.appendChild(document.createElement('br'));

            const strongTestPatterns = document.createElement('strong');
            strongTestPatterns.textContent = 'Test patterns:';
            helpDiv.appendChild(strongTestPatterns);
            helpDiv.appendChild(document.createTextNode(' '));

            const regexLink = document.createElement('a');
            regexLink.href = 'https://regex101.com/?flavor=dotnet';
            regexLink.target = '_blank';
            regexLink.className = 'emby-button-link';
            regexLink.textContent = 'Regex101.com (.NET flavor)';
            helpDiv.appendChild(regexLink);
            ruleGroup.appendChild(helpDiv);
        }
    };

    // ===== LOGIC GROUP MANAGEMENT =====
    SmartLists.createLogicGroupHeader = function () {
        const headerDiv = SmartLists.createStyledElement('div', 'logic-group-header', SmartLists.STYLES.logicGroupHeader);

        const cloneBtn = document.createElement('button');
        cloneBtn.type = 'button';
        cloneBtn.className = 'rule-action-btn clone-btn clone-group-btn';
        cloneBtn.title = 'Clone OR Block';
        cloneBtn.innerHTML = '<span class="material-icons" style="font-size: 1em; height: 31px; display: flex; justify-content: center; align-items: center;">content_copy</span>';

        // Style the clone button using the styling system
        SmartLists.styleRuleActionButton(cloneBtn, 'clone');

        const deleteBtn = document.createElement('button');
        deleteBtn.type = 'button';
        deleteBtn.className = 'rule-action-btn delete-btn delete-group-btn';
        deleteBtn.title = 'Remove OR Block';
        deleteBtn.textContent = '×';

        // Style the delete button
        SmartLists.styleRuleActionButton(deleteBtn, 'delete');

        headerDiv.appendChild(cloneBtn);
        headerDiv.appendChild(deleteBtn);

        return headerDiv;
    };

    SmartLists.createGroupMaxItemsField = function () {
        const container = document.createElement('div');
        container.className = 'group-max-items-container';
        container.style.cssText = 'margin: 10px 0 5px 0; padding: 8px; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;';

        const label = document.createElement('label');
        label.textContent = 'Max Items for this OR block: ';
        label.style.cssText = 'font-size: 0.9em; opacity: 0.6; margin-right: 8px;';

        const input = document.createElement('input');
        input.type = 'number';
        input.className = 'group-max-items-input emby-input';
        input.placeholder = 'Unlimited';
        input.min = '0';
        input.style.cssText = 'width: 120px; display: inline-block;';

        const infoLink = document.createElement('a');
        infoLink.href = 'https://jellyfin-smartlists-plugin.dinsten.se/user-guide/sorting-and-limits/#per-group-max-items';
        infoLink.target = '_blank';
        infoLink.rel = 'noopener noreferrer';
        infoLink.title = 'Documentation';
        infoLink.style.cssText = 'text-decoration: none; color: inherit; opacity: 0.6; display: inline-flex; align-items: center; margin-left: 8px;';

        const infoIcon = document.createElement('span');
        infoIcon.className = 'material-icons';
        infoIcon.setAttribute('aria-hidden', 'true');
        infoIcon.textContent = 'info_outline';
        infoIcon.style.cssText = 'font-size: 1.1em; line-height: 0;';

        infoLink.appendChild(infoIcon);

        container.appendChild(label);
        container.appendChild(input);
        container.appendChild(infoLink);

        return container;
    };

    SmartLists.createInitialLogicGroup = function (page) {
        const rulesContainer = page.querySelector('#rules-container');
        const logicGroupId = 'logic-group-' + Date.now();

        // Create logic group with paperList class for theme-aware background
        const logicGroupDiv = SmartLists.createStyledElement('div', 'logic-group paperList', SmartLists.STYLES.logicGroup);
        logicGroupDiv.setAttribute('data-group-id', logicGroupId);

        rulesContainer.appendChild(logicGroupDiv);

        // Add header with clone button
        const header = SmartLists.createLogicGroupHeader();
        logicGroupDiv.appendChild(header);

        // Add the first rule to this group
        SmartLists.addRuleToGroup(page, logicGroupDiv);

        // Add MaxItems field for this group
        const maxItemsField = SmartLists.createGroupMaxItemsField();
        logicGroupDiv.appendChild(maxItemsField);

        return logicGroupDiv;
    };

    SmartLists.addRuleToGroup = function (page, logicGroup) {
        const existingRules = logicGroup.querySelectorAll('.rule-row');

        // Add AND separator if this isn't the first rule in the group
        if (existingRules.length > 0) {
            const andSeparator = SmartLists.createAndSeparator();
            
            // Insert before MaxItems field if it exists, otherwise append
            const maxItemsContainer = logicGroup.querySelector('.group-max-items-container');
            if (maxItemsContainer) {
                logicGroup.insertBefore(andSeparator, maxItemsContainer);
            } else {
                logicGroup.appendChild(andSeparator);
            }
        }

        const ruleDiv = document.createElement('div');
        ruleDiv.className = 'rule-row';
        ruleDiv.setAttribute('data-rule-id', 'rule-' + Date.now());

        // Create AbortController for this rule's event listeners
        const abortController = SmartLists.createAbortController();
        const signal = abortController.signal;

        // Store the controller on the element for cleanup
        ruleDiv._abortController = abortController;

        const fieldsHtml =
            '<div class="input-group" style="display: flex; gap: 0.5em; align-items: center; margin-bottom: 1em;">' +
            '<select is="emby-select" class="emby-select rule-field-select" style="flex: 0 0 25%;">' +
            '<option value="">-- Select Field --</option>' +
            '</select>' +
            '<select is="emby-select" class="emby-select rule-operator-select" style="flex: 0 0 20%;">' +
            '<option value="">-- Select Operator --</option>' +
            '</select>' +
            '<span class="rule-value-container" style="flex: 1;">' +
            '<input type="text" class="emby-input rule-value-input" placeholder="Value" style="width: 100%;">' +
            '</span>' +
            '<div class="rule-actions" style="display: flex; gap: 4px; align-items: center;">' +
            '<button type="button" class="rule-action-btn and-btn" title="Add AND rule">AND</button>' +
            '<button type="button" class="rule-action-btn or-btn" title="Add OR group">OR</button>' +
            '<button type="button" class="rule-action-btn clone-btn clone-rule-btn" title="Clone rule"><span class="material-icons" style="font-size: 1.2em; height: 31px; display: flex; justify-content: center; align-items: center;">content_copy</span></button>' +
            '<button type="button" class="rule-action-btn delete-btn" title="Remove rule">×</button>' +
            '</div>' +
            '</div>' +
            '<div class="rule-user-selector" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Check for specific user (optional):' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-user-select" style="width: 100%;">' +
            '<option value="">Default (list user)</option>' +
            '</select>' +
            '</div>' +
            '<div class="rule-nextunwatched-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Include unwatched series:' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-nextunwatched-select" style="width: 100%;">' +
            '<option value="true">Yes - Include first episodes of unwatched series</option>' +
            '<option value="false">No - Only show next episodes from started series</option>' +
            '</select>' +
            '</div>' +
            '<div class="rule-collections-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<div class="rule-collections-collection-only" style="margin-bottom: 0.75em;">' +
            '<label style="display: flex; align-items: center; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Include collections only:' +
            '<a href="https://jellyfin-smartlists-plugin.dinsten.se/user-guide/fields-and-operators/#collection-name-options" target="_blank" rel="noopener noreferrer" title="Documentation" style="margin-left: 0.5em; text-decoration: none; color: inherit; display: inline-flex; align-items: center;">' +
            '<span class="material-icons" aria-hidden="true" style="font-size: 1.1em; line-height: 0;">info_outline</span>' +
            '</a>' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-collections-collection-only-select" style="width: 100%;">' +
            '<option value="false">No - Include media items from the collections</option>' +
            '<option value="true">Yes - Only include the actual collections</option>' +
            '</select>' +
            '</div>' +
            '<div class="rule-collections-episodes" style="margin-bottom: 0.75em;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Include episodes within series:' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-collections-select" style="width: 100%;">' +
            '<option value="false">No - Only include the series themselves</option>' +
            '<option value="true">Yes - Include individual episodes from series in collections</option>' +
            '</select>' +
            '</div>' +
            '<div class="rule-collections-depth" style="margin-bottom: 0;">' +
            '<label style="display: flex; align-items: center; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Collection search depth:' +
            '<a href="https://jellyfin-smartlists-plugin.dinsten.se/user-guide/fields-and-operators/#collection-search-depth" target="_blank" rel="noopener noreferrer" title="Documentation" style="margin-left: 0.5em; text-decoration: none; color: inherit; display: inline-flex; align-items: center;">' +
            '<span class="material-icons" aria-hidden="true" style="font-size: 1.1em; line-height: 0;">info_outline</span>' +
            '</a>' +
            '</label>' +
            '<input type="number" class="emby-input rule-collections-depth-input" min="0" max="10" step="1" value="0" style="width: 100%;">' +
            '<div style="font-size: 0.75em; opacity: 0.7; margin-top: 0.25em;">How deep to traverse nested collections (0 = direct members only)</div>' +
            '</div>' +
            '</div>' +
            '<div class="rule-playlists-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<div class="rule-playlists-playlist-only" style="margin-bottom: 0.75em;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Include playlist only:' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-playlists-playlist-only-select" style="width: 100%;">' +
            '<option value="false">No - Include media items from the playlist</option>' +
            '<option value="true">Yes - Only include the playlist itself</option>' +
            '</select>' +
            '</div>' +
            '</div>' +
            '<div class="rule-tags-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Include parent series tags:' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-tags-select" style="width: 100%;">' +
            '<option value="false">No - Only check episode tags</option>' +
            '<option value="true">Yes - Also check tags from parent series</option>' +
            '</select>' +
            '</div>' +
            '<div class="rule-studios-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Include parent series studios:' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-studios-select" style="width: 100%;">' +
            '<option value="false">No - Only check episode studios</option>' +
            '<option value="true">Yes - Also check studios from parent series</option>' +
            '</select>' +
            '</div>' +
            '<div class="rule-genres-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Include parent series genres:' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-genres-select" style="width: 100%;">' +
            '<option value="false">No - Only check episode genres</option>' +
            '<option value="true">Yes - Also check genres from parent series</option>' +
            '</select>' +
            '</div>' +
            '<div class="rule-audiolanguages-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Must be the default language:' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-audiolanguages-select" style="width: 100%;">' +
            '<option value="false">No - Match any audio language</option>' +
            '<option value="true">Yes - Only match default audio language</option>' +
            '</select>' +
            '</div>' +
            '<div class="rule-similarity-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<label style="display: block; margin-bottom: 0.5em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Compare using these metadata fields (default: Genre + Tags):' +
            '</label>' +
            '<div class="similarity-fields-container" style="display: flex; flex-wrap: wrap; gap: 0.5em;">' +
            '<!-- Options will be populated dynamically -->' +
            '</div>' +
            '</div>' +
            '<div class="rule-people-options" style="display: none; margin-bottom: 0.75em; padding: 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 4px;">' +
            '<label style="display: block; margin-bottom: 0.25em; font-size: 0.85em; opacity: 0.8; font-weight: 500;">' +
            'Select person type:' +
            '</label>' +
            '<select is="emby-select" class="emby-select rule-people-select" style="width: 100%;">' +
            '<!-- Options will be populated dynamically -->' +
            '</select>' +
            '</div>';

        ruleDiv.innerHTML = fieldsHtml;
        
        // Insert before MaxItems field if it exists, otherwise append
        const maxItemsContainer = logicGroup.querySelector('.group-max-items-container');
        if (maxItemsContainer) {
            logicGroup.insertBefore(ruleDiv, maxItemsContainer);
        } else {
            logicGroup.appendChild(ruleDiv);
        }

        // Use the ruleDiv we just created instead of querying for it
        const newRuleRow = ruleDiv;
        const fieldSelect = newRuleRow.querySelector('.rule-field-select');
        const operatorSelect = newRuleRow.querySelector('.rule-operator-select');
        const valueContainer = newRuleRow.querySelector('.rule-value-container');

        if (SmartLists.availableFields.ContentFields) {
            SmartLists.populateFieldSelect(fieldSelect, SmartLists.availableFields, null, page);
        }
        if (SmartLists.availableFields.Operators) {
            SmartLists.populateSelect(operatorSelect, SmartLists.availableFields.Operators, null, false);
        }

        SmartLists.setValueInput(fieldSelect.value, valueContainer, operatorSelect.value);
        SmartLists.updateOperatorOptions(fieldSelect.value, operatorSelect);

        // Initialize user selector visibility and load users
        SmartLists.updateUserSelectorVisibility(newRuleRow, fieldSelect.value);
        const userSelect = newRuleRow.querySelector('.rule-user-select');
        if (userSelect) {
            SmartLists.loadUsersForRule(userSelect, true);
        }

        // Initialize NextUnwatched options visibility
        SmartLists.updateNextUnwatchedOptionsVisibility(newRuleRow, fieldSelect.value, page);

        // Initialize Collections options visibility
        SmartLists.updateCollectionsOptionsVisibility(newRuleRow, fieldSelect.value, page);

        // Initialize Playlists options visibility
        SmartLists.updatePlaylistsOptionsVisibility(newRuleRow, fieldSelect.value, page);

        // Initialize Tags options visibility
        SmartLists.updateTagsOptionsVisibility(newRuleRow, fieldSelect.value, page);

        // Initialize Studios options visibility
        SmartLists.updateStudiosOptionsVisibility(newRuleRow, fieldSelect.value, page);

        // Initialize Genres options visibility
        SmartLists.updateGenresOptionsVisibility(newRuleRow, fieldSelect.value, page);

        // Initialize AudioLanguages options visibility
        SmartLists.updateAudioLanguagesOptionsVisibility(newRuleRow, fieldSelect.value, page);

        // Initialize Similarity options visibility
        SmartLists.updateSimilarityOptionsVisibility(newRuleRow, fieldSelect.value);

        // Add event listeners with AbortController signal (if supported)
        const listenerOptions = SmartLists.getEventListenerOptions(signal);
        fieldSelect.addEventListener('change', function () {
            SmartLists.setValueInput(fieldSelect.value, valueContainer, operatorSelect.value);
            SmartLists.updateOperatorOptions(fieldSelect.value, operatorSelect);
            SmartLists.updateUserSelectorVisibility(newRuleRow, fieldSelect.value);
            SmartLists.updateNextUnwatchedOptionsVisibility(newRuleRow, fieldSelect.value, page);
            SmartLists.updateCollectionsOptionsVisibility(newRuleRow, fieldSelect.value, page);
            SmartLists.updatePlaylistsOptionsVisibility(newRuleRow, fieldSelect.value, page);
            SmartLists.updateTagsOptionsVisibility(newRuleRow, fieldSelect.value, page);
            SmartLists.updateStudiosOptionsVisibility(newRuleRow, fieldSelect.value, page);
            SmartLists.updateGenresOptionsVisibility(newRuleRow, fieldSelect.value, page);
            SmartLists.updateAudioLanguagesOptionsVisibility(newRuleRow, fieldSelect.value, page);
            SmartLists.updateSimilarityOptionsVisibility(newRuleRow, fieldSelect.value);
            SmartLists.updatePeopleOptionsVisibility(newRuleRow, fieldSelect.value);
            SmartLists.updateRegexHelp(newRuleRow);
            // Update sort options when Similar To rule is added/removed
            SmartLists.updateAllSortOptionsVisibility(page);
        }, listenerOptions);

        operatorSelect.addEventListener('change', function () {
            SmartLists.updateRegexHelp(newRuleRow);
            // Always re-render the value input on operator change for consistency
            // setValueInput is idempotent and cheap, so this simplifies maintenance
            const fieldValue = fieldSelect.value;
            SmartLists.setValueInput(fieldValue, valueContainer, this.value);
        }, listenerOptions);

        // Add event listener for collection-only select to hide/show episodes field
        const collectionOnlySelect = newRuleRow.querySelector('.rule-collections-collection-only-select');
        if (collectionOnlySelect) {
            collectionOnlySelect.addEventListener('change', function () {
                // Use the centralized visibility function to ensure consistency
                const fieldSelect = newRuleRow.querySelector('.rule-field-select');
                const fieldValue = fieldSelect ? fieldSelect.value : '';
                SmartLists.updateCollectionsOptionsVisibility(newRuleRow, fieldValue, page);
                // Update sort "Use Child Values" checkbox visibility when collection-only option changes
                if (SmartLists.updateUseChildValuesVisibility) {
                    SmartLists.updateUseChildValuesVisibility(page);
                }
            }, listenerOptions);
        }

        // Add change listener for Playlists playlist-only option
        const playlistOnlySelect = newRuleRow.querySelector('.rule-playlists-playlist-only-select');
        if (playlistOnlySelect) {
            playlistOnlySelect.addEventListener('change', function () {
                // Use the centralized visibility function to ensure consistency
                const fieldSelect = newRuleRow.querySelector('.rule-field-select');
                const fieldValue = fieldSelect ? fieldSelect.value : '';
                SmartLists.updatePlaylistsOptionsVisibility(newRuleRow, fieldValue, page);
                // Update sort "Use Child Values" checkbox visibility when playlist-only option changes
                if (SmartLists.updateUseChildValuesVisibility) {
                    SmartLists.updateUseChildValuesVisibility(page);
                }
            }, listenerOptions);
        }

        // Style the action buttons
        const actionButtons = newRuleRow.querySelectorAll('.rule-action-btn');
        actionButtons.forEach(function (button) {
            let buttonType;
            if (button.classList.contains('and-btn')) buttonType = 'and';
            else if (button.classList.contains('or-btn')) buttonType = 'or';
            else if (button.classList.contains('clone-btn')) buttonType = 'clone';
            else if (button.classList.contains('delete-btn')) buttonType = 'delete';

            if (buttonType) {
                // Apply base styles
                SmartLists.styleRuleActionButton(button, buttonType);
            }
        });

        // All action buttons (And/Or/Delete) are handled by delegated listeners in config-init.js
        // No need to attach direct listeners here

        // Update button visibility for all rules in all groups
        SmartLists.updateRuleButtonVisibility(page);
    };

    SmartLists.addNewLogicGroup = function (page) {
        const rulesContainer = page.querySelector('#rules-container');

        // Add OR separator between groups
        const orSeparator = SmartLists.createOrSeparator();
        rulesContainer.appendChild(orSeparator);

        // Create new logic group with paperList class for theme-aware background
        const logicGroupId = 'logic-group-' + Date.now();
        const logicGroupDiv = SmartLists.createStyledElement('div', 'logic-group paperList', SmartLists.STYLES.logicGroup);
        logicGroupDiv.setAttribute('data-group-id', logicGroupId);

        rulesContainer.appendChild(logicGroupDiv);

        // Add header with clone button
        const header = SmartLists.createLogicGroupHeader();
        logicGroupDiv.appendChild(header);

        // Add the first rule to this group
        SmartLists.addRuleToGroup(page, logicGroupDiv);

        // Add MaxItems field for this group
        const maxItemsField = SmartLists.createGroupMaxItemsField();
        logicGroupDiv.appendChild(maxItemsField);

        return logicGroupDiv;
    };

    SmartLists.removeRule = function (page, ruleElement) {
        const logicGroup = ruleElement.closest('.logic-group');
        const rulesInGroup = logicGroup.querySelectorAll('.rule-row');

        // Clean up event listeners before removing
        SmartLists.cleanupRuleEventListeners(ruleElement);

        if (rulesInGroup.length === 1) {
            // This is the last rule in the group, remove the entire group
            SmartLists.removeLogicGroup(page, logicGroup);
        } else {
            // Remove the rule and any adjacent separator
            const nextSibling = ruleElement.nextElementSibling;
            const prevSibling = ruleElement.previousElementSibling;

            if (nextSibling && nextSibling.classList.contains('rule-within-group-separator')) {
                nextSibling.remove();
            } else if (prevSibling && prevSibling.classList.contains('rule-within-group-separator')) {
                prevSibling.remove();
            }

            ruleElement.remove();
            SmartLists.updateRuleButtonVisibility(page);
        }

        // Update sort options in case a Similar To rule was removed
        SmartLists.updateAllSortOptionsVisibility(page);
    };

    // Extract rule configuration from a rule row element
    // Returns { expression, similarityFields } where expression is the rule expression object
    // and similarityFields is an array of selected similarity fields (or null for defaults)
    SmartLists.extractRuleConfig = function (ruleRow) {
        const fieldSelect = ruleRow.querySelector('.rule-field-select');
        const operatorSelect = ruleRow.querySelector('.rule-operator-select');
        const valueContainer = ruleRow.querySelector('.rule-value-container');

        const fieldValue = fieldSelect ? fieldSelect.value : '';
        const operatorValue = operatorSelect ? operatorSelect.value : '';

        // Extract value - handle different input types
        let targetValue = '';
        const isMultiValueOperator = SmartLists.MULTI_VALUE_OPERATORS.indexOf(operatorValue) !== -1;
        const isRelativeDateOperator = SmartLists.RELATIVE_DATE_OPERATORS.indexOf(operatorValue) !== -1;

        if (isMultiValueOperator) {
            // Tag-based input - get from hidden input
            const hiddenInput = valueContainer.querySelector('input[type="hidden"].rule-value-input');
            targetValue = hiddenInput ? hiddenInput.value : '';
        } else if (isRelativeDateOperator) {
            // Relative date - combine number:unit format
            const valueInput = valueContainer.querySelector('.rule-value-input');
            const unitSelect = valueContainer.querySelector('.rule-value-unit');
            if (valueInput && unitSelect && valueInput.value && unitSelect.value) {
                targetValue = valueInput.value + ':' + unitSelect.value;
            } else if (valueInput) {
                targetValue = valueInput.value;
            }
        } else {
            // Regular input or select
            const valueInput = valueContainer.querySelector('.rule-value-input');
            targetValue = valueInput ? valueInput.value : '';
        }

        // Extract user selector value
        const userSelect = ruleRow.querySelector('.rule-user-select');
        const userId = userSelect ? userSelect.value : '';

        // Extract NextUnwatched options
        const nextUnwatchedSelect = ruleRow.querySelector('.rule-nextunwatched-select');
        const nextUnwatchedValue = nextUnwatchedSelect ? nextUnwatchedSelect.value : '';

        // Extract Collections options
        const collectionOnlySelect = ruleRow.querySelector('.rule-collections-collection-only-select');
        const collectionOnlyValue = collectionOnlySelect ? collectionOnlySelect.value : '';
        const collectionsSelect = ruleRow.querySelector('.rule-collections-select');
        const collectionsValue = collectionsSelect ? collectionsSelect.value : '';

        // Extract Playlists options
        const playlistOnlySelect = ruleRow.querySelector('.rule-playlists-playlist-only-select');
        const playlistOnlyValue = playlistOnlySelect ? playlistOnlySelect.value : '';

        // Extract Tags, Studios, Genres, AudioLanguages options
        const tagsSelect = ruleRow.querySelector('.rule-tags-select');
        const tagsValue = tagsSelect ? tagsSelect.value : '';
        const studiosSelect = ruleRow.querySelector('.rule-studios-select');
        const studiosValue = studiosSelect ? studiosSelect.value : '';
        const genresSelect = ruleRow.querySelector('.rule-genres-select');
        const genresValue = genresSelect ? genresSelect.value : '';
        const audioLanguagesSelect = ruleRow.querySelector('.rule-audiolanguages-select');
        const audioLanguagesValue = audioLanguagesSelect ? audioLanguagesSelect.value : '';

        // Extract Similarity options (checkboxes)
        const similarityOptionsDiv = ruleRow.querySelector('.rule-similarity-options');
        let similarityFields = null;
        if (similarityOptionsDiv && similarityOptionsDiv.style.display !== 'none') {
            const checkboxes = similarityOptionsDiv.querySelectorAll('.similarity-field-checkbox:checked');
            const selectedFields = Array.from(checkboxes).map(function (cb) {
                return cb.value;
            });
            // Store the selected fields (even if defaults, so we can restore the checkboxes correctly)
            // The backend will use defaults if Genre + Tags, but we need to restore the UI state
            if (selectedFields.length > 0) {
                similarityFields = selectedFields;
            }
            // If no fields selected, similarityFields remains null (will use defaults in populateSimilarityFields)
        }

        // Extract People options
        const peopleSelect = ruleRow.querySelector('.rule-people-select');
        const peopleValue = peopleSelect ? peopleSelect.value : '';

        // Determine actual member name (for people sub-fields)
        let actualMemberName = fieldValue;
        if (fieldValue === 'People' && peopleValue) {
            actualMemberName = peopleValue;
        }

        // Build expression object
        const expression = {
            MemberName: actualMemberName,
            Operator: operatorValue,
            TargetValue: targetValue
        };

        // Add optional fields
        if (userId) {
            expression.UserId = userId;
        }
        if (fieldValue === 'NextUnwatched' && nextUnwatchedValue === 'false') {
            expression.IncludeUnwatchedSeries = false;
        }
        if (fieldValue === 'Collections') {
            if (collectionOnlyValue === 'true') {
                expression.IncludeCollectionOnly = true;
            }
            if (collectionsValue === 'true') {
                expression.IncludeEpisodesWithinSeries = true;
            }
        }
        if (fieldValue === 'Playlists') {
            if (playlistOnlyValue === 'true') {
                expression.IncludePlaylistOnly = true;
            }
        }
        if (fieldValue === 'Tags' && tagsValue === 'true') {
            expression.IncludeParentSeriesTags = true;
        }
        if (fieldValue === 'Studios' && studiosValue === 'true') {
            expression.IncludeParentSeriesStudios = true;
        }
        if (fieldValue === 'Genres' && genresValue === 'true') {
            expression.IncludeParentSeriesGenres = true;
        }
        if (fieldValue === 'AudioLanguages' && audioLanguagesValue === 'true') {
            expression.OnlyDefaultAudioLanguage = true;
        }

        return {
            expression: expression,
            similarityFields: similarityFields
        };
    };

    SmartLists.cloneRule = function (page, ruleRow) {
        const logicGroup = ruleRow.closest('.logic-group');
        if (!logicGroup) return;

        // Extract rule configuration using shared helper
        const ruleConfig = SmartLists.extractRuleConfig(ruleRow);

        // Create a new rule using addRuleToGroup
        // Clone to the bottom of the group
        SmartLists.addRuleToGroup(page, logicGroup);
        // Use querySelectorAll to get the last rule row, since :last-child doesn't work
        // when the MaxItems container is appended after the rule rows
        const ruleRows = logicGroup.querySelectorAll('.rule-row');
        const newRuleRow = ruleRows[ruleRows.length - 1];

        // Store similarity fields on page for populateRuleRow to access
        // Store even if null (for defaults) so we know to show the options div
        page._cloningPlaylistSimilarityFields = ruleConfig.similarityFields;

        // Populate the new rule with extracted data
        SmartLists.populateRuleRow(newRuleRow, ruleConfig.expression, page);

        // Clear the cloning flag
        page._cloningPlaylistSimilarityFields = null;

        // Update button visibility
        SmartLists.updateRuleButtonVisibility(page);

        // Update sort options in case a Similar To rule was added
        SmartLists.updateAllSortOptionsVisibility(page);
    };

    SmartLists.cloneLogicGroup = function (page, logicGroup) {
        const rulesContainer = page.querySelector('#rules-container');
        if (!rulesContainer) return;

        // Extract all rules from the source logic group
        const sourceRules = logicGroup.querySelectorAll('.rule-row');
        const ruleConfigs = [];

        // Extract configuration for each rule using shared helper
        sourceRules.forEach(function (ruleRow) {
            const ruleConfig = SmartLists.extractRuleConfig(ruleRow);
            ruleConfigs.push(ruleConfig);
        });

        // Extract MaxItems value from source group
        const sourceMaxItemsInput = logicGroup.querySelector('.group-max-items-input');
        const sourceMaxItems = sourceMaxItemsInput ? sourceMaxItemsInput.value : '';

        // Create a new logic group with OR separator
        const orSeparator = SmartLists.createOrSeparator();
        rulesContainer.appendChild(orSeparator);

        // Create cloned logic group with paperList class for theme-aware background
        const logicGroupId = 'logic-group-' + Date.now();
        const newLogicGroupDiv = SmartLists.createStyledElement('div', 'logic-group paperList', SmartLists.STYLES.logicGroup);
        newLogicGroupDiv.setAttribute('data-group-id', logicGroupId);
        rulesContainer.appendChild(newLogicGroupDiv);

        // Add header with clone button
        const header = SmartLists.createLogicGroupHeader();
        newLogicGroupDiv.appendChild(header);

        // Recreate each rule in the new group
        ruleConfigs.forEach(function (ruleConfig, index) {
            // Store similarity fields on page for populateRuleRow
            // Store even if null (for defaults) so we know to show the options div
            page._cloningPlaylistSimilarityFields = ruleConfig.similarityFields;

            // Add rule to group
            SmartLists.addRuleToGroup(page, newLogicGroupDiv);
            // Use querySelectorAll to get the last rule row, since :last-child doesn't work
            // when the MaxItems container is appended after the rule rows
            const ruleRowsInGroup = newLogicGroupDiv.querySelectorAll('.rule-row');
            const newRuleRow = ruleRowsInGroup[ruleRowsInGroup.length - 1];

            // Populate with extracted data
            SmartLists.populateRuleRow(newRuleRow, ruleConfig.expression, page);

            // Clear cloning flag
            page._cloningPlaylistSimilarityFields = null;
        });

        // Add MaxItems field for this group and set the cloned value
        const maxItemsField = SmartLists.createGroupMaxItemsField();
        newLogicGroupDiv.appendChild(maxItemsField);
        const newMaxItemsInput = maxItemsField.querySelector('.group-max-items-input');
        if (newMaxItemsInput && sourceMaxItems) {
            newMaxItemsInput.value = sourceMaxItems;
        }

        // Update button visibility
        SmartLists.updateRuleButtonVisibility(page);

        // Update sort options in case Similar To rules were added
        SmartLists.updateAllSortOptionsVisibility(page);
    };

    SmartLists.cleanupRuleEventListeners = function (ruleElement) {
        // Abort all event listeners for this rule
        if (ruleElement._abortController) {
            ruleElement._abortController.abort();
            ruleElement._abortController = null;
        }
    };

    SmartLists.removeLogicGroup = function (page, logicGroup) {
        const rulesContainer = page.querySelector('#rules-container');
        const allGroups = rulesContainer.querySelectorAll('.logic-group');

        // Clean up all event listeners in this group
        const rulesInGroup = logicGroup.querySelectorAll('.rule-row');
        rulesInGroup.forEach(function (rule) {
            SmartLists.cleanupRuleEventListeners(rule);
        });

        if (allGroups.length === 1) {
            // This is the last group, clear it and add a new rule
            logicGroup.innerHTML = '';

            // Re-add the header which was wiped out
            const header = SmartLists.createLogicGroupHeader();
            logicGroup.appendChild(header);

            SmartLists.addRuleToGroup(page, logicGroup);
            
            // Re-add the MaxItems field which was wiped out
            const maxItemsField = SmartLists.createGroupMaxItemsField();
            logicGroup.appendChild(maxItemsField);
        } else {
            // Remove the group and any adjacent separator
            const nextSibling = logicGroup.nextElementSibling;
            const prevSibling = logicGroup.previousElementSibling;

            if (prevSibling && prevSibling.classList.contains('logic-group-separator')) {
                prevSibling.remove();
            } else if (nextSibling && nextSibling.classList.contains('logic-group-separator')) {
                nextSibling.remove();
            }

            logicGroup.remove();
            SmartLists.updateRuleButtonVisibility(page);
        }

        // Update sort options in case a Similar To rule was removed
        SmartLists.updateAllSortOptionsVisibility(page);
    };

    SmartLists.updateRuleButtonVisibility = function (page) {
        const rulesContainer = page.querySelector('#rules-container');
        const allLogicGroups = rulesContainer.querySelectorAll('.logic-group');

        allLogicGroups.forEach(function (group) {
            const rulesInGroup = group.querySelectorAll('.rule-row');

            rulesInGroup.forEach(function (rule, index) {
                const andBtn = rule.querySelector('.and-btn');
                const orBtn = rule.querySelector('.or-btn');
                const cloneBtn = rule.querySelector('.clone-btn');
                const deleteBtn = rule.querySelector('.delete-btn');

                // Hide AND and OR buttons if this is not the last rule in the group
                if (index < rulesInGroup.length - 1) {
                    andBtn.style.display = 'none';
                    orBtn.style.display = 'none';
                } else {
                    andBtn.style.display = 'inline-flex';
                    orBtn.style.display = 'inline-flex';
                }

                // Always show CLONE and DELETE buttons
                if (cloneBtn) cloneBtn.style.display = 'inline-flex';
                deleteBtn.style.display = 'inline-flex';
            });
        });
    };

    SmartLists.reinitializeExistingRules = function (page) {
        // Clean up existing event listeners for all rules
        const allRules = page.querySelectorAll('.rule-row');
        allRules.forEach(function (rule) {
            SmartLists.cleanupRuleEventListeners(rule);
        });

        // Re-initialize each rule with proper event listeners
        allRules.forEach(function (ruleRow) {
            const fieldSelect = ruleRow.querySelector('.rule-field-select');
            const operatorSelect = ruleRow.querySelector('.rule-operator-select');
            const valueContainer = ruleRow.querySelector('.rule-value-container');

            if (fieldSelect && operatorSelect && valueContainer) {
                // Create new AbortController for this rule
                const abortController = SmartLists.createAbortController();
                const signal = abortController.signal;

                // Store the controller on the element for cleanup
                ruleRow._abortController = abortController;

                // Re-populate field options if needed
                if (SmartLists.availableFields.ContentFields && fieldSelect.children.length <= 1) {
                    SmartLists.populateFieldSelect(fieldSelect, SmartLists.availableFields, fieldSelect.value, page);
                }

                // Re-populate operator options if needed
                if (SmartLists.availableFields.Operators && operatorSelect.children.length <= 1) {
                    SmartLists.populateSelect(operatorSelect, SmartLists.availableFields.Operators, operatorSelect.value, false);
                }

                // Re-set value input based on current field value
                const currentFieldValue = fieldSelect.value;
                if (currentFieldValue) {
                    SmartLists.setValueInput(currentFieldValue, valueContainer, operatorSelect.value);
                    SmartLists.updateOperatorOptions(currentFieldValue, operatorSelect);
                    SmartLists.updateUserSelectorVisibility(ruleRow, currentFieldValue);
                    SmartLists.updateNextUnwatchedOptionsVisibility(ruleRow, currentFieldValue, page);
                    SmartLists.updateCollectionsOptionsVisibility(ruleRow, currentFieldValue, page);
                    SmartLists.updatePlaylistsOptionsVisibility(ruleRow, currentFieldValue, page);
                    SmartLists.updateTagsOptionsVisibility(ruleRow, currentFieldValue, page);
                    SmartLists.updateStudiosOptionsVisibility(ruleRow, currentFieldValue, page);
                    SmartLists.updateGenresOptionsVisibility(ruleRow, currentFieldValue, page);
                    SmartLists.updateAudioLanguagesOptionsVisibility(ruleRow, currentFieldValue, page);
                    SmartLists.updateSimilarityOptionsVisibility(ruleRow, currentFieldValue);
                }

                // Re-add event listeners
                const listenerOptions = SmartLists.getEventListenerOptions(signal);
                fieldSelect.addEventListener('change', function () {
                    SmartLists.setValueInput(fieldSelect.value, valueContainer, operatorSelect.value);
                    SmartLists.updateOperatorOptions(fieldSelect.value, operatorSelect);
                    SmartLists.updateUserSelectorVisibility(ruleRow, fieldSelect.value);
                    SmartLists.updateNextUnwatchedOptionsVisibility(ruleRow, fieldSelect.value, page);
                    SmartLists.updateCollectionsOptionsVisibility(ruleRow, fieldSelect.value, page);
                    SmartLists.updatePlaylistsOptionsVisibility(ruleRow, fieldSelect.value, page);
                    SmartLists.updateTagsOptionsVisibility(ruleRow, fieldSelect.value, page);
                    SmartLists.updateStudiosOptionsVisibility(ruleRow, fieldSelect.value, page);
                    SmartLists.updateGenresOptionsVisibility(ruleRow, fieldSelect.value, page);
                    SmartLists.updateAudioLanguagesOptionsVisibility(ruleRow, fieldSelect.value, page);
                    SmartLists.updateSimilarityOptionsVisibility(ruleRow, fieldSelect.value);
                    SmartLists.updatePeopleOptionsVisibility(ruleRow, fieldSelect.value);
                    SmartLists.updateRegexHelp(ruleRow);
                    // Update sort options when Similar To rule is added/removed
                    SmartLists.updateAllSortOptionsVisibility(page);
                }, listenerOptions);

                operatorSelect.addEventListener('change', function () {
                    SmartLists.updateRegexHelp(ruleRow);
                    // Always re-render the value input on operator change for consistency
                    // setValueInput is idempotent and cheap, so this simplifies maintenance
                    const fieldValue = fieldSelect.value;
                    SmartLists.setValueInput(fieldValue, valueContainer, this.value);
                }, listenerOptions);

                // Add event listener for collection-only select to hide/show episodes field
                const collectionOnlySelect = ruleRow.querySelector('.rule-collections-collection-only-select');
                if (collectionOnlySelect) {
                    collectionOnlySelect.addEventListener('change', function () {
                        // Use the centralized visibility function to ensure consistency
                        const fieldSelect = ruleRow.querySelector('.rule-field-select');
                        const fieldValue = fieldSelect ? fieldSelect.value : '';
                        SmartLists.updateCollectionsOptionsVisibility(ruleRow, fieldValue, page);
                        // Update sort "Use Child Values" checkbox visibility when collection-only option changes
                        if (SmartLists.updateUseChildValuesVisibility) {
                            SmartLists.updateUseChildValuesVisibility(page);
                        }
                    }, listenerOptions);
                }

                // Add event listener for playlist-only select
                const playlistOnlySelect = ruleRow.querySelector('.rule-playlists-playlist-only-select');
                if (playlistOnlySelect) {
                    playlistOnlySelect.addEventListener('change', function () {
                        // Use the centralized visibility function to ensure consistency
                        const fieldSelect = ruleRow.querySelector('.rule-field-select');
                        const fieldValue = fieldSelect ? fieldSelect.value : '';
                        SmartLists.updatePlaylistsOptionsVisibility(ruleRow, fieldValue, page);
                        // Update sort "Use Child Values" checkbox visibility when playlist-only option changes
                        if (SmartLists.updateUseChildValuesVisibility) {
                            SmartLists.updateUseChildValuesVisibility(page);
                        }
                    }, listenerOptions);
                }

                // Re-style action buttons
                const actionButtons = ruleRow.querySelectorAll('.rule-action-btn');
                actionButtons.forEach(function (button) {
                    let buttonType;
                    if (button.classList.contains('and-btn')) buttonType = 'and';
                    else if (button.classList.contains('or-btn')) buttonType = 'or';
                    else if (button.classList.contains('clone-btn')) buttonType = 'clone';
                    else if (button.classList.contains('delete-btn')) buttonType = 'delete';

                    if (buttonType) {
                        // Apply base styles
                        SmartLists.styleRuleActionButton(button, buttonType);

                        // Add hover effects
                        button.addEventListener('mouseenter', function () {
                            SmartLists.styleRuleActionButton(this, buttonType);
                        }, listenerOptions);

                        button.addEventListener('mouseleave', function () {
                            SmartLists.styleRuleActionButton(this, buttonType);
                        }, listenerOptions);
                    }
                });

                // Re-initialize user selector if needed
                const userSelect = ruleRow.querySelector('.rule-user-select');
                if (userSelect && userSelect.children.length <= 1) {
                    SmartLists.loadUsersForRule(userSelect, true);
                }

                // Update regex help if needed
                SmartLists.updateRegexHelp(ruleRow);
            }
        });

        // Update button visibility
        SmartLists.updateRuleButtonVisibility(page);
    };

    // ===== FIELD POPULATION AND VISIBILITY =====
    SmartLists.populateFieldSelect = function (selectElement, fieldGroups, defaultValue, page) {
        if (!selectElement || !fieldGroups) return;

        // Get selected media types for filtering
        const selectedMediaTypes = page ? SmartLists.getSelectedMediaTypes(page) : [];
        const filteredFieldGroups = SmartLists.filterFieldsByMediaType(fieldGroups, selectedMediaTypes);

        // Get the current selected value before clearing
        const currentValue = selectElement.value;

        // Clear existing options
        selectElement.innerHTML = '<option value="">-- Select Field --</option>';

        // Define field group display names and order
        const groupConfig = [
            { key: 'ContentFields', label: 'Content' },
            { key: 'VideoFields', label: 'Video' },
            { key: 'AudioFields', label: 'Audio' },
            { key: 'RatingsPlaybackFields', label: 'Ratings & Playback' },
            { key: 'LibraryFields', label: 'Library' },
            { key: 'FileFields', label: 'File Info' },
            { key: 'PeopleFields', label: 'People' },
            { key: 'CollectionFields', label: 'Collections' }
        ];

        groupConfig.forEach(function (group) {
            const fields = filteredFieldGroups[group.key];
            if (fields && fields.length > 0) {
                const optgroup = document.createElement('optgroup');
                optgroup.label = group.label;

                fields.forEach(function (field) {
                    const option = document.createElement('option');
                    option.value = field.Value;
                    option.textContent = field.Label;

                    // Use defaultValue if provided, otherwise try to restore currentValue
                    const valueToSelect = defaultValue || currentValue;
                    if (valueToSelect && field.Value === valueToSelect) {
                        option.selected = true;
                    }

                    optgroup.appendChild(option);
                });

                selectElement.appendChild(optgroup);
            }
        });
    };

    // Update all field selects across all rules when media types change
    SmartLists.updateAllFieldSelects = function (page) {
        if (!page || !SmartLists.availableFields || Object.keys(SmartLists.availableFields).length === 0) {
            return;
        }

        // Compute selected media types once before the loop for better performance
        const selectedMediaTypes = SmartLists.getSelectedMediaTypes(page);

        const allRuleRows = page.querySelectorAll('.rule-row');
        allRuleRows.forEach(function (ruleRow) {
            const fieldSelect = ruleRow.querySelector('.rule-field-select');
            if (fieldSelect) {
                const currentValue = fieldSelect.value;
                SmartLists.populateFieldSelect(fieldSelect, SmartLists.availableFields, currentValue, page);

                // If the current field is no longer valid, clear it and reset the rule
                if (currentValue && !SmartLists.shouldShowField(currentValue, selectedMediaTypes)) {
                    fieldSelect.value = '';
                    const valueContainer = ruleRow.querySelector('.rule-value-container');
                    if (valueContainer) {
                        valueContainer.innerHTML = '';
                    }
                    const operatorSelect = ruleRow.querySelector('.rule-operator-select');
                    if (operatorSelect) {
                        operatorSelect.innerHTML = '';
                    }

                    // Re-sync field-specific UI after invalidation
                    SmartLists.updateUserSelectorVisibility(ruleRow, '');
                    SmartLists.updateRegexHelp(ruleRow);
                    if (page) {
                        SmartLists.updateNextUnwatchedOptionsVisibility(ruleRow, '', page);
                        SmartLists.updateCollectionsOptionsVisibility(ruleRow, '', page);
                        SmartLists.updatePlaylistsOptionsVisibility(ruleRow, '', page);
                        SmartLists.updateTagsOptionsVisibility(ruleRow, '', page);
                        SmartLists.updateStudiosOptionsVisibility(ruleRow, '', page);
                        SmartLists.updateGenresOptionsVisibility(ruleRow, '', page);
                    }
                    SmartLists.updateSimilarityOptionsVisibility(ruleRow, '');
                    SmartLists.updatePeopleOptionsVisibility(ruleRow, '');
                } else if (currentValue) {
                    // Field is still valid, restore the value
                    fieldSelect.value = currentValue;

                    // Also ensure field-specific UI is aligned with the restored value
                    SmartLists.updateUserSelectorVisibility(ruleRow, currentValue);
                    SmartLists.updateRegexHelp(ruleRow);
                    if (page) {
                        SmartLists.updateNextUnwatchedOptionsVisibility(ruleRow, currentValue, page);
                        SmartLists.updateCollectionsOptionsVisibility(ruleRow, currentValue, page);
                        SmartLists.updateTagsOptionsVisibility(ruleRow, currentValue, page);
                        SmartLists.updateStudiosOptionsVisibility(ruleRow, currentValue, page);
                        SmartLists.updateGenresOptionsVisibility(ruleRow, currentValue, page);
                    }
                    SmartLists.updateSimilarityOptionsVisibility(ruleRow, currentValue);
                    SmartLists.updatePeopleOptionsVisibility(ruleRow, currentValue);
                }
            }
        });
    };

    SmartLists.filterFieldsByMediaType = function (fieldGroups, selectedMediaTypes) {
        if (!selectedMediaTypes || selectedMediaTypes.length === 0) {
            // No filtering when no media types selected - show all fields
            return fieldGroups;
        }

        const filteredGroups = {};

        // Process each field group
        Object.keys(fieldGroups).forEach(function (groupKey) {
            const fields = fieldGroups[groupKey];
            if (fields && Array.isArray(fields)) {
                filteredGroups[groupKey] = fields.filter(function (field) {
                    return SmartLists.shouldShowField(field.Value, selectedMediaTypes);
                });
            } else {
                filteredGroups[groupKey] = fields;
            }
        });

        return filteredGroups;
    };

    // Field visibility definitions based on media types
    SmartLists.shouldShowField = function (fieldValue, selectedMediaTypes) {
        // If no media types selected, show all fields
        if (!selectedMediaTypes || selectedMediaTypes.length === 0) {
            return true;
        }

        const hasEpisode = selectedMediaTypes.indexOf('Episode') !== -1;
        const hasAudio = selectedMediaTypes.indexOf('Audio') !== -1;
        const hasAudioBook = selectedMediaTypes.indexOf('AudioBook') !== -1;
        const hasMusicVideo = selectedMediaTypes.indexOf('MusicVideo') !== -1;

        // Episode-only fields
        if (['SeriesName', 'NextUnwatched'].indexOf(fieldValue) !== -1) {
            return hasEpisode;
        }

        // Audio fields - show when any audio-capable type is selected
        // Audio-capable types: Movie, Episode, Audio, AudioBook, MusicVideo, Video
        // Books don't have audio metadata, Photos don't have audio metadata
        if (SmartLists.AUDIO_FIELD_NAMES.indexOf(fieldValue) !== -1) {
            const hasAudioType = selectedMediaTypes.some(function (type) {
                return SmartLists.AUDIO_CAPABLE_TYPES.indexOf(type) !== -1;
            });
            return hasAudioType;
        }

        // Video fields - show when any video-capable type is selected
        // Video-capable types: Movie, Episode, MusicVideo, Video
        // Audio/AudioBooks don't have video streams, Books/Photos don't have video metadata
        if (SmartLists.VIDEO_FIELD_NAMES.indexOf(fieldValue) !== -1) {
            const hasVideoType = selectedMediaTypes.some(function (type) {
                return SmartLists.VIDEO_CAPABLE_TYPES.indexOf(type) !== -1;
            });
            return hasVideoType;
        }

        // Music-specific fields
        if (['Album', 'Artists', 'AlbumArtists'].indexOf(fieldValue) !== -1) {
            return hasAudio || hasAudioBook || hasMusicVideo;
        }

        // All other fields are universal (Name, ProductionYear, ReleaseDate, etc.)
        return true;
    };

    // ===== FIELD-SPECIFIC OPTIONS VISIBILITY =====
    SmartLists.updateUserSelectorVisibility = function (ruleRow, fieldValue) {
        const isUserDataField = SmartLists.FIELD_TYPES.USER_DATA_FIELDS.indexOf(fieldValue) !== -1;
        const userSelectorDiv = ruleRow.querySelector('.rule-user-selector');

        if (userSelectorDiv) {
            if (isUserDataField) {
                userSelectorDiv.style.display = 'block';
            } else {
                userSelectorDiv.style.display = 'none';
                // Reset to default when hiding
                const userSelect = userSelectorDiv.querySelector('.rule-user-select');
                if (userSelect) {
                    userSelect.value = '';
                }
            }
        }
    };

    // Update user selector visibility for all rules
    SmartLists.updateAllUserSelectorVisibility = function (page) {
        SmartLists.updateAllRules(page, function (ruleRow, fieldValue) {
            SmartLists.updateUserSelectorVisibility(ruleRow, fieldValue);
        });
    };

    SmartLists.updateNextUnwatchedOptionsVisibility = function (ruleRow, fieldValue) {
        const isNextUnwatchedField = fieldValue === 'NextUnwatched';
        const nextUnwatchedOptionsDiv = ruleRow.querySelector('.rule-nextunwatched-options');

        if (nextUnwatchedOptionsDiv) {
            // Show if NextUnwatched field is selected
            // Note: This option is only meaningful for Episode media type, but we show it
            // whenever NextUnwatched is selected so users can configure it
            if (isNextUnwatchedField) {
                nextUnwatchedOptionsDiv.style.display = 'block';
            } else {
                // Hide but preserve user's selection - don't reset value
                nextUnwatchedOptionsDiv.style.display = 'none';
            }
        }
    };

    // Update visibility of NextUnwatched options for all rules when media types change
    SmartLists.updateAllNextUnwatchedOptionsVisibility = function (page) {
        SmartLists.updateAllRules(page, SmartLists.updateNextUnwatchedOptionsVisibility);
    };

    SmartLists.updateCollectionsOptionsVisibility = function (ruleRow, fieldValue, page) {
        const isCollectionsField = fieldValue === 'Collections';
        const collectionsOptionsDiv = ruleRow.querySelector('.rule-collections-options');

        if (collectionsOptionsDiv) {
            if (isCollectionsField) {
                // Get list type to determine visibility
                const listType = page ? SmartLists.getElementValue(page, '#listType', 'Playlist') : 'Playlist';
                const isCollection = listType === 'Collection';

                // Show/hide collection-only option based on list type (only for Collections list type)
                const collectionOnlyDiv = ruleRow.querySelector('.rule-collections-collection-only');
                if (collectionOnlyDiv) {
                    collectionOnlyDiv.style.display = isCollection ? 'block' : 'none';
                }

                // Get collection-only select value
                const collectionOnlySelect = ruleRow.querySelector('.rule-collections-collection-only-select');
                const isCollectionOnly = collectionOnlySelect && collectionOnlySelect.value === 'true';

                // Show/hide episodes option (hidden if collection-only is yes OR Episode media type is not selected)
                const episodesDiv = ruleRow.querySelector('.rule-collections-episodes');
                let episodesVisible = false;
                if (episodesDiv) {
                    // Get selected media types to check if Episode is selected
                    const selectedMediaTypes = page ? SmartLists.getSelectedMediaTypes(page) : [];
                    const hasEpisode = selectedMediaTypes.indexOf('Episode') !== -1;

                    // Show only if collection-only is disabled AND Episode media type is selected
                    episodesVisible = !isCollectionOnly && hasEpisode;
                    episodesDiv.style.display = episodesVisible ? 'block' : 'none';
                }

                // Depth option is always visible when Collection name rule is selected
                // It's useful for both Playlist and Collection list types
                const depthDiv = ruleRow.querySelector('.rule-collections-depth');
                if (depthDiv) {
                    depthDiv.style.display = 'block';
                }

                // Show container if any inner option is visible (depth is always visible for Collections rule)
                collectionsOptionsDiv.style.display = 'block';
            } else {
                // Hide but preserve user's selection - don't reset value
                collectionsOptionsDiv.style.display = 'none';
            }
        }
    };

    // Update visibility of Collections options for all rules when media types change
    SmartLists.updateAllCollectionsOptionsVisibility = function (page) {
        SmartLists.updateAllRules(page, SmartLists.updateCollectionsOptionsVisibility);
    };

    SmartLists.updatePlaylistsOptionsVisibility = function (ruleRow, fieldValue, page) {
        const isPlaylistsField = fieldValue === 'Playlists';
        const playlistsOptionsDiv = ruleRow.querySelector('.rule-playlists-options');

        if (playlistsOptionsDiv) {
            if (isPlaylistsField) {
                // Get list type to determine visibility
                const listType = page ? SmartLists.getElementValue(page, '#listType', 'Playlist') : 'Playlist';
                const isCollection = listType === 'Collection';

                // Show/hide playlist-only option based on list type (only for Collections)
                const playlistOnlyDiv = ruleRow.querySelector('.rule-playlists-playlist-only');
                if (playlistOnlyDiv) {
                    playlistOnlyDiv.style.display = isCollection ? 'block' : 'none';
                }

                // Get playlist-only select value
                // Only show the container if we're creating a collection
                // Note: Playlists don't support recursion depth - they can only contain media items, not other playlists
                playlistsOptionsDiv.style.display = isCollection ? 'block' : 'none';
            } else {
                // Hide but preserve user's selection - don't reset value
                playlistsOptionsDiv.style.display = 'none';
            }
        }
    };

    // Update visibility of Playlists options for all rules when media types change
    SmartLists.updateAllPlaylistsOptionsVisibility = function (page) {
        SmartLists.updateAllRules(page, SmartLists.updatePlaylistsOptionsVisibility);
    };

    SmartLists.updateTagsOptionsVisibility = function (ruleRow, fieldValue, page) {
        const isTagsField = fieldValue === 'Tags';
        const tagsOptionsDiv = ruleRow.querySelector('.rule-tags-options');

        if (tagsOptionsDiv) {
            // Get selected media types to check if Episode is selected
            const selectedMediaTypes = page ? SmartLists.getSelectedMediaTypes(page) : [];
            const hasEpisode = selectedMediaTypes.indexOf('Episode') !== -1;

            // Show only if Tags field is selected AND Episode media type is selected
            if (isTagsField && hasEpisode) {
                tagsOptionsDiv.style.display = 'block';
            } else {
                // Hide but preserve user's selection - don't reset value
                tagsOptionsDiv.style.display = 'none';
            }
        }
    };

    // Update visibility of Tags options for all rules when media types change
    SmartLists.updateAllTagsOptionsVisibility = function (page) {
        SmartLists.updateAllRules(page, SmartLists.updateTagsOptionsVisibility);
    };

    SmartLists.updateStudiosOptionsVisibility = function (ruleRow, fieldValue, page) {
        const isStudiosField = fieldValue === 'Studios';
        const studiosOptionsDiv = ruleRow.querySelector('.rule-studios-options');

        if (studiosOptionsDiv) {
            // Get selected media types to check if Episode is selected
            const selectedMediaTypes = page ? SmartLists.getSelectedMediaTypes(page) : [];
            const hasEpisode = selectedMediaTypes.indexOf('Episode') !== -1;

            // Show only if Studios field is selected AND Episode media type is selected
            if (isStudiosField && hasEpisode) {
                studiosOptionsDiv.style.display = 'block';
            } else {
                // Hide but preserve user's selection - don't reset value
                studiosOptionsDiv.style.display = 'none';
            }
        }
    };

    // Update visibility of Studios options for all rules when media types change
    SmartLists.updateAllStudiosOptionsVisibility = function (page) {
        SmartLists.updateAllRules(page, SmartLists.updateStudiosOptionsVisibility);
    };

    SmartLists.updateGenresOptionsVisibility = function (ruleRow, fieldValue, page) {
        const isGenresField = fieldValue === 'Genres';
        const genresOptionsDiv = ruleRow.querySelector('.rule-genres-options');

        if (genresOptionsDiv) {
            // Get selected media types to check if Episode is selected
            const selectedMediaTypes = page ? SmartLists.getSelectedMediaTypes(page) : [];
            const hasEpisode = selectedMediaTypes.indexOf('Episode') !== -1;

            // Show only if Genres field is selected AND Episode media type is selected
            if (isGenresField && hasEpisode) {
                genresOptionsDiv.style.display = 'block';
            } else {
                // Hide but preserve user's selection - don't reset value
                genresOptionsDiv.style.display = 'none';
            }
        }
    };

    // Update visibility of Genres options for all rules when media types change
    SmartLists.updateAllGenresOptionsVisibility = function (page) {
        SmartLists.updateAllRules(page, SmartLists.updateGenresOptionsVisibility);
    };

    SmartLists.updateAudioLanguagesOptionsVisibility = function (ruleRow, fieldValue, page) {
        const isAudioLanguagesField = fieldValue === 'AudioLanguages';
        const audioLanguagesOptionsDiv = ruleRow.querySelector('.rule-audiolanguages-options');

        if (audioLanguagesOptionsDiv) {
            // Get selected media types to check if any audio-capable type is selected
            const selectedMediaTypes = page ? SmartLists.getSelectedMediaTypes(page) : [];
            const hasAudioCapable = selectedMediaTypes.some(function (type) {
                return SmartLists.AUDIO_CAPABLE_TYPES.indexOf(type) !== -1;
            });

            // Show only if AudioLanguages field is selected AND an audio-capable media type is selected
            if (isAudioLanguagesField && hasAudioCapable) {
                audioLanguagesOptionsDiv.style.display = 'block';
            } else {
                // Hide but preserve user's selection - don't reset value
                audioLanguagesOptionsDiv.style.display = 'none';
            }
        }
    };

    // Update visibility of AudioLanguages options for all rules when media types change
    SmartLists.updateAllAudioLanguagesOptionsVisibility = function (page) {
        SmartLists.updateAllRules(page, SmartLists.updateAudioLanguagesOptionsVisibility);
    };

    SmartLists.updateSimilarityOptionsVisibility = function (ruleRow, fieldValue, savedFields) {
        const isSimilarToField = fieldValue === 'SimilarTo';
        const similarityOptionsDiv = ruleRow.querySelector('.rule-similarity-options');

        if (similarityOptionsDiv) {
            if (isSimilarToField) {
                similarityOptionsDiv.style.display = 'block';
                // Populate similarity fields if not already populated
                const fieldsContainer = similarityOptionsDiv.querySelector('.similarity-fields-container');
                if (fieldsContainer && fieldsContainer.children.length === 0) {
                    SmartLists.populateSimilarityFields(fieldsContainer, savedFields);
                }
            } else {
                similarityOptionsDiv.style.display = 'none';
            }
        }
    };

    SmartLists.populateSimilarityFields = function (container, savedFields) {
        if (!SmartLists.availableFields.SimilarityComparisonFields) {
            return;
        }

        // Clear existing content
        container.innerHTML = '';

        // Use saved fields if provided, otherwise default to Genre and Tags
        // Check for both null/undefined and empty array (backend returns empty array when using defaults)
        const selectedFields = (savedFields && savedFields.length > 0) ? savedFields : ['Genre', 'Tags'];

        // Create checkboxes for each comparison field
        SmartLists.availableFields.SimilarityComparisonFields.forEach(function (field) {
            const checkboxWrapper = document.createElement('label');
            checkboxWrapper.style.cssText = 'display: flex; align-items: center; gap: 0.25em; cursor: pointer; padding: 0.25em 0.5em; background: var(--jf-palette-background-paper); border: 1px solid var(--jf-palette-divider); border-radius: 3px; font-size: 0.9em;';

            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.value = field.Value;
            checkbox.className = 'similarity-field-checkbox';
            checkbox.checked = selectedFields.indexOf(field.Value) !== -1;

            const label = document.createElement('span');
            label.textContent = field.Label;

            checkboxWrapper.appendChild(checkbox);
            checkboxWrapper.appendChild(label);
            container.appendChild(checkboxWrapper);
        });
    };

    SmartLists.getSimilarityComparisonFields = function (ruleRow) {
        const similarityOptionsDiv = ruleRow.querySelector('.rule-similarity-options');
        if (!similarityOptionsDiv || similarityOptionsDiv.style.display === 'none') {
            return null; // Not a SimilarTo rule
        }

        const checkboxes = similarityOptionsDiv.querySelectorAll('.similarity-field-checkbox:checked');
        const selectedFields = Array.from(checkboxes).map(function (cb) {
            return cb.value;
        });

        // Return null if using defaults (Genre + Tags) for backwards compatibility
        if (selectedFields.length === 2 &&
            selectedFields.indexOf('Genre') !== -1 &&
            selectedFields.indexOf('Tags') !== -1) {
            return null;
        }

        return selectedFields.length > 0 ? selectedFields : ['Genre', 'Tags'];
    };

    SmartLists.updatePeopleOptionsVisibility = function (ruleRow, fieldValue) {
        const isPeopleField = fieldValue === 'People';
        const peopleOptionsDiv = ruleRow.querySelector('.rule-people-options');
        const peopleSelect = peopleOptionsDiv ? peopleOptionsDiv.querySelector('.rule-people-select') : null;

        if (peopleOptionsDiv) {
            if (isPeopleField) {
                peopleOptionsDiv.style.display = 'block';
                // Populate the people submenu if not already populated
                if (peopleSelect && SmartLists.availableFields && SmartLists.availableFields.PeopleSubFields && peopleSelect.options.length === 0) {
                    peopleSelect.innerHTML = '';
                    SmartLists.availableFields.PeopleSubFields.forEach(function (field) {
                        const option = document.createElement('option');
                        option.value = field.Value;
                        option.textContent = field.Label;
                        peopleSelect.appendChild(option);
                    });
                    // Set "People (All)" as default if no value is currently selected
                    if (!peopleSelect.value) {
                        peopleSelect.value = 'People';
                    }
                }
            } else {
                peopleOptionsDiv.style.display = 'none';
                // Don't reset value - preserve user's selection in case they switch back
            }
        }
    };

    // Centralized people field mapping - DRY principle
    // Returns a map where keys are people field names and values are 'People' (indicating they're people fields)
    SmartLists.getPeopleFieldMap = function () {
        return {
            'People': 'People', 'Actors': 'People', 'ActorRoles': 'People', 'Directors': 'People', 'Composers': 'People',
            'Writers': 'People', 'GuestStars': 'People', 'Producers': 'People', 'Conductors': 'People',
            'Lyricists': 'People', 'Arrangers': 'People', 'SoundEngineers': 'People', 'Mixers': 'People',
            'Remixers': 'People', 'Creators': 'People', 'PersonArtists': 'People', 'PersonAlbumArtists': 'People',
            'Authors': 'People', 'Illustrators': 'People', 'Pencilers': 'People', 'Inkers': 'People',
            'Colorists': 'People', 'Letterers': 'People', 'CoverArtists': 'People', 'Editors': 'People',
            'Translators': 'People'
        };
    };

    // Check if a field is a people sub-field (not "People" itself)
    SmartLists.isPeopleSubField = function (fieldName) {
        const peopleFieldMap = SmartLists.getPeopleFieldMap();
        return peopleFieldMap[fieldName] === 'People' && fieldName !== 'People';
    };

    // Note: getPeopleFieldDisplayName is defined in config-formatters.js to avoid duplication

    // Generic helper to update all rules using a provided update function
    // Reduces duplication across updateAll* functions
    SmartLists.updateAllRules = function (page, updateFunction) {
        if (!page || typeof updateFunction !== 'function') {
            return;
        }

        const allRuleRows = page.querySelectorAll('.rule-row');
        allRuleRows.forEach(function (ruleRow) {
            const fieldSelect = ruleRow.querySelector('.rule-field-select');
            if (fieldSelect) {
                updateFunction(ruleRow, fieldSelect.value, page);
            }
        });
    };

    // Note: shouldShowSortOption, hasSimilarToRuleInForm, getFilteredSortOptions, and updateAllSortOptionsVisibility
    // are defined in config-sorts.js and config-core.js to avoid duplication

    // ===== RULE COLLECTION =====
    SmartLists.collectRulesFromForm = function (page) {
        const expressionSets = [];
        const selectedMediaTypes = SmartLists.getSelectedMediaTypes(page);
        const hasEpisode = selectedMediaTypes.indexOf('Episode') !== -1;
        const hasAudioCapable = selectedMediaTypes.some(function (type) {
            return SmartLists.AUDIO_CAPABLE_TYPES.indexOf(type) !== -1;
        });

        page.querySelectorAll('.logic-group').forEach(function (logicGroup) {
            const expressions = [];
            logicGroup.querySelectorAll('.rule-row').forEach(function (rule) {
                let memberName = rule.querySelector('.rule-field-select').value;

                // If People field is selected, use the value from the people submenu
                if (memberName === 'People') {
                    const peopleSelect = rule.querySelector('.rule-people-select');
                    if (peopleSelect && peopleSelect.value) {
                        memberName = peopleSelect.value;
                    }
                    // If no value in submenu, default to 'People' (All)
                }
                const operator = rule.querySelector('.rule-operator-select').value;
                let targetValue;
                const valueContainer = rule.querySelector('.rule-value-container');
                
                // Check if this is a multi-value operator (IsIn, IsNotIn)
                const isMultiValueOperator = SmartLists.MULTI_VALUE_OPERATORS.indexOf(operator) !== -1;
                
                if (isMultiValueOperator && valueContainer) {
                    // For multi-value operators, get the value from the hidden input
                    const hiddenInput = valueContainer.querySelector('input[type="hidden"].rule-value-input');
                    targetValue = hiddenInput ? hiddenInput.value : '';
                } else if ((operator === 'NewerThan' || operator === 'OlderThan') && rule.querySelector('.rule-value-unit')) {
                    // Serialize as number:unit
                    const num = rule.querySelector('.rule-value-input').value;
                    const unit = rule.querySelector('.rule-value-unit').value;
                    targetValue = num && unit ? num + ':' + unit : '';
                } else {
                    targetValue = rule.querySelector('.rule-value-input').value;
                }

                if (memberName && operator && targetValue) {
                    const expression = { MemberName: memberName, Operator: operator, TargetValue: targetValue };

                    // Check if a specific user is selected for user data fields
                    const userSelect = rule.querySelector('.rule-user-select');
                    if (userSelect && userSelect.value) {
                        // Only add UserId if a specific user is selected (not default)
                        expression.UserId = userSelect.value;
                    }
                    // If no user is selected or default is selected, the expression works as before
                    // (for the playlist user - backwards compatibility)

                    // Check for NextUnwatched specific options (only if Episode is selected)
                    const nextUnwatchedSelect = rule.querySelector('.rule-nextunwatched-select');
                    if (nextUnwatchedSelect && memberName === 'NextUnwatched' && hasEpisode) {
                        // Convert string to boolean and only include if it's explicitly false
                        const includeUnwatchedSeries = nextUnwatchedSelect.value === 'true';
                        if (!includeUnwatchedSeries) {
                            expression.IncludeUnwatchedSeries = false;
                        }
                        // If true (default), don't include the parameter to save space
                    }

                    // Check for Collections specific options
                    if (memberName === 'Collections') {
                        // Check for collection-only option (only for Collections type)
                        const collectionOnlySelect = rule.querySelector('.rule-collections-collection-only-select');
                        if (collectionOnlySelect) {
                            const includeCollectionOnly = collectionOnlySelect.value === 'true';
                            if (includeCollectionOnly) {
                                expression.IncludeCollectionOnly = true;
                            }
                            // If false (default), don't include the parameter to save space
                        }

                        // Check for episodes option (only if Episode is selected and collection-only is not enabled)
                        const collectionsSelect = rule.querySelector('.rule-collections-select');
                        if (collectionsSelect && hasEpisode) {
                            // Only process if collection-only is not enabled
                            const collectionOnlySelect2 = rule.querySelector('.rule-collections-collection-only-select');
                            const isCollectionOnly = collectionOnlySelect2 && collectionOnlySelect2.value === 'true';
                            if (!isCollectionOnly) {
                                // Convert string to boolean and only include if it's explicitly true
                                const includeEpisodesWithinSeries = collectionsSelect.value === 'true';
                                if (includeEpisodesWithinSeries) {
                                    expression.IncludeEpisodesWithinSeries = true;
                                }
                                // If false (default), don't include the parameter to save space
                            }
                        }

                        // Check for collection search depth
                        const depthInput = rule.querySelector('.rule-collections-depth-input');
                        if (depthInput) {
                            const depthValue = parseInt(depthInput.value, 10);
                            if (!isNaN(depthValue) && depthValue > 0) {
                                expression.CollectionSearchDepth = Math.min(depthValue, 10);
                            }
                            // If 0 (default), don't include the parameter to save space
                        }
                    }

                    // Check for Playlists specific options
                    if (memberName === 'Playlists') {
                        // Check for playlist-only option (only for Collections type)
                        const playlistOnlySelect = rule.querySelector('.rule-playlists-playlist-only-select');
                        if (playlistOnlySelect) {
                            const includePlaylistOnly = playlistOnlySelect.value === 'true';
                            if (includePlaylistOnly) {
                                expression.IncludePlaylistOnly = true;
                            }
                            // If false (default), don't include the parameter to save space
                        }
                    }

                    // Handle Tags-specific options (only if Episode is selected)
                    const tagsSelect = rule.querySelector('.rule-tags-select');
                    if (tagsSelect && memberName === 'Tags' && hasEpisode) {
                        // Convert string to boolean and only include if it's explicitly true
                        const includeParentSeriesTags = tagsSelect.value === 'true';
                        if (includeParentSeriesTags) {
                            expression.IncludeParentSeriesTags = true;
                        }
                        // If false (default), don't include the parameter to save space
                    }

                    // Handle Studios-specific options (only if Episode is selected)
                    const studiosSelect = rule.querySelector('.rule-studios-select');
                    if (studiosSelect && memberName === 'Studios' && hasEpisode) {
                        // Convert string to boolean and only include if it's explicitly true
                        const includeParentSeriesStudios = studiosSelect.value === 'true';
                        if (includeParentSeriesStudios) {
                            expression.IncludeParentSeriesStudios = true;
                        }
                        // If false (default), don't include the parameter to save space
                    }

                    // Handle Genres-specific options (only if Episode is selected)
                    const genresSelect = rule.querySelector('.rule-genres-select');
                    if (genresSelect && memberName === 'Genres' && hasEpisode) {
                        // Convert string to boolean and only include if it's explicitly true
                        const includeParentSeriesGenres = genresSelect.value === 'true';
                        if (includeParentSeriesGenres) {
                            expression.IncludeParentSeriesGenres = true;
                        }
                        // If false (default), don't include the parameter to save space
                    }

                    // Handle AudioLanguages-specific options (only if audio-capable media type is selected)
                    const audioLanguagesSelect = rule.querySelector('.rule-audiolanguages-select');
                    if (audioLanguagesSelect && memberName === 'AudioLanguages' && hasAudioCapable) {
                        // Convert string to boolean and only include if it's explicitly true
                        const onlyDefaultAudioLanguage = audioLanguagesSelect.value === 'true';
                        if (onlyDefaultAudioLanguage) {
                            expression.OnlyDefaultAudioLanguage = true;
                        }
                        // If false (default), don't include the parameter to save space
                    }

                    expressions.push(expression);
                }
            });
            
            if (expressions.length > 0) {
                const expressionSet = { Expressions: expressions };
                
                // Collect MaxItems for this group
                const maxItemsInput = logicGroup.querySelector('.group-max-items-input');
                if (maxItemsInput && maxItemsInput.value) {
                    const maxItems = parseInt(maxItemsInput.value, 10);
                    if (!isNaN(maxItems) && maxItems > 0) {
                        expressionSet.MaxItems = maxItems;
                    }
                }
                
                expressionSets.push(expressionSet);
            }
        });

        return expressionSets;
    };

    // ===== RULE POPULATION (for edit/clone) =====
    SmartLists.populateRuleRow = function (ruleRow, expression, page) {
        try {
            const fieldSelect = ruleRow.querySelector('.rule-field-select');
            const operatorSelect = ruleRow.querySelector('.rule-operator-select');
            const valueContainer = ruleRow.querySelector('.rule-value-container');

            // Determine the actual member name to use (for people sub-fields, this stays as the original)
            let actualMemberName = expression.MemberName;

            if (fieldSelect && expression.MemberName) {
                // Check if this is a people sub-field
                const isPeopleSubFieldValue = SmartLists.isPeopleSubField(expression.MemberName);

                if (isPeopleSubFieldValue) {
                    // Set field select to "People" and submenu to the actual field
                    fieldSelect.value = 'People';
                    SmartLists.updatePeopleOptionsVisibility(ruleRow, 'People');
                    const peopleSelect = ruleRow.querySelector('.rule-people-select');
                    if (peopleSelect) {
                        // Wait for options to be populated, then set the value
                        setTimeout(function () {
                            peopleSelect.value = actualMemberName;
                        }, 0);
                    }
                } else {
                    fieldSelect.value = expression.MemberName;
                    SmartLists.updatePeopleOptionsVisibility(ruleRow, expression.MemberName);
                }

                SmartLists.updateOperatorOptions(actualMemberName, operatorSelect);
                SmartLists.updateUserSelectorVisibility(ruleRow, actualMemberName);
                SmartLists.updateNextUnwatchedOptionsVisibility(ruleRow, actualMemberName, page);
                SmartLists.updateCollectionsOptionsVisibility(ruleRow, actualMemberName, page);
                SmartLists.updatePlaylistsOptionsVisibility(ruleRow, actualMemberName, page);
                SmartLists.updateTagsOptionsVisibility(ruleRow, actualMemberName, page);
                SmartLists.updateStudiosOptionsVisibility(ruleRow, actualMemberName, page);
                SmartLists.updateGenresOptionsVisibility(ruleRow, actualMemberName, page);
                SmartLists.updateAudioLanguagesOptionsVisibility(ruleRow, actualMemberName, page);
                
                // For SimilarTo field, get similarity fields from page state (set by clone/edit flows)
                // and update visibility immediately so the options show up
                if (actualMemberName === 'SimilarTo') {
                    const similarityFields = page._editingPlaylistSimilarityFields || page._cloningPlaylistSimilarityFields;
                    SmartLists.updateSimilarityOptionsVisibility(ruleRow, actualMemberName, similarityFields);
                } else {
                    SmartLists.updateSimilarityOptionsVisibility(ruleRow, actualMemberName);
                }
            }

            if (operatorSelect && expression.Operator) {
                operatorSelect.value = expression.Operator;
            }

            if (valueContainer && expression.TargetValue !== undefined) {
                SmartLists.setValueInput(actualMemberName, valueContainer, expression.Operator, expression.TargetValue);
            }

            // Handle user-specific rules
            if (expression.UserId) {
                const userSelect = ruleRow.querySelector('.rule-user-select');
                if (userSelect) {
                    // Ensure options are loaded before setting the value
                    SmartLists.loadUsersForRule(userSelect, true).then(function () {
                        userSelect.value = expression.UserId;
                    }).catch(function () {
                        // Fallback: set value anyway in case of error
                        userSelect.value = expression.UserId;
                    });
                }
            }

            // Restore per-field option selects for clone/edit flows
            // Note: These must be set AFTER updateCollectionsOptionsVisibility/updateTagsOptionsVisibility
            // to ensure the options divs are visible
            if (expression.MemberName === 'NextUnwatched') {
                const nextUnwatchedSelect = ruleRow.querySelector('.rule-nextunwatched-select');
                if (nextUnwatchedSelect) {
                    const includeValue = expression.IncludeUnwatchedSeries !== false ? 'true' : 'false';
                    nextUnwatchedSelect.value = includeValue;
                }
            }
            if (expression.MemberName === 'Collections') {
                // Restore collection-only option
                const collectionOnlySelect = ruleRow.querySelector('.rule-collections-collection-only-select');
                if (collectionOnlySelect) {
                    const includeCollectionOnlyValue = expression.IncludeCollectionOnly === true ? 'true' : 'false';
                    collectionOnlySelect.value = includeCollectionOnlyValue;
                    // Trigger change to update visibility of episodes field and depth field
                    collectionOnlySelect.dispatchEvent(new Event('change'));
                }

                // Restore episodes option
                const collectionsSelect = ruleRow.querySelector('.rule-collections-select');
                if (collectionsSelect) {
                    const includeValue = expression.IncludeEpisodesWithinSeries === true ? 'true' : 'false';
                    collectionsSelect.value = includeValue;
                }

                // Restore collection search depth
                const depthInput = ruleRow.querySelector('.rule-collections-depth-input');
                if (depthInput) {
                    const depthValue = expression.CollectionSearchDepth !== undefined && expression.CollectionSearchDepth !== null ? expression.CollectionSearchDepth : 0;
                    depthInput.value = depthValue;
                }
            }
            if (expression.MemberName === 'Playlists') {
                // Restore playlist-only option
                const playlistOnlySelect = ruleRow.querySelector('.rule-playlists-playlist-only-select');
                if (playlistOnlySelect) {
                    const includePlaylistOnlyValue = expression.IncludePlaylistOnly === true ? 'true' : 'false';
                    playlistOnlySelect.value = includePlaylistOnlyValue;
                }
            }
            if (expression.MemberName === 'Tags') {
                const tagsSelect = ruleRow.querySelector('.rule-tags-select');
                if (tagsSelect) {
                    const includeValue = expression.IncludeParentSeriesTags === true ? 'true' : 'false';
                    tagsSelect.value = includeValue;
                }
            }
            if (expression.MemberName === 'Studios') {
                const studiosSelect = ruleRow.querySelector('.rule-studios-select');
                if (studiosSelect) {
                    const includeValue = expression.IncludeParentSeriesStudios === true ? 'true' : 'false';
                    studiosSelect.value = includeValue;
                }
            }
            if (expression.MemberName === 'Genres') {
                const genresSelect = ruleRow.querySelector('.rule-genres-select');
                if (genresSelect) {
                    const includeValue = expression.IncludeParentSeriesGenres === true ? 'true' : 'false';
                    genresSelect.value = includeValue;
                }
            }
            if (expression.MemberName === 'AudioLanguages') {
                const audioLanguagesSelect = ruleRow.querySelector('.rule-audiolanguages-select');
                if (audioLanguagesSelect) {
                    const includeValue = expression.OnlyDefaultAudioLanguage === true ? 'true' : 'false';
                    audioLanguagesSelect.value = includeValue;
                }
            }
            if (expression.MemberName === 'SimilarTo') {
                // Restore similarity comparison fields if provided
                // SimilarityComparisonFields is stored at the playlist/collection level, not per-expression
                // The edit/clone flows set page._editingPlaylistSimilarityFields or page._cloningPlaylistSimilarityFields
                // before calling populateRuleRow, so we read from the page state here
                // Note: This is a fallback - the main call happens earlier when setting the field value
                const similarityFields = page._editingPlaylistSimilarityFields || page._cloningPlaylistSimilarityFields;
                // Always call updateSimilarityOptionsVisibility for SimilarTo to ensure the div is shown
                // populateSimilarityFields will handle null/undefined by using defaults
                SmartLists.updateSimilarityOptionsVisibility(ruleRow, expression.MemberName, similarityFields);
            }

            // Update regex help if needed
            SmartLists.updateRegexHelp(ruleRow);
        } catch (error) {
            console.error('Error populating rule row:', error);
        }
    };

})(window.SmartLists = window.SmartLists || {});

