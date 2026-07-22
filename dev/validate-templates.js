#!/usr/bin/env node
// Validates the template catalog (config-templates.js) against the frontend
// vocabulary in config-core.js. Run: node dev/validate-templates.js
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const cfgDir = path.join(__dirname, '..', 'Jellyfin.Plugin.SmartLists', 'Configuration');
const coreSrc = fs.readFileSync(path.join(cfgDir, 'config-core.js'), 'utf8');
// rulesSrc is intentionally never executed in the vm - it is only text-searched
// (indexOf) for quoted field names; running it would need far heavier DOM stubs
const rulesSrc = fs.readFileSync(path.join(cfgDir, 'config-rules.js'), 'utf8');
const templatesSrc = fs.readFileSync(path.join(cfgDir, 'config-templates.js'), 'utf8');

const stubEl = { classList: { contains: function () { return false; } } };
const sandbox = {
    window: {},
    document: {
        querySelector: function () { return null; },
        querySelectorAll: function () { return []; },
        addEventListener: function () {},
        createElement: function () { return stubEl; }
    },
    console: console,
    setTimeout: setTimeout,
    clearTimeout: clearTimeout
};
sandbox.window.SmartLists = {};
vm.createContext(sandbox);
vm.runInContext(coreSrc, sandbox, { filename: 'config-core.js' });
vm.runInContext(templatesSrc, sandbox, { filename: 'config-templates.js' });

const SL = sandbox.window.SmartLists;
const errors = [];
function check(cond, msg) { if (!cond) { errors.push(msg); } }

check(Array.isArray(SL.TEMPLATES) && SL.TEMPLATES.length > 0, 'SmartLists.TEMPLATES missing or empty');

const sortValues = (SL.SORT_OPTIONS || []).map(function (o) { return typeof o === 'string' ? o : o.value; });
const groupFields = (SL.ROUND_ROBIN_GROUP_FIELDS || []).map(function (o) { return typeof o === 'string' ? o : o.value; });
const OPERATORS = ['Equal', 'NotEqual', 'Contains', 'NotContains', 'IsIn', 'IsNotIn', 'MatchRegex',
    'GreaterThan', 'LessThan', 'GreaterThanOrEqual', 'LessThanOrEqual',
    'After', 'Before', 'NewerThan', 'OlderThan', 'Weekday'];
const MEDIA_TYPES = ['Episode', 'Series', 'Season', 'Movie', 'Audio', 'MusicAlbum', 'MusicVideo', 'Video', 'Photo', 'Book', 'AudioBook'];
const AUTO_REFRESH = ['Never', 'OnLibraryChanges', 'OnAllChanges'];
const TRIGGERS = ['None', 'Daily', 'Weekly', 'Monthly', 'Interval', 'Yearly'];
const seenIds = {};

(SL.TEMPLATES || []).forEach(function (t) {
    const p = 'template "' + t.id + '": ';
    check(t.id && t.name && t.category && t.description, p + 'id/name/category/description required');
    check(!seenIds[t.id], p + 'duplicate id');
    seenIds[t.id] = true;
    check(t.dto && typeof t.dto === 'object', p + 'dto required');
    if (!t.dto) { return; }
    const dto = t.dto;
    check(dto.Type === 'Playlist' || dto.Type === 'Collection', p + 'Type must be Playlist|Collection');
    check(Array.isArray(dto.MediaTypes) && dto.MediaTypes.length > 0, p + 'MediaTypes required');
    (dto.MediaTypes || []).forEach(function (m) {
        check(MEDIA_TYPES.indexOf(m) !== -1, p + 'unknown MediaType ' + m);
    });
    check(Array.isArray(dto.ExpressionSets), p + 'ExpressionSets must be an array');
    ['Id', 'FileName', 'JellyfinPlaylistId', 'JellyfinCollectionId', 'DateCreated', 'LastRefreshed',
        'ItemCount', 'TotalRuntimeMinutes', 'CustomImages', 'UserPlaylists', 'UserId', 'CreatedByUserId'
    ].forEach(function (k) {
        check(!(k in dto), p + 'instance field ' + k + ' must not be in a template dto');
    });
    function checkExpressionSets(sets, where) {
        (sets || []).forEach(function (set) {
            check(Array.isArray(set.Expressions), p + where + ' set missing Expressions array');
            (set.Expressions || []).forEach(function (e) {
                check(typeof e.MemberName === 'string' && e.MemberName.length > 0, p + where + ' rule missing MemberName');
                check(OPERATORS.indexOf(e.Operator) !== -1, p + where + ' unknown Operator ' + e.Operator);
                check(typeof e.TargetValue === 'string', p + where + ' TargetValue must be a string');
                // Every valid field name appears as a quoted string in the frontend sources
                const quoted = "'" + e.MemberName + "'";
                check(coreSrc.indexOf(quoted) !== -1 || rulesSrc.indexOf(quoted) !== -1,
                    p + where + ' MemberName not found in config-core.js/config-rules.js: ' + e.MemberName);
            });
        });
    }
    checkExpressionSets(dto.ExpressionSets, 'rule');
    if (dto.Bumpers) {
        checkExpressionSets(dto.Bumpers.ExpressionSets, 'bumper');
        check(['Random', 'Name', 'ReleaseDate'].indexOf(dto.Bumpers.BumperOrder) !== -1, p + 'bad BumperOrder');
        check(dto.Bumpers.Interval >= 1, p + 'bumper Interval must be >= 1');
        check(dto.Type === 'Playlist', p + 'Bumpers are playlist-only');
    }
    ((dto.Order || {}).SortOptions || []).forEach(function (s) {
        check(sortValues.indexOf(s.SortBy) !== -1, p + 'unknown SortBy ' + s.SortBy);
        check(s.SortOrder === 'Ascending' || s.SortOrder === 'Descending', p + 'bad SortOrder for ' + s.SortBy);
        if (s.GroupByField) {
            check(groupFields.indexOf(s.GroupByField) !== -1, p + 'unknown GroupByField ' + s.GroupByField);
        }
        if (s.WithinGroupOrder) {
            check(s.WithinGroupOrder === 'AirDate', p + 'WithinGroupOrder must be AirDate');
        }
        if (s.AirBlockWindowDays !== undefined) {
            check(s.GroupByField === 'Collections' && s.WithinGroupOrder === 'AirDate',
                p + 'AirBlockWindowDays needs GroupByField Collections + WithinGroupOrder AirDate');
            check(s.AirBlockWindowDays >= 0 && s.AirBlockWindowDays <= 30, p + 'AirBlockWindowDays out of range');
        }
    });
    if (dto.AutoRefresh !== undefined) {
        check(AUTO_REFRESH.indexOf(dto.AutoRefresh) !== -1, p + 'bad AutoRefresh ' + dto.AutoRefresh);
    }
    (dto.Schedules || []).concat(dto.VisibilitySchedules || []).forEach(function (s) {
        check(TRIGGERS.indexOf(s.Trigger) !== -1, p + 'bad schedule Trigger ' + s.Trigger);
        if (s.Time !== undefined) {
            check(/^\d{2}:\d{2}:00$/.test(s.Time), p + 'schedule Time must be HH:MM:00, got ' + s.Time);
        }
        if (s.DayOfWeek !== undefined) {
            check(typeof s.DayOfWeek === 'number' && s.DayOfWeek >= 0 && s.DayOfWeek <= 6, p + 'DayOfWeek must be integer 0-6');
        }
    });
    (dto.VisibilitySchedules || []).forEach(function (s) {
        check(s.Action === 'Enable' || s.Action === 'Disable', p + 'visibility schedule needs Action Enable|Disable');
    });
    if (dto.RandomGroupSelection) {
        check(['Artists', 'AlbumArtists', 'Album', 'SeriesName', 'Genres', 'Studios', 'Tags'].indexOf(dto.RandomGroupSelection.GroupBy) !== -1,
            p + 'bad RandomGroupSelection.GroupBy');
    }
    if (t.inputHint) {
        // Placeholder templates must actually contain an empty value to fill
        const allSets = (dto.ExpressionSets || []).concat(dto.Bumpers ? dto.Bumpers.ExpressionSets : []);
        const hasEmpty = allSets.some(function (set) {
            return (set.Expressions || []).some(function (e) { return e.TargetValue === ''; });
        });
        check(hasEmpty, p + 'inputHint set but no empty TargetValue');
    }
});

if (errors.length) {
    console.error('TEMPLATE VALIDATION FAILED (' + errors.length + '):');
    errors.forEach(function (e) { console.error('  - ' + e); });
    process.exit(1);
}
console.log('All ' + SL.TEMPLATES.length + ' templates valid.');
