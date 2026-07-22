// Template catalog for the "Start from a template" picker on the Create tab.
// Each dto is a partial SmartListDto containing only definition fields —
// never instance fields (Id, FileName, Jellyfin IDs, dates, stats, images).
// Rule MemberName/Operator/TargetValue strings and sort names must match the
// backend vocabulary (FieldRegistry.cs / OrderFactory) — dev/validate-templates.js
// checks the catalog against config-core.js.
(function (SmartLists) {
    'use strict';

    SmartLists.TEMPLATES = [
        {
            id: 'tv-channel',
            name: 'TV Channel',
            category: 'TV',
            description: 'Interleaves unwatched episodes from all your shows like a TV channel: one episode per series, round-robin, in order.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Episode'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'PlaybackStatus', Operator: 'Equal', TargetValue: 'Unplayed' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'Round Robin', SortOrder: 'Ascending', GroupByField: 'SeriesName' }] },
                AutoRefresh: 'OnLibraryChanges'
            }
        },
        {
            id: 'franchise-tv-channel',
            name: 'Franchise TV Channel',
            category: 'TV',
            description: 'Rotates through your collections (franchises) in broadcast order, resuming the least recently watched one. Crossover episodes that aired within 3 days stay together.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Episode'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'PlaybackStatus', Operator: 'Equal', TargetValue: 'Unplayed' }
                    ]
                }],
                Order: {
                    SortOptions: [{
                        SortBy: 'Least Recently Watched Round Robin',
                        SortOrder: 'Ascending',
                        GroupByField: 'Collections',
                        WithinGroupOrder: 'AirDate',
                        AirBlockWindowDays: 3
                    }]
                },
                AutoRefresh: 'OnAllChanges'
            }
        },
        {
            id: 'continue-watching',
            name: 'Continue Watching',
            category: 'TV',
            description: 'The next unwatched episode of every show you are in the middle of, with the show you have not touched longest surfacing first. Updates itself as you watch.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Episode'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'NextUnwatched', Operator: 'Equal', TargetValue: 'true' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'Least Recently Watched Round Robin', SortOrder: 'Ascending', GroupByField: 'SeriesName' }] },
                AutoRefresh: 'OnAllChanges'
            }
        },
        {
            id: 'saturday-cartoons',
            name: 'Saturday Morning Cartoons',
            category: 'TV',
            description: 'Animated episodes with bumper clips woven in every 2 episodes, visible only on weekend mornings. Tag your bumper videos and enter that tag in the bumper rule.',
            adminOnly: false,
            inputHint: 'Enter the tag of your bumper videos in the empty bumper rule value (tag some short videos first).',
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Episode'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'Genres', Operator: 'Contains', TargetValue: 'Animation' },
                        { MemberName: 'PlaybackStatus', Operator: 'Equal', TargetValue: 'Unplayed' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'Round Robin', SortOrder: 'Ascending', GroupByField: 'SeriesName' }] },
                Bumpers: {
                    ExpressionSets: [{
                        Expressions: [
                            { MemberName: 'Tags', Operator: 'Contains', TargetValue: '' }
                        ]
                    }],
                    MediaTypes: ['Video'],
                    BumperOrder: 'Random',
                    Interval: 2
                },
                VisibilitySchedules: [
                    { Action: 'Enable', Trigger: 'Weekly', DayOfWeek: 6, Time: '06:00:00' },
                    { Action: 'Disable', Trigger: 'Weekly', DayOfWeek: 6, Time: '12:00:00' },
                    { Action: 'Enable', Trigger: 'Weekly', DayOfWeek: 0, Time: '06:00:00' },
                    { Action: 'Disable', Trigger: 'Weekly', DayOfWeek: 0, Time: '12:00:00' }
                ]
            }
        },
        {
            id: 'because-you-watched',
            name: 'Because You Watched…',
            category: 'Movies',
            description: 'Movies similar to a title you pick, best matches first. Compares genres, tags, actors and directors.',
            adminOnly: false,
            inputHint: 'Type the title to find similar movies for in the empty rule value.',
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Movie'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'SimilarTo', Operator: 'Equal', TargetValue: '' }
                    ]
                }],
                SimilarityComparisonFields: ['Genre', 'Tags', 'Actors', 'Directors'],
                Order: { SortOptions: [{ SortBy: 'Similarity', SortOrder: 'Descending' }] },
                MaxItems: 25
            }
        },
        {
            id: 'balanced-mix',
            name: 'Balanced Genre Mix',
            category: 'Movies',
            description: 'Up to 15 movies each of Action, Comedy and Drama, kept in that block order. Change the genres to taste — each rule block has its own item limit.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Movie'],
                ExpressionSets: [
                    { Expressions: [{ MemberName: 'Genres', Operator: 'Contains', TargetValue: 'Action' }], MaxItems: 15 },
                    { Expressions: [{ MemberName: 'Genres', Operator: 'Contains', TargetValue: 'Comedy' }], MaxItems: 15 },
                    { Expressions: [{ MemberName: 'Genres', Operator: 'Contains', TargetValue: 'Drama' }], MaxItems: 15 }
                ],
                Order: { SortOptions: [{ SortBy: 'Rule Block Order', SortOrder: 'Ascending' }] }
            }
        },
        {
            id: 'fresh-and-unseen',
            name: 'Fresh & Unseen',
            category: 'Movies',
            description: 'Movies added in the last 30 days that you have not watched yet, newest first. Kept current automatically.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Movie'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'DateCreated', Operator: 'NewerThan', TargetValue: '30:days' },
                        { MemberName: 'PlaybackStatus', Operator: 'Equal', TargetValue: 'Unplayed' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'DateCreated', SortOrder: 'Descending' }] },
                AutoRefresh: 'OnAllChanges'
            }
        },
        {
            id: 'trakt-trending',
            name: 'Trending Now (Trakt)',
            category: 'Movies',
            description: 'A collection of the movies trending on Trakt right now, refreshed daily. Requires a Trakt Client ID in the plugin settings (admin, Settings tab). Hidden while empty.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Collection',
                MediaTypes: ['Movie'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'ExternalList', Operator: 'Equal', TargetValue: 'https://trakt.tv/movies/trending' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'External List Order', SortOrder: 'Ascending' }] },
                Schedules: [{ Trigger: 'Daily', Time: '06:00:00' }],
                HideWhenEmpty: true,
                MaxItems: 50
            }
        },
        {
            id: 'weekly-jams',
            name: 'Weekly Jams (ListenBrainz)',
            category: 'Music',
            description: 'Your personalized ListenBrainz Weekly Jams as a playlist, in list order, refreshed weekly. No API key needed — just your ListenBrainz username in the feed URL.',
            adminOnly: false,
            inputHint: 'Paste your feed URL in the empty rule value: https://listenbrainz.org/syndication-feed/user/YOUR_USERNAME/recommendations?recommendation_type=weekly-jams',
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Audio'],
                ExpressionSets: [{
                    Expressions: [
                        { MemberName: 'ExternalList', Operator: 'Equal', TargetValue: '' }
                    ]
                }],
                Order: { SortOptions: [{ SortBy: 'External List Order', SortOrder: 'Ascending' }] },
                Schedules: [{ Trigger: 'Weekly', DayOfWeek: 1, Time: '08:00:00' }]
            }
        },
        {
            id: 'album-roulette',
            name: 'Album Roulette',
            category: 'Music',
            description: 'A few random complete albums (at least 5 tracks each), tracks in album order, capped at 3 hours. Re-roll by refreshing the list.',
            adminOnly: false,
            inputHint: null,
            dto: {
                Type: 'Playlist',
                MediaTypes: ['Audio'],
                ExpressionSets: [],
                RandomGroupSelection: { Enabled: true, GroupBy: 'Album', MinimumItems: 5 },
                Order: {
                    SortOptions: [
                        { SortBy: 'AlbumName', SortOrder: 'Ascending' },
                        { SortBy: 'TrackNumber', SortOrder: 'Ascending' }
                    ]
                },
                MaxPlayTimeMinutes: 180
            }
        }
    ];

    function visibleTemplates(page) {
        const isUserPage = SmartLists.isUserPage(page);
        const caps = SmartLists.userCapabilities || {};
        return SmartLists.TEMPLATES.filter(function (t) {
            if (isUserPage && t.adminOnly) {
                return false;
            }
            // Hide collection templates from users who cannot create collections
            if (isUserPage && t.dto.Type === 'Collection' && !caps.CanCreateCollections) {
                return false;
            }
            return true;
        });
    }

    function findTemplate(templateId) {
        for (let i = 0; i < SmartLists.TEMPLATES.length; i++) {
            if (SmartLists.TEMPLATES[i].id === templateId) {
                return SmartLists.TEMPLATES[i];
            }
        }
        return null;
    }

    // Clear selection, description, and Use-button state, and re-show the
    // picker (populateFormFromDto hides it in edit mode). Called from
    // initTemplatePicker after rebuilding the options and from clearForm.
    SmartLists.resetTemplatePicker = function (page) {
        const container = page.querySelector('#templatePickerContainer');
        if (container) {
            container.style.display = '';
        }
        const select = page.querySelector('#templateSelect');
        if (select) {
            select.value = '';
        }
        const descriptionDiv = page.querySelector('#templateDescription');
        if (descriptionDiv) {
            descriptionDiv.textContent = '';
            descriptionDiv.style.display = 'none';
        }
        const useBtn = page.querySelector('#useTemplateBtn');
        if (useBtn) {
            useBtn.disabled = true;
        }
    };

    SmartLists.initTemplatePicker = function (page) {
        const select = page.querySelector('#templateSelect');
        if (!select) {
            return;
        }

        const templates = visibleTemplates(page);
        let html = '<option value="">Select a template...</option>';
        let currentCategory = null;
        templates.forEach(function (t) {
            if (t.category !== currentCategory) {
                if (currentCategory !== null) {
                    html += '</optgroup>';
                }
                html += '<optgroup label="' + SmartLists.escapeHtml(t.category) + '">';
                currentCategory = t.category;
            }
            html += '<option value="' + SmartLists.escapeHtml(t.id) + '">' + SmartLists.escapeHtml(t.name) + '</option>';
        });
        if (currentCategory !== null) {
            html += '</optgroup>';
        }
        select.innerHTML = html;

        // Rebuilding options silently resets the value to '' without a change
        // event - sync the description and Use button so an SPA page revisit
        // doesn't keep a stale description and an enabled Use button
        SmartLists.resetTemplatePicker(page);

        if (!select._templatePickerBound) {
            select._templatePickerBound = true;
            select.addEventListener('change', function () {
                const template = findTemplate(select.value);
                const descriptionDiv = page.querySelector('#templateDescription');
                const useBtn = page.querySelector('#useTemplateBtn');
                if (descriptionDiv) {
                    descriptionDiv.textContent = template ? template.description : '';
                    descriptionDiv.style.display = template ? 'block' : 'none';
                }
                if (useBtn) {
                    useBtn.disabled = !template;
                }
            });
        }
    };

    SmartLists.useTemplate = function (page, templateId) {
        const template = findTemplate(templateId);
        if (!template) {
            return;
        }

        // Deep-copy so form population can never mutate the catalog
        const dto = JSON.parse(JSON.stringify(template.dto));

        try {
            SmartLists.populateFormFromDto(page, dto, { name: template.name });
        } catch (formError) {
            console.error('Error applying template:', formError);
            SmartLists.showNotification('Error applying template: ' + formError.message);
            // Re-arm the guard: a partial population may have left the
            // template's empty placeholder rule in the form
            page._templatePlaceholderPending = !!template.inputHint;
            return;
        }

        page._templatePlaceholderPending = !!template.inputHint;

        if (template.inputHint) {
            SmartLists.showNotification(template.inputHint, 'info');
            // Notifications auto-dismiss - keep the hint (which may contain a
            // URL pattern to copy) readable in the picker description too
            const descriptionDiv = page.querySelector('#templateDescription');
            if (descriptionDiv) {
                descriptionDiv.textContent = template.description + ' — ' + template.inputHint;
                descriptionDiv.style.display = 'block';
            }
            // Focus the first empty rule value so the user sees what to fill in
            const emptyInput = SmartLists.findFirstEmptyRuleValueInput(page);
            if (emptyInput) {
                emptyInput.focus();
            }
        } else {
            SmartLists.showNotification('Template applied - review the settings and click Create.', 'success');
        }
    };
})(window.SmartLists = window.SmartLists || {});
