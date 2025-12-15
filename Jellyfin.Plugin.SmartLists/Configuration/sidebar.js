/**
 * SmartLists Sidebar Injection Script
 * 
 * This script attempts to inject a "SmartLists" menu item into the Jellyfin dashboard sidebar.
 * It waits for the sidebar to load and then adds a menu item pointing to the plugin configuration page.
 */

(function() {
    'use strict';

    console.log('[SmartLists] Sidebar injection script loaded');

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
        // Always check parent window if in iframe
        const isInIframe = window.self !== window.top;
        const win = isInIframe ? window.top : window;
        const doc = win.document;
        
        try {
            return doc.getElementById(MENU_ITEM_ID) !== null;
        } catch (e) {
            // Cross-origin error, check current document
            return document.getElementById(MENU_ITEM_ID) !== null;
        }
    }

    /**
     * Creates the sidebar menu item element
     */
    function createSidebarItem() {
        // Get the correct document context
        const isInIframe = window.self !== window.top;
        const doc = isInIframe && window.top ? window.top.document : document;
        
        return createSidebarItemInDocument(doc);
    }

    /**
     * Attempts to find the sidebar navigation container in a specific document
     */
    function findSidebarContainerInDocument(doc) {
        // First, let's do comprehensive debugging to understand the DOM structure
        try {
            console.log('[SmartLists] Searching for sidebar in document:', doc.location?.href || 'unknown');
            
            // Check for common Jellyfin sidebar structures
            const drawerContent = doc.querySelector('.drawerContent');
            const mainDrawer = doc.querySelector('.mainDrawer');
            const drawer = doc.querySelector('.drawer');
            
            console.log('[SmartLists] .drawerContent:', drawerContent ? 'found' : 'not found');
            console.log('[SmartLists] .mainDrawer:', mainDrawer ? 'found' : 'not found');
            console.log('[SmartLists] .drawer:', drawer ? 'found' : 'not found');
            
            // Look for Material-UI components (Jellyfin uses MUI)
            const muiListItems = doc.querySelectorAll('.MuiListItemButton, .MuiListItem-root, [class*="MuiListItem"]');
            if (muiListItems.length > 0) {
                console.log('[SmartLists] Found', muiListItems.length, 'MUI list items');
                
                // Find the common parent container
                const firstItem = muiListItems[0];
                let container = firstItem.parentElement;
                let depth = 0;
                
                // First, try to find the MuiList-root container
                const muiList = firstItem.closest('.MuiList-root, .MuiList, [class*="MuiList"]');
                if (muiList) {
                    const itemsInList = muiList.querySelectorAll('.MuiListItemButton, .MuiListItem-root');
                    if (itemsInList.length >= 2) {
                        console.log('[SmartLists] ✓ Found MUI List container with', itemsInList.length, 'items:', muiList.className);
                        return muiList;
                    }
                }
                
                // Otherwise, trace up from the first item
                while (container && container !== doc.body && depth < 10) {
                    const itemsInContainer = container.querySelectorAll('.MuiListItemButton, .MuiListItem-root');
                    if (itemsInContainer.length >= 2) {
                        console.log('[SmartLists] ✓ Found MUI container with', itemsInContainer.length, 'items:', container.tagName, container.className);
                        // Look for the scroll container or list container
                        const scrollContainer = container.closest('.mainDrawer-scrollContainer, .MuiList-root, [class*="scrollContainer"]');
                        if (scrollContainer) {
                            console.log('[SmartLists] ✓ Using scroll container:', scrollContainer.className);
                            return scrollContainer;
                        }
                        return container;
                    }
                    container = container.parentElement;
                    depth++;
                }
            }
            
            if (drawerContent) {
                console.log('[SmartLists] .drawerContent children:', drawerContent.children.length);
                for (let i = 0; i < drawerContent.children.length; i++) {
                    const child = drawerContent.children[i];
                    console.log('[SmartLists]   Child', i, ':', child.tagName, child.className);
                    const items = child.querySelectorAll('a, .listItem, [data-role="button"]');
                    console.log('[SmartLists]     Items:', items.length);
                }
            }
            
            // Check .mainDrawer more thoroughly
            if (mainDrawer) {
                console.log('[SmartLists] .mainDrawer found, inspecting structure...');
                console.log('[SmartLists] .mainDrawer children:', mainDrawer.children.length);
                for (let i = 0; i < mainDrawer.children.length; i++) {
                    const child = mainDrawer.children[i];
                    console.log('[SmartLists]   Child', i, ':', child.tagName, child.className, child.id);
                    const items = child.querySelectorAll('a, .listItem, [data-role="button"]');
                    console.log('[SmartLists]     Direct items:', items.length);
                    // Check nested structures
                    const nested = child.querySelectorAll('*');
                    console.log('[SmartLists]     Nested elements:', nested.length);
                    // Look for ul, nav, or div containers
                    const containers = child.querySelectorAll('ul, nav, div[class*="nav"], div[class*="menu"]');
                    console.log('[SmartLists]     Container elements:', containers.length);
                    for (let j = 0; j < containers.length; j++) {
                        const container = containers[j];
                        const containerItems = container.querySelectorAll('a, .listItem');
                        if (containerItems.length > 0) {
                            console.log('[SmartLists]     ✓ Container', j, 'has', containerItems.length, 'items:', container.tagName, container.className);
                        }
                    }
                }
                
                // Try to find all links inside mainDrawer
                const allDrawerLinks = mainDrawer.querySelectorAll('a');
                console.log('[SmartLists] All links in .mainDrawer:', allDrawerLinks.length);
                if (allDrawerLinks.length > 0) {
                    // Find the parent container of these links
                    const firstLink = allDrawerLinks[0];
                    let container = firstLink.parentElement;
                    let depth = 0;
                    while (container && container !== mainDrawer && depth < 10) {
                        const linksInContainer = container.querySelectorAll('a');
                        if (linksInContainer.length >= 2) {
                            console.log('[SmartLists] ✓ Found container with', linksInContainer.length, 'links:', container.tagName, container.className);
                            return container;
                        }
                        container = container.parentElement;
                        depth++;
                    }
                }
            }
            
            // Look for any elements with "nav" or "menu" in class names
            const allElements = doc.querySelectorAll('*');
            const navLikeElements = [];
            for (let i = 0; i < Math.min(allElements.length, 1000); i++) {
                const el = allElements[i];
                const className = el.className || '';
                if (typeof className === 'string' && (
                    className.includes('nav') || 
                    className.includes('menu') || 
                    className.includes('drawer') ||
                    className.includes('sidebar')
                )) {
                    navLikeElements.push({
                        tag: el.tagName,
                        class: className,
                        id: el.id,
                        items: el.querySelectorAll('a, .listItem').length
                    });
                }
            }
            console.log('[SmartLists] Found', navLikeElements.length, 'nav-like elements (showing first 10):');
            navLikeElements.slice(0, 10).forEach((el, idx) => {
                console.log('[SmartLists]   ', idx, ':', el.tag, el.class, 'items:', el.items);
            });
            
            // Look for any links that might be menu items
            const allLinks = doc.querySelectorAll('a');
            const menuLinks = [];
            for (let i = 0; i < Math.min(allLinks.length, 100); i++) {
                const link = allLinks[i];
                const text = link.textContent?.trim() || '';
                if (text && (text.includes('Dashboard') || text.includes('Libraries') || text.includes('Collections') || text.includes('Playback'))) {
                    // Find the full parent hierarchy
                    let parent = link.parentElement;
                    const hierarchy = [];
                    while (parent && parent !== doc.body && hierarchy.length < 5) {
                        hierarchy.push({
                            tag: parent.tagName,
                            class: parent.className,
                            id: parent.id
                        });
                        parent = parent.parentElement;
                    }
                    
                    menuLinks.push({
                        text: text,
                        href: link.href,
                        parent: link.parentElement?.tagName,
                        parentClass: link.parentElement?.className,
                        class: link.className,
                        hierarchy: hierarchy
                    });
                }
            }
            if (menuLinks.length > 0) {
                console.log('[SmartLists] Found menu-like links:', menuLinks);
                // Log the full hierarchy for debugging
                menuLinks.forEach((link, idx) => {
                    console.log('[SmartLists] Link', idx, 'hierarchy:', link.hierarchy);
                });
                
                // Try to find the common parent container of these links
                if (menuLinks.length >= 2) {
                    const firstLink = allLinks[Array.from(allLinks).findIndex(l => l.textContent?.includes(menuLinks[0].text))];
                    const secondLink = allLinks[Array.from(allLinks).findIndex(l => l.textContent?.includes(menuLinks[1].text))];
                    if (firstLink && secondLink && firstLink !== secondLink) {
                        const commonParent = findCommonAncestor(firstLink, secondLink);
                        if (commonParent) {
                            console.log('[SmartLists] Common parent of menu links:', commonParent.tagName, commonParent.className, commonParent.id);
                            
                            // If the common parent is a link itself, go up further
                            let container = commonParent;
                            if (container.tagName === 'A' || container.classList.contains('MuiListItemButton')) {
                                container = container.parentElement;
                                console.log('[SmartLists] Common parent was a link, going up to:', container?.tagName, container?.className);
                            }
                            
                            // Look for MUI List container or similar
                            const muiList = container?.closest('.MuiList-root, .MuiList, [class*="MuiList"]');
                            if (muiList) {
                                console.log('[SmartLists] ✓ Found MUI List container:', muiList.className);
                                const items = muiList.querySelectorAll('.MuiListItemButton, a');
                                console.log('[SmartLists] Items in MUI List:', items.length);
                                if (items.length >= 2) {
                                    return muiList;
                                }
                            }
                            
                            // Try to find a container with multiple menu items
                            let current = container;
                            let depth = 0;
                            while (current && current !== doc.body && depth < 10) {
                                const items = current.querySelectorAll('.MuiListItemButton, a[class*="MuiListItem"], .listItem');
                                if (items.length >= 2) {
                                    console.log('[SmartLists] ✓ Found container with', items.length, 'menu items:', current.tagName, current.className);
                                    return current;
                                }
                                current = current.parentElement;
                                depth++;
                            }
                            
                            // Fallback: use the container we found
                            const items = container?.querySelectorAll('a, .listItem, .MuiListItemButton');
                            console.log('[SmartLists] Items in common parent:', items?.length || 0);
                            if (items && items.length > 0) {
                                console.log('[SmartLists] ✓ Using common parent as container');
                                return container;
                            }
                        } else {
                            console.log('[SmartLists] Could not find common ancestor of menu links');
                        }
                    } else {
                        console.log('[SmartLists] First and second link are the same or not found');
                    }
                }
            }
            
            // Helper function to find common ancestor
            function findCommonAncestor(el1, el2) {
                const parents1 = [];
                let current = el1;
                while (current && current !== doc.body) {
                    parents1.push(current);
                    current = current.parentElement;
                }
                
                current = el2;
                while (current && current !== doc.body) {
                    if (parents1.includes(current)) {
                        return current;
                    }
                    current = current.parentElement;
                }
                return null;
            }
        } catch (e) {
            console.error('[SmartLists] Error during DOM inspection:', e);
        }
        
        // Common sidebar selectors in Jellyfin (ordered by likelihood)
        // Updated to include Material-UI selectors
        const selectors = [
            '.mainDrawer-scrollContainer .MuiList-root',
            '.mainDrawer-scrollContainer',
            '.mainDrawer .MuiList-root',
            '.drawerContent .navMenuSection',
            '.drawerContent .navMenu',
            '.drawerContent nav',
            '.drawerContent ul',
            '.drawerContent > div',
            '.drawerContent',
            '.mainDrawer .drawerContent',
            '.mainDrawer',
            '.drawer .drawerContent',
            '.drawer',
            '[data-role="navigation"]',
            '.sidebar-nav',
            'nav ul',
            'nav'
        ];

        for (const selector of selectors) {
            try {
                const container = doc.querySelector(selector);
                if (container) {
                    // Verify it's actually a navigation menu by checking for list items
                    // Include MUI components
                    const listItems = container.querySelectorAll(
                        '.listItem, li > a, a[data-role="button"], .listItem-button, a.listItem, ' +
                        '.MuiListItemButton, .MuiListItem-root, [class*="MuiListItem"]'
                    );
                    const hasListItems = listItems.length > 0;
                    
                    if (hasListItems) {
                        console.log('[SmartLists] ✓ Found sidebar container with selector:', selector, '-', listItems.length, 'menu items found');
                        return container;
                    } else if (selector.includes('drawerContent') || selector.includes('drawer') || selector.includes('scrollContainer')) {
                        // drawerContent or scrollContainer might be the container even without direct items
                        console.log('[SmartLists] Found', selector, ', checking children...');
                        const children = container.children;
                        for (let i = 0; i < children.length; i++) {
                            const child = children[i];
                            const childItems = child.querySelectorAll(
                                '.listItem, li > a, a[data-role="button"], a.listItem, ' +
                                '.MuiListItemButton, .MuiListItem-root'
                            );
                            if (childItems.length > 0) {
                                console.log('[SmartLists] ✓ Found nav items in child:', child.className, '-', childItems.length, 'items');
                                return child;
                            }
                        }
                        // For scrollContainer, return it anyway as it might be dynamically populated
                        if (selector.includes('scrollContainer')) {
                            console.log('[SmartLists] Using scrollContainer (will wait for content):', container.className);
                            return container;
                        }
                    }
                }
            } catch (e) {
                // Cross-origin or other error, skip this selector
                console.log('[SmartLists] Error checking selector', selector, ':', e.message);
            }
        }

        return null;
    }
    
    /**
     * Attempts to find the sidebar navigation container
     * Tries multiple common selectors used in Jellyfin
     */
    function findSidebarContainer() {
        // Always use parent window if available (plugin pages are often in iframes)
        const isInIframe = window.self !== window.top;
        const win = isInIframe ? window.top : window;
        const doc = win.document;
        
        return findSidebarContainerInDocument(doc);
    }
    
    /**
     * Injects the sidebar item into a specific container
     */
    function injectIntoContainer(container, doc) {
        // Prevent duplicate injection
        if (doc.getElementById(MENU_ITEM_ID)) {
            return true;
        }

        // Find all existing menu items to understand the structure
        const allMenuItems = container.querySelectorAll('a.listItem, a[data-role="button"], li > a, .listItem, .listItem-button');
        console.log('[SmartLists] Found', allMenuItems.length, 'existing menu items');

        if (allMenuItems.length === 0) {
            console.log('[SmartLists] No existing menu items found');
            return false;
        }

        // Find insertion point
        let insertionPoint = null;
        
        if (allMenuItems.length > 0) {
            const firstItem = allMenuItems[0];
            insertionPoint = firstItem.parentElement;
            
            if (insertionPoint && insertionPoint.tagName === 'LI') {
                insertionPoint = insertionPoint.parentElement;
            }
            
            insertionPoint = firstItem.closest('ul, nav, .navMenuSection, .navMenu') || insertionPoint;
        }

        if (!insertionPoint) {
            insertionPoint = container;
        }

        console.log('[SmartLists] Insertion point:', insertionPoint.tagName, insertionPoint.className);

        const menuItem = createSidebarItemInDocument(doc);
        
        const needsWrapper = allMenuItems.length > 0 && 
            allMenuItems[0].parentElement && 
            allMenuItems[0].parentElement.tagName === 'LI';
        
        if (needsWrapper) {
            const wrapper = doc.createElement('li');
            wrapper.appendChild(menuItem);
            insertionPoint.appendChild(wrapper);
        } else {
            const lastItem = allMenuItems[allMenuItems.length - 1];
            if (lastItem.parentElement === insertionPoint) {
                insertionPoint.appendChild(menuItem);
            } else {
                if (lastItem.nextSibling) {
                    lastItem.parentElement.insertBefore(menuItem, lastItem.nextSibling);
                } else {
                    lastItem.parentElement.appendChild(menuItem);
                }
            }
        }

        console.log('[SmartLists] Successfully injected sidebar item');
        return true;
    }
    
    /**
     * Creates the sidebar menu item element in a specific document
     */
    function createSidebarItemInDocument(doc) {
        const item = doc.createElement('a');
        item.id = MENU_ITEM_ID;
        item.className = 'listItem listItem-button';
        item.href = PLUGIN_URL;
        item.setAttribute('data-role', 'button');
        
        const icon = doc.createElement('span');
        icon.className = 'listItemIcon material-icons';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = 'playlist_play';
        
        const text = doc.createElement('span');
        text.className = 'listItemBody';
        text.textContent = PLUGIN_NAME;
        
        const content = doc.createElement('div');
        content.className = 'listItemContent';
        content.appendChild(icon);
        content.appendChild(text);
        
        item.appendChild(content);
        
        return item;
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
            console.warn('[SmartLists] Could not find template item, creating from scratch');
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
        
        console.log('[SmartLists] Cloned item with classes:', cloned.className);
        
        return cloned;
    }

    /**
     * Attempts to find an existing menu item to insert after
     * Looks for common dashboard items like "Dashboard", "Libraries", "Playback Reporting", etc.
     */
    function findInsertionPoint(container) {
        // Try to find after "Playback Reporting" if it exists
        const playbackReporting = Array.from(container.querySelectorAll('a')).find(
            item => item.textContent && item.textContent.includes('Playback Reporting')
        );
        if (playbackReporting && playbackReporting.parentElement) {
            return playbackReporting.parentElement;
        }

        // Try to find after "Dashboard" or "Libraries"
        const dashboard = Array.from(container.querySelectorAll('a')).find(
            item => item.textContent && (item.textContent.includes('Dashboard') || item.textContent.includes('Libraries'))
        );
        if (dashboard && dashboard.parentElement) {
            return dashboard.parentElement;
        }

        // Try to find any list item to insert after
        const listItems = container.querySelectorAll('.listItem, li, [data-role="button"]');
        if (listItems.length > 0) {
            return listItems[listItems.length - 1].parentElement || container;
        }

        // Fallback: append to container
        return container;
    }

    /**
     * Injects the sidebar menu item
     */
    function injectSidebarItem() {
        // Prevent duplicate injection
        if (sidebarItemExists()) {
            return true;
        }

        // Get the correct document context (parent if in iframe)
        const isInIframe = window.self !== window.top;
        const win = isInIframe ? window.top : window;
        const doc = win.document;
        
        const container = findSidebarContainer();
        if (!container) {
            console.log('[SmartLists] Could not find sidebar container');
            return false;
        }

        // Find all existing menu items to understand the structure
        // Include Material-UI components
        const allMenuItems = container.querySelectorAll(
            'a.listItem, a[data-role="button"], li > a, .listItem, .listItem-button, ' +
            '.MuiListItemButton, .MuiListItem-root, a[class*="MuiListItemButton"]'
        );
        console.log('[SmartLists] Found', allMenuItems.length, 'existing menu items');

        if (allMenuItems.length === 0) {
            console.log('[SmartLists] No existing menu items found, container HTML:', container.innerHTML.substring(0, 300));
            // If it's a scrollContainer, wait for content to load
            if (container.classList.contains('scrollContainer') || container.classList.contains('mainDrawer-scrollContainer')) {
                console.log('[SmartLists] ScrollContainer is empty, will wait for content via MutationObserver');
                return false; // Will retry
            }
            return false;
        }

        // Find insertion point - look for the parent container of menu items
        let insertionPoint = null;
        
        // Try to find the parent of existing items
        if (allMenuItems.length > 0) {
            const firstItem = allMenuItems[0];
            insertionPoint = firstItem.parentElement;
            
            // If parent is a list item, go up one more level to get the <ul>
            if (insertionPoint && insertionPoint.tagName === 'LI') {
                insertionPoint = insertionPoint.parentElement;
            }
            
            // If still not a good container, try going up more levels
            if (!insertionPoint || (insertionPoint.tagName !== 'UL' && insertionPoint.tagName !== 'NAV' && !insertionPoint.classList.contains('navMenuSection'))) {
                insertionPoint = firstItem.closest('ul, nav, .navMenuSection, .navMenu');
            }
        }

        // Fallback to container itself
        if (!insertionPoint) {
            insertionPoint = container;
        }

        console.log('[SmartLists] Insertion point:', insertionPoint.tagName, insertionPoint.className);

        // Check if we're dealing with MUI components
        const isMUI = allMenuItems.length > 0 && 
            (allMenuItems[0].classList.contains('MuiListItemButton') || 
             allMenuItems[0].classList.contains('MuiListItem-root') ||
             allMenuItems[0].classList.toString().includes('MuiListItem'));
        
        if (isMUI) {
            console.log('[SmartLists] Detected MUI components, creating MUI-compatible menu item');
            const menuItem = createMUISidebarItem(doc);
            
            // Find the MUI List container
            const muiList = insertionPoint.querySelector('.MuiList-root') || 
                           allMenuItems[0].closest('.MuiList-root') ||
                           insertionPoint;
            
            // Try to find "Plugins" menu item to insert after it
            const pluginsItem = Array.from(allMenuItems).find(item => {
                const text = item.textContent?.trim() || '';
                return text === 'Plugins' || text.includes('Plugins');
            });
            
            if (pluginsItem) {
                console.log('[SmartLists] Found Plugins item, inserting after it');
                // Find the parent list item (MuiListItem-root) - this is the actual container element
                let pluginsListItem = pluginsItem.closest('.MuiListItem-root');
                
                console.log('[SmartLists] Plugins item:', pluginsItem.tagName, pluginsItem.className);
                console.log('[SmartLists] Plugins list item:', pluginsListItem?.tagName, pluginsListItem?.className);
                console.log('[SmartLists] MUI List:', muiList.tagName, muiList.className);
                
                // If no MuiListItem-root, the pluginsItem itself might be the item, or its parent might be
                if (!pluginsListItem) {
                    // Check if pluginsItem's parent is a list item or if pluginsItem needs to be wrapped
                    const pluginsParent = pluginsItem.parentElement;
                    console.log('[SmartLists] No MuiListItem-root, pluginsItem parent:', pluginsParent?.tagName, pluginsParent?.className);
                    
                    // If parent is a UL (nested list), we need to insert after the pluginsItem within that list
                    if (pluginsParent && pluginsParent.tagName === 'UL' && pluginsParent.classList.contains('MuiList-root')) {
                        // Insert right after pluginsItem in the nested list
                        const nextSib = pluginsItem.nextSibling;
                        if (nextSib === null || (nextSib.parentElement === pluginsParent)) {
                            pluginsParent.insertBefore(menuItem, nextSib);
                            console.log('[SmartLists] Successfully inserted after Plugins in nested list');
                            return true;
                        } else {
                            pluginsParent.appendChild(menuItem);
                            console.log('[SmartLists] Appended to nested list after Plugins');
                            return true;
                        }
                    }
                    
                    // If parent is a div that could be a list item, use it
                    if (pluginsParent && pluginsParent.tagName === 'DIV' && pluginsParent.classList.contains('MuiListItem-root')) {
                        pluginsListItem = pluginsParent;
                    }
                }
                
                if (pluginsListItem) {
                    const pluginsParent = pluginsListItem.parentElement;
                    const nextSibling = pluginsListItem.nextSibling;
                    
                    console.log('[SmartLists] Plugins parent:', pluginsParent?.tagName, pluginsParent?.className);
                    console.log('[SmartLists] Next sibling:', nextSibling?.nodeName, nextSibling?.nodeType);
                    console.log('[SmartLists] Is pluginsParent === muiList?', pluginsParent === muiList);
                    
                    // Verify that pluginsListItem is actually a child of muiList
                    if (pluginsParent === muiList) {
                        // Safe to insert - nextSibling can be null (which appends) or a valid sibling
                        if (nextSibling === null) {
                            muiList.appendChild(menuItem);
                            console.log('[SmartLists] Successfully appended after Plugins item (no next sibling)');
                        } else if (nextSibling.parentElement === muiList) {
                            muiList.insertBefore(menuItem, nextSibling);
                            console.log('[SmartLists] Successfully inserted before next sibling');
                        } else {
                            muiList.appendChild(menuItem);
                            console.log('[SmartLists] Next sibling not a child, appended instead');
                        }
                    } else if (pluginsParent) {
                        // Plugins item is in a different container, try to insert after it
                        if (nextSibling === null) {
                            pluginsParent.appendChild(menuItem);
                            console.log('[SmartLists] Successfully appended after Plugins item in different container');
                        } else if (nextSibling.parentElement === pluginsParent) {
                            pluginsParent.insertBefore(menuItem, nextSibling);
                            console.log('[SmartLists] Successfully inserted after Plugins item in different container');
                        } else {
                            pluginsParent.appendChild(menuItem);
                            console.log('[SmartLists] Next sibling not a child, appended instead');
                        }
                    } else {
                        muiList.appendChild(menuItem);
                        console.log('[SmartLists] No parent found, appended to muiList');
                    }
                } else {
                    // No MuiListItem-root found, pluginsItem is directly in a list
                    const pluginsParent = pluginsItem.parentElement;
                    console.log('[SmartLists] No MuiListItem-root, using pluginsItem parent:', pluginsParent?.tagName, pluginsParent?.className);
                    
                    // Check if parent is a nested UL list (section)
                    if (pluginsParent && pluginsParent.tagName === 'UL' && pluginsParent.classList.contains('MuiList-root')) {
                        // Insert after pluginsItem in the nested list
                        const nextSib = pluginsItem.nextSibling;
                        if (nextSib === null || nextSib.parentElement === pluginsParent) {
                            pluginsParent.insertBefore(menuItem, nextSib);
                            console.log('[SmartLists] Successfully inserted after Plugins in nested UL');
                            return true;
                        } else {
                            pluginsParent.appendChild(menuItem);
                            console.log('[SmartLists] Appended to nested UL after Plugins');
                            return true;
                        }
                    }
                    
                    // If parent's parent is muiList, insert after parent
                    if (pluginsParent && pluginsParent.parentElement === muiList) {
                        const nextSib = pluginsParent.nextSibling;
                        if (nextSib === null || nextSib.parentElement === muiList) {
                            muiList.insertBefore(menuItem, nextSib);
                            console.log('[SmartLists] Successfully inserted using pluginsItem parent');
                        } else {
                            muiList.appendChild(menuItem);
                            console.log('[SmartLists] Next sibling issue, appended instead');
                        }
                    } else {
                        // Last resort: append to end
                        muiList.appendChild(menuItem);
                        console.log('[SmartLists] Last resort: appended to muiList');
                    }
                }
            } else {
                console.log('[SmartLists] Plugins item not found, inserting at end');
                // Insert after the last MUI item
                const lastItem = allMenuItems[allMenuItems.length - 1];
                const lastListItem = lastItem.closest('.MuiListItem-root');
                if (lastListItem && lastListItem.parentElement === muiList) {
                    try {
                        muiList.insertBefore(menuItem, lastListItem.nextSibling);
                    } catch (e) {
                        console.warn('[SmartLists] insertBefore failed, using appendChild:', e);
                        muiList.appendChild(menuItem);
                    }
                } else {
                    muiList.appendChild(menuItem);
                }
            }
        } else {
            const menuItem = createSidebarItemInDocument(doc);
            
            // Determine if we need a wrapper (check if other items are wrapped in <li>)
            const needsWrapper = allMenuItems.length > 0 && 
                allMenuItems[0].parentElement && 
                allMenuItems[0].parentElement.tagName === 'LI';
            
            if (needsWrapper) {
                const wrapper = doc.createElement('li');
                wrapper.appendChild(menuItem);
                insertionPoint.appendChild(wrapper);
            } else {
                // Insert after the last item
                const lastItem = allMenuItems[allMenuItems.length - 1];
                if (lastItem.parentElement === insertionPoint) {
                    // Same parent, append to end
                    insertionPoint.appendChild(menuItem);
                } else {
                    // Different parent structure, append after last item
                    if (lastItem.nextSibling) {
                        lastItem.parentElement.insertBefore(menuItem, lastItem.nextSibling);
                    } else {
                        lastItem.parentElement.appendChild(menuItem);
                    }
                }
            }
        }

        console.log('[SmartLists] Successfully injected sidebar item');
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
        const hasContent = (scrollContainer?.querySelectorAll('.MuiListItemButton, .listItem').length || 0) > 0 ||
                          (mainDrawer?.querySelectorAll('.MuiListItemButton, .listItem').length || 0) > 0;
        if (hasContent) {
            console.log('[SmartLists] Container already has content, no observer needed');
            return null;
        }

        console.log('[SmartLists] Setting up MutationObserver for sidebar content');
        const target = scrollContainer || mainDrawer;
        
        const observer = new MutationObserver((mutations) => {
            const nowHasContent = target.querySelectorAll('.MuiListItemButton, .listItem, .MuiList-root').length > 0;
            if (nowHasContent) {
                console.log('[SmartLists] Content detected in sidebar, attempting injection');
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

        // Also watch the entire document for MUI components appearing
        const docObserver = new MutationObserver((mutations) => {
            const muiItems = doc.querySelectorAll('.MuiListItemButton');
            if (muiItems.length >= 2) {
                console.log('[SmartLists] MUI items detected in document, attempting injection');
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

        // Try to inject
        if (injectSidebarItem()) {
            console.log('[SmartLists] Sidebar item injected successfully');
            return;
        }

        // If this is the first attempt and we found a scrollContainer, set up observer
        if (retryCount === 0) {
            const isInIframe = window.self !== window.top;
            const win = isInIframe ? window.top : window;
            const doc = win.document;
            setupSidebarObserver(doc);
        }

        // Retry if we haven't exceeded max retries
        if (retryCount < MAX_RETRIES) {
            setTimeout(() => {
                attemptInjection(retryCount + 1);
            }, RETRY_INTERVAL);
        } else {
            console.warn('[SmartLists] Failed to inject sidebar item after maximum retries');
        }
    }

    /**
     * Injects the sidebar script into the main document so it persists globally
     * This ensures it runs on dashboard pages, not just the plugin configuration page
     */
    function injectIntoMainDocument() {
        try {
            // Check if we're in an iframe (plugin pages are often in iframes)
            const isInIframe = window.self !== window.top;
            const targetWindow = isInIframe ? window.top : window;
            const targetDocument = targetWindow.document;
            
            // Check if script already injected
            if (targetDocument.getElementById('smartlists-sidebar-script')) {
                console.log('[SmartLists] Script already injected in main window');
                // Trigger injection if sidebar exists
                setTimeout(() => {
                    if (targetWindow.smartListsSidebarInitialized) {
                        targetWindow.dispatchEvent(new Event('smartlists-trigger-injection'));
                    }
                }, 500);
                return;
            }

            // Get the current script's URL - try multiple methods
            let scriptUrl = '/web/configurationpage?name=sidebar.js';
            
            // Try document.currentScript first
            if (document.currentScript && document.currentScript.src) {
                scriptUrl = document.currentScript.src;
            } else {
                // Try to find the script tag
                const scripts = document.getElementsByTagName('script');
                for (let i = scripts.length - 1; i >= 0; i--) {
                    const s = scripts[i];
                    if (s.src && (s.src.includes('sidebar.js') || s.getAttribute('data-smartlists'))) {
                        scriptUrl = s.src;
                        break;
                    }
                }
            }
            
            // Make sure it's an absolute URL
            if (scriptUrl && !scriptUrl.startsWith('http') && !scriptUrl.startsWith('/')) {
                scriptUrl = '/web/' + scriptUrl;
            }
            if (!scriptUrl || scriptUrl === '') {
                scriptUrl = '/web/configurationpage?name=sidebar.js';
            }

            console.log('[SmartLists] Injecting script into main window:', scriptUrl);

            // Use direct code injection for more reliable execution
            // This ensures the script runs immediately in the main window context
            injectScriptCodeDirectly(targetWindow, targetDocument);
            
            // Also try the src-based approach as primary method
            const script = targetDocument.createElement('script');
            script.id = 'smartlists-sidebar-script-src';
            script.src = scriptUrl;
            script.setAttribute('data-smartlists', 'injected-src');
            
            // Add load handler to verify it loaded
            script.onload = function() {
                console.log('[SmartLists] Src-based injected script loaded successfully in main window');
                // Give it a moment to initialize, then trigger injection
                setTimeout(() => {
                    if (targetWindow.smartListsSidebarInitialized) {
                        targetWindow.dispatchEvent(new Event('smartlists-trigger-injection'));
                    }
                }, 1000);
            };
            
            // Add error handler
            script.onerror = function() {
                console.warn('[SmartLists] Src-based script failed to load, but inline version should work');
            };
            
            // Wait for main document to be ready
            const injectSrcScript = () => {
                if (!targetDocument.getElementById('smartlists-sidebar-script-src')) {
                    targetDocument.head.appendChild(script);
                }
            };
            
            if (targetDocument.readyState === 'loading') {
                targetDocument.addEventListener('DOMContentLoaded', injectSrcScript);
            } else {
                injectSrcScript();
            }
            
        } catch (e) {
            console.error('[SmartLists] Error injecting into main window:', e);
            // Fallback: try to inject code directly
            try {
                const targetWindow = isInIframe ? window.top : window;
                const targetDocument = targetWindow.document;
                injectScriptCodeDirectly(targetWindow, targetDocument);
            } catch (e2) {
                console.error('[SmartLists] Fallback injection also failed:', e2);
            }
        }
    }
    
    /**
     * Injects the script code directly into the target window
     * This ensures the script runs immediately in the main window context
     * Uses a persistent injection mechanism via localStorage
     */
    function injectScriptCodeDirectly(targetWindow, targetDocument) {
        // Check localStorage to see if we should inject
        const injectionKey = 'smartlists-sidebar-injected';
        const lastInjection = targetWindow.localStorage?.getItem(injectionKey);
        const now = Date.now();
        
        // If injected recently (within last hour), skip to avoid spam
        if (lastInjection && (now - parseInt(lastInjection, 10)) < 3600000) {
            console.log('[SmartLists] Script injection skipped (recently injected)');
            // Still trigger injection check if script is already loaded
            setTimeout(() => {
                if (targetWindow.smartListsSidebarInitialized) {
                    targetWindow.dispatchEvent(new Event('smartlists-trigger-injection'));
                }
            }, 500);
            return;
        }
        
        if (targetDocument.getElementById('smartlists-sidebar-script')) {
            console.log('[SmartLists] Script already injected, triggering injection check');
            setTimeout(() => {
                if (targetWindow.smartListsSidebarInitialized) {
                    targetWindow.dispatchEvent(new Event('smartlists-trigger-injection'));
                }
            }, 500);
            return;
        }
        
        console.log('[SmartLists] Injecting script code directly into main window');
        
        // Mark as injected in localStorage
        if (targetWindow.localStorage) {
            targetWindow.localStorage.setItem(injectionKey, now.toString());
        }
        
        // Create a script that will load and run the sidebar injection
        const script = targetDocument.createElement('script');
        script.id = 'smartlists-sidebar-script';
        script.setAttribute('data-smartlists', 'injected');
        script.src = '/web/configurationpage?name=sidebar.js';
        
        script.onload = function() {
            console.log('[SmartLists] Sidebar script loaded in main window');
            // The script will initialize itself when it loads
        };
        
        script.onerror = function() {
            console.error('[SmartLists] Failed to load sidebar script');
            // Remove from localStorage on error so we can retry
            if (targetWindow.localStorage) {
                targetWindow.localStorage.removeItem(injectionKey);
            }
        };
        
        const injectScript = () => {
            if (!targetDocument.getElementById('smartlists-sidebar-script')) {
                targetDocument.head.appendChild(script);
                console.log('[SmartLists] Script tag injected into main window head');
            }
        };
        
        if (targetDocument.readyState === 'loading') {
            targetDocument.addEventListener('DOMContentLoaded', injectScript);
        } else {
            injectScript();
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

        console.log('[SmartLists] Setting up MutationObserver to watch for sidebar');

        const observer = new MutationObserver((mutations) => {
            let shouldCheck = false;
            mutations.forEach((mutation) => {
                if (mutation.addedNodes.length > 0) {
                    // Check if any added nodes might be sidebar-related
                    for (const node of mutation.addedNodes) {
                        if (node.nodeType === Node.ELEMENT_NODE) {
                            const element = node;
                            if (element.classList && (
                                element.classList.contains('drawerContent') ||
                                element.classList.contains('navMenu') ||
                                element.classList.contains('navMenuSection') ||
                                element.querySelector('.drawerContent, .navMenu, .navMenuSection')
                            )) {
                                shouldCheck = true;
                                console.log('[SmartLists] Sidebar-related element detected in DOM changes');
                                break;
                            }
                        }
                    }
                }
            });

            if (shouldCheck && !sidebarItemExists()) {
                console.log('[SmartLists] Sidebar detected, attempting injection');
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
            console.log('[SmartLists] MutationObserver active, watching for sidebar');
        }
    }

    /**
     * Initialize when DOM is ready
     */
    function init() {
        // Check if we're in an iframe (plugin pages are often in iframes)
        const isInIframe = window.self !== window.top;
        const isPluginPage = document.querySelector('.SmartListsConfigurationPage') !== null || 
                            window.location.href.includes('configurationpage?name=SmartLists');
        
        // If we're on the plugin configuration page, inject script into main window and try direct injection
        if (isPluginPage) {
            console.log('[SmartLists] Running on plugin page');
            
            // Try to inject directly into parent window's sidebar if accessible
            if (isInIframe) {
                try {
                    const parentWin = window.top;
                    const parentDoc = parentWin.document;
                    
                    // Try to find and inject into parent's sidebar immediately
                    const attemptParentInjection = (retries = 0) => {
                        if (retries > 20) {
                            console.log('[SmartLists] Giving up on direct parent injection after 20 retries');
                            return;
                        }
                        
                        const parentContainer = findSidebarContainerInDocument(parentDoc);
                        if (parentContainer) {
                            console.log('[SmartLists] Found sidebar in parent window, injecting directly');
                            if (injectIntoContainer(parentContainer, parentDoc)) {
                                console.log('[SmartLists] Successfully injected into parent sidebar');
                                return;
                            }
                        }
                        
                        // Retry
                        setTimeout(() => attemptParentInjection(retries + 1), 500);
                    };
                    
                    // Start attempting injection
                    setTimeout(() => attemptParentInjection(0), 500);
                } catch (e) {
                    console.log('[SmartLists] Cannot access parent window directly:', e.message);
                }
            }
            
            // Inject script into main window for persistence across navigation
            console.log('[SmartLists] Injecting script into main window for persistence');
            injectIntoMainDocument();
            
            // Don't run injection logic on plugin page itself (it doesn't have a sidebar)
            return;
        }
        
        if (isInIframe) {
            // We're in an iframe but not the plugin page - inject into parent
            console.log('[SmartLists] Running in iframe, injecting script into parent window');
            injectIntoMainDocument();
            return;
        }

        // We're in the main window on a regular page - prevent multiple initializations
        if (window.smartListsSidebarInitialized) {
            console.log('[SmartLists] Already initialized, skipping');
            return;
        }
        window.smartListsSidebarInitialized = true;

        console.log('[SmartLists] Running in main window, initializing sidebar injection');
        console.log('[SmartLists] Current URL:', window.location.href);

        // Function to attempt injection with retries
        const tryInjection = () => {
            // Check if sidebar exists on this page
            const hasSidebar = findSidebarContainer() !== null;
            if (!hasSidebar) {
                console.log('[SmartLists] No sidebar found on this page, will inject when sidebar appears');
                // Set up observer to watch for sidebar appearance
                setupMutationObserver();
                return;
            }
            
            console.log('[SmartLists] Sidebar found, attempting injection');
            attemptInjection();
            setupMutationObserver();
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

        // Listen for custom event to trigger injection (from plugin page or navigation)
        window.addEventListener('smartlists-trigger-injection', () => {
            console.log('[SmartLists] Received injection trigger event');
            setTimeout(tryInjection, 200);
        });

        // Also listen for navigation events (Jellyfin uses SPA navigation)
        // Re-inject if the sidebar is re-rendered
        // Use a flag to prevent multiple wrappers
        if (!window.smartListsHistoryWrapped) {
            window.smartListsHistoryWrapped = true;
            
            const originalPushState = history.pushState;
            history.pushState = function() {
                originalPushState.apply(history, arguments);
                setTimeout(() => {
                    if (!sidebarItemExists()) {
                        console.log('[SmartLists] Navigation detected (pushState), attempting injection');
                        attemptInjection();
                    }
                }, 500);
            };

            const originalReplaceState = history.replaceState;
            history.replaceState = function() {
                originalReplaceState.apply(history, arguments);
                setTimeout(() => {
                    if (!sidebarItemExists()) {
                        console.log('[SmartLists] Navigation detected (replaceState), attempting injection');
                        attemptInjection();
                    }
                }, 500);
            };
            
            // Also listen for popstate (back/forward)
            window.addEventListener('popstate', () => {
                setTimeout(() => {
                    if (!sidebarItemExists()) {
                        console.log('[SmartLists] Navigation detected (popstate), attempting injection');
                        attemptInjection();
                    }
                }, 500);
            });
        }

        // Also listen for Jellyfin's custom navigation events if they exist
        document.addEventListener('pagebeforeshow', () => {
            setTimeout(() => {
                if (!sidebarItemExists()) {
                    console.log('[SmartLists] Page before show event, attempting injection');
                    attemptInjection();
                }
            }, 500);
        }, true);
        
        // Listen for pageshow event
        window.addEventListener('pageshow', () => {
            setTimeout(() => {
                if (!sidebarItemExists()) {
                    console.log('[SmartLists] Page show event, attempting injection');
                    attemptInjection();
                }
            }, 500);
        });
    }

    // Start initialization
    init();
})();

