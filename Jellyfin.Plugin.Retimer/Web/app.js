(function () {
    'use strict';

    // The page lives at <base>/Retimer, next to the web client at <base>/web/.
    var base = location.pathname.replace(/\/Retimer\/?$/i, '');
    var token = null;
    var status = null;

    var view = document.getElementById('view');
    var messageBar = document.getElementById('message');
    var activeCount = document.getElementById('activeCount');

    // Every job the server told the page about, by id, and the counter of the last answer.
    var jobs = {};
    var jobsVersion = 0;
    var pollTimer = null;
    var onJobs = null;      // The open screen's way to hear about changed jobs.
    var leave = null;       // The open screen's clean-up.
    var routeToken = 0;     // Grows with every navigation, so a late answer of an old screen is dropped.

    document.getElementById('homeLink').href = base + '/web/';

    // ---------------------------------------------------------------- small helpers

    function h(tag, props) {
        var node = document.createElement(tag);
        var key;
        props = props || {};
        for (key in props) {
            if (!Object.prototype.hasOwnProperty.call(props, key)) { continue; }
            var value = props[key];
            if (value === null || value === undefined || value === false) { continue; }
            if (key === 'class') { node.className = value; }
            else if (key === 'text') { node.textContent = value; }
            else if (key === 'on') { for (var name in value) { node.addEventListener(name, value[name]); } }
            else if (value === true) { node.setAttribute(key, ''); }
            else { node.setAttribute(key, value); }
        }
        for (var i = 2; i < arguments.length; i++) { add(node, arguments[i]); }
        return node;
    }

    function add(node, child) {
        if (child === null || child === undefined || child === false) { return; }
        if (Array.isArray(child)) { child.forEach(function (c) { add(node, c); }); return; }
        node.append(child);
    }

    // Empties a node and gives it new children; null, false and lists are handled as in h().
    function fill(node) {
        node.replaceChildren();
        for (var i = 1; i < arguments.length; i++) { add(node, arguments[i]); }
    }

    function show(node) {
        view.replaceChildren(node);
    }

    function say(text, kind) {
        if (!text) { messageBar.hidden = true; return; }
        messageBar.className = 'message' + (kind === 'info' ? ' info' : '');
        messageBar.textContent = text;
        messageBar.hidden = false;
    }

    function api(path, options) {
        options = options || {};
        options.headers = Object.assign({ 'Accept': 'application/json' }, options.headers || {});
        if (token) { options.headers['Authorization'] = 'MediaBrowser Token="' + token + '"'; }
        if (options.body !== undefined && typeof options.body !== 'string') {
            options.headers['Content-Type'] = 'application/json';
            options.body = JSON.stringify(options.body);
        }
        return fetch(base + path, options);
    }

    function problem(response) {
        return response.json().then(function (body) {
            return body && body.detail ? body.detail : 'The request failed (' + response.status + ').';
        }, function () {
            return 'The request failed (' + response.status + ').';
        });
    }

    // Calls the server and gives the answer's JSON. A refusal becomes an Error with the server's sentence.
    function call(path, options) {
        return api(path, options).then(function (response) {
            if (response.ok) { return response.status === 204 ? null : response.json(); }
            return problem(response).then(function (text) { throw new Error(text); });
        }, function () {
            throw new Error('The server could not be reached.');
        });
    }

    function image(id, tag, width, kind) {
        return base + '/Items/' + id + '/Images/' + (kind || 'Primary') + '?fillWidth=' + width + '&quality=90' + (tag ? '&tag=' + encodeURIComponent(tag) : '');
    }

    // A picture the server does not have, such as the poster of a show that was removed, is asked for once.
    var noPicture = {};

    function picture(id, tag, width, kind) {
        if (!tag) { return null; }
        var src = image(id, tag, width, kind);
        if (noPicture[src]) { return null; }
        var img = h('img', { src: src, alt: '', loading: 'lazy' });
        img.addEventListener('error', function () { noPicture[src] = true; img.remove(); });
        return img;
    }

    function plural(count, one, many) {
        return count + ' ' + (count === 1 ? one : many);
    }

    function money(value) {
        if (value > 0 && value < 0.001) { return 'less than $0.001'; }
        return '$' + (value < 0.1 && value > 0 ? value.toFixed(3) : value.toFixed(2));
    }

    // Keeps a number and its unit on one line, so a sentence never breaks before "s.".
    function tidy(text) {
        return text ? text.replace(/(\d) (s|ms|min)\b/g, '$1\u00a0$2') : text;
    }

    function ago(iso) {
        if (!iso) { return ''; }
        var seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
        if (seconds < 45) { return 'just now'; }
        if (seconds < 3600) { return plural(Math.round(seconds / 60), 'minute', 'minutes') + ' ago'; }
        if (seconds < 86400) { return plural(Math.round(seconds / 3600), 'hour', 'hours') + ' ago'; }
        return new Date(iso).toLocaleString();
    }

    function size(bytes) {
        if (!bytes) { return null; }
        return bytes < 1024 * 1024 ? Math.max(1, Math.round(bytes / 1024)) + ' KB' : (bytes / 1024 / 1024).toFixed(1) + ' MB';
    }

    function isActive(job) {
        return job.State === 'queued' || job.State === 'working';
    }

    function usageText(usage) {
        if (!usage) { return ''; }
        var text = 'Today: ' + (usage.MaxJobs > 0 ? usage.Jobs + ' of ' + plural(usage.MaxJobs, 'job', 'jobs') : plural(usage.Jobs, 'job', 'jobs'));
        text += ', ' + money(usage.CostUsd) + (usage.MaxCostUsd > 0 ? ' of ' + money(usage.MaxCostUsd) : '') + ' spent.';
        return text;
    }

    function nearLimit(usage) {
        return !!usage && ((usage.MaxJobs > 0 && usage.Jobs >= usage.MaxJobs * 0.8) || (usage.MaxCostUsd > 0 && usage.CostUsd >= usage.MaxCostUsd * 0.8));
    }

    // Asks before something that costs or removes. Resolves to true when the user agrees.
    function confirmBox(title, body, okText) {
        var dialog = document.getElementById('confirm');
        document.getElementById('confirmTitle').textContent = title;
        var holder = document.getElementById('confirmBody');
        holder.replaceChildren();
        (Array.isArray(body) ? body : [body]).forEach(function (line) { if (line) { holder.append(h('p', { text: line })); } });
        document.getElementById('confirmOk').textContent = okText;
        return new Promise(function (resolve) {
            function closed() {
                dialog.removeEventListener('close', closed);
                resolve(dialog.returnValue === 'ok');
            }
            dialog.addEventListener('close', closed);
            dialog.returnValue = 'cancel';
            dialog.showModal();
        });
    }

    // ---------------------------------------------------------------- jobs

    function pollJobs() {
        clearTimeout(pollTimer);
        if (!status || document.hidden) { return; }
        call('/Retimer/Jobs?since=' + jobsVersion).then(function (answer) {
            var changed = answer.Jobs || [];
            jobsVersion = answer.Version;
            status.Usage = answer.Usage;
            changed.forEach(function (job) { jobs[job.Id] = job; });
            renderCount();
            if (changed.length > 0 && onJobs) { onJobs(changed); }
        }).catch(function () {
            // The next round tries again. A lost server shows up when the user does something.
        }).then(function () {
            var busy = Object.keys(jobs).some(function (id) { return isActive(jobs[id]); });
            pollTimer = setTimeout(pollJobs, busy ? 2000 : 12000);
        });
    }

    function renderCount() {
        var count = Object.keys(jobs).filter(function (id) { return isActive(jobs[id]); }).length;
        activeCount.textContent = count;
        activeCount.hidden = count === 0;
    }

    function activeFor(itemId) {
        var found = null;
        Object.keys(jobs).forEach(function (id) {
            if (jobs[id].ItemId === itemId && isActive(jobs[id])) { found = jobs[id]; }
        });
        return found;
    }

    // Starts jobs and tells what the server made of the request.
    function start(request) {
        return call('/Retimer/Jobs', { method: 'POST', body: request }).then(function (answer) {
            var parts = [];
            if (answer.Refused) { parts.push(answer.Refused); }
            if (answer.Skipped > 0) {
                parts.push(plural(answer.Skipped, 'video was', 'videos were') + ' left out. ' + (answer.SkippedBecause || ''));
            }
            if (answer.Refused && answer.Started > 0) { parts.unshift(plural(answer.Started, 'job was', 'jobs were') + ' started before that.'); }
            say(parts.join(' '), answer.Refused || answer.Skipped ? undefined : 'info');
            pollJobs();
            return answer;
        });
    }

    // The jobs the user asked to cancel. A running job takes a few seconds to stop, and the button says so meanwhile.
    var cancelling = {};

    function cancelButton(job, redraw) {
        var asked = !!cancelling[job.Id];
        return h('button', { class: 'button', text: asked ? 'Cancelling…' : 'Cancel', disabled: asked, on: { click: function () {
            cancelling[job.Id] = true;
            redraw();
            call('/Retimer/Jobs/' + job.Id, { method: 'DELETE' }).then(pollJobs).catch(function (error) {
                delete cancelling[job.Id];
                redraw();
                say(error.message);
            });
        } } });
    }

    document.addEventListener('visibilitychange', function () { if (!document.hidden) { pollJobs(); } });

    // ---------------------------------------------------------------- screen: the shows

    function showsScreen(libraryId) {
        var mine = routeToken;
        var holder = h('div');
        show(h('p', { class: 'muted pad', text: 'Loading your libraries…' }));

        call('/Retimer/Libraries').then(function (libraries) {
            if (mine !== routeToken) { return; }
            if (libraries.length === 0) {
                show(h('div', { class: 'pad' },
                    h('h1', { text: 'Your shows' }),
                    h('p', { class: 'muted', text: 'No library of shows or films is visible to you here.' + (status.IsAdministrator ? ' Check the libraries in the settings.' : '') })));
                return;
            }

            var library = libraries.filter(function (l) { return l.Id === libraryId; })[0] || libraries[0];
            var tabs = h('nav', { class: 'tabs', 'aria-label': 'Libraries' }, libraries.map(function (l) {
                return h('a', { href: '#/library/' + l.Id, text: l.Name, class: l.Id === library.Id ? 'current' : null, 'aria-current': l.Id === library.Id ? 'page' : null });
            }));

            var filter = h('input', { type: 'search', placeholder: 'Filter by title', 'aria-label': 'Filter by title' });
            var sort = h('select', { 'aria-label': 'Sort by' },
                h('option', { value: 'name', text: 'Sort by name' }),
                h('option', { value: 'missing', text: 'Most missing first' }));
            var onlyMissing = h('input', { type: 'checkbox' });
            var summary = h('span', { class: 'muted' });
            var grid = h('div', { class: 'grid' });
            var shows = [];

            function draw() {
                var text = filter.value.trim().toLowerCase();
                var list = shows.filter(function (s) {
                    return (!text || s.Name.toLowerCase().indexOf(text) >= 0) && (!onlyMissing.checked || s.Have < s.Total);
                });
                if (sort.value === 'missing') {
                    list = list.slice().sort(function (a, b) { return (b.Total - b.Have) - (a.Total - a.Have) || a.Name.localeCompare(b.Name); });
                }

                var busy = {};
                Object.keys(jobs).forEach(function (id) { if (isActive(jobs[id])) { busy[jobs[id].ShowId] = true; } });

                grid.replaceChildren();
                list.forEach(function (s) {
                    var missing = s.Total - s.Have;
                    var poster = h('div', { class: 'poster' },
                        h('div', { class: 'letter', text: s.Name }),
                        picture(s.Id, s.ImageTag, 300),
                        s.Total > 0 ? h('span', { class: 'badge' + (missing === 0 ? ' done' : ''), text: missing === 0 ? '✓' : String(missing), title: missing === 0 ? 'Every video has a subtitle' : plural(missing, 'video has', 'videos have') + ' no subtitle' }) : null,
                        busy[s.Id] ? h('span', { class: 'badge busy', text: 'Working' }) : null);
                    if (s.Total > 0) {
                        var meter = h('i');
                        meter.style.width = Math.round(100 * s.Have / s.Total) + '%';
                        poster.append(h('div', { class: 'meter' }, meter));
                    }
                    var line = s.Total === 0 ? 'No videos'
                        : s.IsMovie ? (missing === 0 ? 'Has a subtitle' : 'No subtitle')
                        : missing === 0 ? 'All ' + s.Total + ' have a subtitle' : missing + ' of ' + s.Total + ' missing';
                    grid.append(h('a', { class: 'card', href: '#/show/' + s.Id },
                        poster,
                        h('div', { class: 'title', text: s.Name, title: s.Name }),
                        h('div', { class: 'line', text: (s.Year ? s.Year + ' · ' : '') + line + (s.OwnLanguage ? ' · ' + s.Language.toUpperCase() : '') })));
                });
                if (list.length === 0) {
                    grid.append(h('p', { class: 'muted', text: shows.length === 0 ? 'This library is empty.' : 'No show matches this filter.' }));
                }

                var videos = shows.reduce(function (sum, s) { return sum + s.Total; }, 0);
                var have = shows.reduce(function (sum, s) { return sum + s.Have; }, 0);
                summary.textContent = plural(shows.length, 'title', 'titles') + ' · ' + have + ' of ' + plural(videos, 'video has', 'videos have') + ' a subtitle';
            }

            filter.addEventListener('input', draw);
            sort.addEventListener('change', draw);
            onlyMissing.addEventListener('change', draw);

            holder.append(
                tabs,
                h('div', { class: 'tools' },
                    h('h1', { text: library.Name, class: 'grow' }),
                    summary),
                h('div', { class: 'tools' },
                    filter, sort,
                    h('label', { class: 'check' }, onlyMissing, h('span', { text: 'Only with missing subtitles' }))),
                grid);
            grid.append(h('p', { class: 'muted', text: 'Loading…' }));
            show(holder);

            function load() {
                return call('/Retimer/Libraries/' + library.Id + '/Shows').then(function (answer) {
                    if (mine !== routeToken) { return; }
                    shows = answer;
                    draw();
                });
            }

            // A finished job changes a show's count, so the list is read again then.
            onJobs = function (changed) {
                if (changed.some(function (job) { return !isActive(job); })) { load().catch(function () {}); } else { draw(); }
            };
            return load();
        }).catch(function (error) {
            if (mine === routeToken) { show(h('p', { class: 'pad bad', text: error.message })); }
        });
    }

    // ---------------------------------------------------------------- screen: one show

    function showScreen(showId) {
        var mine = routeToken;
        var data = null;
        var reloadTimer = null;
        var rows = {};
        var openMenu = null;
        var drawnMissing = 0;
        var settling = {};      // Videos whose job ended and whose new state is not read yet.
        var holder = h('div');
        show(h('p', { class: 'muted pad', text: 'Loading…' }));

        function closeMenu() {
            if (openMenu) { openMenu.remove(); openMenu = null; }
        }

        function outside(event) {
            if (openMenu && !openMenu.parentNode.contains(event.target)) { closeMenu(); }
        }

        function escape(event) {
            if (event.key === 'Escape') { closeMenu(); }
        }

        document.addEventListener('click', outside);
        document.addEventListener('keydown', escape);
        leave = function () {
            document.removeEventListener('click', outside);
            document.removeEventListener('keydown', escape);
            clearTimeout(reloadTimer);
        };

        function run(promise) {
            say(null);
            return promise.catch(function (error) { say(error.message); });
        }

        function get(episode, kind) {
            return run(start({ ItemIds: [episode.Id], Kind: kind }));
        }

        function remove(episode) {
            confirmBox('Remove this subtitle?', 'The subtitle file the plugin saved for “' + episode.Name + '” is deleted. Other subtitles of the video stay.', 'Remove subtitle').then(function (yes) {
                if (!yes) { return; }
                run(call('/Retimer/Items/' + episode.Id + '/Subtitle', { method: 'DELETE' }).then(load));
            });
        }

        // What a row offers, from the episode's state. The first entry is the button, the rest go in the menu.
        function offers(episode, state) {
            var main = null;
            var more = [];
            if (!status.MayFetch || !episode.CanSave) { return { main: null, more: [] }; }
            var choose = { text: 'Choose a file', go: function () { location.hash = '#/pick/' + episode.Id; } };
            var plain = status.RetimerReady ? { text: 'Save without retiming', go: function () { get(episode, 'plain'); } } : null;
            var another = { text: 'Try another file', go: function () { get(episode, 'another'); } };
            var removeIt = { text: 'Remove subtitle', go: function () { remove(episode); } };

            if (state === 'none') {
                main = { text: 'Get subtitle', primary: true, go: function () { get(episode, 'get'); } };
                more = [choose, plain];
            } else if (state === 'failed') {
                main = { text: 'Try again', primary: true, go: function () { get(episode, 'get'); } };
                more = [choose, plain];
            } else if (state === 'nofit') {
                main = choose;
                more = [{ text: 'Search the sources again', go: function () { get(episode, 'get'); } }];
            } else if (state === 'raw') {
                main = episode.CanRetime ? { text: 'Retime', primary: true, go: function () { get(episode, 'retime'); } } : null;
                more = [another, choose, removeIt];
            } else if (state === 'retimed') {
                more = [another, choose, removeIt];
            } else if (state === 'present') {
                more = [{ text: 'Get one from the sources', go: function () { get(episode, 'get'); } }, choose];
            }
            return { main: main, more: more.filter(Boolean) };
        }

        function drawRow(episode) {
            var row = rows[episode.Id];
            var job = activeFor(episode.Id);
            var state = job ? job.State : episode.State;
            var text = episode.Text;
            var detail = episode.Detail;

            // The job ended and the show is not read again yet. The row waits for the result and offers nothing meanwhile,
            // so it never looks as if the job had failed.
            var finishing = !job && (settling[episode.Id] || state === 'queued' || state === 'working');
            if (job && job.State === 'working') {
                text = cancelling[job.Id] ? 'Cancelling…' : (job.StepText || 'Starting');
                detail = job.StepNote;
            } else if (job) {
                text = 'Queued';
                detail = job.Position <= 1 ? 'Starts when the one before it is done' : (job.Position - 1) + ' more before it';
            } else if (finishing) {
                state = 'working';
                text = 'Finishing…';
                detail = null;
            }

            var stateBox = h('div', { class: 'state' },
                h('div', { class: 'text' }, h('span', { class: 'dot ' + state }), h('span', { text: text })),
                detail ? h('div', { class: 'muted small', text: tidy(detail) }) : null);
            if (job && job.State === 'working' && job.Steps > 0) {
                var bar = h('i');
                bar.style.width = Math.round(100 * job.Step / job.Steps) + '%';
                stateBox.append(h('div', { class: 'steps', title: 'Step ' + job.Step + ' of ' + job.Steps }, bar));
            }
            if (!job && !finishing && episode.FromPlugin && episode.FileName) {
                // The whole name is shown: its end is what tells the files of an episode apart.
                stateBox.append(h('div', { class: 'muted small', text: 'From ' + sourceName(episode.Source) + (episode.PickedBy ? ', picked by ' + episode.PickedBy : '') }));
                stateBox.append(h('div', { class: 'muted small fileName', text: episode.FileName }));
            }

            var actions = h('div', { class: 'actions' });
            if (job) {
                if (job.MayCancel) {
                    actions.append(cancelButton(job, function () { say(null); drawRow(episode); }));
                }
            } else if (!finishing) {
                var offer = offers(episode, state);
                if (offer.main) {
                    actions.append(h('button', { class: 'button' + (offer.main.primary ? ' primary' : ''), text: offer.main.text, on: { click: offer.main.go } }));
                }
                if (offer.more.length > 0) {
                    var toggle = h('button', { class: 'button quiet', text: 'More', 'aria-haspopup': 'menu', 'aria-label': 'More for ' + episode.Name, on: { click: function (event) {
                        event.stopPropagation();
                        var wasOpen = openMenu && openMenu.parentNode === actions;
                        closeMenu();
                        if (wasOpen) { return; }
                        openMenu = h('div', { class: 'menu', role: 'menu' }, offer.more.map(function (entry) {
                            return h('button', { role: 'menuitem', text: entry.text, on: { click: function () { closeMenu(); entry.go(); } } });
                        }));
                        actions.append(openMenu);
                        openMenu.firstChild.focus();
                    } } });
                    actions.append(toggle);
                }
            }

            fill(row,
                h('div', { class: 'thumb' },
                    picture(episode.Id, episode.ImageTag, 320),
                    episode.Number !== null && episode.Number !== undefined ? h('span', { class: 'num', text: String(episode.Number) }) : null),
                h('div', { class: 'what' },
                    h('div', { class: 'name', text: (episode.Number !== null && episode.Number !== undefined && !data.IsMovie ? episode.Number + '. ' : '') + episode.Name }),
                    h('div', { class: 'muted small', text: (episode.Minutes ? episode.Minutes + ' min' : '') + (episode.CanSave ? '' : ' · Not a file on this server') })),
                stateBox,
                actions);
        }

        function sourceName(id) {
            var found = (status.Sources || []).filter(function (s) { return s.Id === id; })[0];
            return found ? found.Name : (id || 'a source');
        }

        function missingOf(episodes) {
            return episodes.filter(function (e) { return (e.State === 'none' || e.State === 'failed') && e.CanSave && !activeFor(e.Id) && !settling[e.Id]; });
        }

        function getAll(episodes, where) {
            var list = missingOf(episodes);
            if (list.length === 0) { return; }
            var lines = [
                'They are fetched one after the other, and you can leave this page meanwhile.',
                status.RetimerReady ? 'Each one is also retimed with Retimer API, which takes between half a minute and two minutes and costs a few cents at most.' : null,
                status.ModelName ? status.ModelName + ' picks the files.' : null,
                usageText(status.Usage)
            ];
            confirmBox('Get ' + plural(list.length, 'subtitle', 'subtitles') + ' for ' + where + '?', lines, 'Get ' + plural(list.length, 'subtitle', 'subtitles')).then(function (yes) {
                if (yes) { run(start({ ItemIds: list.map(function (e) { return e.Id; }), Kind: 'get' })); }
            });
        }

        function draw() {
            closeMenu();
            rows = {};
            var all = [];
            data.Seasons.forEach(function (s) { all = all.concat(s.Episodes); });
            var have = all.filter(function (e) { return e.State === 'retimed' || e.State === 'raw' || e.State === 'present'; }).length;

            var language = h('select', { id: 'showLanguage' }, status.Languages.map(function (l) {
                return h('option', { value: l.Code === status.Language ? '' : l.Code, text: l.Name + (l.Code === status.Language ? ' (server default)' : ''), selected: l.Code === data.Language });
            }));
            language.disabled = !status.MayFetch;
            language.addEventListener('change', function () {
                run(call('/Retimer/Shows/' + data.Id + '/Language', { method: 'PUT', body: { Language: language.value } }).then(load));
            });

            var missingAll = missingOf(all);
            drawnMissing = missingAll.length;
            var notices = [];
            if (!status.MayFetch) {
                notices.push('You can look around here. Getting subtitles needs the right to manage subtitles in Jellyfin, which an administrator gives.');
            } else if (data.Sources.length === 0) {
                notices.push('No subtitle source is set up for ' + data.LanguageName + '.' + (status.IsAdministrator ? ' Set one up in the settings.' : ' Ask the administrator of this server.'));
            }
            if (status.MayFetch && nearLimit(status.Usage)) {
                notices.push("Today's limit is close. " + usageText(status.Usage));
            }

            fill(holder,
                data.BackdropTag ? h('div', { class: 'backdrop' }, picture(data.Id, data.BackdropTag, 1600, 'Backdrop')) : h('div', { class: 'backdrop' }),
                h('div', { class: 'showHead' },
                    h('div', { class: 'poster' }, h('div', { class: 'letter', text: data.Name }), picture(data.Id, data.PosterTag, 400)),
                    h('div', { class: 'about' },
                        h('h1', { text: data.Name }),
                        h('div', { class: 'muted', text: [data.Year, data.IsMovie ? 'Film' : plural(all.length, 'episode', 'episodes'), have + ' with a ' + data.LanguageName + ' subtitle'].filter(Boolean).join(' · ') }),
                        h('div', { class: 'muted small', text: data.Sources.length > 0 ? 'Sources: ' + data.Sources.join(', ') + (status.RetimerReady ? ' · Retimed with Retimer API' : ' · Saved as downloaded (Retimer API is off)') : '' }))),
                h('div', { class: 'showTools' },
                    h('div', { class: 'stack' },
                        h('label', { for: 'showLanguage', class: 'muted small', text: 'Subtitle language for this ' + (data.IsMovie ? 'film' : 'show') }),
                        language),
                    status.MayFetch && !data.IsMovie && missingAll.length > 0 ? h('button', { class: 'button primary', text: 'Get all missing (' + missingAll.length + ')', on: { click: function () { getAll(all, data.Name); } } }) : null,
                    h('a', { class: 'button quiet', href: base + '/web/#/details?id=' + data.Id, text: 'Open in Jellyfin' })),
                notices.length > 0 ? h('div', { class: 'section' }, notices.map(function (n) { return h('div', { class: 'notice', text: n }); })) : null);

            data.Seasons.forEach(function (season) {
                var missing = missingOf(season.Episodes);
                var section = h('section', { class: 'section' },
                    h('div', { class: 'sectionHead' },
                        h('h2', { text: season.Name, class: 'grow' }),
                        h('span', { class: 'muted', text: data.IsMovie ? '' : plural(season.Episodes.length, 'episode', 'episodes') }),
                        status.MayFetch && data.Seasons.length > 1 && missing.length > 0
                            ? h('button', { class: 'button', text: 'Get missing (' + missing.length + ')', on: { click: function () { getAll(season.Episodes, season.Name); } } })
                            : null));
                season.Episodes.forEach(function (episode) {
                    rows[episode.Id] = h('div', { class: 'episode' });
                    section.append(rows[episode.Id]);
                    drawRow(episode);
                });
                holder.append(section);
            });
            if (all.length === 0) {
                holder.append(h('p', { class: 'muted pad', text: 'This show has no episodes in the library.' }));
            }
        }

        function load() {
            // What ended before this request is in its answer. What ends while it is on its way waits for the next one.
            var settled = Object.keys(settling);
            return call('/Retimer/Shows/' + showId).then(function (answer) {
                if (mine !== routeToken) { return; }
                settled.forEach(function (id) { delete settling[id]; });
                data = answer;
                document.title = data.Name + ' · Subtitle Retimer';
                if (!holder.parentNode) { show(holder); }
                draw();
            }, function (error) {
                // Without an answer the rows show what was read last, with their buttons.
                settled.forEach(function (id) { delete settling[id]; });
                if (mine === routeToken && data) { draw(); }
                throw error;
            });
        }

        onJobs = function (changed) {
            if (!data) { return; }
            var here = changed.filter(function (job) { return job.ShowId === data.Id; });
            if (here.length === 0) { return; }
            here.forEach(function (job) {
                if (!isActive(job)) { settling[job.ItemId] = true; delete cancelling[job.Id]; }
            });
            var byId = {};
            var all = [];
            data.Seasons.forEach(function (s) { s.Episodes.forEach(function (e) { byId[e.Id] = e; all.push(e); }); });
            if (missingOf(all).length !== drawnMissing) {
                // A job started or ended, so the "Get missing" buttons count differently now.
                draw();
            } else {
                here.forEach(function (job) { if (byId[job.ItemId] && rows[job.ItemId]) { drawRow(byId[job.ItemId]); } });
            }

            // A job that ended changed what the library holds, so the show is read again, once for a burst of changes.
            if (here.some(function (job) { return !isActive(job); })) {
                clearTimeout(reloadTimer);
                reloadTimer = setTimeout(function () { load().catch(function () {}); }, 300);
            }
        };

        load().catch(function (error) {
            if (mine === routeToken) { show(h('p', { class: 'pad bad', text: error.message })); }
        });
    }

    // ---------------------------------------------------------------- screen: choose a file

    function pickerScreen(itemId) {
        var mine = routeToken;
        var data = null;
        var chosen = null;
        var showAll = false;
        var fileRows = [];
        var footName = h('span', { class: 'muted grow clip' });
        var footButtons = [];
        var holder = h('div', { class: 'narrow' });

        // Shows which file is chosen, without drawing the list again.
        function mark() {
            fileRows.forEach(function (entry) {
                entry.row.className = 'file' + (chosen === entry.file ? ' chosen' : '') + (entry.file.Unusable ? ' off' : '');
            });
            footName.textContent = chosen ? chosen.Name : 'Choose a file above.';
            footButtons.forEach(function (button) { button.disabled = !chosen; });
        }

        function loading(text) {
            show(h('div', { class: 'narrow' },
                h('h1', { text: 'Choose a subtitle' }),
                h('p', { class: 'muted', text: text })));
        }

        function back() {
            location.hash = data ? '#/show/' + data.ShowId : '#/';
        }

        function save(retime) {
            if (!chosen) { return; }
            say(null);
            start({ ItemIds: [itemId], Kind: 'chosen', Retime: retime, Source: chosen.Source, EntryId: chosen.EntryId, FileId: chosen.FileId })
                .then(function (answer) { if (answer.Started > 0 || answer.Already > 0) { back(); } })
                .catch(function (error) { say(error.message); });
        }

        // "first" marks the file the plugin would take by itself.
        function fileRow(file, first) {
            var usable = !file.Unusable;
            var radio = h('input', { type: 'radio', name: 'file', disabled: !usable, checked: chosen === file });
            var meta = [file.SourceName, (file.Name.split('.').pop() || '').toUpperCase(), size(file.Size), file.Modified ? new Date(file.Modified).toLocaleDateString() : null,
                file.Downloads ? file.Downloads + ' downloads' : null, file.HearingImpaired ? 'for the hearing impaired' : null, file.MachineTranslated ? 'machine translated' : null].filter(Boolean).join(' · ');
            var row = h('label', { class: 'file' + (chosen === file ? ' chosen' : '') + (usable ? '' : ' off') },
                radio,
                h('div', { class: 'grow' },
                    h('div', { class: 'name', text: file.Name }),
                    h('div', { class: 'muted small', text: meta }),
                    h('div', { class: 'small' },
                        file.Current ? h('span', { class: 'tag current', text: 'In use now' }) : null,
                        file.SuggestedBy && first && !file.Tried && !file.RejectedBefore ? h('span', { class: 'tag suggested', text: 'Suggested by ' + file.SuggestedBy }) : null,
                        file.Tried ? h('span', { class: 'tag tried', text: 'Already tried: ' + file.Tried }) : null,
                        file.RejectedBefore && !file.Tried ? h('span', { class: 'tag tried', text: 'A file of this release was rejected for another episode: ' + file.RejectedBefore }) : null,
                        h('span', { class: 'muted', text: file.Unusable || file.Reason || '' }))));
            radio.addEventListener('change', function () { chosen = file; mark(); });
            fileRows.push({ file: file, row: row });
            return row;
        }

        function wrongShow() {
            var input = h('input', { type: 'search', value: data.ShowName, 'aria-label': 'Title to search for', class: 'grow' });
            var results = h('div');
            function search() {
                results.replaceChildren(h('p', { class: 'muted', text: 'Searching…' }));
                call('/Retimer/Items/' + itemId + '/Entries?query=' + encodeURIComponent(input.value)).then(function (entries) {
                    results.replaceChildren();
                    if (entries.length === 0) { results.append(h('p', { class: 'muted', text: 'The sources have no entry with that title.' })); }
                    entries.forEach(function (entry) {
                        results.append(h('div', { class: 'model' },
                            h('div', { class: 'grow' },
                                h('div', { text: entry.JapaneseName || entry.Name }),
                                h('div', { class: 'muted small', text: [entry.SourceName, entry.EnglishName, entry.Name].filter(Boolean).join(' · ') })),
                            entry.Current ? h('span', { class: 'tag current', text: 'In use now' })
                                : h('button', { class: 'button', text: 'Use this entry', on: { click: function () {
                                    call('/Retimer/Items/' + itemId + '/Entry', { method: 'PUT', body: { Source: entry.Source, EntryId: entry.Id } })
                                        .then(function () { say('The plugin now uses “' + (entry.JapaneseName || entry.Name) + '” for this ' + (data.IsMovie ? 'film' : 'season') + '.', 'info'); load(); })
                                        .catch(function (error) { say(error.message); });
                                } } })));
                    });
                }).catch(function (error) { results.replaceChildren(h('p', { class: 'bad', text: error.message })); });
            }
            return h('div', { class: 'sub' },
                h('h3', { text: 'Wrong show? Search the sources' }),
                h('p', { class: 'hint', text: 'Type the title as the source knows it. The entry you choose is used for every episode of this ' + (data.IsMovie ? 'film' : 'season') + '.' }),
                h('form', { class: 'row', on: { submit: function (event) { event.preventDefault(); search(); } } }, input, h('button', { class: 'button', text: 'Search' })),
                results);
        }

        function draw() {
            fileRows = [];
            var saveRaw = data.RetimerReady ? h('button', { class: 'button', text: 'Save as it is', on: { click: function () { save(false); } } }) : null;
            var saveMain = h('button', { class: 'button primary', text: data.RetimerReady ? 'Retime and save' : 'Save', on: { click: function () { save(data.RetimerReady); } } });
            footButtons = [saveRaw, saveMain].filter(Boolean);
            var fits = data.Files.filter(function (f) { return f.Fits; });
            var rest = data.Files.filter(function (f) { return !f.Fits; });
            var list = h('div');
            fits.forEach(function (f, i) { list.append(fileRow(f, i === 0)); });
            if (fits.length === 0) {
                list.append(h('p', { class: 'muted', text: 'No file is clearly for this ' + (data.IsMovie ? 'film' : 'episode') + '.' + (rest.length > 0 ? ' Look through all files below.' : '') }));
            }
            if (rest.length > 0) {
                if (showAll) {
                    list.append(h('h3', { text: 'All other files' }));
                    rest.forEach(function (f) { list.append(fileRow(f)); });
                } else {
                    list.append(h('button', { class: 'button quiet', text: 'Show all files (' + rest.length + ' more)', on: { click: function () { showAll = true; draw(); } } }));
                }
            }

            var sources = data.Sources.map(function (s) {
                var text = s.Name + ': ' + (s.Problem ? s.Problem
                    : (s.EntryName ? 'entry “' + s.EntryName + '”' + (s.MatchedBy ? ', matched by ' + s.MatchedBy : '') + ', ' : '') + plural(s.Files, 'file', 'files'));
                return h('div', { class: 'small ' + (s.Problem ? 'warn' : 'muted'), text: text });
            });

            fill(holder,
                h('p', {}, h('a', { href: '#/show/' + data.ShowId, text: '← ' + data.ShowName })),
                h('h1', { text: 'Choose a subtitle' }),
                h('p', { class: 'muted', text: data.ShowName + ' · ' + data.ItemLabel + ' · ' + data.LanguageName }),
                sources,
                data.ModelProblem ? h('div', { class: 'notice', text: 'The model gave no answer (' + data.ModelProblem + '), so the files are ranked by their names.' }) : null,
                h('h2', { text: 'Files for this ' + (data.IsMovie ? 'film' : 'episode') + ', best first' }),
                list,
                data.CanSearchEntries ? wrongShow() : null,
                h('div', { class: 'stickyFoot' },
                    footName,
                    h('button', { class: 'button', text: 'Cancel', on: { click: back } }),
                    saveRaw,
                    saveMain));
            mark();
        }

        function load() {
            chosen = null;
            loading(status.ModelName ? 'Searching the sources. ' + status.ModelName + ' reads the file names, which can take a moment…' : 'Searching the sources…');
            return call('/Retimer/Items/' + itemId + '/Files').then(function (answer) {
                if (mine !== routeToken) { return; }
                data = answer;
                document.title = 'Choose a subtitle · Subtitle Retimer';
                show(holder);
                draw();
            }).catch(function (error) {
                if (mine !== routeToken) { return; }
                show(h('div', { class: 'narrow' },
                    h('h1', { text: 'Choose a subtitle' }),
                    h('p', { class: 'bad', text: error.message }),
                    h('p', {}, h('a', { href: '#/', text: 'Back to your shows' }))));
            });
        }

        load();
    }

    // ---------------------------------------------------------------- screen: activity

    var stateNames = {
        queued: 'Queued', working: 'Working', saved: 'Saved, retimed', raw: 'Saved, not retimed',
        nofit: 'No file fits', failed: 'Failed', cancelled: 'Cancelled'
    };

    function activityScreen() {
        var list = h('div');
        var usage = h('p', { class: 'muted' });
        var clear = status.IsAdministrator ? h('button', { class: 'button quiet', text: 'Clear the list', on: { click: function () {
            call('/Retimer/Jobs', { method: 'DELETE' }).then(function () {
                Object.keys(jobs).forEach(function (id) { if (!isActive(jobs[id])) { delete jobs[id]; } });
                draw();
            }).catch(function (error) { say(error.message); });
        } } }) : null;

        function order(a, b) {
            var rank = { working: 0, queued: 1 };
            var ra = rank[a.State] === undefined ? 2 : rank[a.State];
            var rb = rank[b.State] === undefined ? 2 : rank[b.State];
            if (ra !== rb) { return ra - rb; }
            if (ra === 1) { return a.Position - b.Position; }
            return new Date(b.Finished || b.Created) - new Date(a.Finished || a.Created);
        }

        function draw() {
            var all = Object.keys(jobs).map(function (id) { return jobs[id]; }).sort(order);

            // Only the newest job of a video offers to try again. An older one was tried again already.
            var newest = {};
            all.forEach(function (job) {
                var key = job.ItemId + ':' + job.Language;
                var at = new Date(job.Created).getTime();
                if (!newest[key] || at > newest[key].at) { newest[key] = { at: at, id: job.Id }; }
            });
            usage.textContent = usageText(status.Usage);
            usage.className = nearLimit(status.Usage) ? 'warn' : 'muted';
            if (clear) { clear.hidden = !all.some(function (job) { return !isActive(job); }); }
            list.replaceChildren();
            if (all.length === 0) {
                list.append(h('p', { class: 'muted', text: 'Nothing yet. Open a show and get a subtitle.' }));
            }
            all.forEach(function (job) {
                var active = isActive(job);
                var latest = newest[job.ItemId + ':' + job.Language].id === job.Id;
                var head = stateNames[job.State] || job.State;
                var detail = job.Detail;
                if (!active) { delete cancelling[job.Id]; }
                if (job.State === 'working') {
                    head = cancelling[job.Id] ? 'Cancelling…' : (job.StepText || 'Starting') + (job.Steps > 0 ? ' (step ' + job.Step + ' of ' + job.Steps + ')' : '');
                    detail = job.StepNote;
                } else if (job.State === 'queued') {
                    detail = job.Position <= 1 ? 'Starts when the one before it is done' : (job.Position - 1) + ' more before it';
                }
                var dot = job.State === 'saved' ? 'retimed' : job.State === 'cancelled' ? '' : job.State;
                var buttons = h('div', { class: 'row' });
                if (active && job.MayCancel) {
                    buttons.append(cancelButton(job, draw));
                }
                if (!active && latest && status.MayFetch && (job.State === 'failed' || job.State === 'cancelled' || job.State === 'nofit')) {
                    buttons.append(h('button', { class: 'button', text: 'Try again', on: { click: function () {
                        call('/Retimer/Jobs/' + job.Id + '/Retry', { method: 'POST' }).then(function (answer) {
                            say(answer.Refused || (answer.Skipped ? answer.SkippedBecause : null));
                            pollJobs();
                        }).catch(function (error) { say(error.message); });
                    } } }));
                }
                if (!active && latest && status.MayFetch && (job.State === 'failed' || job.State === 'nofit')) {
                    buttons.append(h('a', { class: 'button', href: '#/pick/' + job.ItemId, text: 'Choose a file' }));
                }
                buttons.append(h('a', { class: 'button quiet', href: '#/show/' + job.ShowId, text: 'Open show' }));

                var when = active ? (job.Started ? 'Started ' + ago(job.Started) : 'Added ' + ago(job.Created)) : ago(job.Finished);
                list.append(h('div', { class: 'job' },
                    h('div', { class: 'poster' }, picture(job.ShowId, 'job', 120)),
                    h('div', { class: 'body' },
                        h('div', { class: 'head' }, h('span', { class: 'dot ' + dot }), h('span', { text: head })),
                        detail ? h('div', { text: tidy(detail) }) : null,
                        h('div', {}, h('a', { href: '#/show/' + job.ShowId, text: job.ShowName }), h('span', { class: 'muted', text: ' · ' + job.ItemLabel + ' · ' + job.LanguageName })),
                        h('div', { class: 'muted small', text: [when, 'Started by ' + job.StartedBy, job.CostUsd > 0 ? 'Cost ' + money(job.CostUsd) : null].filter(Boolean).join(' · ') }),
                        job.Tried && job.Tried.length > 0 ? h('details', {},
                            h('summary', { class: 'small', text: plural(job.Tried.length, 'file was', 'files were') + ' tried and rejected' }),
                            h('ul', { class: 'small muted' }, job.Tried.map(function (t) { return h('li', { text: t.Name + ': ' + t.Reason }); }))) : null),
                    buttons));
            });
        }

        document.title = 'Activity · Subtitle Retimer';
        show(h('div', { class: 'narrow' },
            h('div', { class: 'row' }, h('h1', { text: 'Activity', class: 'grow' }), clear),
            h('p', { class: 'muted', text: 'Jobs run one after the other. They keep running when you leave this page.' }),
            usage,
            list));
        onJobs = draw;
        draw();
        pollJobs();
    }

    // ---------------------------------------------------------------- screen: settings

    function settingsScreen() {
        var mine = routeToken;
        var s = null;
        var clearSecrets = {};
        var saveStatus = h('span', { role: 'status', 'aria-live': 'polite' });
        var holder = h('div', { class: 'narrow' });
        var fields = {};
        show(h('p', { class: 'muted pad', text: 'Loading the settings…' }));

        function field(label, control, hint) {
            var id = 'f' + Math.random().toString(36).slice(2);
            control.id = id;
            return h('div', { class: 'field' }, h('label', { for: id, text: label }), control, hint ? h('div', { class: 'hint', text: hint }) : null);
        }

        function check(name, label, hint, on) {
            var box = h('input', { type: 'checkbox', checked: !!on });
            fields[name] = box;
            return h('label', { class: 'check' }, box, h('span', {}, h('span', { text: label }), hint ? h('span', { class: 'hint', text: hint }) : null));
        }

        function radios(name, options, value) {
            var group = h('div', { role: 'radiogroup' });
            var id = 'r' + Math.random().toString(36).slice(2);
            options.forEach(function (o) {
                var radio = h('input', { type: 'radio', name: id, value: o.value, checked: o.value === value });
                group.append(h('label', { class: 'check' }, radio, h('span', {}, h('span', { text: o.text }), o.hint ? h('span', { class: 'hint', text: o.hint }) : null)));
            });
            fields[name] = { get value() { var c = group.querySelector('input:checked'); return c ? c.value : value; } };
            return group;
        }

        function select(name, options, value) {
            var control = h('select', {}, options.map(function (o) { return h('option', { value: o.value, text: o.text, selected: o.value === value }); }));
            fields[name] = control;
            return control;
        }

        // A key is never shown. The field says whether one is stored, and typing replaces it.
        function secret(name, label, stored, hint) {
            var input = h('input', { type: 'password', autocomplete: 'off', spellcheck: 'false', placeholder: stored ? 'A key is stored. Type a new one to replace it.' : '' });
            fields[name] = input;
            var note = h('span', { class: 'hint' });
            var removeButton = h('button', { class: 'linkButton', type: 'button', text: 'Remove the stored one', hidden: !stored, on: { click: function () {
                clearSecrets[name] = true;
                input.placeholder = '';
                removeButton.hidden = true;
                note.textContent = 'It is removed when you save.';
            } } });
            var box = field(label, input, hint);
            box.append(h('div', { class: 'row' }, removeButton, note));
            return box;
        }

        function tester(target, label, modelId) {
            var result = h('span', { role: 'status', 'aria-live': 'polite', class: 'small' });
            var button = h('button', { class: 'button', type: 'button', text: label, on: { click: function () {
                button.disabled = true;
                result.className = 'small muted';
                result.textContent = 'Saving, then checking…';
                save(true).then(function (saved) {
                    if (!saved) { result.textContent = ''; return null; }
                    return call('/Retimer/Test/' + target, { method: 'POST', body: { ModelId: modelId || null } }).then(function (answer) {
                        result.className = 'small ' + (answer.Ok ? 'ok' : 'bad');
                        result.textContent = answer.Message;
                    });
                }).catch(function (error) {
                    result.className = 'small bad';
                    result.textContent = error.message;
                }).then(function () { button.disabled = false; });
            } } });
            return h('div', { class: 'row' }, button, result);
        }

        var models = [];
        var modelInUse = '';
        var modelList = h('div');

        function drawModels() {
            modelList.replaceChildren();
            if (models.length === 0) {
                modelList.append(h('p', { class: 'muted', text: 'No model is in the list, so the plugin picks by ids and episode numbers.' }));
            }
            models.forEach(function (m) {
                var inUse = m.Id === modelInUse;
                modelList.append(h('div', { class: 'model' },
                    h('div', { class: 'grow' },
                        h('div', {}, h('strong', { text: m.Name }), inUse ? h('span', { class: 'tag current', text: 'In use' }) : null),
                        h('div', { class: 'muted small', text: m.Id })),
                    inUse ? null : h('button', { class: 'button', type: 'button', text: 'Use this model', on: { click: function () { modelInUse = m.Id; drawModels(); } } }),
                    tester('model', 'Test', m.Id),
                    h('button', { class: 'button quiet', type: 'button', text: 'Remove', on: { click: function () {
                        models = models.filter(function (x) { return x.Id !== m.Id; });
                        if (modelInUse === m.Id) { modelInUse = models.length > 0 ? models[0].Id : ''; }
                        drawModels();
                    } } })));
            });
        }

        function collect() {
            var update = {
                SubtitleLanguage: fields.SubtitleLanguage.value,
                PreferredSource: fields.PreferredSource.value,
                JimakuEnabled: fields.JimakuEnabled.checked,
                OpenSubtitlesEnabled: fields.OpenSubtitlesEnabled.checked,
                OpenSubtitlesUsername: fields.OpenSubtitlesUsername.value,
                RetimerEnabled: fields.RetimerEnabled.checked,
                RetimerUrl: fields.RetimerUrl.value,
                SendAudio: fields.SendAudio.value,
                ConvertPictureSubtitles: fields.ConvertPictureSubtitles.checked,
                RemoveHearingImpaired: fields.RemoveHearingImpaired.checked,
                RemoveSongs: fields.RemoveSongs.checked,
                RemoveSignsAndCredits: fields.RemoveSignsAndCredits.checked,
                RemoveSymbols: fields.RemoveSymbols.checked,
                RemoveStyling: fields.RemoveStyling.checked,
                OutputFormat: fields.OutputFormat.value,
                Models: models,
                ModelInUse: modelInUse,
                WithoutModel: fields.WithoutModel.value,
                HiddenLibraryIds: s.Libraries.filter(function (l) { return !fields['lib' + l.Id].checked; }).map(function (l) { return l.Id; }),
                FetchAccess: fields.FetchAccess.value,
                MaxJobsPerDay: Math.max(0, parseInt(fields.MaxJobsPerDay.value, 10) || 0),
                MaxCostPerDay: Math.max(0, parseFloat(fields.MaxCostPerDay.value) || 0),
                ClearSecrets: Object.keys(clearSecrets)
            };
            ['JimakuApiKey', 'OpenSubtitlesApiKey', 'OpenSubtitlesPassword', 'RetimerApiKey', 'OpenRouterApiKey'].forEach(function (name) {
                if (fields[name].value) { update[name] = fields[name].value; }
            });
            return update;
        }

        // Saves, and gives true when the server took the settings. A check saves quietly first.
        function save(quiet) {
            saveStatus.className = 'muted';
            saveStatus.textContent = 'Saving…';
            return call('/Retimer/Settings', { method: 'PUT', body: collect() }).then(function (answer) {
                s = answer;
                clearSecrets = {};
                saveStatus.className = 'ok';
                saveStatus.textContent = 'Saved.';
                return refreshStatus().then(function () {
                    if (!quiet) { draw(); }
                    else {
                        // The typed keys are stored now, so the fields are emptied and say so.
                        ['JimakuApiKey', 'OpenSubtitlesApiKey', 'OpenSubtitlesPassword', 'RetimerApiKey', 'OpenRouterApiKey'].forEach(function (name) {
                            if (fields[name].value) { fields[name].value = ''; fields[name].placeholder = 'A key is stored. Type a new one to replace it.'; }
                        });
                    }
                    return true;
                });
            }).catch(function (error) {
                saveStatus.className = 'bad';
                saveStatus.textContent = error.message;
                return false;
            });
        }

        function draw() {
            fields = {};
            models = s.Models.map(function (m) { return { Id: m.Id, Name: m.Name }; });
            modelInUse = s.ModelInUse;
            drawModels();

            var newId = h('input', { type: 'text', placeholder: 'anthropic/claude-haiku-5.5', spellcheck: 'false' });
            var newName = h('input', { type: 'text', placeholder: 'Haiku 5.5' });
            var addError = h('span', { class: 'bad small', role: 'alert' });

            fields.OpenSubtitlesUsername = h('input', { type: 'text', value: s.OpenSubtitlesUsername, autocomplete: 'off', spellcheck: 'false' });
            fields.RetimerUrl = h('input', { type: 'url', value: s.RetimerUrl, placeholder: 'https://retimer.example.org', spellcheck: 'false' });
            fields.MaxJobsPerDay = h('input', { type: 'number', min: '0', step: '1', value: String(s.MaxJobsPerDay) });
            fields.MaxCostPerDay = h('input', { type: 'number', min: '0', step: '0.5', value: String(s.MaxCostPerDay) });

            var retimerHint = h('div', { class: 'hint' });
            var retimerSwitch = check('RetimerEnabled', 'Retime subtitles with Retimer API', null, s.RetimerEnabled);
            function hintRetimer() {
                retimerHint.textContent = fields.RetimerEnabled.checked
                    ? 'On. Each subtitle is checked and retimed before it is saved.'
                    : 'Off. Subtitles are saved as downloaded.';
            }
            fields.RetimerEnabled.addEventListener('change', hintRetimer);
            hintRetimer();

            fill(holder,
                h('h1', { text: 'Settings' }),
                h('p', { class: 'muted', text: 'Only administrators can open this page. The keys stay on this server and are never shown again.' }),

                h('section', { class: 'panel' },
                    h('h2', { text: 'Subtitle sources' }),
                    h('p', { class: 'hint', text: 'Where subtitles come from. Every source that is switched on is searched for each episode.' }),
                    field('Subtitle language, the default for every show',
                        select('SubtitleLanguage', s.Languages.map(function (l) { return { value: l.Code, text: l.Name }; }), s.SubtitleLanguage),
                        'A single show can use another language. You set that on the show\'s page, and it overrides this default.'),
                    field('Preferred source',
                        select('PreferredSource', s.Sources.map(function (x) { return { value: x.Id, text: x.Name }; }), s.PreferredSource),
                        'The preferred source wins when two files are equally good.'),
                    h('div', { class: 'sub' },
                        h('div', { class: 'row' }, h('div', {}, h('h3', { text: 'Jimaku' }), h('div', { class: 'hint', text: 'Japanese subtitles for anime and dramas' })), check('JimakuEnabled', 'Search Jimaku', null, s.JimakuEnabled)),
                        secret('JimakuApiKey', 'Jimaku API key', s.HasJimakuApiKey, 'Your own key, from the account page on jimaku.cc.'),
                        tester('jimaku', 'Check Jimaku')),
                    h('div', { class: 'sub' },
                        h('div', { class: 'row' }, h('div', {}, h('h3', { text: 'OpenSubtitles' }), h('div', { class: 'hint', text: 'Films and series in many languages' })), check('OpenSubtitlesEnabled', 'Search OpenSubtitles', null, s.OpenSubtitlesEnabled)),
                        secret('OpenSubtitlesApiKey', 'OpenSubtitles API key', s.HasOpenSubtitlesApiKey, 'Create the key in your OpenSubtitles account under “API consumers”.'),
                        field('User name', fields.OpenSubtitlesUsername),
                        secret('OpenSubtitlesPassword', 'Password', s.HasOpenSubtitlesPassword, 'OpenSubtitles hands out files only to a login, and limits downloads per day.'),
                        s.OpenSubtitlesRemaining !== null && s.OpenSubtitlesRemaining !== undefined ? h('p', { class: 'hint', text: plural(s.OpenSubtitlesRemaining, 'download is', 'downloads are') + ' left today.' }) : null,
                        tester('opensubtitles', 'Check OpenSubtitles'))),

                h('section', { class: 'panel' },
                    h('h2', { text: 'Retimer API' }),
                    h('p', { class: 'hint', text: 'A separate service that compares a subtitle with the video\'s own subtitle tracks, or with its audio, and fixes its timing. It is off until you switch it on.' }),
                    retimerSwitch, retimerHint,
                    h('p', {}, h('a', { href: s.RetimerProjectUrl, target: '_blank', rel: 'noopener noreferrer', text: 'What is Retimer API? See it on GitHub' })),
                    field('Retimer API address', fields.RetimerUrl, 'The hosted Retimer API, or your own server that runs it. Both take the same calls.'),
                    secret('RetimerApiKey', 'Retimer API key', s.HasRetimerApiKey, 'The key for that address.'),
                    tester('retimer', 'Check Retimer API'),
                    h('h3', { text: 'What Retimer API compares a subtitle with' }),
                    h('p', { class: 'hint', text: 'The plugin sends the text subtitle tracks that are inside the video file. They are in sync with the video, and Retimer API picks the one to use. Tracks in the subtitle\'s own language and English ones go first, up to six. Tracks in other languages are sent only when none of those fits.' }),
                    h('div', { class: 'fieldLabel', text: 'Send the audio' }),
                    radios('SendAudio', [
                        { value: 'never', text: 'Never', hint: 'A video without a usable subtitle track is saved without retiming.' },
                        { value: 'fallback', text: 'Only when no subtitle track in the video fits', hint: 'The audio is sent when the video has no text subtitle track, or when Retimer API finds that none of them fits. It is about 4 MB for an episode.' },
                        { value: 'always', text: 'Always, together with the subtitle tracks', hint: 'Every subtitle is also checked against what is spoken. The audio is about 4 MB for an episode.' }
                    ], s.SendAudio),
                    check('ConvertPictureSubtitles', 'Convert picture subtitles with Retimer API', 'Blu-ray subtitles are pictures (PGS). Retimer API reads them into text, for a small cost each. This covers picture tracks in the video and picture files from the sources. DVD subtitles cannot be read.', s.ConvertPictureSubtitles),
                    h('h3', { text: 'Clean-up while retiming' }),
                    h('p', { class: 'hint', text: 'Retimer API does this to every subtitle it retimes. A subtitle saved without retiming stays as downloaded.' }),
                    check('RemoveHearingImpaired', 'Remove hearing-impaired text', 'Sound descriptions and speaker names, for example （笑い） or [door slams].', s.RemoveHearingImpaired),
                    check('RemoveSongs', 'Remove songs', 'Opening and ending lyrics and other sung lines.', s.RemoveSongs),
                    check('RemoveSignsAndCredits', 'Remove signs and credits', 'Text that is shown on screen and staff credits, which nobody speaks.', s.RemoveSignsAndCredits),
                    check('RemoveSymbols', 'Remove music notes and other symbols', 'The words of the line stay.', s.RemoveSymbols),
                    check('RemoveStyling', 'Remove styling', 'Colours, fonts and positions go, the words stay.', s.RemoveStyling),
                    field('Save the subtitle as', select('OutputFormat', [
                        { value: 'same', text: 'The format of the downloaded file' }, { value: 'srt', text: 'SRT' }, { value: 'ass', text: 'ASS' }
                    ], s.OutputFormat))),

                h('section', { class: 'panel' },
                    h('h2', { text: 'AI model' }),
                    h('p', { class: 'hint', text: 'A model chooses whenever the plugin picks by itself: “Get subtitle” and “Get all missing”. It searches the subtitle sources for the show, picks the entry and ranks the files for each episode. After a download it reads a sample of the file and checks that it is in the wanted language. It can only choose among files the sources list, and it never gets the video.' }),
                    secret('OpenRouterApiKey', 'OpenRouter API key', s.HasOpenRouterApiKey, 'From openrouter.ai. The model\'s calls are billed to this key.'),
                    h('h3', { text: 'Models' }),
                    h('p', { class: 'hint', text: 'The model in use makes the choices. The others stay in the list, so you can switch. “Test” saves the settings and asks the model one small question.' }),
                    modelList,
                    h('div', { class: 'sub' },
                        h('h3', { text: 'Add a model' }),
                        field('Model id on OpenRouter', newId, 'Copy the id from the model\'s page on openrouter.ai. The model must be able to call tools.'),
                        field('Name to show (optional)', newName),
                        h('div', { class: 'row' },
                            h('button', { class: 'button', type: 'button', text: 'Add model', on: { click: function () {
                                var id = newId.value.trim();
                                if (!/^[A-Za-z0-9][A-Za-z0-9._:\/@-]{1,99}$/.test(id)) { addError.textContent = 'A model id looks like anthropic/claude-haiku-5.5.'; return; }
                                if (models.some(function (m) { return m.Id === id; })) { addError.textContent = 'That model is already in the list.'; return; }
                                addError.textContent = '';
                                models.push({ Id: id, Name: newName.value.trim() || id });
                                if (!modelInUse) { modelInUse = id; }
                                newId.value = '';
                                newName.value = '';
                                drawModels();
                            } } }),
                            addError)),
                    h('div', { class: 'fieldLabel', text: 'When no model is set, or the model gives no answer' }),
                    radios('WithoutModel', [
                        { value: 'rules', text: 'Pick by ids and episode numbers, without a model' },
                        { value: 'ask', text: 'Stop and ask me to choose a file' }
                    ], s.WithoutModel)),

                h('section', { class: 'panel' },
                    h('h2', { text: 'Libraries' }),
                    h('p', { class: 'hint', text: 'The libraries that appear on the page. Each user still sees only the libraries Jellyfin lets them see.' }),
                    s.Libraries.length === 0 ? h('p', { class: 'muted', text: 'This server has no library of shows or films.' }) : null,
                    s.Libraries.map(function (l) { return check('lib' + l.Id, l.Name, null, !l.Hidden); })),

                h('section', { class: 'panel' },
                    h('h2', { text: 'Who may fetch' }),
                    h('p', { class: 'hint', text: 'Everyone who is signed in can browse and see the states. Fetching uses the keys above.' }),
                    radios('FetchAccess', [
                        { value: 'managers', text: 'Administrators, and users who may manage subtitles in Jellyfin' },
                        { value: 'everyone', text: 'Every signed-in user' }
                    ], s.FetchAccess)),

                h('section', { class: 'panel' },
                    h('h2', { text: 'Limits for a day' }),
                    h('p', { class: 'hint', text: 'The plugin stops starting jobs when a limit is reached, and says so. The day ends at midnight UTC. 0 means no limit. ' + usageText(status.Usage) }),
                    field('Jobs on one day', fields.MaxJobsPerDay, 'Every job that starts to work counts, also one that fails. A job that is cancelled while it waits does not.'),
                    field('Cost on one day, in US dollars', fields.MaxCostPerDay, 'Counted from what Retimer API and OpenRouter report for each job. A retimed episode usually costs one to three cents.')),

                h('div', { class: 'saveBar' },
                    h('button', { class: 'button primary', type: 'button', text: 'Save', on: { click: function () { save(false); } } }),
                    saveStatus));
        }

        call('/Retimer/Settings').then(function (answer) {
            if (mine !== routeToken) { return; }
            s = answer;
            document.title = 'Settings · Subtitle Retimer';
            show(holder);
            draw();
        }).catch(function (error) {
            if (mine === routeToken) { show(h('p', { class: 'pad bad', text: error.message })); }
        });
    }

    // ---------------------------------------------------------------- routes and start

    function route() {
        routeToken++;
        if (leave) { leave(); leave = null; }
        onJobs = null;
        say(null);
        document.title = 'Subtitle Retimer';
        var parts = location.hash.replace(/^#\/?/, '').split('/');
        var name = parts[0] || '';
        var current = name === 'activity' ? 'navActivity' : name === 'settings' ? 'navSettings' : 'navShows';
        ['navShows', 'navActivity', 'navSettings'].forEach(function (id) {
            var link = document.getElementById(id);
            link.className = id === current ? 'current' : '';
            if (id === current) { link.setAttribute('aria-current', 'page'); } else { link.removeAttribute('aria-current'); }
        });
        window.scrollTo(0, 0);

        if (name === 'show' && parts[1]) { showScreen(parts[1]); }
        else if (name === 'pick' && parts[1]) { pickerScreen(parts[1]); }
        else if (name === 'activity') { activityScreen(); }
        else if (name === 'settings' && status.IsAdministrator) { settingsScreen(); }
        else { showsScreen(name === 'library' ? parts[1] : null); }
    }

    function refreshStatus() {
        return call('/Retimer/Status').then(function (answer) { status = answer; });
    }

    // The web client keeps the login in the browser's storage for this address.
    // The page reuses it, so there is no second login and no key of its own.
    function readTokens() {
        try {
            var stored = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            return (stored.Servers || []).filter(function (server) { return server && server.AccessToken; });
        } catch (e) {
            return [];
        }
    }

    // Tries each stored login until the server accepts one.
    function signIn(tokens) {
        if (tokens.length === 0) {
            token = null;
            show(h('div', { class: 'pad' },
                h('h1', { text: 'Sign in first' }),
                h('p', { class: 'muted', text: 'Sign in to Jellyfin in this browser, then open this page again.' }),
                h('p', {}, h('a', { class: 'button primary', href: base + '/web/', text: 'Open Jellyfin' }))));
            return;
        }

        token = tokens[0].AccessToken;
        api('/Retimer/Status').then(function (response) {
            if (response.status === 401) { return signIn(tokens.slice(1)); }
            if (!response.ok) { return problem(response).then(function (text) { show(h('p', { class: 'pad bad', text: text })); }); }
            return response.json().then(function (answer) {
                status = answer;
                jobsVersion = 0;
                document.getElementById('version').textContent = 'Subtitle Retimer ' + status.Version + ' · Signed in as ' + status.UserName;
                document.getElementById('navSettings').hidden = !status.IsAdministrator;
                window.addEventListener('hashchange', route);
                route();
                pollJobs();
            });
        }).catch(function () {
            show(h('p', { class: 'pad bad', text: 'The server could not be reached.' }));
        });
    }

    signIn(readTokens());
})();
