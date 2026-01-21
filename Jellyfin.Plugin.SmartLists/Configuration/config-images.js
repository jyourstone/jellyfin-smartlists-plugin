(function (SmartLists) {
    'use strict';

    // Valid image types (in display order)
    var VALID_IMAGE_TYPES = [
        { value: 'Primary', label: 'Primary (Cover)' },
        { value: 'Art', label: 'Clearart' },
        { value: 'Backdrop', label: 'Backdrop' },
        { value: 'Banner', label: 'Banner' },
        { value: 'Box', label: 'Box' },
        { value: 'BoxRear', label: 'Box (rear)' },
        { value: 'Disc', label: 'Disc' },
        { value: 'Logo', label: 'Logo' },
        { value: 'Menu', label: 'Menu' },
        { value: 'Thumb', label: 'Thumb' }
    ];

    // Track pending images for upload
    var existingImages = {};
    var currentSmartListId = null;

    // Track images marked for deletion (imageType -> true)
    var pendingImageDeletions = {};

    // Generate unique ID for image rows
    var imageRowCounter = 0;

    /**
     * Get image types that are already used (either existing or pending upload).
     * @param {Element} page - The page element
     * @returns {Array} Array of image type values that are already in use
     */
    function getUsedImageTypes(page) {
        var usedTypes = [];
        var container = page.querySelector('#custom-images-container');
        if (!container) {
            return usedTypes;
        }

        var rows = container.querySelectorAll('.image-upload-row');
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var typeSelect = row.querySelector('.image-type-select');
            if (typeSelect && typeSelect.value) {
                usedTypes.push(typeSelect.value);
            }
        }

        return usedTypes;
    }

    /**
     * Get available image types for upload (excluding already used types).
     * @param {Element} page - The page element
     * @param {string} currentType - Optional current type to always include (for editing existing)
     * @returns {Array} Array of available image type objects
     */
    function getAvailableImageTypes(page, currentType) {
        var usedTypes = getUsedImageTypes(page);
        return VALID_IMAGE_TYPES.filter(function(type) {
            // Always include the current type (for existing images)
            if (currentType && type.value === currentType) {
                return true;
            }
            // Exclude types that are already used
            return usedTypes.indexOf(type.value) === -1;
        });
    }

    /**
     * Update all image type dropdowns to reflect currently used types.
     * @param {Element} page - The page element
     */
    function updateImageTypeDropdowns(page) {
        var container = page.querySelector('#custom-images-container');
        if (!container) {
            return;
        }

        var rows = container.querySelectorAll('.image-upload-row');
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var typeSelect = row.querySelector('.image-type-select');
            if (!typeSelect || typeSelect.disabled) {
                continue; // Skip disabled selects (existing images)
            }

            var currentValue = typeSelect.value;
            var availableTypes = getAvailableImageTypes(page, currentValue);

            // Rebuild options
            typeSelect.innerHTML = '';
            for (var j = 0; j < availableTypes.length; j++) {
                var type = availableTypes[j];
                var option = document.createElement('option');
                option.value = type.value;
                option.textContent = type.label;
                if (type.value === currentValue) {
                    option.selected = true;
                }
                typeSelect.appendChild(option);
            }
        }

        // Update "Add Image" button visibility
        updateAddImageButtonVisibility(page);
    }

    /**
     * Hide "Add Image" button if all image types are used.
     * @param {Element} page - The page element
     */
    function updateAddImageButtonVisibility(page) {
        var addBtn = page.querySelector('#addImageBtn');
        if (!addBtn) {
            return;
        }

        var availableTypes = getAvailableImageTypes(page, null);
        if (availableTypes.length === 0) {
            addBtn.style.display = 'none';
        } else {
            addBtn.style.display = '';
        }
    }

    /**
     * Initialize the custom images container
     */
    SmartLists.initCustomImagesContainer = function (page) {
        var container = page.querySelector('#custom-images-container');
        if (container) {
            container.innerHTML = '';
        }
        existingImages = {};
        currentSmartListId = null;
        pendingImageDeletions = {};
        imageRowCounter = 0;

        // Show the Add Image button (it may have been hidden if all types were used)
        updateAddImageButtonVisibility(page);
    };

    /**
     * Add a new image upload row
     */
    SmartLists.addImageRow = function (page, existingImage) {
        var container = page.querySelector('#custom-images-container');
        if (!container) {
            return;
        }

        var rowId = 'image-row-' + (++imageRowCounter);
        var isExisting = existingImage && existingImage.fileName;

        // Get available image types (excluding already used ones, but including current type for existing)
        var currentType = isExisting ? existingImage.imageType : null;
        var typesForDropdown = getAvailableImageTypes(page, currentType);

        // Don't add a row if no types are available
        if (typesForDropdown.length === 0) {
            SmartLists.showNotification('All image types are already in use.', 'warning');
            return;
        }

        // Create the row element
        var row = document.createElement('div');
        row.id = rowId;
        row.className = 'image-upload-row paperList';
        SmartLists.applyStyles(row, {
            display: 'flex',
            alignItems: 'center',
            gap: '1em',
            marginBottom: '0.5em',
            padding: '0.75em 4em 0.75em 1em',
            border: '1px solid var(--jf-palette-divider)',
            borderRadius: '4px',
            position: 'relative'
        });

        // Image type select (disabled for existing images - must delete and re-upload to change type)
        var typeSelect = document.createElement('select');
        typeSelect.className = 'image-type-select emby-select-withcolor emby-select';
        typeSelect.setAttribute('is', 'emby-select');
        typeSelect.style.flex = '0 0 180px';
        if (isExisting) {
            typeSelect.disabled = true;
        }

        for (var i = 0; i < typesForDropdown.length; i++) {
            var type = typesForDropdown[i];
            var option = document.createElement('option');
            option.value = type.value;
            option.textContent = type.label;
            if (existingImage && existingImage.imageType === type.value) {
                option.selected = true;
            }
            typeSelect.appendChild(option);
        }
        row.appendChild(typeSelect);

        // File input or existing image
        if (isExisting) {
            // Existing image - show preview link
            var existingDiv = document.createElement('div');
            existingDiv.style.cssText = 'flex: 1; display: flex; align-items: center; gap: 0.5em;';

            var checkIcon = document.createElement('span');
            checkIcon.className = 'material-icons';
            checkIcon.style.color = '#4CAF50';
            checkIcon.textContent = 'check_circle';
            existingDiv.appendChild(checkIcon);

            var link = document.createElement('a');
            link.href = existingImage.previewUrl;
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            link.style.color = 'var(--jf-palette-primary)';
            link.textContent = existingImage.fileName;
            existingDiv.appendChild(link);

            var hiddenInput = document.createElement('input');
            hiddenInput.type = 'hidden';
            hiddenInput.className = 'existing-image';
            hiddenInput.setAttribute('data-image-type', existingImage.imageType);
            hiddenInput.value = existingImage.fileName;
            existingDiv.appendChild(hiddenInput);

            row.appendChild(existingDiv);
        } else {
            // New upload - styled file input wrapper (matching import button style)
            var fileInputWrapper = document.createElement('div');
            fileInputWrapper.style.cssText = 'flex: 1; display: flex; align-items: center; gap: 0.5em;';

            var fileInput = document.createElement('input');
            fileInput.type = 'file';
            fileInput.className = 'image-file-input';
            fileInput.accept = 'image/jpeg,image/png,image/webp,image/gif,image/bmp,image/avif,image/svg+xml,image/tiff,image/apng,image/x-icon';
            fileInput.style.display = 'none';
            fileInput.id = 'file-input-' + rowId;

            var fileLabel = document.createElement('label');
            fileLabel.htmlFor = 'file-input-' + rowId;
            fileLabel.className = 'emby-button raised';
            fileLabel.style.cssText = 'display: inline-block; cursor: pointer; margin: 0;';
            fileLabel.textContent = 'Choose File';

            var fileNameSpan = document.createElement('span');
            fileNameSpan.className = 'selected-file-name';
            fileNameSpan.style.cssText = 'opacity: 0.8; font-size: 0.9em;';
            fileNameSpan.textContent = 'No file selected';

            fileInputWrapper.appendChild(fileInput);
            fileInputWrapper.appendChild(fileLabel);
            fileInputWrapper.appendChild(fileNameSpan);
            row.appendChild(fileInputWrapper);
        }

        // Preview thumbnail
        var previewContainer = document.createElement('div');
        previewContainer.className = 'image-preview-container';
        previewContainer.style.cssText = 'width: 60px; height: 60px; display: flex; align-items: center; justify-content: center; border: 1px solid var(--jf-palette-divider); border-radius: 4px; overflow: hidden;';

        if (existingImage && existingImage.previewUrl) {
            var previewImg = document.createElement('img');
            previewImg.src = existingImage.previewUrl;
            previewImg.style.cssText = 'max-width: 100%; max-height: 100%; object-fit: contain;';
            previewContainer.appendChild(previewImg);
        } else {
            var placeholderIcon = document.createElement('span');
            placeholderIcon.className = 'material-icons';
            placeholderIcon.style.cssText = 'color: var(--jf-palette-text-secondary); font-size: 24px;';
            placeholderIcon.textContent = 'image';
            previewContainer.appendChild(placeholderIcon);
        }
        row.appendChild(previewContainer);

        // Remove button - styled like schedule/sort remove buttons
        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'remove-image-btn';
        removeBtn.setAttribute('data-row-id', rowId);
        removeBtn.textContent = '\u00D7'; // × symbol
        removeBtn.title = 'Remove image';
        SmartLists.applyStyles(removeBtn, SmartLists.STYLES.scheduleRemoveBtn);
        row.appendChild(removeBtn);

        container.appendChild(row);

        // Set up event listeners
        var fileInputEl = row.querySelector('.image-file-input');
        if (fileInputEl) {
            fileInputEl.addEventListener('change', function (e) {
                var file = e.target.files[0];
                SmartLists.handleImageFileSelect(row, file);
                // Update file name display
                var nameSpan = row.querySelector('.selected-file-name');
                if (nameSpan) {
                    nameSpan.textContent = file ? file.name : 'No file selected';
                }
            });
        }

        // Add change listener to type select to update other dropdowns
        if (!isExisting) {
            typeSelect.addEventListener('change', function () {
                updateImageTypeDropdowns(page);
            });
        }

        // Update dropdowns and button visibility
        updateImageTypeDropdowns(page);
    };

    /**
     * Handle file selection - update preview
     */
    SmartLists.handleImageFileSelect = function (row, file) {
        if (!file) {
            return;
        }

        var previewContainer = row.querySelector('.image-preview-container');
        if (previewContainer && file.type.startsWith('image/')) {
            var reader = new FileReader();
            reader.onload = function (e) {
                previewContainer.innerHTML = '<img src="' + e.target.result + '" style="max-width: 100%; max-height: 100%; object-fit: contain;">';
            };
            reader.readAsDataURL(file);
        }
    };

    /**
     * Remove an image row
     */
    SmartLists.removeImageRow = function (page, rowId) {
        var row = page.querySelector('#' + rowId);
        if (row) {
            // If it's an existing image, mark it for deletion (will be applied on Save)
            var existingInput = row.querySelector('.existing-image');
            if (existingInput) {
                var imageType = existingInput.getAttribute('data-image-type');
                if (imageType) {
                    pendingImageDeletions[imageType] = true;
                }
            }
            row.parentNode.removeChild(row);

            // Update dropdowns to show the now-available type
            updateImageTypeDropdowns(page);
        }
    };

    /**
     * Get pending images to upload
     */
    SmartLists.getPendingImageUploads = function (page) {
        var container = page.querySelector('#custom-images-container');
        if (!container) {
            return [];
        }

        var uploads = [];
        var rows = container.querySelectorAll('.image-upload-row');

        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var fileInput = row.querySelector('.image-file-input');
            var typeSelect = row.querySelector('.image-type-select');

            if (fileInput && fileInput.files && fileInput.files.length > 0 && typeSelect) {
                uploads.push({
                    file: fileInput.files[0],
                    imageType: typeSelect.value
                });
            }
        }

        return uploads;
    };

    /**
     * Upload images for a smart list
     */
    SmartLists.uploadImages = function (smartListId, pendingUploads) {
        if (!pendingUploads || pendingUploads.length === 0) {
            return Promise.resolve();
        }

        var apiClient = SmartLists.getApiClient ? SmartLists.getApiClient() : ApiClient;
        var baseUrl = SmartLists.IS_USER_PAGE ? 'Plugins/SmartLists/User' : 'Plugins/SmartLists';

        // Upload images sequentially to avoid file locking issues
        var promise = Promise.resolve();
        pendingUploads.forEach(function (upload) {
            promise = promise.then(function () {
                var formData = new FormData();
                formData.append('file', upload.file);
                formData.append('imageType', upload.imageType);

                var url = apiClient.getUrl(baseUrl + '/' + smartListId + '/images');

                return fetch(url, {
                    method: 'POST',
                    headers: {
                        'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
                    },
                    body: formData
                }).then(function (response) {
                    if (!response.ok) {
                        return response.json().then(function (errorData) {
                            throw new Error(errorData.message || 'Upload failed');
                        }).catch(function () {
                            throw new Error('Upload failed with status ' + response.status);
                        });
                    }
                    return response.json();
                });
            });
        });

        return promise;
    };

    /**
     * Load existing images for a smart list
     */
    SmartLists.loadExistingImages = function (page, smartListId) {
        if (!smartListId) {
            return Promise.resolve();
        }

        currentSmartListId = smartListId;
        var apiClient = SmartLists.getApiClient ? SmartLists.getApiClient() : ApiClient;
        var baseUrl = SmartLists.IS_USER_PAGE ? 'Plugins/SmartLists/User' : 'Plugins/SmartLists';

        return apiClient.ajax({
            type: 'GET',
            url: apiClient.getUrl(baseUrl + '/' + smartListId + '/images'),
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('Failed to load images');
            }
            return response.json();
        }).then(function (images) {
            SmartLists.initCustomImagesContainer(page);

            if (images && images.length > 0) {
                // Sort images according to VALID_IMAGE_TYPES order
                var sortedImages = images.slice().sort(function(a, b) {
                    var indexA = -1;
                    var indexB = -1;
                    for (var i = 0; i < VALID_IMAGE_TYPES.length; i++) {
                        if (VALID_IMAGE_TYPES[i].value === a.ImageType) {
                            indexA = i;
                        }
                        if (VALID_IMAGE_TYPES[i].value === b.ImageType) {
                            indexB = i;
                        }
                    }
                    // Put unknown types at the end
                    if (indexA === -1) indexA = 999;
                    if (indexB === -1) indexB = 999;
                    return indexA - indexB;
                });

                for (var i = 0; i < sortedImages.length; i++) {
                    var img = sortedImages[i];
                    // Add cache-busting parameter to ensure fresh images after updates
                    var imageUrl = apiClient.getUrl(baseUrl + '/' + smartListId + '/images/' + img.ImageType + '/file');
                    imageUrl += (imageUrl.indexOf('?') === -1 ? '?' : '&') + '_t=' + Date.now();
                    SmartLists.addImageRow(page, {
                        imageType: img.ImageType,
                        fileName: img.FileName,
                        previewUrl: imageUrl
                    });
                    existingImages[img.ImageType] = img.FileName;
                }
            }
        }).catch(function (err) {
            console.error('Failed to load images:', err);
        });
    };

    /**
     * Delete an image
     */
    SmartLists.deleteImage = function (smartListId, imageType) {
        var apiClient = SmartLists.getApiClient ? SmartLists.getApiClient() : ApiClient;
        var baseUrl = SmartLists.IS_USER_PAGE ? 'Plugins/SmartLists/User' : 'Plugins/SmartLists';

        return apiClient.ajax({
            type: 'DELETE',
            url: apiClient.getUrl(baseUrl + '/' + smartListId + '/images/' + imageType),
            contentType: 'application/json'
        });
    };

    /**
     * Apply pending image deletions for a smart list
     * @param {string} smartListId The smart list ID
     * @param {Array} pendingUploads Optional array of pending uploads to check for replacement
     * @returns {Promise} Promise that resolves when all deletions are applied
     */
    SmartLists.applyImageDeletions = function (smartListId, pendingUploads) {
        var deletions = Object.keys(pendingImageDeletions);
        if (deletions.length === 0 || !smartListId) {
            return Promise.resolve();
        }

        // Filter out deletions where a new image of the same type is being uploaded
        // This handles the "replace image" scenario: user removes old Primary, adds new Primary
        if (pendingUploads && pendingUploads.length > 0) {
            var uploadTypes = {};
            for (var i = 0; i < pendingUploads.length; i++) {
                uploadTypes[pendingUploads[i].imageType] = true;
            }
            deletions = deletions.filter(function (imageType) {
                return !uploadTypes[imageType];
            });
        }

        if (deletions.length === 0) {
            // All deletions were skipped because new images of the same types are being uploaded
            pendingImageDeletions = {};
            return Promise.resolve();
        }

        var apiClient = SmartLists.getApiClient ? SmartLists.getApiClient() : ApiClient;
        var baseUrl = SmartLists.IS_USER_PAGE ? 'Plugins/SmartLists/User' : 'Plugins/SmartLists';

        // Apply deletions sequentially
        var promise = Promise.resolve();
        deletions.forEach(function (imageType) {
            var apiUrl = apiClient.getUrl(baseUrl + '/' + smartListId + '/images/' + imageType);
            promise = promise.then(function () {
                return apiClient.ajax({
                    type: 'DELETE',
                    url: apiUrl,
                    contentType: 'application/json'
                }).then(function (response) {
                    if (!response.ok) {
                        throw new Error('Failed to delete image');
                    }
                }).catch(function (err) {
                    console.error('Image deletion error:', err);
                    throw err;
                });
            });
        });

        return promise.then(function () {
            pendingImageDeletions = {};
        });
    };

    /**
     * Clear all pending images (called when form is cleared)
     */
    SmartLists.clearPendingImages = function (page) {
        SmartLists.initCustomImagesContainer(page);
    };

    /**
     * Check if there are pending image deletions
     */
    SmartLists.hasPendingImageDeletions = function () {
        return Object.keys(pendingImageDeletions).length > 0;
    };

    /**
     * Get image URL for display in manage list
     */
    SmartLists.getImageDisplayUrl = function (smartListId, imageType) {
        var apiClient = SmartLists.getApiClient ? SmartLists.getApiClient() : ApiClient;
        var baseUrl = SmartLists.IS_USER_PAGE ? 'Plugins/SmartLists/User' : 'Plugins/SmartLists';
        return apiClient.getUrl(baseUrl + '/' + smartListId + '/images/' + imageType + '/file');
    };

})(window.SmartLists = window.SmartLists || {});
