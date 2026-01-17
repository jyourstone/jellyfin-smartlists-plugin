(function () {
    'use strict';

    let statusPollingInterval = null;
    let aggressivePollingInterval = null;
    let aggressivePollingTimeout = null;

    /**
     * Escape HTML to prevent XSS (using safe DOM-based approach)
     */
    function escapeHtml(text) {
        if (text == null) return '';
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    /**
     * Get the active configuration page element
     */
    function getActiveConfigPage() {
        return document.querySelector('.SmartListsConfigurationPage:not(.hide)');
    }

    /**
     * Load and display the status page
     */
    function loadStatusPage() {
        fetchStatusData();
    }

    /**
     * Fetch status data from the API
     */
    function fetchStatusData() {
        const apiClient = SmartLists.getApiClient();
        if (!apiClient) {
            console.error('API client not available');
            showError('API client not available');
            return;
        }

        const url = apiClient.getUrl('Plugins/SmartLists/Status');

        apiClient.ajax({
            type: 'GET',
            url: url,
            contentType: 'application/json'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status + ': ' + response.statusText);
            }
            return response.json();
        }).then(function (data) {
            if (!data) {
                console.warn('No data received from status endpoint');
                showError('No data received from server');
                return;
            }
            renderStatusPage(data);
        }).catch(function (error) {
            console.error('Error fetching status:', error);
            const errorMsg = error.message || error.toString() || 'Unknown error';
            showError('Error loading status data: ' + errorMsg);

            // Show error in all containers
            const page = getActiveConfigPage();
            if (page) {
                const containers = ['ongoing-operations-container', 'statistics-container', 'refresh-history-container'];
                containers.forEach(function (containerId) {
                    const container = page.querySelector('#' + containerId);
                    if (container) {
                        container.innerHTML = '<p style="color: var(--jf-palette-error-main);">Error: ' + escapeHtml(errorMsg) + '</p>';
                    }
                });
            }
        });
    }

    /**
     * Render the status page with data
     */
    function renderStatusPage(data) {
        renderOngoingOperations(data.ongoingOperations || []);
        renderStatistics(data.statistics || {}, data.ongoingOperations || []);
        renderRefreshHistory(data.history || []);

        // Auto-refresh polling: Poll every 2 seconds when operations are active, every 30 seconds when idle
        const hasOngoing = (data.ongoingOperations || []).length > 0;

        if (hasOngoing) {
            // Operations are active - use 2-second polling
            stopAggressivePolling(); // Stop aggressive polling if operations are found
            // Always restart polling with the active interval
            stopPolling();
            startPolling(2000); // 2 seconds when active
        } else {
            // No operations - use 30-second idle polling
            // Only switch to idle polling if aggressive polling is not active
            if (!aggressivePollingInterval) {
                // Always restart polling with the idle interval
                stopPolling();
                startPolling(30000); // 30 seconds when idle
            }
        }
    }

    /**
     * Render ongoing operations
     */
    function renderOngoingOperations(operations) {
        // Query within the visible page to avoid duplicate container issues
        // Query within the visible page to avoid duplicate container issues
        const page = getActiveConfigPage();
        const container = page ? page.querySelector('#ongoing-operations-container') : null;
        if (!container) return;

        if (!operations || operations.length === 0) {
            container.innerHTML = '<p style="opacity: 0.6;">No ongoing refresh operations.</p>';
            return;
        }

        let html = '<div style="display: flex; flex-direction: column; gap: 1em;">';
        operations.forEach(op => {
            const progress = op.totalItems > 0 ? (op.processedItems / op.totalItems * 100) : 0;
            const progressPercent = Math.round(progress);
            const elapsedTime = formatDuration(op.elapsedTime);
            const estimatedTime = op.estimatedTimeRemaining ? formatDuration(op.estimatedTimeRemaining) : 'Calculating...';

            html += `
                <div style="padding: 1em; border: 1px solid var(--jf-palette-divider); border-radius: 4px; margin-bottom: 0.5em;">
                    <div style="display: flex; justify-content: space-between; margin-bottom: 0.5em;">
                        <div>
                            <strong>${escapeHtml(op.listName)}</strong>
                            <span style="margin-left: 0.5em; font-size: 0.9em; color: var(--jf-palette-text-secondary);">
                                (${escapeHtml(String(op.listType))}) - ${escapeHtml(String(op.triggerType))}
                            </span>
                        </div>
                        <div style="font-size: 0.9em; color: var(--jf-palette-text-secondary);">
                            Started: ${formatDateTime(op.startTime)}
                        </div>
                    </div>
                    <div style="margin-bottom: 0.5em;">
                        <div style="display: flex; justify-content: space-between; margin-bottom: 0.25em;">
                            <span>Progress: ${op.processedItems} / ${op.totalItems} items</span>
                            <span>${progressPercent}%</span>
                        </div>
                        <div style="width: 100%; height: 20px; background: var(--jf-palette-action-hover); border-radius: 10px; overflow: hidden;">
                            <div style="width: ${progressPercent}%; height: 100%; background: var(--jf-palette-primary-main); transition: width 0.3s;"></div>
                        </div>
                    </div>
                    <div style="display: flex; justify-content: space-between; font-size: 0.9em; color: var(--jf-palette-text-secondary);">
                        <span>Elapsed: ${elapsedTime}</span>
                        <span>Estimated remaining: ${estimatedTime}</span>
                    </div>
                    ${op.errorMessage ? `<div style="margin-top: 0.5em; color: var(--jf-palette-error-main);">Error: ${escapeHtml(op.errorMessage)}</div>` : ''}
                </div>
            `;
        });
        html += '</div>';
        container.innerHTML = html;
    }

    /**
     * Render statistics
     */
    function renderStatistics(stats, ongoingOperations) {
        // Query within the visible page to avoid duplicate container issues
        // Query within the visible page to avoid duplicate container issues
        const page = getActiveConfigPage();
        const container = page ? page.querySelector('#statistics-container') : null;
        if (!container) {
            return;
        }

        // Always show the statistics table, even if there are no stats yet
        const lastRefresh = stats?.lastRefreshTime ? formatDateTime(stats.lastRefreshTime) : 'Never';
        const avgDuration = stats?.averageRefreshDuration ? formatDuration(stats.averageRefreshDuration) : 'N/A';

        // Find batch progress info from ongoing operations
        // Show the highest batch index (current operation being processed) or count of ongoing operations
        const batchOps = ongoingOperations?.filter(op => op.batchCurrentIndex != null && op.batchTotalCount != null) || [];
        let ongoingOpsText;
        if (batchOps.length > 0) {
            // Find the highest batch index to show current progress
            const maxIndex = Math.max(...batchOps.map(op => op.batchCurrentIndex));
            const totalCount = batchOps[0].batchTotalCount; // All should have same total
            ongoingOpsText = `${maxIndex} of ${totalCount}`;
        } else {
            ongoingOpsText = stats.ongoingOperationsCount || 0;
        }

        // Get queue count from statistics
        const queuedCount = stats.queuedOperationsCount || 0;

        const newHTML = `
            <div style="display: flex; flex-direction: column; gap: 1em;">
                <div style="display: grid; grid-template-columns: repeat(3, 1fr); gap: 1em;">
                    <div style="padding: 1em; border: 1px solid var(--jf-palette-divider); border-radius: 4px;">
                        <div style="font-size: 0.9em; color: var(--jf-palette-text-secondary); margin-bottom: 0.25em;">In Queue</div>
                        <div style="font-size: 1.5em; font-weight: bold;">${queuedCount}</div>
                    </div>
                    <div style="padding: 1em; border: 1px solid var(--jf-palette-divider); border-radius: 4px;">
                        <div style="font-size: 0.9em; color: var(--jf-palette-text-secondary); margin-bottom: 0.25em;">Avg Duration</div>
                        <div style="font-size: 1.1em;">${avgDuration}</div>
                    </div>
                    <div style="padding: 1em; border: 1px solid var(--jf-palette-divider); border-radius: 4px;">
                        <div style="font-size: 0.9em; color: var(--jf-palette-text-secondary); margin-bottom: 0.25em;">Last Refresh</div>
                        <div style="font-size: 1.1em;">${lastRefresh}</div>
                    </div>
                </div>
                <div style="display: grid; grid-template-columns: repeat(3, 1fr); gap: 1em;">
                    <div style="padding: 1em; border: 1px solid var(--jf-palette-divider); border-radius: 4px;">
                        <div style="font-size: 0.9em; color: var(--jf-palette-text-secondary); margin-bottom: 0.25em;">Total Lists Processed</div>
                        <div style="font-size: 1.5em; font-weight: bold;">${stats.totalLists || 0}</div>
                    </div>
                    <div style="padding: 1em; border: 1px solid var(--jf-palette-divider); border-radius: 4px;">
                        <div style="font-size: 0.9em; color: var(--jf-palette-text-secondary); margin-bottom: 0.25em;">Successful</div>
                        <div style="font-size: 1.5em; font-weight: bold; color: var(--jf-palette-success-main);">${stats.successfulRefreshes || 0}</div>
                    </div>
                    <div style="padding: 1em; border: 1px solid var(--jf-palette-divider); border-radius: 4px;">
                        <div style="font-size: 0.9em; color: var(--jf-palette-text-secondary); margin-bottom: 0.25em;">Failed</div>
                        <div style="font-size: 1.5em; font-weight: bold; color: var(--jf-palette-error-main);">${stats.failedRefreshes || 0}</div>
                    </div>
                </div>
            </div>
        `;

        container.innerHTML = newHTML;
    }

    /**
     * Render refresh history
     */
    function renderRefreshHistory(history) {
        // Query within the visible page to avoid duplicate container issues
        // Query within the visible page to avoid duplicate container issues
        const page = getActiveConfigPage();
        const container = page ? page.querySelector('#refresh-history-container') : null;
        if (!container) return;

        if (!history || history.length === 0) {
            container.innerHTML = '<p style="opacity: 0.6;">No refresh history available. History will appear after refreshing lists.</p>';
            return;
        }

        // Sort by end time (most recent first)
        const sortedHistory = [...history].sort((a, b) => {
            const timeA = a.endTime ? new Date(a.endTime).getTime() : new Date(a.startTime).getTime();
            const timeB = b.endTime ? new Date(b.endTime).getTime() : new Date(b.startTime).getTime();
            return timeB - timeA;
        });

        let html = '<div style="overflow-x: auto;"><table style="width: 100%; border-collapse: collapse;">';
        html += '<thead><tr style="border-bottom: 1px solid var(--jf-palette-divider);">';
        html += '<th style="text-align: left; padding: 0.75em;">List Name</th>';
        html += '<th style="text-align: left; padding: 0.75em;">Type</th>';
        html += '<th style="text-align: left; padding: 0.75em;">Trigger</th>';
        html += '<th style="text-align: left; padding: 0.75em;">Status</th>';
        html += '<th style="text-align: left; padding: 0.75em;">Duration</th>';
        html += '<th style="text-align: left; padding: 0.75em;">End Time</th>';
        html += '</tr></thead><tbody>';

        sortedHistory.forEach(entry => {
            const statusColor = entry.success ? 'var(--jf-palette-success-main)' : 'var(--jf-palette-error-main)';
            const statusText = entry.success ? 'Success' : 'Failed';
            const duration = formatDuration(entry.duration);
            const endTime = entry.endTime ? formatDateTime(entry.endTime) : 'N/A';

            html += '<tr style="border-bottom: 1px solid var(--jf-palette-divider);">';
            html += `<td style="padding: 0.75em;">${escapeHtml(entry.listName)}</td>`;
            html += `<td style="padding: 0.75em;">${escapeHtml(String(entry.listType))}</td>`;
            html += `<td style="padding: 0.75em;">${escapeHtml(String(entry.triggerType))}</td>`;
            html += `<td style="padding: 0.75em; color: ${statusColor};">${statusText}</td>`;
            html += `<td style="padding: 0.75em;">${duration}</td>`;
            html += `<td style="padding: 0.75em;">${endTime}</td>`;
            html += '</tr>';
        });

        html += '</tbody></table></div>';
        container.innerHTML = html;
    }

    /**
     * Format duration in seconds to human-readable string
     */
    function formatDuration(seconds) {
        if (!seconds && seconds !== 0) return 'N/A';

        const totalSeconds = Math.round(seconds);
        if (totalSeconds < 60) {
            return `${totalSeconds}s`;
        } else if (totalSeconds < 3600) {
            const minutes = Math.floor(totalSeconds / 60);
            const secs = totalSeconds % 60;
            return secs > 0 ? `${minutes}m ${secs}s` : `${minutes}m`;
        } else {
            const hours = Math.floor(totalSeconds / 3600);
            const minutes = Math.floor((totalSeconds % 3600) / 60);
            return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
        }
    }

    /**
     * Format ISO date string to readable format
     */
    function formatDateTime(isoString) {
        if (!isoString) return 'N/A';
        try {
            const date = new Date(isoString);
            return date.toLocaleString();
        } catch (e) {
            return isoString;
        }
    }

    /**
     * Show error message
     */
    function showError(message) {
        const page = getActiveConfigPage();
        const container = page ? page.querySelector('#ongoing-operations-container') : null;
        if (container) {
            container.innerHTML = `<p style="color: var(--jf-palette-error-main);">${escapeHtml(message)}</p>`;
        }
    }

    /**
     * Start polling for status updates
     */
    function startPolling(interval) {
        // Don't start polling if aggressive polling is active (it will handle transitions)
        if (aggressivePollingInterval) {
            return;
        }

        if (statusPollingInterval) {
            clearInterval(statusPollingInterval);
        }
        statusPollingInterval = setInterval(() => {
            fetchStatusData();
        }, interval);
    }

    /**
     * Stop polling for status updates
     */
    function stopPolling() {
        if (statusPollingInterval) {
            clearInterval(statusPollingInterval);
            statusPollingInterval = null;
        }
    }

    /**
     * Start aggressive polling (every 1 second) for a short period after refresh operations start
     * This helps catch operations that just began
     */
    function startAggressivePolling() {
        // Stop any existing polling before starting aggressive mode
        stopPolling();
        stopAggressivePolling();

        // Poll every 1 second
        aggressivePollingInterval = setInterval(() => {
            fetchStatusData();
        }, 1000);

        // After 15 seconds, stop aggressive polling
        // The next fetchStatusData call (which happens every 1s) will trigger renderStatusPage
        // which will then start appropriate polling (2s if operations active, 30s if idle)
        aggressivePollingTimeout = setTimeout(() => {
            stopAggressivePolling();
            // Immediately fetch status to trigger transition to appropriate polling
            fetchStatusData();
        }, 15000);
    }

    /**
     * Stop aggressive polling
     */
    function stopAggressivePolling() {
        if (aggressivePollingInterval) {
            clearInterval(aggressivePollingInterval);
            aggressivePollingInterval = null;
        }
        if (aggressivePollingTimeout) {
            clearTimeout(aggressivePollingTimeout);
            aggressivePollingTimeout = null;
        }
    }

    /**
     * Initialize status page event handlers
     */
    function initializeStatusPage() {
        setupRefreshButton();
    }

    /**
     * Setup refresh button - can be called multiple times safely
     */
    function setupRefreshButton() {
        // Query within the visible page to avoid duplicate container issues
        // Query within the visible page to avoid duplicate container issues
        const page = getActiveConfigPage();
        const refreshBtn = page ? page.querySelector('#refresh-status-btn') : null;
        if (refreshBtn && !refreshBtn._statusListenerAttached) {
            refreshBtn.addEventListener('click', function () {
                fetchStatusData();
            });
            refreshBtn._statusListenerAttached = true;
        }
    }

    // Export functions for use in config-init.js
    window.SmartLists = window.SmartLists || {};
    window.SmartLists.Status = {
        loadStatusPage: loadStatusPage,
        initializeStatusPage: initializeStatusPage,
        setupRefreshButton: setupRefreshButton,
        stopPolling: stopPolling,
        startAggressivePolling: startAggressivePolling,
        stopAggressivePolling: stopAggressivePolling
    };

    // Auto-setup refresh button when DOM is ready (if script loads after DOM)
    if (document.readyState !== 'loading') {
        // DOM already loaded, try to setup button immediately
        setTimeout(setupRefreshButton, 0);
    } else {
        // Wait for DOM to be ready
        document.addEventListener('DOMContentLoaded', setupRefreshButton);
    }
})();

