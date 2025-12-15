/**
 * SmartLists Sidebar Injection Script
 * 
 * This script attempts to inject a "SmartLists" menu item into the Jellyfin dashboard sidebar.
 * It waits for the sidebar to load and then adds a menu item pointing to the plugin configuration page.
 */

(function() {
    'use strict';

    // Configuration
    const PLUGIN_NAME = 'SmartLists';
    const PLUGIN_URL = '/web/#/configurationpage?name=SmartLists&tab=create';
    const MENU_ITEM_ID = 'smartlists-sidebar-item';
    const MAX_RETRIES = 50; // Maximum attempts to find sidebar (5 seconds with 100ms intervals)
    const RETRY_INTERVAL = 100; // Milliseconds between retries

    /**
     * Checks if the sidebar item already exists to prevent duplicates
     */
    function sidebarItemExists() {
        return document.getElementById(MENU_ITEM_ID) !== null;
    }

    /**
     * Attempts to find the sidebar navigation container in a specific document
     */
    function findSidebarContainerInDocument(doc) {
        try {
            // Look for Material-UI components (Jellyfin uses MUI)
            const muiListItems = doc.querySelectorAll('.MuiListItemButton, .MuiListItem-root, [class*="MuiListItem"]');
            if (muiListItems.length > 0) {
                const firstItem = muiListItems[0];
                
                // First, try to find the MuiList-root container
                const muiList = firstItem.closest('.MuiList-root, .MuiList, [class*="MuiList"]');
                if (muiList) {
                    const itemsInList = muiList.querySelectorAll('.MuiListItemButton, .MuiListItem-root');
                    if (itemsInList.length >= 2) {
                        return muiList;
                    }
                }
                
                // Otherwise, trace up from the first item
                let container = firstItem.parentElement;
                let depth = 0;
                while (container && container !== doc.body && depth < 10) {
                    const itemsInContainer = container.querySelectorAll('.MuiListItemButton, .MuiListItem-root');
                    if (itemsInContainer.length >= 2) {
                        const scrollContainer = container.closest('.mainDrawer-scrollContainer, .MuiList-root, [class*="scrollContainer"]');
                        if (scrollContainer) {
                            return scrollContainer;
                        }
                        return container;
                    }
                    container = container.parentElement;
                    depth++;
                }
            }
            
            // Fallback selectors
            const selectors = [
                '.mainDrawer-scrollContainer .MuiList-root',
                '.mainDrawer-scrollContainer',
                '.mainDrawer .MuiList-root',
                '.mainDrawer'
            ];

            for (const selector of selectors) {
                try {
                    const container = doc.querySelector(selector);
                    if (container) {
                        const listItems = container.querySelectorAll(
                            '.MuiListItemButton, .MuiListItem-root, [class*="MuiListItem"]'
                        );
                        if (listItems.length > 0) {
                            return container;
                        }
                    }
                } catch (e) {
                    // Skip on error
                }
            }
        } catch (e) {
            // Error during search
        }

        return null;
    }
    
    /**
     * Attempts to find the sidebar navigation container
     * Tries multiple common selectors used in Jellyfin
     */
    function findSidebarContainer() {
        return findSidebarContainerInDocument(document);
    }
    
    /**
     * Creates a Material-UI compatible sidebar menu item
     */
    function createMUISidebarItem(doc) {
        // Find an existing menu item (like Plugins) to clone its structure and CSS classes
        const existingItems = doc.querySelectorAll('a.MuiListItemButton-root');
        let templateItem = null;
        
        if (existingItems.length > 0) {
            // Prefer Plugins item, fallback to Dashboard or Libraries
            templateItem = Array.from(existingItems).find(item => {
                const text = item.textContent?.trim() || '';
                return text === 'Plugins';
            }) || Array.from(existingItems).find(item => {
                const text = item.textContent?.trim() || '';
                return text === 'Dashboard' || text === 'Libraries';
            }) || existingItems[0];
        }
        
        if (!templateItem) {
            // Fallback: create basic structure
            const link = doc.createElement('a');
            link.id = MENU_ITEM_ID;
            link.href = PLUGIN_URL;
            link.className = 'MuiButtonBase-root MuiListItemButton-root MuiListItemButton-gutters';
            link.setAttribute('tabindex', '0');
            return link;
        }
        
        // Clone the entire structure to preserve all CSS classes (including dynamically generated ones)
        const cloned = templateItem.cloneNode(true);
        cloned.id = MENU_ITEM_ID;
        cloned.href = PLUGIN_URL;
        
        // Remove any state attributes that shouldn't be copied
        cloned.removeAttribute('aria-selected');
        cloned.removeAttribute('data-emby');
        cloned.classList.remove('Mui-selected');
        
        // Update the icon - look for SVG or material-icons
        const iconWrapper = cloned.querySelector('.MuiListItemIcon-root');
        if (iconWrapper) {
            // Remove existing icon
            const existingIcon = iconWrapper.querySelector('svg, .material-icons');
            if (existingIcon) {
                existingIcon.remove();
            }
            
            // Add playlist_play icon - try SVG first, fallback to material-icons
            const svgIcon = doc.createElementNS('http://www.w3.org/2000/svg', 'svg');
            svgIcon.setAttribute('class', existingIcon?.getAttribute('class') || 'MuiSvgIcon-root MuiSvgIcon-fontSizeMedium');
            svgIcon.setAttribute('focusable', 'false');
            svgIcon.setAttribute('aria-hidden', 'true');
            svgIcon.setAttribute('viewBox', '0 0 24 24');
            svgIcon.setAttribute('data-testid', 'PlaylistPlayIcon');
            
            const path = doc.createElementNS('http://www.w3.org/2000/svg', 'path');
            path.setAttribute('d', 'M19 9H2v2h17V9zm0-4H2v2h17V5zM2 15h13v-2H2v2zm15-2v6l5-3-5-3z');
            svgIcon.appendChild(path);
            
            iconWrapper.appendChild(svgIcon);
        }
        
        // Update the text
        const textSpan = cloned.querySelector('.MuiTypography-root.MuiListItemText-primary, .MuiListItemText-root .MuiTypography-root');
        if (textSpan) {
            textSpan.textContent = PLUGIN_NAME;
        } else {
            // Fallback: find any text element
            const textElements = cloned.querySelectorAll('.MuiTypography-root, .MuiListItemText-root span');
            if (textElements.length > 0) {
                textElements[0].textContent = PLUGIN_NAME;
            }
        }
        
        // Handle click for hash-based routing
        cloned.addEventListener('click', function(e) {
            // Check if Jellyfin's router is available
            if (window.Dashboard && window.Dashboard.navigate) {
                e.preventDefault();
                window.Dashboard.navigate(PLUGIN_URL.replace('/web/#', ''));
            } else if (window.location.hash) {
                // Use hash-based navigation
                e.preventDefault();
                window.location.hash = PLUGIN_URL.replace('/web/#', '');
            }
            // If neither works, let the default href behavior handle it
        });
        
        return cloned;
    }

    /**
     * Helper function to safely insert menuItem after a reference element
     */
    function insertAfterMenuItem(menuItem, referenceElement, parentList) {
        const nextSibling = referenceElement.nextSibling;
        if (nextSibling === null || nextSibling.parentElement === parentList) {
            parentList.insertBefore(menuItem, nextSibling);
        } else {
            parentList.appendChild(menuItem);
        }
    }

    /**
     * Injects the sidebar menu item
     */
    function injectSidebarItem() {
        // Prevent duplicate injection
        if (sidebarItemExists()) {
            return true;
        }

        const container = findSidebarContainer();
        if (!container) {
            return false;
        }

        const allMenuItems = container.querySelectorAll(
            '.MuiListItemButton, .MuiListItem-root, a[class*="MuiListItemButton"]'
        );

        if (allMenuItems.length === 0) {
            return false;
        }

        // Find the MUI List container
        const muiList = allMenuItems[0].closest('.MuiList-root') || container;
        const menuItem = createMUISidebarItem(document);
            
            // Try to find "Plugins" menu item to insert after it
            const pluginsItem = Array.from(allMenuItems).find(item => {
                const text = item.textContent?.trim() || '';
                return text === 'Plugins' || text.includes('Plugins');
            });
            
            if (pluginsItem) {
                let pluginsListItem = pluginsItem.closest('.MuiListItem-root');
                
                if (!pluginsListItem) {
                    const pluginsParent = pluginsItem.parentElement;
                    
                    // If parent is a UL (nested list), insert after pluginsItem within that list
                    if (pluginsParent && pluginsParent.tagName === 'UL' && pluginsParent.classList.contains('MuiList-root')) {
                        insertAfterMenuItem(menuItem, pluginsItem, pluginsParent);
                        return true;
                    }
                    
                    if (pluginsParent && pluginsParent.tagName === 'DIV' && pluginsParent.classList.contains('MuiListItem-root')) {
                        pluginsListItem = pluginsParent;
                    }
                }
                
                if (pluginsListItem) {
                    const pluginsParent = pluginsListItem.parentElement || muiList;
                    insertAfterMenuItem(menuItem, pluginsListItem, pluginsParent);
                } else {
                    const pluginsParent = pluginsItem.parentElement;
                    
                    if (pluginsParent && pluginsParent.tagName === 'UL' && pluginsParent.classList.contains('MuiList-root')) {
                        insertAfterMenuItem(menuItem, pluginsItem, pluginsParent);
                        return true;
                    }
                    
                    if (pluginsParent && pluginsParent.parentElement === muiList) {
                        insertAfterMenuItem(menuItem, pluginsParent, muiList);
                    } else {
                        muiList.appendChild(menuItem);
                    }
                }
            } else {
                // Insert after the last MUI item
                const lastItem = allMenuItems[allMenuItems.length - 1];
                const lastListItem = lastItem.closest('.MuiListItem-root');
                if (lastListItem && lastListItem.parentElement === muiList) {
                    try {
                        muiList.insertBefore(menuItem, lastListItem.nextSibling);
                    } catch (e) {
                        muiList.appendChild(menuItem);
                    }
                } else {
                    muiList.appendChild(menuItem);
                }
            }

        return true;
    }

    /**
     * Sets up a MutationObserver to watch for sidebar content loading
     */
    function setupSidebarObserver(doc) {
        // Watch both the scrollContainer and the mainDrawer
        const scrollContainer = doc.querySelector('.mainDrawer-scrollContainer');
        const mainDrawer = doc.querySelector('.mainDrawer');
        
        if (!scrollContainer && !mainDrawer) {
            return null;
        }

        // Check if already has content anywhere
        const hasContent = (scrollContainer?.querySelectorAll('.MuiListItemButton').length || 0) > 0 ||
                          (mainDrawer?.querySelectorAll('.MuiListItemButton').length || 0) > 0;
        if (hasContent) {
            return null;
        }

        const target = scrollContainer || mainDrawer;
        
        const observer = new MutationObserver(() => {
            const nowHasContent = target.querySelectorAll('.MuiListItemButton, .MuiList-root').length > 0;
            if (nowHasContent) {
                observer.disconnect();
                setTimeout(() => {
                    attemptInjection(0);
                }, 500);
            }
        });

        observer.observe(target, {
            childList: true,
            subtree: true
        });

        const docObserver = new MutationObserver(() => {
            const muiItems = doc.querySelectorAll('.MuiListItemButton');
            if (muiItems.length >= 2) {
                docObserver.disconnect();
                observer.disconnect();
                setTimeout(() => {
                    attemptInjection(0);
                }, 500);
            }
        });

        docObserver.observe(doc.body, {
            childList: true,
            subtree: true
        });

        // Also set a timeout to stop observing after a while
        setTimeout(() => {
            observer.disconnect();
            docObserver.disconnect();
        }, 30000); // Stop after 30 seconds

        return observer;
    }

    /**
     * Main injection function with retry logic
     */
    function attemptInjection(retryCount = 0) {
        // Check if already injected
        if (sidebarItemExists()) {
            return;
        }

        if (injectSidebarItem()) {
            return;
        }

        // If this is the first attempt, set up observer
        if (retryCount === 0) {
            setupSidebarObserver(document);
        }

        // Retry if we haven't exceeded max retries
        if (retryCount < MAX_RETRIES) {
            setTimeout(() => {
                attemptInjection(retryCount + 1);
            }, RETRY_INTERVAL);
        }
    }

    /**
     * Sets up MutationObserver to watch for sidebar changes
     */
    function setupMutationObserver() {
        if (window.smartListsObserverSetup) {
            return;
        }
        window.smartListsObserverSetup = true;

        const observer = new MutationObserver((mutations) => {
            let shouldCheck = false;
            mutations.forEach((mutation) => {
                if (mutation.addedNodes.length > 0) {
                    for (const node of mutation.addedNodes) {
                        if (node.nodeType === Node.ELEMENT_NODE) {
                            const element = node;
                            if (element.classList && (
                                element.classList.contains('MuiList-root') ||
                                element.classList.contains('MuiListItemButton') ||
                                element.querySelector('.MuiList-root, .MuiListItemButton')
                            )) {
                                shouldCheck = true;
                                break;
                            }
                        }
                    }
                }
            });

            if (shouldCheck && !sidebarItemExists()) {
                setTimeout(attemptInjection, 200);
            }
        });

        // Observe the entire document for changes
        const target = document.body || document.documentElement;
        if (target) {
            observer.observe(target, {
                childList: true,
                subtree: true
            });
        }
    }

    /**
     * Initialize when DOM is ready
     */
    function init() {
        // Skip if running in iframe (middleware injects script in main window)
        const isInIframe = window.self !== window.top;
        if (isInIframe) {
            return;
        }

        if (window.smartListsSidebarInitialized) {
            return;
        }
        window.smartListsSidebarInitialized = true;

        const tryInjection = () => {
            const hasSidebar = findSidebarContainer() !== null;
            if (!hasSidebar) {
                setupMutationObserver();
                return;
            }
            
            attemptInjection();
        };

        // Wait for DOM to be ready
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', () => {
                setTimeout(tryInjection, 1500); // Longer delay to ensure sidebar is fully rendered
            });
        } else {
            // DOM already ready
            setTimeout(tryInjection, 1500);
        }


        // Helper function for navigation handlers
        const handleNavigation = () => {
            setTimeout(() => {
                if (!sidebarItemExists()) {
                    attemptInjection();
                }
            }, 500);
        };

        // Listen for navigation events (Jellyfin uses SPA navigation)
        if (!window.smartListsHistoryWrapped) {
            window.smartListsHistoryWrapped = true;
            
            const originalPushState = history.pushState;
            history.pushState = function() {
                originalPushState.apply(history, arguments);
                handleNavigation();
            };

            const originalReplaceState = history.replaceState;
            history.replaceState = function() {
                originalReplaceState.apply(history, arguments);
                handleNavigation();
            };
            
            window.addEventListener('popstate', handleNavigation);
        }

        document.addEventListener('pagebeforeshow', handleNavigation, true);
        window.addEventListener('pageshow', handleNavigation);
    }

    // Start initialization
    init();
})();

